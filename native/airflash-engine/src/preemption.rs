//! Receiver-takeover policy: a live `statusFlags` probe decides, control-plane
//! symptoms only leave evidence.
//!
//! The reference sender does not infer a takeover from what the control channel
//! does. An unexpected `TEARDOWN` or a `Connection: close` is logged with
//! context (`policy`, `rtp_healthy`, `packets_prepared`) and then the receiver is
//! asked directly: `GET /info` and read `statusFlags`. That live answer is the
//! only criterion:
//!
//! | probe result | action |
//! | --- | --- |
//! | no active session | confirmation probe, then reclaim if it agrees |
//! | an active session that is ours | not preemption: fall through |
//! | an active session that is not ours | confirmation probe, then queue |
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
//! Ownership is **derived live**, never latched (Q32): healthy RTP always wins
//! over a control-plane signal, and an "our session ended" conclusion is trusted
//! for [`ENDED_TRUST_WINDOW`] only. `403` is an access/authentication error in
//! the reference classification, never takeover evidence: the feedback worker
//! routes it to the existing authentication fault path, so it cannot reach this
//! module at all. That closes the deadlock where a `403` latched "not ours",
//! every probe reported our own session as somebody else's, and the queue never
//! ended.
//!
//! Two more guards make every state reachable from a probe finite:
//! - a state-changing verdict (`Free` or `BusyElsewhere`) is re-probed once
//!   after [`CONFIRM_DELAY`]; a disagreement keeps the non-destructive outcome
//!   (never reclaim);
//! - consecutive confirmed `BusyElsewhere` requeues are bounded by
//!   [`BUSY_LIMIT`]; at the limit the sender reclaims anyway and logs
//!   `busy_exhausted`.
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

/// How long the "the receiver ended our session" conclusion is trusted while
/// RTP is unhealthy. Q32 requires the conclusion to be self-healing; an
/// indefinitely latched `false` turns a momentary unhealthy RTP sample into a
/// permanent `BusyElsewhere` loop. 2 s is the reference implementation's media
/// gap window, the same window [`Tracker::we_hold_session`] uses for
/// `rtp_healthy`.
pub const ENDED_TRUST_WINDOW: Duration = Duration::from_secs(2);

/// Delay between a state-changing verdict and the one confirmation probe Q32
/// asks for. Our choice: Q32 recovered the need for a second probe and its two
/// failure modes (a false `Free` reclaims; a false `Busy` stalls), but not
/// WAC's interval.
pub const CONFIRM_DELAY: Duration = Duration::from_millis(500);

/// Consecutive confirmed `BusyElsewhere` requeues after which the sender stops
/// queueing and reclaims anyway (`busy_exhausted`). Our choice: Q32 asks for an
/// upper bound and leaves the number to us. 3 mirrors [`TAKEOVER_LIMIT`] and,
/// at [`REPROBE_DELAY`] = 3 s, bounds one session attempt to roughly 9 s of
/// queueing before the reclaim path takes over. The takeover counter still
/// applies on top, so even repeated forced reclaims end in `yield`.
pub const BUSY_LIMIT: u32 = 3;

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
///
/// A `403` is deliberately absent: Q32 classifies it as an access/auth error
/// (the same family as password/PIN/470), never as "our session ended". The
/// feedback worker routes it to the existing `authentication_failed` path, so
/// it does not become preemption evidence either.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Signal {
    /// The receiver sent a `TEARDOWN` we did not ask for.
    UnexpectedTeardown,
    /// The receiver asked for the control connection to be closed.
    ConnectionClose,
}

impl Signal {
    pub fn name(self) -> &'static str {
        match self {
            Self::UnexpectedTeardown => "unexpected_teardown",
            Self::ConnectionClose => "connection_close",
        }
    }

    /// Whether this signal genuinely means the receiver ended **our** session.
    ///
    /// Only an unexpected `TEARDOWN` does. `Connection: close` can be a
    /// keep-alive or event-channel restart while the RTSP session is still
    /// ours. There is no `403` arm at all (Q32): it is an access/auth error and
    /// treating it as "session ended" is what latched the old tracker into
    /// `BusyElsewhere` forever.
    pub fn ends_session(self) -> bool {
        matches!(self, Self::UnexpectedTeardown)
    }
}

/// A signal the receiver sent us. `None` means the message was a response to a
/// request this sender made — including the reply to our own TEARDOWN, which is
/// part of every normal session ending and must never look like a takeover.
pub fn incoming_teardown(method: Option<&str>) -> Option<Signal> {
    (method == Some("TEARDOWN")).then_some(Signal::UnexpectedTeardown)
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
/// own media is still flowing and the receiver's "session ended" conclusion (if
/// any) is still fresh; see [`we_hold_session`] for the exact derivation. The
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

/// What to do after the verdict table — and, for state-changing verdicts, its
/// confirmation probe — has been applied.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProbeAction {
    /// Stop the old session and take the receiver back on a fresh connection.
    Reclaim,
    /// The bounded busy queue was exhausted: reclaim even though the receiver
    /// still reports an active session (`busy_exhausted`). A fail-open exit, so
    /// a permanently busy (or permanently misreported) receiver can never keep
    /// the sender queueing forever.
    ReclaimBusyExhausted,
    /// Leave the receiver alone for now and ask again after [`REPROBE_DELAY`].
    Requeue,
    /// Not preemption: the receiver's active session is ours. Take no
    /// preemption action, so the control symptom falls through to the existing
    /// non-preemption path and a healthy session is never interrupted.
    FallThrough,
    /// The first verdict would change state and is waiting for its one
    /// confirmation probe after [`CONFIRM_DELAY`]. Nothing has been decided.
    Confirm,
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

/// Adjudicate a state-changing first verdict against its confirmation probe.
///
/// `second` is `None` when the confirmation probe failed; the first verdict
/// then stands (fail-open on the reclaim path, the queue on the busy path).
/// When the two probes disagree the **non-destructive** interpretation wins and
/// a reclaim is never issued: `OursActive` falls through, `BusyElsewhere`
/// keeps queueing. `Unknown` is not a verdict, so it never counts as a
/// disagreement; the first verdict stands unchanged.
fn confirm(
    first: ProbeOutcome,
    second: Option<ProbeOutcome>,
) -> (ProbeAction, Option<(ProbeOutcome, ProbeOutcome)>) {
    let Some(second) = second else {
        return (action_for(first), None);
    };
    if second == ProbeOutcome::Unknown {
        return (action_for(first), None);
    }
    if second == first {
        return (action_for(first), None);
    }
    let action = if first == ProbeOutcome::OursActive || second == ProbeOutcome::OursActive {
        ProbeAction::FallThrough
    } else if first == ProbeOutcome::BusyElsewhere || second == ProbeOutcome::BusyElsewhere {
        ProbeAction::Requeue
    } else {
        // Both usable verdicts are in the Free family; nothing destructive
        // contradicted the first one.
        action_for(first)
    };
    (action, Some((first, second)))
}

/// Whether the receiver's active session is ours, derived **live** from the
/// evidence this engine already has (Q32):
///
/// - `rtp_healthy` is `MediaTransport::media_healthy`: we successfully moved
///   audio to this receiver within the health window. It always wins — even
///   over a receiver `TEARDOWN` — because that is the reference
///   implementation's "control poison with healthy RTP ⇒ deferring teardown"
///   rule, and because a UDP send keeps "succeeding" long enough that a stale
///   control conclusion must not override live media.
/// - `ended_at` is when the receiver last ended this session (unexpected
///   `TEARDOWN`). That conclusion is honored for [`ENDED_TRUST_WINDOW`] only:
///   after the window the tracker holds the session again, so the evidence is
///   self-healing instead of latching `false` forever (Q32). A `403` is never
///   written here at all.
///
/// `now` is explicit so the window boundary is unit-testable.
///
/// Q32's sketch writes the window as `t.elapsed() < 2s`. Read literally that
/// returns `false` forever once the window expires, which is exactly the latch
/// the same answer forbids; the invariant Q32 states is self-healing, so the
/// *ended* conclusion is trusted for [`ENDED_TRUST_WINDOW`] and then expires
/// (`elapsed >= window` ⇒ we hold again).
pub fn we_hold_session(rtp_healthy: bool, ended_at: Option<Instant>, now: Instant) -> bool {
    if rtp_healthy {
        return true;
    }
    match ended_at {
        // The "receiver ended our session" conclusion expires: once it is older
        // than the trust window it is no longer evidence against ownership. This
        // is the self-healing half of Q32 — a recent control symptom may make us
        // queue, but it can never wedge the probe in `BusyElsewhere` forever.
        Some(ended) => now.saturating_duration_since(ended) >= ENDED_TRUST_WINDOW,
        None => true,
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
/// media is still flowing and the receiver's ended conclusion (if any) is fresh
/// (see [`we_hold_session`]). It is an input, never something the probe invents.
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

/// One state-changing verdict waiting for its confirmation probe.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct PendingProbe {
    first: ProbeOutcome,
}

/// What one probe result means after the confirmation rule and the bounded busy
/// queue have been applied.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ProbeNote {
    /// True when the diagnostic log line changed (same rule as before): a
    /// repeated busy verdict does not print the same line every interval.
    pub log_changed: bool,
    /// The action to take now. [`ProbeAction::Confirm`] means the confirmation
    /// probe is scheduled and nothing has been decided yet.
    pub action: ProbeAction,
    /// Set when the first and confirmation probes gave different usable
    /// verdicts. The caller logs the disagreement.
    pub disagreement: Option<(ProbeOutcome, ProbeOutcome)>,
    /// The busy queue hit [`BUSY_LIMIT`] and the verdict was forced to reclaim.
    pub busy_exhausted: bool,
}

/// Per-member evidence and probe scheduling. Only the session thread probes;
/// feedback workers merely append [`Signal`]s to `Health`.
#[derive(Debug, Default)]
pub struct Tracker {
    signals: Vec<Signal>,
    next_probe: Option<Instant>,
    last_report: Option<ProbeLog>,
    /// When the receiver last ended our own session (an unexpected `TEARDOWN`).
    /// Deliberately **not** sticky: [`we_hold_session`] trusts it for
    /// [`ENDED_TRUST_WINDOW`] only. A `403` is never recorded here (Q32).
    ended_at: Option<Instant>,
    /// First state-changing verdict waiting for its confirmation probe.
    pending: Option<PendingProbe>,
    /// Confirmed `BusyElsewhere` requeues in a row for this session attempt.
    busy_requeues: u32,
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
        if signal.ends_session() {
            self.ended_at = Some(at);
        }
        if self.signals.len() == MAX_SIGNALS {
            self.signals.remove(0);
        }
        self.signals.push(signal);
        // A new signal asks for a probe now, unless a probe is already queued.
        // Repeated symptoms must not shorten the confirmation or re-probe
        // interval.
        if self.next_probe.is_none() {
            self.next_probe = Some(at);
        }
    }

    /// Whether the receiver's active session is ours to lose — the
    /// `we_hold_session` dimension [`classify`] needs. See the free
    /// [`we_hold_session`] for the exact derivation; this only supplies the
    /// tracker's recorded `ended_at`.
    pub fn we_hold_session(&self, rtp_healthy: bool, now: Instant) -> bool {
        we_hold_session(rtp_healthy, self.ended_at, now)
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

    /// Feed one probe result back and apply the confirmation rule plus the
    /// bounded busy queue.
    ///
    /// A `Free` or `BusyElsewhere` first verdict is not acted on: it schedules
    /// one confirmation probe after [`CONFIRM_DELAY`]. `OursActive` and the
    /// fail-open paths (`Unknown`, error, timeout) are acted on immediately.
    /// A confirmed `BusyElsewhere` queues another probe after [`REPROBE_DELAY`]
    /// until [`BUSY_LIMIT`], then turns into [`ProbeAction::ReclaimBusyExhausted`].
    pub fn note_result(&mut self, result: &Result<ProbeReport>, now: Instant) -> ProbeNote {
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
        let log_changed = self.last_report != Some(log);
        self.last_report = Some(log);

        if let Some(pending) = self.pending.take() {
            let second = result.as_ref().ok().map(|report| report.outcome);
            let (action, disagreement) = confirm(pending.first, second);
            return self.settle(action, disagreement, log_changed, now);
        }

        match result {
            Ok(report) if report.outcome == ProbeOutcome::Free => {
                self.schedule_confirmation(ProbeOutcome::Free, log_changed, now)
            }
            Ok(report) if report.outcome == ProbeOutcome::BusyElsewhere => {
                self.schedule_confirmation(ProbeOutcome::BusyElsewhere, log_changed, now)
            }
            // `OursActive` changes nothing; `Unknown`/error are fail-open and
            // reclaim immediately. Neither waits for a confirmation.
            _ => {
                let action = action_for_result(result);
                self.settle(action, None, log_changed, now)
            }
        }
    }

    fn schedule_confirmation(
        &mut self,
        first: ProbeOutcome,
        log_changed: bool,
        now: Instant,
    ) -> ProbeNote {
        let due = now + CONFIRM_DELAY;
        self.pending = Some(PendingProbe { first });
        self.next_probe = Some(due);
        ProbeNote {
            log_changed,
            action: ProbeAction::Confirm,
            disagreement: None,
            busy_exhausted: false,
        }
    }

    fn settle(
        &mut self,
        action: ProbeAction,
        disagreement: Option<(ProbeOutcome, ProbeOutcome)>,
        log_changed: bool,
        now: Instant,
    ) -> ProbeNote {
        let (action, busy_exhausted) = match action {
            ProbeAction::Requeue => {
                self.busy_requeues = self.busy_requeues.saturating_add(1);
                if self.busy_requeues >= BUSY_LIMIT {
                    // The queue must have an exit even if the receiver keeps
                    // answering "somebody else" forever (Q32).
                    self.next_probe = None;
                    (ProbeAction::ReclaimBusyExhausted, true)
                } else {
                    self.next_probe = Some(now + REPROBE_DELAY);
                    (ProbeAction::Requeue, false)
                }
            }
            ProbeAction::Reclaim | ProbeAction::ReclaimBusyExhausted | ProbeAction::FallThrough => {
                // Any non-busy verdict breaks the consecutive run.
                self.busy_requeues = 0;
                self.next_probe = None;
                (action, false)
            }
            ProbeAction::Confirm => unreachable!("a confirmation is scheduled, not settled"),
        };
        ProbeNote {
            log_changed,
            action,
            disagreement,
            busy_exhausted,
        }
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

    fn busy() -> Result<ProbeReport> {
        Ok(report(ProbeOutcome::BusyElsewhere))
    }

    fn free() -> Result<ProbeReport> {
        Ok(report(ProbeOutcome::Free))
    }

    /// Run one complete confirmed `BusyElsewhere` cycle and return the note of
    /// the confirmation probe.
    fn confirm_busy(tracker: &mut Tracker, now: Instant) -> (ProbeNote, Instant) {
        let first = tracker.note_result(&busy(), now);
        assert_eq!(
            first.action,
            ProbeAction::Confirm,
            "the first busy verdict must be confirmed before anything is queued"
        );
        let confirmation = now + CONFIRM_DELAY;
        let second = tracker.note_result(&busy(), confirmation);
        (second, confirmation)
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

        // Only the receiver's own TEARDOWN ends our session. `Connection:
        // close` can be a keep-alive/event-channel restart; 403 is not even a
        // signal any more (it is an access/auth error, Q32).
        assert!(Signal::UnexpectedTeardown.ends_session());
        assert!(!Signal::ConnectionClose.ends_session());
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
    fn healthy_rtp_always_wins_over_an_ended_control_state() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::UnexpectedTeardown, at(base, 0));
        // The receiver says it ended the session and RTP is not healthy: trust
        // the conclusion inside the window.
        assert!(!tracker.we_hold_session(false, at(base, 0)));
        // RTP is still flowing: that is stronger than any control signal, even
        // the receiver's own TEARDOWN (WAC: control poison + healthy RTP =>
        // deferring teardown).
        for elapsed in [0, 1, 1_999, 60_000] {
            assert!(
                tracker.we_hold_session(true, at(base, elapsed)),
                "healthy RTP must never be overridden by a control signal"
            );
        }
    }

    #[test]
    fn a_403_cannot_wedge_the_probe() {
        // The old deadlock: 403 latched `session_ended = true`, every probe saw
        // our own session bit and requeued, forever. A 403 is now an
        // access/auth fault on the feedback channel and never reaches this
        // tracker. What remains is the self-healing ended conclusion, which
        // cannot wedge even if a control signal does mark the session ended.
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::UnexpectedTeardown, at(base, 0));
        assert!(!tracker.we_hold_session(false, at(base, 0)));
        assert!(
            !tracker.we_hold_session(false, at(base, 1_999)),
            "the ended conclusion is trusted inside the window"
        );
        assert!(
            tracker.we_hold_session(false, at(base, 2_000)),
            "after ENDED_TRUST_WINDOW the tracker recovers by itself"
        );
        // And a healthy RTP sample recovers it immediately, without waiting.
        assert!(tracker.we_hold_session(true, at(base, 1)));
        // Repeated observations refresh the trust window, as fresh evidence.
        tracker.observe(Signal::UnexpectedTeardown, at(base, 5_000));
        assert!(!tracker.we_hold_session(false, at(base, 5_000)));
        assert!(tracker.we_hold_session(false, at(base, 7_000)));
    }

    #[test]
    fn a_connection_close_never_ends_the_session_by_itself() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::ConnectionClose, at(base, 0));
        assert!(tracker.we_hold_session(false, at(base, 0)));
        assert!(tracker.we_hold_session(true, at(base, 0)));
    }

    #[test]
    fn a_state_changing_verdict_is_confirmed_before_it_is_acted_on() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::ConnectionClose, at(base, 0));

        let first = tracker.note_result(&free(), at(base, 0));
        assert_eq!(
            first.action,
            ProbeAction::Confirm,
            "a lone Free must not reclaim: the receiver may be handing us back"
        );
        assert!(!first.busy_exhausted);
        assert_eq!(first.disagreement, None);
        assert_eq!(
            tracker.scheduled(),
            Some(at(base, 0) + CONFIRM_DELAY),
            "exactly one confirmation probe, CONFIRM_DELAY later"
        );
        assert!(!tracker.due(
            at(base, 0) + CONFIRM_DELAY - Duration::from_millis(1)
        ));

        let second = tracker.note_result(&free(), at(base, 0) + CONFIRM_DELAY);
        assert_eq!(
            second.action,
            ProbeAction::Reclaim,
            "two agreeing Free probes reclaim"
        );
        assert_eq!(tracker.scheduled(), None);
    }

    #[test]
    fn ours_active_and_fail_open_verdicts_are_not_delayed_by_a_confirmation() {
        for result in [
            Ok(report(ProbeOutcome::OursActive)),
            Ok(report(ProbeOutcome::Unknown)),
            Err(anyhow!("probe timed out")),
        ] {
            let mut tracker = Tracker::default();
            let note = tracker.note_result(&result, Instant::now());
            assert_eq!(
                note.action,
                action_for_result(&result),
                "OursActive falls through and fail-open reclaims without a wait"
            );
            assert_eq!(tracker.scheduled(), None);
        }
    }

    #[test]
    fn a_single_free_busy_disagreement_does_not_reclaim() {
        let base = Instant::now();
        // Free first, BusyElsewhere on the confirmation probe.
        let mut tracker = Tracker::default();
        assert_eq!(
            tracker.note_result(&free(), at(base, 0)).action,
            ProbeAction::Confirm
        );
        let note = tracker.note_result(
            &busy(),
            at(base, 0) + CONFIRM_DELAY,
        );
        assert_eq!(
            note.action,
            ProbeAction::Requeue,
            "a disagreement must not reclaim: prefer the non-destructive outcome"
        );
        assert_eq!(
            note.disagreement,
            Some((ProbeOutcome::Free, ProbeOutcome::BusyElsewhere))
        );
        assert_eq!(
            tracker.scheduled(),
            Some(at(base, 0) + CONFIRM_DELAY + REPROBE_DELAY),
            "the queue wins a Free/Busy disagreement"
        );

        // BusyElsewhere first, Free on the confirmation probe.
        let mut tracker = Tracker::default();
        assert_eq!(
            tracker.note_result(&busy(), at(base, 0)).action,
            ProbeAction::Confirm
        );
        let note = tracker.note_result(&free(), at(base, 0) + CONFIRM_DELAY);
        assert_eq!(note.action, ProbeAction::Requeue);
        assert_eq!(
            note.disagreement,
            Some((ProbeOutcome::BusyElsewhere, ProbeOutcome::Free))
        );

        // Either first verdict against OursActive falls through: our session is
        // active, nothing is reclaimed and nothing is queued.
        for first in [free(), busy()] {
            let mut tracker = Tracker::default();
            tracker.note_result(&first, at(base, 0));
            let note = tracker.note_result(
                &Ok(report(ProbeOutcome::OursActive)),
                at(base, 0) + CONFIRM_DELAY,
            );
            assert_eq!(note.action, ProbeAction::FallThrough);
            assert!(note.disagreement.is_some());
            assert_eq!(tracker.scheduled(), None);
        }
    }

    #[test]
    fn busy_elsewhere_never_loops_forever() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::ConnectionClose, at(base, 0));
        let mut now = at(base, 0);
        for attempt in 1..=BUSY_LIMIT {
            let (note, confirmation) = confirm_busy(&mut tracker, now);
            if attempt < BUSY_LIMIT {
                assert_eq!(
                    note.action,
                    ProbeAction::Requeue,
                    "confirmed busy {attempt} still queues"
                );
                assert!(!note.busy_exhausted);
                assert_eq!(
                    tracker.scheduled(),
                    Some(confirmation + REPROBE_DELAY),
                    "the queue never shortens the re-probe interval"
                );
                now = confirmation + REPROBE_DELAY;
            } else {
                assert_eq!(
                    note.action,
                    ProbeAction::ReclaimBusyExhausted,
                    "after BUSY_LIMIT confirmed busy verdicts the sender must escape"
                );
                assert!(note.busy_exhausted);
                assert_eq!(
                    tracker.scheduled(),
                    None,
                    "the forced reclaim must leave no queued probe behind"
                );
            }
        }
    }

    #[test]
    fn a_non_busy_verdict_resets_the_consecutive_busy_run() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        let (first, confirmation) = confirm_busy(&mut tracker, at(base, 0));
        assert_eq!(first.action, ProbeAction::Requeue);
        let (second, confirmation) = confirm_busy(&mut tracker, confirmation + REPROBE_DELAY);
        assert_eq!(second.action, ProbeAction::Requeue);
        // Our own session appears: no queue, and the run is broken.
        assert_eq!(
            tracker.note_result(&Ok(report(ProbeOutcome::OursActive)), confirmation).action,
            ProbeAction::FallThrough
        );
        // A fresh busy run starts from one again, so it cannot inherit a stale
        // near-limit count from minutes ago.
        let (next, _) = confirm_busy(&mut tracker, confirmation + Duration::from_secs(10));
        assert_eq!(next.action, ProbeAction::Requeue);
        assert!(!next.busy_exhausted);
    }

    #[test]
    fn busy_outcome_queues_the_next_probe_instead_of_failing() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::ConnectionClose, at(base, 0));
        assert!(tracker.due(at(base, 0)));
        let (note, confirmation) = confirm_busy(&mut tracker, at(base, 0));
        assert_eq!(note.action, ProbeAction::Requeue);
        assert_eq!(
            tracker.scheduled(),
            Some(confirmation + REPROBE_DELAY),
            "busy is a queue, not a retry storm"
        );
        assert!(!tracker.due(confirmation + REPROBE_DELAY - Duration::from_millis(1)));
        assert!(tracker.due(confirmation + REPROBE_DELAY));
        // A repeated busy verdict does not change the log line, so it stays
        // quiet, but it is still confirmed before it is queued.
        let repeated = tracker.note_result(&busy(), confirmation + REPROBE_DELAY);
        assert!(!repeated.log_changed);
        assert_eq!(
            repeated.action,
            ProbeAction::Confirm,
            "each verdict is confirmed"
        );
        // Fresh evidence while queued must not cut the interval short.
        tracker.observe(
            Signal::ConnectionClose,
            confirmation + REPROBE_DELAY + Duration::from_millis(500),
        );
        assert_eq!(
            tracker.scheduled(),
            Some(confirmation + REPROBE_DELAY + CONFIRM_DELAY),
            "a signal while a confirmation is pending must not reschedule it"
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
            let note = tracker.note_result(&result, at(base, 100));
            if matches!(result, Ok(report) if report.outcome == ProbeOutcome::Free) {
                // Free is confirmed once; the second agreeing probe clears it.
                assert_eq!(note.action, ProbeAction::Confirm);
                assert_eq!(tracker.scheduled(), Some(at(base, 100) + CONFIRM_DELAY));
                let settled = tracker.note_result(&result, at(base, 100) + CONFIRM_DELAY);
                assert_eq!(settled.action, ProbeAction::Reclaim);
            } else {
                assert_eq!(note.action, ProbeAction::Reclaim);
            }
            assert_eq!(tracker.scheduled(), None);
            assert!(!tracker.due(at(base, 5_000)));
        }
    }

    #[test]
    fn probe_log_only_changes_on_a_different_result() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::ConnectionClose, at(base, 0));
        assert!(
            tracker.note_result(&free(), at(base, 0)).log_changed,
            "the first Free is logged"
        );
        assert!(
            !tracker
                .note_result(&free(), at(base, 0) + CONFIRM_DELAY)
                .log_changed,
            "an agreeing confirmation is not a new log line"
        );
        assert!(
            tracker
                .note_result(&busy(), at(base, 4_000))
                .log_changed,
            "a different outcome is logged"
        );
        // The busy count is irrelevant to the log dedup.
        assert!(
            !tracker
                .note_result(&busy(), at(base, 4_000) + CONFIRM_DELAY)
                .log_changed
        );
        assert!(
            tracker
                .note_result(&free(), at(base, 8_000))
                .log_changed,
            "a later Free after a busy run is logged"
        );
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
}
