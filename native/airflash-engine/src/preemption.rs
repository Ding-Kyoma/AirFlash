//! Receiver-takeover policy: a live `statusFlags` probe decides, control-plane
//! symptoms only leave evidence.
//!
//! The reference sender does not infer a takeover from what the control channel
//! does. An unexpected `TEARDOWN`, a `403`, or `Connection: close` is logged with
//! context (`policy`, `rtp_healthy`, `packets_prepared`) and then the receiver is
//! asked directly: `GET /info` and read `statusFlags`. That live answer is the
//! only criterion:
//!
//! | probe result | action |
//! | --- | --- |
//! | free | reclaim |
//! | another sender is playing | queue one re-probe, not a failure |
//! | probe failed / timed out | reclaim anyway (fail-open) |
//!
//! Re-announcing is never enough on its own: a receiver that is serving another
//! sender still announces. The anti-storm limiter is a consecutive counter: a
//! takeover that happens within [`TAKEOVER_WINDOW`] of the previous reclaim
//! increments the count; at [`TAKEOVER_LIMIT`] the sender yields instead of
//! fighting for the device.
//!
//! The meaning of the individual `statusFlags` bits is **unknown** (Q30 §6 calls
//! it static-undeterminable; Q31 later recovered `ReceiverSessionActive` but not
//! an "another sender" bit). [`ANOTHER_SENDER_BIT`] is therefore `None`: while
//! it stays `None`, [`another_sender`] can only be `false` and a well-formed
//! probe can only be [`ProbeOutcome::Free`], never `BusyElsewhere`. AirFlash can
//! never wedge waiting on a bit it does not understand; the raw value is logged
//! so it can be mapped from a real receiver.
use crate::rtsp::{Cancellation, Connection};
use anyhow::{Context, Result};
use plist::Value;
use std::{
    collections::HashMap,
    fmt,
    net::{IpAddr, SocketAddr},
    time::{Duration, Instant},
};

/// How long a live status probe may take. Our choice: Q30 recovered neither
/// WAC's probe timeout nor its re-probe interval.
pub const PROBE_TIMEOUT: Duration = Duration::from_secs(2);

/// How long to wait before asking again when the receiver is busy with another
/// sender. This is a queue, not a failure retry. Our choice: Q30 recovered
/// neither WAC's probe timeout nor its re-probe interval; Q31's answer suggests
/// 3-5 s, so 3 s is the low end of that range and still leaves audible media
/// alone.
pub const REPROBE_DELAY: Duration = Duration::from_secs(3);

/// A takeover counter reaches this many consecutive reclaims and the sender
/// yields. Our choice: Q30 only recovered the *shape* of the limiter
/// (`taken over <n> times in a row`), not WAC's threshold.
pub const TAKEOVER_LIMIT: u32 = 3;

/// Two reclaims count as "in a row" only when the second happens at most this
/// long after the first. Our choice: WAC's window was not recovered either.
pub const TAKEOVER_WINDOW: Duration = Duration::from_secs(30);

/// The `statusFlags` bit that means "another sender is playing". Q30 §6 marks
/// the bit map as static-undeterminable. Q31's answer recovered a *related*
/// WAC constant, `ReceiverSessionActive = 0x20000` (a 64-bit `ulong`), but that
/// bit is true for **our own** session too; distinguishing "another sender"
/// needs a `we_hold_session` dimension that this engine does not reliably have
/// at probe time. Leave this `None`: a wrong mask would either reclaim our own
/// healthy session or queue forever on a receiver we own. Do **not** guess it.
pub const ANOTHER_SENDER_BIT: Option<u32> = None;

/// One control-plane thing the receiver did. Evidence only, never a verdict.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Signal {
    /// The receiver sent a `TEARDOWN` we did not ask for.
    UnexpectedTeardown,
    /// The receiver answered an RTSP request with `403 Forbidden`.
    Forbidden,
    /// The receiver asked for the control connection to be closed.
    ConnectionClose,
}

impl Signal {
    pub fn name(self) -> &'static str {
        match self {
            Self::UnexpectedTeardown => "unexpected_teardown",
            Self::Forbidden => "forbidden",
            Self::ConnectionClose => "connection_close",
        }
    }
}

/// A signal the receiver sent us. `None` means the message was a response to a
/// request this sender made — including the reply to our own TEARDOWN, which is
/// part of every normal session ending and must never look like a takeover.
pub fn incoming_teardown(method: Option<&str>) -> Option<Signal> {
    (method == Some("TEARDOWN")).then_some(Signal::UnexpectedTeardown)
}

/// A status code that is preemption evidence. Only `403`: `401` and `454` stay
/// on the authentication/session path they had before this module existed and
/// must never be reclassified just because another controller might be around.
pub fn status_signal(status: u16) -> Option<Signal> {
    (status == 403).then_some(Signal::Forbidden)
}

/// What a live `GET /info` probe could tell us.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProbeOutcome {
    /// The receiver is not playing another sender's session.
    Free,
    /// The receiver is playing another sender's session. Only reachable when
    /// [`ANOTHER_SENDER_BIT`] is known.
    BusyElsewhere,
    /// The probe completed but `statusFlags` was missing or not a `u32`.
    /// Structurally unclassifiable; the caller reclaims (fail-open).
    Unknown,
}

impl ProbeOutcome {
    pub fn name(self) -> &'static str {
        match self {
            Self::Free => "free",
            Self::BusyElsewhere => "busy_elsewhere",
            Self::Unknown => "unknown",
        }
    }
}

/// A successful probe, including the raw value needed to map the bit map later.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ProbeReport {
    pub outcome: ProbeOutcome,
    /// WAC's field is a 64-bit `ulong`; keep the full value for the log even
    /// though [`another_sender`] currently interprets the low 32 bits.
    pub status_flags: Option<u64>,
}

/// The one and only interpreter of `statusFlags` bit semantics.
pub fn another_sender(flags: u32) -> bool {
    ANOTHER_SENDER_BIT.is_some_and(|mask| flags & mask != 0)
}

fn outcome_for_flags(flags: u64) -> ProbeOutcome {
    // `another_sender` is the one bit interpreter and takes the low 32 bits,
    // which cover every bit recovered so far (WAC's bit 17). High bits are
    // still logged; they cannot mark BusyElsewhere while the mask is `None`.
    if another_sender(flags as u32) {
        ProbeOutcome::BusyElsewhere
    } else {
        // With ANOTHER_SENDER_BIT = None this is every well-formed probe.
        ProbeOutcome::Free
    }
}

fn status_flags(info: &Value) -> Option<u64> {
    info.as_dictionary()?.get("statusFlags")?.as_unsigned_integer()
}

/// Parse one `/info` body. Kept separate from the socket so the unknown-bit
/// property is unit-testable without a receiver.
fn report_from_info(info: &Value) -> ProbeReport {
    match status_flags(info) {
        Some(flags) => ProbeReport {
            outcome: outcome_for_flags(flags),
            status_flags: Some(flags),
        },
        None => ProbeReport {
            outcome: ProbeOutcome::Unknown,
            status_flags: None,
        },
    }
}

/// What to do after the verdict table has seen the probe result.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProbeAction {
    /// Stop the old session and take the receiver back on a fresh connection.
    Reclaim,
    /// Leave the receiver alone for now and ask again after [`REPROBE_DELAY`].
    Requeue,
}

/// The Q30 verdict table, for a probe that completed.
pub fn action_for(outcome: ProbeOutcome) -> ProbeAction {
    match outcome {
        ProbeOutcome::BusyElsewhere => ProbeAction::Requeue,
        ProbeOutcome::Free | ProbeOutcome::Unknown => ProbeAction::Reclaim,
    }
}

/// The Q30 verdict table including probe failure and timeout: **fail-open**.
/// Errors must never fall through to the generic transport-fault branch.
pub fn action_for_result(result: &Result<ProbeReport>) -> ProbeAction {
    match result {
        Ok(report) => action_for(report.outcome),
        Err(_) => ProbeAction::Reclaim,
    }
}

fn probe_infos(addr: SocketAddr, cancel: &Cancellation, timeout: Duration) -> Result<ProbeReport> {
    let mut connection =
        Connection::connect(addr, cancel.clone()).with_context(|| format!("status probe {addr}"))?;
    connection.set_timeout(timeout)?;
    let message = connection
        .request("GET", "/info", &[], &[])
        .context("status probe GET /info")?;
    let info = message.plist().context("status probe /info plist")?;
    Ok(report_from_info(&info))
}

/// Probe the receiver's live status on a fresh RTSP connection.
///
/// The caller's existing control connection is deliberately not used: after a
/// takeover it belongs to a session the receiver already ended, and encrypted
/// RTSP cannot be re-keyed mid-session (Q16). Any TCP/RTSP/plist error or read
/// timeout is returned as `Err`; the policy layer turns that into a reclaim.
pub fn probe(addr: SocketAddr, cancel: &Cancellation, timeout: Duration) -> Result<ProbeOutcome> {
    Ok(probe_infos(addr, cancel, timeout)?.outcome)
}

/// Same as [`probe`], but keeps the raw `statusFlags` for the diagnostic log.
pub fn probe_report(
    addr: SocketAddr,
    cancel: &Cancellation,
    timeout: Duration,
) -> Result<ProbeReport> {
    probe_infos(addr, cancel, timeout)
}

/// Why the sender stopped reclaiming.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ReclaimReason {
    /// A genuine takeover: the old session ends and the receiver is reclaimed.
    Takeover,
    /// The consecutive limit was hit: the sender yields and does not reconnect.
    Limit,
}

impl ReclaimReason {
    pub fn name(self) -> &'static str {
        match self {
            Self::Takeover => "takeover",
            Self::Limit => "limit",
        }
    }
}

/// Result of counting one reclaim for one receiver.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TakeoverVerdict {
    /// Below the limit: reclaim is allowed and `consecutive` is the count.
    Reclaim { consecutive: u32 },
    /// At the limit: stop reclaiming and report `PREEMPTED(limit)`.
    Yield { consecutive: u32 },
}

/// The "taken over N times in a row" counter, one instance per receiver.
#[derive(Clone, Copy, Debug, Default)]
pub struct TakeoverCounter {
    consecutive: u32,
    last_reclaim: Option<Instant>,
}

impl TakeoverCounter {
    pub fn consecutive(&self) -> u32 {
        self.consecutive
    }

    /// Record one reclaim. A reclaim inside [`TAKEOVER_WINDOW`] of the previous
    /// one increments the count; a reclaim after the window starts over at 1
    /// (not 0). Reaching [`TAKEOVER_LIMIT`] yields.
    pub fn record(&mut self, now: Instant) -> TakeoverVerdict {
        self.consecutive = match self.last_reclaim {
            Some(last) if now.saturating_duration_since(last) <= TAKEOVER_WINDOW => {
                self.consecutive.saturating_add(1)
            }
            _ => 1,
        };
        self.last_reclaim = Some(now);
        if self.consecutive >= TAKEOVER_LIMIT {
            TakeoverVerdict::Yield {
                consecutive: self.consecutive,
            }
        } else {
            TakeoverVerdict::Reclaim {
                consecutive: self.consecutive,
            }
        }
    }
}

/// Per-receiver counters. A stereo pair can be displaced independently, so the
/// count must not be shared between members.
#[derive(Default)]
pub struct TakeoverCounters {
    counters: HashMap<IpAddr, TakeoverCounter>,
}

impl TakeoverCounters {
    pub fn record(&mut self, host: IpAddr, now: Instant) -> TakeoverVerdict {
        self.counters.entry(host).or_default().record(now)
    }

    pub fn consecutive(&self, host: IpAddr) -> u32 {
        self.counters.get(&host).map_or(0, TakeoverCounter::consecutive)
    }
}

/// A reclaim that is not a terminal fault: the live loop must open a fresh
/// connection and continue. Used as an `anyhow` error so it can travel through
/// the existing setup/streaming call stack without a second return type.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Reclaim {
    pub host: IpAddr,
    pub consecutive: u32,
}

impl fmt::Display for Reclaim {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "receiver {} reclaimed after takeover {}",
            self.host, self.consecutive
        )
    }
}

impl std::error::Error for Reclaim {}

/// Per-member evidence and probe scheduling. Only the session thread probes;
/// feedback workers merely append [`Signal`]s to `Health`.
#[derive(Debug, Default)]
pub struct Tracker {
    signals: Vec<Signal>,
    next_probe: Option<Instant>,
    last_report: Option<ProbeLog>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct ProbeLog {
    outcome: Option<ProbeOutcome>,
    status_flags: Option<u64>,
    error: bool,
}

impl Tracker {
    pub fn observe(&mut self, signal: Signal, at: Instant) {
        const MAX_SIGNALS: usize = 8;
        if self.signals.len() == MAX_SIGNALS {
            self.signals.remove(0);
        }
        self.signals.push(signal);
        // A new signal asks for a probe now, unless a busy queue is already
        // waiting. Repeated symptoms must not shorten the re-probe interval.
        if self.next_probe.is_none() {
            self.next_probe = Some(at);
        }
    }

    /// Signals collected since the last call, for the one evidence log line
    /// that carries `policy`/`rtp_healthy`/`packets_prepared`.
    pub fn take_signals(&mut self) -> Vec<Signal> {
        std::mem::take(&mut self.signals)
    }

    pub fn due(&self, now: Instant) -> bool {
        self.next_probe.is_some_and(|at| now >= at)
    }

    pub fn scheduled(&self) -> Option<Instant> {
        self.next_probe
    }

    /// Feed one probe result back. Returns true when the diagnostic log line
    /// changed (first result or a different outcome/raw value), so a busy queue
    /// does not print the same line every interval.
    pub fn note_result(&mut self, result: &Result<ProbeReport>, now: Instant) -> bool {
        let log = match result {
            Ok(report) => ProbeLog {
                outcome: Some(report.outcome),
                status_flags: report.status_flags,
                error: false,
            },
            Err(_) => ProbeLog {
                outcome: None,
                status_flags: None,
                error: true,
            },
        };
        let changed = self.last_report != Some(log);
        self.last_report = Some(log);
        self.next_probe = match result {
            Ok(report) if report.outcome == ProbeOutcome::BusyElsewhere => {
                Some(now + REPROBE_DELAY)
            }
            _ => None,
        };
        changed
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use anyhow::anyhow;

    fn at(base: Instant, ms: u64) -> Instant {
        base + Duration::from_millis(ms)
    }

    fn report(outcome: ProbeOutcome) -> ProbeReport {
        ProbeReport {
            outcome,
            status_flags: Some(0),
        }
    }

    #[test]
    fn control_signals_are_evidence_only_and_keep_their_classification() {
        assert_eq!(incoming_teardown(None), None);
        assert_eq!(incoming_teardown(Some("SETUP")), None);
        assert_eq!(
            incoming_teardown(Some("TEARDOWN")),
            Some(Signal::UnexpectedTeardown)
        );
        // Our own TEARDOWN is sent as a request and answered with a response;
        // `incoming_teardown` never sees a method on that response.
        assert_eq!(incoming_teardown(None), None);

        assert_eq!(status_signal(403), Some(Signal::Forbidden));
        assert_eq!(status_signal(401), None);
        assert_eq!(status_signal(454), None);
        assert_eq!(status_signal(200), None);
    }

    #[test]
    fn verdict_table_matches_q30() {
        assert_eq!(action_for(ProbeOutcome::Free), ProbeAction::Reclaim);
        assert_eq!(
            action_for(ProbeOutcome::BusyElsewhere),
            ProbeAction::Requeue
        );
        // A probe that completed but could not be classified is fail-open.
        assert_eq!(action_for(ProbeOutcome::Unknown), ProbeAction::Reclaim);
        assert_eq!(
            action_for_result(&Ok(report(ProbeOutcome::Free))),
            ProbeAction::Reclaim
        );
        assert_eq!(
            action_for_result(&Ok(report(ProbeOutcome::BusyElsewhere))),
            ProbeAction::Requeue
        );
        assert_eq!(
            action_for_result(&Ok(report(ProbeOutcome::Unknown))),
            ProbeAction::Reclaim
        );
    }

    #[test]
    fn probe_failure_and_timeout_are_fail_open() {
        for error in [
            anyhow!("connection refused"),
            anyhow!("status probe /info: control read timeout"),
        ] {
            assert_eq!(
                action_for_result(&Err(error)),
                ProbeAction::Reclaim,
                "a probe error must reclaim, never fall into the generic fault branch"
            );
        }
    }

    #[test]
    fn an_unknown_bitmap_can_only_ever_yield_free_never_busy() {
        assert_eq!(ANOTHER_SENDER_BIT, None);
        for flags in [
            0,
            1,
            0b10,
            1 << 7,
            1 << 31,
            0x0000_0100,
            0x8000_0000,
            u32::MAX,
        ] {
            assert!(
                !another_sender(flags),
                "statusFlags=0x{flags:08x} must not be guessed as busy"
            );
            assert_eq!(outcome_for_flags(u64::from(flags)), ProbeOutcome::Free);
            assert_eq!(
                action_for(outcome_for_flags(u64::from(flags))),
                ProbeAction::Reclaim
            );
        }
    }

    #[test]
    fn missing_or_malformed_status_flags_is_unknown_and_reclaims() {
        let mut info = plist::Dictionary::new();
        let report = report_from_info(&Value::Dictionary(info.clone()));
        assert_eq!(
            report,
            ProbeReport {
                outcome: ProbeOutcome::Unknown,
                status_flags: None
            }
        );
        info.insert("statusFlags".into(), Value::String("not a number".into()));
        let report = report_from_info(&Value::Dictionary(info));
        assert_eq!(report.outcome, ProbeOutcome::Unknown);
        assert_eq!(action_for(report.outcome), ProbeAction::Reclaim);
    }

    #[test]
    fn raw_status_flags_survive_parsing_for_the_device_mapping() {
        let mut info = plist::Dictionary::new();
        info.insert("statusFlags".into(), Value::Integer(0x1234_5678.into()));
        assert_eq!(
            report_from_info(&Value::Dictionary(info)),
            ProbeReport {
                outcome: ProbeOutcome::Free,
                status_flags: Some(0x1234_5678)
            }
        );
        // WAC's field is a 64-bit ulong; a high bit is still logged and must
        // not turn a well-formed probe into `Unknown` while the mask is unset.
        let mut wide = plist::Dictionary::new();
        wide.insert(
            "statusFlags".into(),
            Value::Integer(0x1_0000_0002u64.into()),
        );
        let report = report_from_info(&Value::Dictionary(wide));
        assert_eq!(report.outcome, ProbeOutcome::Free);
        assert_eq!(report.status_flags, Some(0x1_0000_0002));
    }

    #[test]
    fn the_first_takeover_counts_as_one_and_reclaims() {
        let base = Instant::now();
        let mut counter = TakeoverCounter::default();
        assert_eq!(
            counter.record(at(base, 0)),
            TakeoverVerdict::Reclaim { consecutive: 1 }
        );
        assert_eq!(counter.consecutive(), 1);
    }

    #[test]
    fn takeovers_inside_the_window_increment_in_a_row() {
        let base = Instant::now();
        let mut counter = TakeoverCounter::default();
        assert_eq!(
            counter.record(at(base, 0)),
            TakeoverVerdict::Reclaim { consecutive: 1 }
        );
        assert_eq!(
            counter.record(at(base, 1_000)),
            TakeoverVerdict::Reclaim { consecutive: 2 }
        );
        // Exactly on the boundary is still "within the window".
        assert_eq!(
            counter.record(at(base, 1_000) + TAKEOVER_WINDOW),
            TakeoverVerdict::Yield { consecutive: 3 }
        );
    }

    #[test]
    fn a_takeover_after_the_window_resets_the_count_to_one() {
        let base = Instant::now();
        let mut counter = TakeoverCounter::default();
        counter.record(at(base, 0));
        counter.record(at(base, 1_000));
        assert_eq!(
            counter.record(at(base, 1_000) + TAKEOVER_WINDOW + Duration::from_millis(1)),
            TakeoverVerdict::Reclaim { consecutive: 1 }
        );
    }

    #[test]
    fn hitting_the_limit_yields_and_a_later_takeover_starts_over() {
        let base = Instant::now();
        let mut counter = TakeoverCounter::default();
        counter.record(at(base, 0));
        counter.record(at(base, 100));
        assert_eq!(
            counter.record(at(base, 200)),
            TakeoverVerdict::Yield {
                consecutive: TAKEOVER_LIMIT
            }
        );
        assert_eq!(
            counter.record(at(base, 200) + TAKEOVER_WINDOW + Duration::from_millis(1)),
            TakeoverVerdict::Reclaim { consecutive: 1 }
        );
    }

    #[test]
    fn counters_are_per_receiver_not_per_group() {
        let base = Instant::now();
        let mut counters = TakeoverCounters::default();
        let left: IpAddr = "192.0.2.10".parse().unwrap();
        let right: IpAddr = "192.0.2.11".parse().unwrap();
        assert_eq!(
            counters.record(left, at(base, 0)),
            TakeoverVerdict::Reclaim { consecutive: 1 }
        );
        assert_eq!(
            counters.record(right, at(base, 0)),
            TakeoverVerdict::Reclaim { consecutive: 1 }
        );
        assert_eq!(counters.consecutive(left), 1);
        assert_eq!(counters.consecutive(right), 1);
    }

    #[test]
    fn a_signal_schedules_an_immediate_probe() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::UnexpectedTeardown, at(base, 0));
        assert!(tracker.due(at(base, 0)));
        assert_eq!(tracker.take_signals(), vec![Signal::UnexpectedTeardown]);
        assert!(tracker.take_signals().is_empty());
    }

    #[test]
    fn busy_outcome_queues_the_next_probe_instead_of_failing() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::Forbidden, at(base, 0));
        assert!(tracker.due(at(base, 0)));
        assert!(tracker.note_result(&Ok(report(ProbeOutcome::BusyElsewhere)), at(base, 0)));
        assert_eq!(
            tracker.scheduled(),
            Some(at(base, 0) + REPROBE_DELAY),
            "busy is a queue, not a retry storm"
        );
        assert!(!tracker.due(at(base, 0) + REPROBE_DELAY - Duration::from_millis(1)));
        assert!(tracker.due(at(base, 0) + REPROBE_DELAY));
        // A second busy probe does not change the log line, so it stays quiet.
        assert!(!tracker.note_result(
            &Ok(report(ProbeOutcome::BusyElsewhere)),
            at(base, REPROBE_DELAY.as_millis() as u64)
        ));
        // Fresh evidence while queued must not cut the interval short.
        tracker.observe(Signal::ConnectionClose, at(base, 500));
        assert_eq!(
            tracker.scheduled(),
            Some(at(base, 0) + REPROBE_DELAY + REPROBE_DELAY)
        );
    }

    #[test]
    fn free_or_failed_probe_clears_the_queue_for_this_tracker() {
        let base = Instant::now();
        for result in [
            Ok(report(ProbeOutcome::Free)),
            Ok(report(ProbeOutcome::Unknown)),
            Err(anyhow!("timed out")),
        ] {
            let mut tracker = Tracker::default();
            tracker.observe(Signal::UnexpectedTeardown, at(base, 0));
            assert!(tracker.note_result(&result, at(base, 100)));
            assert_eq!(tracker.scheduled(), None);
            assert!(!tracker.due(at(base, 5_000)));
        }
    }

    #[test]
    fn probe_log_only_changes_on_a_different_result() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::Forbidden, at(base, 0));
        assert!(tracker.note_result(
            &Ok(report(ProbeOutcome::BusyElsewhere)),
            at(base, 0)
        ));
        assert!(!tracker.note_result(
            &Ok(report(ProbeOutcome::BusyElsewhere)),
            at(base, 2_000)
        ));
        assert!(tracker.note_result(&Ok(report(ProbeOutcome::Free)), at(base, 4_000)));
    }
}
