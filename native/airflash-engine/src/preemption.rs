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
//! | no active session | reclaim |
//! | an active session that is ours | not preemption: fall through |
//! | an active session that is not ours | queue one re-probe, not a failure |
//! | probe failed / timed out | reclaim anyway (fail-open) |
//!
//! The bit values are recovered from the reference implementation (Q31 answer):
//! `ReceiverStatusFlags.ReceiverSessionActive = 131072 (0x20000, bit 17)`,
//! `ReceiverStatusFlags.StereoPartnerUnreachable = 16384 (0x4000, bit 14)` and
//! `ReceiverAccessPolicy.PasswordRequiredFlag = 128 (0x80, bit 7)`, all on a
//! 64-bit (`ulong`) field. Only the session bit is a decision input, and it is
//! **not** an "another sender" bit: it is true for our own session too. The
//! reference sender's "is playing another sender's session" line is context —
//! it probes from a reclaim path that already lost the session — so [`classify`]
//! takes a second dimension, [`Tracker::we_hold_session`]. Without it, filling
//! in `0x20000` would misjudge our own healthy session as a takeover and
//! immediately reclaim it, interrupting working audio.
//!
//! Re-announcing is never enough on its own: a receiver that is serving another
//! sender still announces. The anti-storm limiter is a consecutive counter: a
//! takeover that happens within [`TAKEOVER_WINDOW`] of the previous reclaim
//! increments the count; at [`TAKEOVER_LIMIT`] the sender yields instead of
//! fighting for the device.
//!
//! Real-device values are still unverified: the raw `statusFlags` value is
//! always logged so it can be mapped from a receiver that owns the session.
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

/// `ReceiverStatusFlags.ReceiverSessionActive` in the reference implementation:
/// `131072uL`, i.e. bit 17 of a 64-bit `ulong` `statusFlags` field (Q31).
///
/// The literal meaning is "the receiver has an active session" — **including our
/// own**. It is therefore not enough to decide a takeover; [`classify`] adds the
/// locally derived `we_hold_session` dimension. A real HomePod's value is still
/// unverified, which is why the raw field is logged on every probe.
pub const RECEIVER_SESSION_ACTIVE: u64 = 0x0002_0000;

/// `ReceiverStatusFlags.StereoPartnerUnreachable`: `16384uL`, i.e. bit 14 (Q31).
/// It says nothing about who owns the active session, so it must never influence
/// [`classify`]; the probe log surfaces it as telemetry only.
pub const STEREO_PARTNER_UNREACHABLE: u64 = 0x0000_4000;

/// `ReceiverAccessPolicy.PasswordRequiredFlag`: `128uL`, i.e. bit 7 (Q31). The
/// reference implementation classifies the receiver's access policy from it; it
/// is not a session-ownership signal and must never influence [`classify`].
pub const PASSWORD_REQUIRED: u64 = 0x0000_0080;

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

    /// Whether this signal means the receiver ended or refused **our** session.
    /// An unexpected `TEARDOWN` ends it outright; a `403` is the receiver
    /// refusing a request on it. Both say the session is not ours any more, so
    /// no later probe on this member can make us its owner again.
    /// `Connection: close` is deliberately **not** here: a keep-alive or event
    /// channel can be closed while the RTSP session itself is still ours.
    pub fn ends_session(self) -> bool {
        matches!(self, Self::UnexpectedTeardown | Self::Forbidden)
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
    /// No active session on the receiver ([`RECEIVER_SESSION_ACTIVE`] is clear),
    /// whatever the other bits say.
    Free,
    /// The receiver's active session is **ours**: the session bit is set and we
    /// still hold the session ([`Tracker::we_hold_session`]). Never a reclaim
    /// trigger — the receiver is still serving us, so the control-plane symptom
    /// is something else.
    OursActive,
    /// The receiver has an active session and we do not hold it: another sender
    /// owns the device. Queue one re-probe, do not fight.
    BusyElsewhere,
    /// The probe completed but `statusFlags` was missing or not an unsigned
    /// 64-bit integer. Structurally unclassifiable; the caller reclaims
    /// (fail-open), exactly like a failed probe.
    Unknown,
}

impl ProbeOutcome {
    pub fn name(self) -> &'static str {
        match self {
            Self::Free => "free",
            Self::OursActive => "ours_active",
            Self::BusyElsewhere => "busy_elsewhere",
            Self::Unknown => "unknown",
        }
    }
}

/// A successful probe, including the raw value needed to map the bit map later.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ProbeReport {
    pub outcome: ProbeOutcome,
    /// The reference implementation's field is a 64-bit `ulong`; keep the whole
    /// value for the log instead of truncating it to the bits we know.
    pub status_flags: Option<u64>,
}

/// The one and only interpreter of `statusFlags` session bits.
///
/// `we_hold_session` is the context the receiver cannot give us (the reference
/// sender gets it from where in its lifecycle it probes). It is true when our
/// own media is still flowing and the receiver never ended this session; see
/// [`Tracker::we_hold_session`] for where that evidence comes from. The
/// three-way shape is Q31's prescription: the session bit alone cannot
/// distinguish our own session from another sender's.
pub fn classify(status_flags: u64, we_hold_session: bool) -> ProbeOutcome {
    if status_flags & RECEIVER_SESSION_ACTIVE == 0 {
        ProbeOutcome::Free
    } else if we_hold_session {
        ProbeOutcome::OursActive
    } else {
        ProbeOutcome::BusyElsewhere
    }
}

fn status_flags(info: &Value) -> Option<u64> {
    info.as_dictionary()?.get("statusFlags")?.as_unsigned_integer()
}

/// Parse one `/info` body. Kept separate from the socket so the classification
/// table is unit-testable without a receiver.
fn report_from_info(info: &Value, we_hold_session: bool) -> ProbeReport {
    match status_flags(info) {
        Some(flags) => ProbeReport {
            outcome: classify(flags, we_hold_session),
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
    /// Not preemption: the receiver's active session is ours. Take no
    /// preemption action, so the control symptom falls through to the existing
    /// non-preemption path and a healthy session is never interrupted.
    FallThrough,
}

/// The Q30 verdict table, for a probe that completed.
pub fn action_for(outcome: ProbeOutcome) -> ProbeAction {
    match outcome {
        ProbeOutcome::Free | ProbeOutcome::Unknown => ProbeAction::Reclaim,
        // Never a reclaim: the receiver is still serving our own session.
        ProbeOutcome::OursActive => ProbeAction::FallThrough,
        ProbeOutcome::BusyElsewhere => ProbeAction::Requeue,
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

fn probe_infos(
    addr: SocketAddr,
    cancel: &Cancellation,
    timeout: Duration,
    we_hold_session: bool,
) -> Result<ProbeReport> {
    let mut connection =
        Connection::connect(addr, cancel.clone()).with_context(|| format!("status probe {addr}"))?;
    connection.set_timeout(timeout)?;
    let message = connection
        .request("GET", "/info", &[], &[])
        .context("status probe GET /info")?;
    let info = message.plist().context("status probe /info plist")?;
    Ok(report_from_info(&info, we_hold_session))
}

/// Probe the receiver's live status on a fresh RTSP connection.
///
/// `we_hold_session` is the locally derived context [`classify`] needs: our own
/// media is still flowing and the receiver has not ended this session (see
/// [`Tracker::we_hold_session`]). It is an input, never something the probe
/// invents.
///
/// The caller's existing control connection is deliberately not used: after a
/// takeover it belongs to a session the receiver already ended, and encrypted
/// RTSP cannot be re-keyed mid-session (Q16). Any TCP/RTSP/plist error or read
/// timeout is returned as `Err`; the policy layer turns that into a reclaim.
pub fn probe(
    addr: SocketAddr,
    cancel: &Cancellation,
    timeout: Duration,
    we_hold_session: bool,
) -> Result<ProbeOutcome> {
    Ok(probe_infos(addr, cancel, timeout, we_hold_session)?.outcome)
}

/// Same as [`probe`], but keeps the raw `statusFlags` for the diagnostic log.
pub fn probe_report(
    addr: SocketAddr,
    cancel: &Cancellation,
    timeout: Duration,
    we_hold_session: bool,
) -> Result<ProbeReport> {
    probe_infos(addr, cancel, timeout, we_hold_session)
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
    /// The receiver ended or refused our own session (a `TEARDOWN` it did not
    /// get from us, or a `403`). Sticky for this member: the member is dropped
    /// and rebuilt on every reclaim, so a fresh session starts with a clean
    /// tracker while a displaced one can never claim ownership again.
    session_ended: bool,
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
        self.session_ended |= signal.ends_session();
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

    /// Whether the receiver's active session is ours to lose — the
    /// `we_hold_session` dimension [`classify`] needs.
    ///
    /// Derived from evidence this engine already has, never from a new probe:
    /// - `rtp_healthy` is `MediaTransport::media_healthy`: we successfully moved
    ///   audio to this receiver within the health window. It is the same piece of
    ///   context the reference implementation logs next to a control EOF, and it
    ///   is why an active session must not be treated as a takeover by default:
    ///   our own healthy session has exactly that bit set.
    /// - the receiver never ended this session ([`Self::session_ended`]): a UDP
    ///   send keeps "succeeding" after a takeover, so RTP health alone stays
    ///   true and would hide a real takeover behind `ours_active` forever.
    ///
    /// Media health is always computable inside the live loop (the tracker is
    /// only polled from the streaming loop, after the media watchdog is armed),
    /// so there is no "unavailable" case to fall back from. If it ever were
    /// unknown, `false` is the conservative value: a false only turns a
    /// session-active probe into [`ProbeOutcome::BusyElsewhere`] — the requeue
    /// branch, which neither reclaims nor ends the session — while `true` would
    /// declare a session ours with no evidence at all.
    pub fn we_hold_session(&self, rtp_healthy: bool) -> bool {
        rtp_healthy && !self.session_ended
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
    fn verdict_table_matches_q30_and_never_reclaims_our_own_session() {
        assert_eq!(action_for(ProbeOutcome::Free), ProbeAction::Reclaim);
        assert_eq!(
            action_for(ProbeOutcome::BusyElsewhere),
            ProbeAction::Requeue
        );
        // A probe that completed but could not be classified is fail-open.
        assert_eq!(action_for(ProbeOutcome::Unknown), ProbeAction::Reclaim);
        // The receiver still serving us is not a preemption verdict.
        assert_eq!(
            action_for(ProbeOutcome::OursActive),
            ProbeAction::FallThrough
        );
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
        assert_eq!(
            action_for_result(&Ok(report(ProbeOutcome::OursActive))),
            ProbeAction::FallThrough
        );
    }

    #[test]
    fn recovered_bit_values_match_the_reference_implementation() {
        // Q31: ReceiverStatusFlags / ReceiverAccessPolicy, a 64-bit `ulong`.
        assert_eq!(RECEIVER_SESSION_ACTIVE, 131_072u64);
        assert_eq!(STEREO_PARTNER_UNREACHABLE, 16_384u64);
        assert_eq!(PASSWORD_REQUIRED, 128u64);
        assert_eq!(RECEIVER_SESSION_ACTIVE, 1 << 17);
        assert_eq!(STEREO_PARTNER_UNREACHABLE, 1 << 14);
        assert_eq!(PASSWORD_REQUIRED, 1 << 7);
        // The telemetry bits must never overlap the decision bit.
        assert_eq!(
            RECEIVER_SESSION_ACTIVE & (STEREO_PARTNER_UNREACHABLE | PASSWORD_REQUIRED),
            0
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
    fn no_session_is_free_even_when_other_bits_are_set() {
        // 0x20000 clear => nobody is being served, whatever else the receiver
        // reports and whoever we think we are.
        for flags in [
            0u64,
            1,
            1 << 7,
            1 << 14,
            (1 << 7) | (1 << 14),
            0x0000_0100,
            0x8000_0000,
            0x0000_1000_0000,
            u64::from(u32::MAX) & !RECEIVER_SESSION_ACTIVE,
        ] {
            for we_hold_session in [false, true] {
                assert_eq!(
                    classify(flags, we_hold_session),
                    ProbeOutcome::Free,
                    "statusFlags=0x{flags:016x} has no active session"
                );
                assert_eq!(
                    action_for(classify(flags, we_hold_session)),
                    ProbeAction::Reclaim
                );
            }
        }
    }

    #[test]
    fn an_active_session_we_hold_is_ours_and_never_a_reclaim() {
        // The scenario Q31 warns about: our own healthy session sets the same
        // bit the reference sender's reclaim path reads as "another sender".
        for flags in [
            RECEIVER_SESSION_ACTIVE,
            RECEIVER_SESSION_ACTIVE | STEREO_PARTNER_UNREACHABLE,
            RECEIVER_SESSION_ACTIVE | PASSWORD_REQUIRED,
            RECEIVER_SESSION_ACTIVE | STEREO_PARTNER_UNREACHABLE | PASSWORD_REQUIRED,
            0x0001_0002_0000,
        ] {
            assert_eq!(
                classify(flags, true),
                ProbeOutcome::OursActive,
                "statusFlags=0x{flags:016x} is our own session"
            );
            assert_ne!(
                action_for(classify(flags, true)),
                ProbeAction::Reclaim,
                "our own active session must never be reclaimed"
            );
        }
    }

    #[test]
    fn an_active_session_we_do_not_hold_is_busy_elsewhere() {
        for flags in [
            RECEIVER_SESSION_ACTIVE,
            RECEIVER_SESSION_ACTIVE | STEREO_PARTNER_UNREACHABLE,
            RECEIVER_SESSION_ACTIVE | PASSWORD_REQUIRED,
            0x0001_0002_0000,
        ] {
            assert_eq!(
                classify(flags, false),
                ProbeOutcome::BusyElsewhere,
                "statusFlags=0x{flags:016x} is somebody else's session"
            );
            assert_eq!(
                action_for(classify(flags, false)),
                ProbeAction::Requeue,
                "another sender is a queue, never a reclaim"
            );
        }
    }

    #[test]
    fn stereo_and_password_bits_do_not_influence_the_verdict() {
        let telemetry_bits = [
            0u64,
            STEREO_PARTNER_UNREACHABLE,
            PASSWORD_REQUIRED,
            STEREO_PARTNER_UNREACHABLE | PASSWORD_REQUIRED,
        ];
        for telemetry in telemetry_bits {
            for session in [0u64, RECEIVER_SESSION_ACTIVE] {
                for we_hold_session in [false, true] {
                    assert_eq!(
                        classify(session | telemetry, we_hold_session),
                        classify(session, we_hold_session),
                        "statusFlags=0x{:016x} must not change the verdict",
                        session | telemetry
                    );
                }
            }
        }
    }

    #[test]
    fn status_flags_above_u32_max_are_classified_on_the_full_64_bits() {
        // The old mask was 32-bit; a value with a high bit plus bit 17 must
        // still be recognised as an active session.
        for flags in [1u64 << 32, 0xFFFF_FFFF_0000_0000, 0x1_0000_0000] {
            assert_eq!(classify(flags, false), ProbeOutcome::Free);
        }
        for flags in [
            (1u64 << 32) | RECEIVER_SESSION_ACTIVE,
            0xFFFF_FFFF_0002_0000,
            0x8000_0000_0002_0000,
        ] {
            assert_eq!(classify(flags, true), ProbeOutcome::OursActive);
            assert_eq!(classify(flags, false), ProbeOutcome::BusyElsewhere);
        }
    }

    #[test]
    fn missing_or_malformed_status_flags_is_unknown_and_reclaims() {
        let mut info = plist::Dictionary::new();
        for we_hold_session in [false, true] {
            let report = report_from_info(&Value::Dictionary(info.clone()), we_hold_session);
            assert_eq!(
                report,
                ProbeReport {
                    outcome: ProbeOutcome::Unknown,
                    status_flags: None
                },
                "a missing field is unclassifiable, never a guess"
            );
        }
        info.insert("statusFlags".into(), Value::String("not a number".into()));
        let report = report_from_info(&Value::Dictionary(info.clone()), true);
        assert_eq!(report.outcome, ProbeOutcome::Unknown);
        assert_eq!(action_for(report.outcome), ProbeAction::Reclaim);
        // A negative integer is not a valid unsigned statusFlags either.
        info.insert("statusFlags".into(), Value::Integer((-1i64).into()));
        assert_eq!(
            report_from_info(&Value::Dictionary(info), true).outcome,
            ProbeOutcome::Unknown
        );
    }

    #[test]
    fn raw_status_flags_survive_parsing_for_the_device_mapping() {
        let mut info = plist::Dictionary::new();
        info.insert("statusFlags".into(), Value::Integer(0x1234_5678.into()));
        assert_eq!(
            report_from_info(&Value::Dictionary(info), false),
            ProbeReport {
                outcome: ProbeOutcome::Free,
                status_flags: Some(0x1234_5678)
            }
        );
        // The field is a 64-bit `ulong`: a high bit is preserved in the log and
        // must not turn a well-formed probe into `Unknown`.
        let mut wide = plist::Dictionary::new();
        wide.insert(
            "statusFlags".into(),
            Value::Integer(0x1_0000_0002u64.into()),
        );
        let report = report_from_info(&Value::Dictionary(wide), false);
        assert_eq!(report.outcome, ProbeOutcome::Free);
        assert_eq!(report.status_flags, Some(0x1_0000_0002));
        let mut active = plist::Dictionary::new();
        active.insert(
            "statusFlags".into(),
            Value::Integer(0x1_0002_0000u64.into()),
        );
        let report = report_from_info(&Value::Dictionary(active), false);
        assert_eq!(report.outcome, ProbeOutcome::BusyElsewhere);
        assert_eq!(report.status_flags, Some(0x1_0002_0000));
    }

    #[test]
    fn a_session_the_receiver_ended_is_never_ours_again() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        assert!(tracker.we_hold_session(true));
        assert!(!tracker.we_hold_session(false));
        // A control-channel close can be a keep-alive restart: it is evidence,
        // not proof that the session changed hands.
        tracker.observe(Signal::ConnectionClose, at(base, 0));
        assert!(
            tracker.we_hold_session(true),
            "a Connection: close does not end the session by itself"
        );
        tracker.observe(Signal::UnexpectedTeardown, at(base, 1));
        assert!(
            !tracker.we_hold_session(true),
            "after the receiver's own TEARDOWN our RTP cannot own bit 17"
        );
        // Sticky: a later healthy sample cannot undo it for this member.
        assert!(!tracker.we_hold_session(true));
    }

    #[test]
    fn a_forbidden_response_means_we_no_longer_hold_the_session() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::Forbidden, at(base, 0));
        assert!(!tracker.we_hold_session(true));
        assert!(!tracker.we_hold_session(false));
    }

    #[test]
    fn only_a_busy_probe_is_requeued() {
        let base = Instant::now();
        let mut ours = Tracker::default();
        ours.observe(Signal::ConnectionClose, at(base, 0));
        assert!(ours.note_result(&Ok(report(ProbeOutcome::OursActive)), at(base, 0)));
        assert_eq!(
            ours.scheduled(),
            None,
            "our own session must not start a re-probe queue"
        );
        let mut busy = Tracker::default();
        busy.observe(Signal::ConnectionClose, at(base, 0));
        assert!(busy.note_result(&Ok(report(ProbeOutcome::BusyElsewhere)), at(base, 0)));
        assert_eq!(busy.scheduled(), Some(at(base, 0) + REPROBE_DELAY));
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
