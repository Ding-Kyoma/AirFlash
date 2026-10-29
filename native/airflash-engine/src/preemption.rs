//! Receiver-takeover policy: a live `statusFlags` probe and a receiver-side TCP
//! liveness probe decide, control-plane symptoms only leave evidence.
//!
//! The reference sender does not infer a takeover from what the control channel
//! does. An unexpected `TEARDOWN` or a `Connection: close` is logged with
//! context (`policy`, `rtp_healthy`, `packets_prepared`, `tcp_reachable`) and
//! then the receiver is asked directly: `GET /info` and read `statusFlags`. The
//! live answer is combined with a TCP connect probe of the control port (Q34,
//! corrected by Q42):
//!
//! | media healthy | liveness | verdict |
//! | --- | --- | --- |
//! | yes | Alive | `OursActive` — our session is still being served |
//! | yes | Refused | `Gone` — the host is alive but not serving us; reclaim |
//! | yes | Dead | `Gone` — the device is gone, reclaim, never `BusyElsewhere` |
//! | yes | Unknown | `OursActive` — conservative, a failed probe is not a death |
//! | no | any | consult `statusFlags` (see [`classify`]) |
//!
//! Q42 corrects two things about that dimension:
//!
//! - **`ConnectionRefused` does not mean the device is dead.** A returned RST
//!   proves the host is alive and its network stack answered; it simply is not
//!   serving the control port. Action-wise it is still a fail-open reclaim (no
//!   `statusFlags` can be read), but it stays a distinct state in the log, or
//!   "judged gone" and "refused the connection" become indistinguishable.
//! - **`Unknown` is "temporarily unknown", not "counts as mine forever".**
//!   [`LivenessTracker`] escalates a chain of non-definitive probes to `Dead`
//!   after [`LIVENESS_PATIENCE`] consecutive failures, instead of enumerating
//!   platform error codes.
//!
//! A group-wide cross-check closes the last ambiguous case: when **every**
//! receiver of a multi-receiver session fails its probe in the same round, the
//! common cause is our own side (routing table, interface, Wi-Fi), not several
//! devices dying at once, so no device is marked dead
//! ([`local_network_suspected`]).
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
//! Two guards make every state reachable from a probe finite:
//! - a state-changing verdict (`Free` or `BusyElsewhere`) is re-probed once
//!   after [`CONFIRM_DELAY`]; a disagreement keeps the non-destructive outcome
//!   (never reclaim);
//! - a confirmed `BusyElsewhere` is **queueing, not a takeover**: it backs off
//!   [`REPROBE_BACKOFF`] (3 s → 6 s → 12 s → 24 s → 60 s, capped at 60 s) and
//!   polls forever. A receiver that stays busy for an hour is polled once a
//!   minute; it is never reclaimed for being busy (Q34).
//!
//! Re-announcing is never enough on its own: a receiver that is serving another
//! sender still announces. The anti-storm limiter counts **reclaim → takeover
//! cycles** (Q34), not time spent queueing: a takeover only continues the run
//! when it follows a successful reclaim inside [`TAKEOVER_WINDOW`]; otherwise it
//! starts a new run at 1. At [`TAKEOVER_LIMIT`] the sender yields — a terminal
//! state that schedules no further probe and resumes only on an explicit
//! external trigger ([`Tracker::trigger`]) or a new playback run that finds the
//! receiver `Free`.
//!
//! Real-device values are still unverified: the raw `statusFlags` value is
//! always logged so it can be mapped from a receiver that owns the session.
use crate::rtsp::{Cancellation, Connection};
use anyhow::{Context, Result};
use plist::Value;
use std::{
    collections::HashMap,
    fmt,
    io::ErrorKind,
    net::{IpAddr, SocketAddr, TcpStream},
    time::{Duration, Instant},
};

/// How long a live status probe may take. Our choice: Q30 recovered neither
/// WAC's probe timeout nor its re-probe interval.
pub const PROBE_TIMEOUT: Duration = Duration::from_secs(2);

/// How long the receiver-side TCP connect probe may take. It is the same budget
/// as [`PROBE_TIMEOUT`]: both are one control-plane round trip to a LAN address.
pub const LIVENESS_TIMEOUT: Duration = Duration::from_secs(2);

/// Q42's escalation timer: how many **consecutive** non-definitive liveness
/// probes are tolerated before the receiver counts as gone. Three is our
/// choice — Q42 only fixed the shape ("`Unknown` must have a patience limit"),
/// not the number:
///
/// - one or two failures are exactly what a transient local hiccup looks like
///   (a firewall refresh, `WSAENOBUFS`, a routing-table blip), and the whole
///   point of not enumerating errno is to survive those;
/// - three independent control-port probes with no success in between is no
///   longer a hiccup, and with [`LIVENESS_RETRY`] it is reached in about 6 s —
///   far quicker than waiting for the media-health backstop, which never fires
///   while UDP sends keep "succeeding";
/// - Q42's own sketch escalates at `>= 3`.
pub const LIVENESS_PATIENCE: u32 = 3;

/// The wall-clock half of Q42's escalation timer: a receiver that has not been
/// proved alive for this long is treated as gone once a **second** consecutive
/// non-definitive probe confirms it is not a one-off. Our choice: 60 s matches
/// the longest busy-queue interval, so even a slow probe cadence cannot keep an
/// undecided receiver alive forever. A single `Unknown` never escalates on this
/// clause alone — one anomalous error after a long quiet period is precisely the
/// transient case the rule must not punish.
pub const LIVENESS_PATIENCE_WINDOW: Duration = Duration::from_secs(60);

/// How long to wait before re-sampling a receiver whose liveness probe was
/// non-definitive. Q42's timer needs repeated samples: without this, a single
/// `Unknown` would be the only sample the tracker ever sees (the next probe is
/// only scheduled by fresh control-plane evidence, and a dead feedback worker
/// produces no more), so the patience limit could never be reached. It is the
/// first busy-queue backoff step, and the loop is bounded — the third unknown
/// escalates to `Dead` and reclaims.
pub const LIVENESS_RETRY: Duration = Duration::from_secs(3);

/// The queueing backoff for a confirmed `BusyElsewhere` verdict: 3 s, 6 s,
/// 12 s, 24 s, then 60 s forever. This is a queue, never a failure retry: a
/// receiver that stays busy for an hour is polled once a minute and is never
/// reclaimed for being busy (Q34 §四). The growth factor and the cap are our
/// choice — WAC only exposed the shape of its takeover limiter, not its re-probe
/// interval (Q31 suggested 3–5 s for the first interval).
pub const REPROBE_BACKOFF: [Duration; 5] = [
    Duration::from_secs(3),
    Duration::from_secs(6),
    Duration::from_secs(12),
    Duration::from_secs(24),
    Duration::from_secs(60),
];

/// The backoff cap: after the fifth confirmed busy verdict the interval stays at
/// 60 s, forever.
pub const REPROBE_MAX_DELAY: Duration = Duration::from_secs(60);

/// The delay after `requeues` completed requeues. The first requeue waits 3 s,
/// the second 6 s, then 12 s, 24 s, and 60 s for every later one.
pub fn requeue_delay(requeues: u32) -> Duration {
    let index = (requeues as usize).min(REPROBE_BACKOFF.len() - 1);
    REPROBE_BACKOFF[index]
}

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

/// A takeover counter reaches this many consecutive reclaim → takeover cycles
/// and the sender yields. Our choice: Q30 only recovered the *shape* of the
/// limiter (`taken over <n> times in a row`), not WAC's threshold.
pub const TAKEOVER_LIMIT: u32 = 3;

/// A takeover counts as another cycle in the same run only when it happens at
/// most this long after a **successful reclaim**. Our choice: WAC's window was
/// not recovered either; Q34 calls it "WAC 的量级" and suggests 30 s.
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

/// The receiver-side liveness dimension Q34 recovered from the reference
/// sender: one TCP connect to the control port, not an RTP feedback path.
///
/// Q42 splits what used to be `Dead` into two states. A refused connect is a
/// *returned RST*: the host is up and answered, it just does not serve that
/// port. Only a timeout or an explicitly unreachable host/network means the
/// device is not there. The action is the same fail-open reclaim for both, but
/// the states must not be collapsed: the log has to distinguish "the device is
/// gone" from "the device refused the connection".
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ReceiverLiveness {
    /// `TcpStream::connect_timeout` succeeded: the receiver is alive.
    Alive,
    /// The connection was refused (a returned RST): the host is alive, its
    /// stack answered, but the control port is not serving. No `statusFlags`
    /// can be read, so this reclaims like `Dead` — it is simply never *called*
    /// dead (Q42).
    Refused,
    /// The connect timed out, or the host/network was reported unreachable: the
    /// device is not there.
    Dead,
    /// The probe failed in a way that proves neither life nor death (a firewall
    /// rule, a transient local socket error, IPv6 neighbour discovery, ...).
    /// Never treated as a death on its own; [`LivenessTracker`] escalates a
    /// chain of these instead.
    Unknown,
}

impl ReceiverLiveness {
    pub fn name(self) -> &'static str {
        match self {
            Self::Alive => "alive",
            Self::Refused => "refused",
            Self::Dead => "dead",
            Self::Unknown => "unknown",
        }
    }

    /// The boolean the reference sender exposes as `tcp_reachable=<b>`. Only a
    /// completed connect is `true`; `Unknown` did not prove reachability, and
    /// `Refused` proves the *host* answered but not that it can serve us.
    pub fn reachable(self) -> bool {
        matches!(self, Self::Alive)
    }

    /// Whether the probe proved the host's network stack answered us at all. A
    /// refused connect (RST) does; a timeout or an unreachable error does not.
    /// This is the "did anything answer?" dimension Q42's group-wide
    /// cross-check uses, and it is the reason `Refused` cannot be folded into
    /// `Dead`.
    pub fn host_answered(self) -> bool {
        matches!(self, Self::Alive | Self::Refused)
    }
}

/// Classify one failed control-port `connect` into the liveness dimension.
///
/// Q42 names exactly three "the host is not there" errors — timeout, host
/// unreachable, network unreachable — and treats a returned RST as proof of
/// life, not death. Everything else is [`ReceiverLiveness::Unknown`] and is
/// left to the escalation timer: enumerating more platform codes would only
/// move the next surprise to the next OS/version.
pub fn liveness_from_error(error: &std::io::Error) -> ReceiverLiveness {
    match error.kind() {
        ErrorKind::ConnectionRefused => ReceiverLiveness::Refused,
        ErrorKind::TimedOut | ErrorKind::HostUnreachable | ErrorKind::NetworkUnreachable => {
            ReceiverLiveness::Dead
        }
        _ => ReceiverLiveness::Unknown,
    }
}

/// One TCP connect probe against the receiver's control port.
///
/// This is the cheap "is the device still there?" dimension WAC uses after a
/// feedback timeout. It talks no protocol — a bare `connect` on a port we
/// already open RTSP on — and collapses to [`ReceiverLiveness`]: a returned RST
/// is `Refused` (the host is alive), a timeout or an unreachable error is
/// `Dead`, any other error is `Unknown` so an odd local networking error can
/// never fake a dead device on its own (Q42).
pub fn receiver_liveness(ip: IpAddr, port: u16, timeout: Duration) -> ReceiverLiveness {
    match TcpStream::connect_timeout(&SocketAddr::new(ip, port), timeout) {
        Ok(_) => ReceiverLiveness::Alive,
        Err(error) => liveness_from_error(&error),
    }
}

/// The liveness verdict after Q42's escalation rule has been applied. It is
/// what the combine table consumes; the raw [`ReceiverLiveness`] is kept for
/// the diagnostic line, so a `Refused` still reads `refused` there while its
/// action is the `Dead` one.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Verdict {
    Alive,
    Dead,
    Unknown,
}

impl Verdict {
    pub fn name(self) -> &'static str {
        match self {
            Self::Alive => "alive",
            Self::Dead => "dead",
            Self::Unknown => "unknown",
        }
    }

    /// The liveness value the combine table should use. `Refused` and `Dead`
    /// share the `Dead` action by design (Q42): both leave us unable to read
    /// `statusFlags`.
    pub fn liveness(self) -> ReceiverLiveness {
        match self {
            Self::Alive => ReceiverLiveness::Alive,
            Self::Dead => ReceiverLiveness::Dead,
            Self::Unknown => ReceiverLiveness::Unknown,
        }
    }
}

/// One receiver's liveness sample for a polling round.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LivenessSample {
    /// The control-port probe answered (or failed) on its own.
    Probe(ReceiverLiveness),
    /// Every receiver of the group failed in the same round, so the common cause
    /// is far more likely local than N devices dying at once (Q42). This is not
    /// evidence about this receiver: the verdict is `Unknown`, the escalation
    /// counter is neither advanced nor reset, and no re-probe is scheduled.
    LocalSuspect,
}

/// What one liveness sample settled on, plus whether the diagnostic line needs
/// to be printed again.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct LivenessNote {
    pub verdict: Verdict,
    /// True when the liveness half of the diagnostic line changed (raw state,
    /// verdict or consecutive-failure count).
    pub log_changed: bool,
}

/// One receiver's liveness history — Q42's escalation timer.
///
/// `Unknown` is "temporarily unknown", never "mine forever":
///
/// ```text
/// Ok                        -> consecutive_failures = 0, last_ok = now, Alive
/// Refused                   -> Dead   (host alive, not serving => no statusFlags)
/// Timeout | HostUnreachable
///   | NetworkUnreachable    -> Dead
/// anything else             -> Unknown, but consecutive_failures += 1;
///                              at the patience limit (or after the patience
///                              window from the second failure on) => Dead
/// ```
///
/// One `Alive` resets the counter and the clock. The tracker never guesses a
/// platform error code: it only counts "probes that proved nothing".
#[derive(Clone, Copy, Debug)]
pub struct LivenessTracker {
    consecutive_failures: u32,
    /// When the receiver was last proved alive. A tracker is created when the
    /// session is established and completing the RTSP handshake is itself a
    /// reachability proof, so a fresh tracker starts as if just confirmed.
    last_ok: Instant,
}

impl LivenessTracker {
    pub fn new(now: Instant) -> Self {
        Self {
            consecutive_failures: 0,
            last_ok: now,
        }
    }

    pub fn consecutive_failures(&self) -> u32 {
        self.consecutive_failures
    }

    pub fn last_ok(&self) -> Instant {
        self.last_ok
    }

    /// Apply Q42's rule to one probe. See the type docs for the table.
    pub fn observe(&mut self, liveness: ReceiverLiveness, now: Instant) -> Verdict {
        match liveness {
            ReceiverLiveness::Alive => {
                self.consecutive_failures = 0;
                self.last_ok = now;
                Verdict::Alive
            }
            // A returned RST proves the host is alive, but it cannot serve the
            // session any more than a timeout can: both are fail-open reclaims.
            // The counter is not reset — "not serving" is not a success.
            ReceiverLiveness::Refused | ReceiverLiveness::Dead => Verdict::Dead,
            ReceiverLiveness::Unknown => {
                self.consecutive_failures = self.consecutive_failures.saturating_add(1);
                let patient = self.consecutive_failures >= LIVENESS_PATIENCE;
                let stale = self.consecutive_failures >= 2
                    && now.saturating_duration_since(self.last_ok) >= LIVENESS_PATIENCE_WINDOW;
                if patient || stale {
                    Verdict::Dead
                } else {
                    Verdict::Unknown
                }
            }
        }
    }
}

/// Q42's group-wide cross-check: "in a multi-receiver session, all failing
/// probes at once means the problem is local".
///
/// It is the one judgement that needs no errno knowledge — our own routing
/// table, interface or Wi-Fi cannot be told apart from several devices dying at
/// the same instant by looking at one receiver, but it can by looking at the
/// whole round. The rule deliberately requires evidence about **every**
/// receiver: a single receiver failing is that receiver's problem, and a member
/// that was not probed this round leaves the round inconclusive (its own
/// verdict then stands). A `Refused` probe proves the host answered, so an
/// all-refused round is *not* a local network failure — the devices are alive
/// and simply not serving.
pub fn local_network_suspected(probes: &[ReceiverLiveness], receivers: usize) -> bool {
    receivers > 1
        && probes.len() == receivers
        && probes.iter().all(|probed| !probed.host_answered())
}

/// What a live `GET /info` probe could tell us.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProbeOutcome {
    /// No active session on the receiver ([`RECEIVER_SESSION_ACTIVE`] is clear),
    /// whatever the other bits say.
    Free,
    /// The receiver's active session is **ours**: the session bit is set and we
    /// still hold the session ([`Tracker::we_hold_session`]), or healthy media
    /// plus a reachable receiver made the auxiliary probe irrelevant. Never a
    /// reclaim trigger — the receiver is still serving us, so the control-plane
    /// symptom is something else.
    OursActive,
    /// The receiver has an active session and we do not hold it: another sender
    /// owns the device. Queue one re-probe, do not fight.
    BusyElsewhere,
    /// The probe completed but `statusFlags` was missing or not an unsigned
    /// 64-bit integer. Structurally unclassifiable; the caller reclaims
    /// (fail-open), exactly like a failed probe.
    Unknown,
    /// The receiver did not answer a TCP connect on its control port while our
    /// media was still healthy (Q34), or it refused the connection (Q42: the
    /// host is alive but not serving, so `statusFlags` cannot be read either).
    /// Either way this reclaims and must never be classified as
    /// `BusyElsewhere`; the diagnostic line keeps the raw state (`dead` vs
    /// `refused`).
    Gone,
}

impl ProbeOutcome {
    pub fn name(self) -> &'static str {
        match self {
            Self::Free => "free",
            Self::OursActive => "ours_active",
            Self::BusyElsewhere => "busy_elsewhere",
            Self::Unknown => "unknown",
            Self::Gone => "gone",
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

/// Q34's two-dimension verdict, exactly:
///
/// ```text
/// (media healthy, Alive)   => OursActive
/// (media healthy, Refused) => Gone       // alive, but not serving us
/// (media healthy, Dead)    => Gone       // the device is gone, not taken over
/// (media healthy, Unknown) => OursActive // conservative
/// (!media healthy, _)      => consult statusFlags
/// ```
///
/// The status-flags consultation is [`classify`] (and `Unknown` when the field
/// is missing), i.e. the fail-open path is unchanged for an unhealthy media
/// session. `Dead` and `Refused` win over the flags in every case so they can
/// never resolve to `BusyElsewhere`: a receiver that cannot accept a TCP
/// connection is not "busy with another sender". Q42 keeps them distinct as
/// states (see [`ReceiverLiveness`]) while sharing this action.
///
/// `now` is implicit on purpose: the media-health sample and the liveness probe
/// both belong to the caller's current poll.
pub fn combine(
    media_healthy: bool,
    liveness: ReceiverLiveness,
    status_flags: Option<u64>,
    we_hold_session: bool,
) -> ProbeOutcome {
    use ReceiverLiveness as L;
    match (media_healthy, liveness) {
        (_, L::Dead | L::Refused) => ProbeOutcome::Gone,
        (true, L::Alive | L::Unknown) => ProbeOutcome::OursActive,
        (false, _) => match status_flags {
            Some(flags) => classify(flags, we_hold_session),
            None => ProbeOutcome::Unknown,
        },
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
    /// Leave the receiver alone for now and ask again after the next backoff
    /// interval ([`requeue_delay`]). There is no upper bound on queueing: a
    /// confirmed busy receiver is polled forever, never reclaimed for being
    /// busy (Q34).
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
        ProbeOutcome::Free | ProbeOutcome::Unknown | ProbeOutcome::Gone => ProbeAction::Reclaim,
        // Never a reclaim: the receiver is still serving our own session.
        ProbeOutcome::OursActive => ProbeAction::FallThrough,
        ProbeOutcome::BusyElsewhere => ProbeAction::Requeue,
    }
}

/// The Q30 verdict table including probe failure and timeout: **fail-open**.
/// Errors must never fall through to the generic transport-fault branch.
///
/// The liveness dimension is applied by [`combine`] before this function: a
/// healthy media session with a reachable (or unproven) receiver never reaches
/// the error path. When media is unhealthy, a probe error still reclaims.
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
///
/// The settled outcome is returned too: it is what the action is based on, so
/// the caller can tell a confirmed `Free` (a real takeover cycle) from the
/// fail-open paths without re-deriving the confirmation rule.
fn confirm(
    first: ProbeOutcome,
    second: Option<ProbeOutcome>,
) -> (
    ProbeAction,
    Option<(ProbeOutcome, ProbeOutcome)>,
    ProbeOutcome,
) {
    let Some(second) = second else {
        return (action_for(first), None, first);
    };
    if second == ProbeOutcome::Unknown {
        return (action_for(first), None, first);
    }
    if second == first {
        return (action_for(first), None, first);
    }
    if first == ProbeOutcome::OursActive || second == ProbeOutcome::OursActive {
        return (
            ProbeAction::FallThrough,
            Some((first, second)),
            ProbeOutcome::OursActive,
        );
    }
    if first == ProbeOutcome::BusyElsewhere || second == ProbeOutcome::BusyElsewhere {
        return (
            ProbeAction::Requeue,
            Some((first, second)),
            ProbeOutcome::BusyElsewhere,
        );
    }
    // Both usable verdicts are in the Free family; nothing destructive
    // contradicted the first one.
    (action_for(first), Some((first, second)), first)
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

/// Result of counting one takeover for one receiver.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TakeoverVerdict {
    /// Below the limit: reclaim is allowed and `consecutive` is the count.
    Reclaim { consecutive: u32 },
    /// At the limit: stop reclaiming and report `PREEMPTED(limit)`.
    Yield { consecutive: u32 },
}

/// The "taken over N times in a row, each within N s of a reclaim" counter, one
/// instance per receiver.
///
/// The reference sender's counter measures a **cycle rate** — reclaim a
/// receiver, get taken over again quickly — not how long a sender queued. Only
/// a successful reclaim arms the window ([`Self::reclaim_succeeded`]); a
/// takeover that follows it inside [`TAKEOVER_WINDOW`] continues the run, and
/// one that does not (or that follows no successful reclaim at all) starts a new
/// run at 1. Queueing for a busy receiver never touches this counter (Q34 §四).
#[derive(Clone, Copy, Debug, Default)]
pub struct TakeoverCounter {
    consecutive: u32,
    /// When our last reclaim of this receiver completed (a fresh session is up).
    /// `None` until the first successful reclaim — the very first takeover of a
    /// run therefore counts as 1, not as a lost cycle.
    last_reclaim: Option<Instant>,
}

impl TakeoverCounter {
    pub fn consecutive(&self) -> u32 {
        self.consecutive
    }

    /// Record a **successful** reclaim: the fresh session is established. Only
    /// from here can a following takeover continue the same run.
    pub fn reclaim_succeeded(&mut self, now: Instant) {
        self.last_reclaim = Some(now);
    }

    /// Record one genuine takeover of our session.
    ///
    /// A takeover inside [`TAKEOVER_WINDOW`] of a successful reclaim is another
    /// turn of the same reclaim → takeover cycle and increments the count. With
    /// no such reclaim the count resets to 1 (never 0). Reaching
    /// [`TAKEOVER_LIMIT`] yields. One takeover consumes the successful-reclaim
    /// marker, so repeated probes of the same displacement do not count twice.
    pub fn takeover(&mut self, now: Instant) -> TakeoverVerdict {
        self.consecutive = match self.last_reclaim {
            Some(last) if now.saturating_duration_since(last) <= TAKEOVER_WINDOW => {
                self.consecutive.saturating_add(1)
            }
            _ => 1,
        };
        self.last_reclaim = None;
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
    /// Record that a reclaim of `host` succeeded (a fresh session is up).
    pub fn reclaimed(&mut self, host: IpAddr, now: Instant) {
        self.counters
            .entry(host)
            .or_default()
            .reclaim_succeeded(now);
    }

    /// Record one genuine takeover of `host`'s session.
    pub fn takeover(&mut self, host: IpAddr, now: Instant) -> TakeoverVerdict {
        self.counters.entry(host).or_default().takeover(now)
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

/// What one probe result means after the confirmation rule and the queue
/// backoff have been applied.
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
    /// The settled verdict is `Free` and the action is [`ProbeAction::Reclaim`]:
    /// a genuine takeover cycle, the only event that feeds a
    /// [`TakeoverCounter`]. Queueing (`Requeue`), a dead receiver (`Gone`) and
    /// the fail-open paths are never takeovers.
    pub takeover: bool,
}

/// Per-member evidence and probe scheduling. Only the session thread probes;
/// feedback workers merely append [`Signal`]s to `Health`.
#[derive(Debug)]
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
    /// Completed confirmed-`BusyElsewhere` requeues in the current run. It
    /// selects the next backoff interval; it is deliberately **not** a limit.
    requeues: u32,
    /// Terminal `yielding` state (Q34): no automatic probe is scheduled, and
    /// signals cannot re-arm one. Only an explicit [`Self::trigger`] leaves it.
    suspended: bool,
    /// Q42's liveness escalation timer: `Unknown` is tolerated only for
    /// [`LIVENESS_PATIENCE`] consecutive samples.
    liveness: LivenessTracker,
    /// The last liveness half of the diagnostic line, so a repeated
    /// non-definitive probe stays quiet while a change (including the failure
    /// count) is printed.
    last_liveness_log: Option<(Option<ReceiverLiveness>, Verdict, u32)>,
}

impl Default for Tracker {
    fn default() -> Self {
        Self {
            signals: Vec::new(),
            next_probe: None,
            last_report: None,
            ended_at: None,
            pending: None,
            requeues: 0,
            suspended: false,
            // A tracker is created when the session is established: the RTSP
            // handshake just proved the receiver reachable, so the clock starts
            // at "last seen alive" rather than at an arbitrary epoch.
            liveness: LivenessTracker::new(Instant::now()),
            last_liveness_log: None,
        }
    }
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
        // interval. A suspended (yielded) tracker never re-arms itself: its
        // evidence stays recorded, but only an explicit trigger resumes it.
        if self.next_probe.is_none() && !self.suspended {
            self.next_probe = Some(at);
        }
    }

    /// Whether the receiver's active session is ours to lose — the
    /// `we_hold_session` dimension [`classify`] needs. See the free
    /// [`we_hold_session`] for the exact derivation; this only supplies the
    /// tracker's recorded `ended_at`.
    ///
    /// One addition: while a **confirmed busy run** is in progress, the stale
    /// `ended_at` self-heal must not flip the verdict back to `OursActive` after
    /// [`ENDED_TRUST_WINDOW`]. Two agreeing probes already proved another sender
    /// owns the receiver; only renewed healthy media can prove it is ours again.
    /// Without this, the queue could never poll once a minute forever — the
    /// 2 s window would dissolve the queue on its first backoff round (Q34 §四).
    pub fn we_hold_session(&self, rtp_healthy: bool, now: Instant) -> bool {
        if rtp_healthy {
            return true;
        }
        if self.queueing() {
            return false;
        }
        we_hold_session(false, self.ended_at, now)
    }

    /// Whether a confirmed-`BusyElsewhere` queue is in progress: at least one
    /// confirmed requeue, or a busy verdict waiting for its confirmation.
    pub fn queueing(&self) -> bool {
        self.requeues > 0
            || matches!(
                self.pending,
                Some(PendingProbe {
                    first: ProbeOutcome::BusyElsewhere
                })
            )
    }

    /// Signals collected since the last call, for the one evidence log line
    /// that carries `policy`/`rtp_healthy`/`packets_prepared`/`tcp_reachable`.
    pub fn take_signals(&mut self) -> Vec<Signal> {
        std::mem::take(&mut self.signals)
    }

    /// Feed one liveness sample through Q42's escalation rule. The verdict is
    /// what the combine table consumes; `log_changed` says whether the
    /// diagnostic line's liveness half changed.
    pub fn observe_liveness(&mut self, sample: LivenessSample, now: Instant) -> LivenessNote {
        let (raw, verdict) = match sample {
            LivenessSample::Probe(probed) => (Some(probed), self.liveness.observe(probed, now)),
            // The group-wide rule says the failure belongs to our side, not to
            // this device: no counter movement, no escalation, just "unknown".
            LivenessSample::LocalSuspect => (None, Verdict::Unknown),
        };
        let key = (raw, verdict, self.liveness.consecutive_failures());
        let log_changed = self.last_liveness_log != Some(key);
        self.last_liveness_log = Some(key);
        LivenessNote {
            verdict,
            log_changed,
        }
    }

    /// Consecutive non-definitive liveness probes, for the diagnostic line.
    pub fn liveness_failures(&self) -> u32 {
        self.liveness.consecutive_failures()
    }

    /// Q42's timer needs repeated samples. After an undecided (`Unknown`) round
    /// ask for the next liveness probe at [`LIVENESS_RETRY`]; otherwise a single
    /// `Unknown` would be the only sample this tracker ever sees (the next probe
    /// is otherwise only scheduled by fresh control-plane evidence, and a dead
    /// feedback worker produces no more), so the patience limit could never be
    /// reached. It never overrides a busy queue or the terminal yielded state,
    /// and it is bounded: the third unknown escalates to `Dead`.
    pub fn await_liveness(&mut self, now: Instant) {
        if self.suspended || self.queueing() {
            return;
        }
        self.next_probe = Some(now + LIVENESS_RETRY);
    }

    pub fn due(&self, now: Instant) -> bool {
        !self.suspended && self.next_probe.is_some_and(|at| now >= at)
    }

    pub fn scheduled(&self) -> Option<Instant> {
        self.next_probe
    }

    /// Enter the terminal `yielding` state (Q34 §四): the receiver keeps being
    /// reclaimed and taken over in cycles too fast to be a legitimate queue.
    /// Every queued probe is dropped and nothing is scheduled again — the
    /// caller reports `preempted(reason="limit")` and stops automatic
    /// reconnection. This is not a timer: [`Self::trigger`] is the only way back.
    pub fn suspend(&mut self) {
        self.suspended = true;
        self.pending = None;
        self.next_probe = None;
    }

    /// Whether the tracker is in the terminal yielded state.
    pub fn suspended(&self) -> bool {
        self.suspended
    }

    /// Explicit external trigger (the user pressed play): the only way out of
    /// the yielded state other than a brand-new playback run. It re-arms one
    /// immediate probe and restarts the queue backoff. The low-frequency
    /// "is it free again?" poll of a *new* run is the other, slower resume
    /// path: a fresh run starts a fresh tracker whose first probe observes
    /// `Free`. Returns whether the trigger did anything.
    pub fn trigger(&mut self, now: Instant) -> bool {
        if !self.suspended {
            return false;
        }
        self.suspended = false;
        self.requeues = 0;
        self.pending = None;
        self.next_probe = Some(now);
        true
    }

    /// Feed one probe result back and apply the confirmation rule plus the
    /// queue backoff.
    ///
    /// A `Free` or `BusyElsewhere` first verdict is not acted on: it schedules
    /// one confirmation probe after [`CONFIRM_DELAY`]. `OursActive` and the
    /// fail-open paths (`Unknown`, `Gone`, error, timeout) are acted on
    /// immediately. A confirmed `BusyElsewhere` queues another probe after the
    /// next [`REPROBE_BACKOFF`] interval — forever. It never forces a reclaim.
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
            let (action, disagreement, settled) = confirm(pending.first, second);
            return self.settle(action, disagreement, settled, log_changed, now);
        }

        match result {
            Ok(report) if report.outcome == ProbeOutcome::Free => {
                self.schedule_confirmation(ProbeOutcome::Free, log_changed, now)
            }
            Ok(report) if report.outcome == ProbeOutcome::BusyElsewhere => {
                self.schedule_confirmation(ProbeOutcome::BusyElsewhere, log_changed, now)
            }
            // `OursActive` changes nothing; `Unknown`/`Gone`/error are
            // fail-open and reclaim immediately. Neither waits for a
            // confirmation.
            _ => {
                let action = action_for_result(result);
                let settled = result
                    .as_ref()
                    .map_or(ProbeOutcome::Unknown, |report| report.outcome);
                self.settle(action, None, settled, log_changed, now)
            }
        }
    }

    fn schedule_confirmation(
        &mut self,
        first: ProbeOutcome,
        log_changed: bool,
        now: Instant,
    ) -> ProbeNote {
        // A first verdict that is not another sender's session ends the busy
        // run immediately: the receiver is free (or ours), so the queue's
        // backoff must not leak into a later busy run.
        if first != ProbeOutcome::BusyElsewhere {
            self.requeues = 0;
        }
        let due = now + CONFIRM_DELAY;
        self.pending = Some(PendingProbe { first });
        self.next_probe = Some(due);
        ProbeNote {
            log_changed,
            action: ProbeAction::Confirm,
            disagreement: None,
            takeover: false,
        }
    }

    fn settle(
        &mut self,
        action: ProbeAction,
        disagreement: Option<(ProbeOutcome, ProbeOutcome)>,
        settled: ProbeOutcome,
        log_changed: bool,
        now: Instant,
    ) -> ProbeNote {
        let takeover = settled == ProbeOutcome::Free && action == ProbeAction::Reclaim;
        match action {
            ProbeAction::Requeue => {
                // Queueing is unbounded and backs off: the interval grows
                // 3 s → 6 s → 12 s → 24 s → 60 s and then stays at 60 s. This
                // is the *only* thing a busy receiver changes; it is never
                // turned into a reclaim (Q34 §四).
                let delay = requeue_delay(self.requeues);
                self.requeues = self.requeues.saturating_add(1);
                if !self.suspended {
                    self.next_probe = Some(now + delay);
                }
            }
            ProbeAction::Reclaim | ProbeAction::FallThrough => {
                // Any non-busy verdict breaks the consecutive count.
                self.requeues = 0;
                self.next_probe = None;
            }
            ProbeAction::Confirm => unreachable!("a confirmation is scheduled, not settled"),
        }
        ProbeNote {
            log_changed,
            action,
            disagreement,
            takeover,
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
    fn the_combine_table_matches_q34() {
        use ReceiverLiveness as L;
        let active = RECEIVER_SESSION_ACTIVE;
        // Healthy media + reachable (or unproven) receiver: our session, even
        // when statusFlags says nobody is active.
        for liveness in [L::Alive, L::Unknown] {
            for flags in [0u64, active, active | STEREO_PARTNER_UNREACHABLE] {
                for hold in [false, true] {
                    assert_eq!(
                        combine(true, liveness, Some(flags), hold),
                        ProbeOutcome::OursActive,
                        "healthy media + {liveness:?} (flags=0x{flags:016x}) is ours"
                    );
                }
            }
        }
        // Healthy media + a dead receiver: the device is gone, reclaim.
        for flags in [0u64, active] {
            for hold in [false, true] {
                assert_eq!(
                    combine(true, L::Dead, Some(flags), hold),
                    ProbeOutcome::Gone
                );
            }
        }
        // Q42: a refused connect is a different *state* but the same action —
        // the host is alive, it just cannot give us statusFlags.
        for flags in [0u64, active] {
            for hold in [false, true] {
                for media_healthy in [false, true] {
                    assert_eq!(
                        combine(media_healthy, L::Refused, Some(flags), hold),
                        ProbeOutcome::Gone
                    );
                }
            }
        }
        // Unhealthy media: consult statusFlags exactly as before.
        assert_eq!(
            combine(false, L::Alive, Some(0), false),
            ProbeOutcome::Free
        );
        assert_eq!(
            combine(false, L::Alive, Some(active), false),
            ProbeOutcome::BusyElsewhere
        );
        assert_eq!(
            combine(false, L::Alive, Some(active), true),
            ProbeOutcome::OursActive
        );
        assert_eq!(
            combine(false, L::Unknown, Some(active), false),
            ProbeOutcome::BusyElsewhere
        );
        assert_eq!(
            combine(false, L::Alive, None, false),
            ProbeOutcome::Unknown
        );
        // Even with unhealthy media a dead receiver never becomes busy.
        assert_eq!(
            combine(false, L::Dead, Some(active), false),
            ProbeOutcome::Gone
        );
    }

    #[test]
    fn a_dead_receiver_reclaims_and_is_never_busy_elsewhere() {
        use ReceiverLiveness as L;
        // `Refused` is included on purpose: same action, distinct state (Q42).
        for liveness in [L::Dead, L::Refused] {
            for media_healthy in [true, false] {
                for hold in [false, true] {
                    let outcome = combine(
                        media_healthy,
                        liveness,
                        Some(RECEIVER_SESSION_ACTIVE),
                        hold,
                    );
                    assert_eq!(outcome, ProbeOutcome::Gone);
                    assert_eq!(action_for(outcome), ProbeAction::Reclaim);
                    assert_ne!(
                        action_for(outcome),
                        ProbeAction::Requeue,
                        "a device that cannot accept TCP is gone, not another sender's"
                    );
                }
            }
        }
    }

    #[test]
    fn the_liveness_flag_is_only_true_for_a_completed_connect() {
        assert!(ReceiverLiveness::Alive.reachable());
        assert!(!ReceiverLiveness::Refused.reachable());
        assert!(!ReceiverLiveness::Unknown.reachable());
        assert!(!ReceiverLiveness::Dead.reachable());
        assert_eq!(ReceiverLiveness::Alive.name(), "alive");
        assert_eq!(ReceiverLiveness::Refused.name(), "refused");
        assert_eq!(ReceiverLiveness::Unknown.name(), "unknown");
        assert_eq!(ReceiverLiveness::Dead.name(), "dead");
        // Q42: a returned RST proves the host answered; a timeout or an
        // unreachable error does not. That is what keeps refused out of dead.
        assert!(ReceiverLiveness::Alive.host_answered());
        assert!(ReceiverLiveness::Refused.host_answered());
        assert!(!ReceiverLiveness::Dead.host_answered());
        assert!(!ReceiverLiveness::Unknown.host_answered());
    }

    #[test]
    fn only_the_q42_errors_are_dead_and_a_refusal_is_not_one_of_them() {
        assert_eq!(
            liveness_from_error(&std::io::Error::from(ErrorKind::ConnectionRefused)),
            ReceiverLiveness::Refused,
            "a returned RST means the host is alive and answered"
        );
        for kind in [
            ErrorKind::TimedOut,
            ErrorKind::HostUnreachable,
            ErrorKind::NetworkUnreachable,
        ] {
            assert_eq!(
                liveness_from_error(&std::io::Error::from(kind)),
                ReceiverLiveness::Dead,
                "{kind:?} means the host is not there"
            );
        }
        // Everything else proves neither life nor death and is left to the
        // escalation timer — no errno enumeration (Q42).
        for kind in [
            ErrorKind::ConnectionReset,
            ErrorKind::NotConnected,
            ErrorKind::PermissionDenied,
            ErrorKind::Other,
        ] {
            assert_eq!(
                liveness_from_error(&std::io::Error::from(kind)),
                ReceiverLiveness::Unknown,
                "{kind:?} proves neither life nor death"
            );
        }
    }

    #[test]
    fn an_unknown_chain_escalates_to_dead_and_one_ok_resets_it() {
        let base = Instant::now();
        let mut liveness = LivenessTracker::new(base);
        assert_eq!(
            liveness.observe(ReceiverLiveness::Unknown, at(base, 1_000)),
            Verdict::Unknown,
            "one failed probe is still only 'temporarily unknown'"
        );
        assert_eq!(
            liveness.observe(ReceiverLiveness::Unknown, at(base, 2_000)),
            Verdict::Unknown
        );
        assert_eq!(liveness.consecutive_failures(), 2);
        assert_eq!(
            liveness.observe(ReceiverLiveness::Unknown, at(base, 3_000)),
            Verdict::Dead,
            "the patience limit is three consecutive unknowns"
        );
        assert_eq!(liveness.consecutive_failures(), LIVENESS_PATIENCE);
        // One successful probe resets the counter and the clock...
        assert_eq!(
            liveness.observe(ReceiverLiveness::Alive, at(base, 4_000)),
            Verdict::Alive
        );
        assert_eq!(liveness.consecutive_failures(), 0);
        assert_eq!(liveness.last_ok(), at(base, 4_000));
        // ...so the chain has to start over.
        for elapsed in 5..7 {
            assert_eq!(
                liveness.observe(ReceiverLiveness::Unknown, at(base, elapsed * 1_000)),
                Verdict::Unknown
            );
        }
        assert_eq!(
            liveness.observe(ReceiverLiveness::Unknown, at(base, 7_000)),
            Verdict::Dead
        );
    }

    #[test]
    fn refused_and_dead_are_decided_without_waiting() {
        let base = Instant::now();
        let mut liveness = LivenessTracker::new(base);
        assert_eq!(
            liveness.observe(ReceiverLiveness::Refused, at(base, 1)),
            Verdict::Dead,
            "refused cannot read statusFlags either: fail-open reclaim"
        );
        assert_eq!(
            liveness.observe(ReceiverLiveness::Dead, at(base, 2)),
            Verdict::Dead
        );
        assert_eq!(
            liveness.consecutive_failures(),
            0,
            "definitive states are not counted as unknown failures"
        );
        // A refusal is not a success: it must not refresh the alive clock.
        assert_eq!(liveness.last_ok(), base);
    }

    #[test]
    fn the_patience_window_only_escalates_after_a_second_failure() {
        let base = Instant::now();
        let mut liveness = LivenessTracker::new(base);
        // One anomalous error after a long quiet period is exactly the
        // transient case the rule must not punish.
        assert_eq!(
            liveness.observe(ReceiverLiveness::Unknown, at(base, 120_000)),
            Verdict::Unknown
        );
        // A second one past the window is no longer a one-off.
        assert_eq!(
            liveness.observe(ReceiverLiveness::Unknown, at(base, 121_000)),
            Verdict::Dead
        );
    }

    #[test]
    fn all_receivers_failing_in_one_round_is_a_local_problem() {
        use ReceiverLiveness as L;
        // Every receiver produced no evidence of life: the common cause is our
        // own side, not several devices dying at once.
        assert!(local_network_suspected(&[L::Dead, L::Unknown], 2));
        assert!(local_network_suspected(&[L::Dead, L::Dead, L::Unknown], 3));
        // One alive receiver proves the local network works: the failure
        // belongs to the device that failed.
        assert!(!local_network_suspected(&[L::Dead, L::Alive], 2));
        // A returned RST proves the host answered, so an all-refused round is
        // not a local routing failure.
        assert!(!local_network_suspected(&[L::Refused, L::Refused], 2));
        assert!(!local_network_suspected(&[L::Refused, L::Dead], 2));
        // A single receiver can never be "all failed": that is its own problem.
        assert!(!local_network_suspected(&[L::Dead], 1));
        // A member that was not probed this round leaves the round inconclusive.
        assert!(!local_network_suspected(&[L::Dead], 2));
        assert!(!local_network_suspected(&[], 2));
    }

    #[test]
    fn a_local_suspect_round_neither_counts_nor_resets_the_device_failures() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        assert_eq!(
            tracker
                .observe_liveness(LivenessSample::Probe(ReceiverLiveness::Unknown), at(base, 0))
                .verdict,
            Verdict::Unknown
        );
        assert_eq!(tracker.liveness_failures(), 1);
        // Every receiver failed at once: this is not evidence about this device.
        let local = tracker.observe_liveness(LivenessSample::LocalSuspect, at(base, 1_000));
        assert_eq!(local.verdict, Verdict::Unknown);
        assert_eq!(
            tracker.liveness_failures(),
            1,
            "a local round neither advances nor resets the device counter"
        );
        // The device's own chain still needs its two remaining failures.
        assert_eq!(
            tracker
                .observe_liveness(LivenessSample::Probe(ReceiverLiveness::Unknown), at(base, 2_000))
                .verdict,
            Verdict::Unknown
        );
        assert_eq!(
            tracker
                .observe_liveness(LivenessSample::Probe(ReceiverLiveness::Unknown), at(base, 3_000))
                .verdict,
            Verdict::Dead
        );
    }

    #[test]
    fn an_undecided_round_schedules_another_sample() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::ConnectionClose, at(base, 0));
        assert!(tracker.due(at(base, 0)));
        let note = tracker.observe_liveness(
            LivenessSample::Probe(ReceiverLiveness::Unknown),
            at(base, 0),
        );
        assert_eq!(note.verdict, Verdict::Unknown);
        assert!(note.log_changed, "the first unknown is printed");
        // The session loop asks for another sample after an undecided verdict;
        // without it a single unknown would be the only sample ever taken.
        tracker.await_liveness(at(base, 0));
        assert_eq!(tracker.scheduled(), Some(at(base, 0) + LIVENESS_RETRY));
        assert!(!tracker.due(at(base, 0) + LIVENESS_RETRY - Duration::from_millis(1)));
        assert!(tracker.due(at(base, 0) + LIVENESS_RETRY));
        // An unchanged sample stays quiet; a reset does change the line.
        let alive = tracker.observe_liveness(
            LivenessSample::Probe(ReceiverLiveness::Alive),
            at(base, 9_000),
        );
        assert_eq!(alive.verdict, Verdict::Alive);
        assert!(alive.log_changed);
        assert!(
            !tracker
                .observe_liveness(LivenessSample::Probe(ReceiverLiveness::Alive), at(base, 12_000))
                .log_changed,
            "a repeated alive sample must not re-print the line"
        );
    }

    #[test]
    fn the_liveness_timer_never_cuts_a_busy_queue_short() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::ConnectionClose, at(base, 0));
        let (note, confirmation) = confirm_busy(&mut tracker, at(base, 0));
        assert_eq!(note.action, ProbeAction::Requeue);
        let scheduled = tracker.scheduled().unwrap();
        tracker.await_liveness(confirmation + Duration::from_millis(1));
        assert_eq!(
            tracker.scheduled(),
            Some(scheduled),
            "the busy backoff interval owns the schedule"
        );
        // The terminal yielded state is not re-armed either.
        tracker.suspend();
        tracker.await_liveness(confirmation + Duration::from_secs(1));
        assert_eq!(tracker.scheduled(), None);
    }

    #[test]
    fn a_tcp_connect_probe_reports_alive_or_refused() {
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        assert_eq!(
            receiver_liveness(address.ip(), address.port(), PROBE_TIMEOUT),
            ReceiverLiveness::Alive,
            "a listening control port is reachable"
        );
        // Accept and drop the probe connection so the listener is not left with
        // a pending handshake.
        let (socket, _) = listener.accept().unwrap();
        drop(socket);
        drop(listener);
        // A generous timeout: the point is the *state*, and a slow local stack
        // must not turn the RST into a timeout. Both are the same action, but
        // only one of them is "the device is gone" (Q42).
        let probed = receiver_liveness(address.ip(), address.port(), Duration::from_secs(5));
        assert_ne!(probed, ReceiverLiveness::Alive);
        assert!(
            matches!(probed, ReceiverLiveness::Refused | ReceiverLiveness::Dead),
            "a closed control port is refused or unreachable, never alive: {probed:?}"
        );
        assert!(!probed.reachable());
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
        assert!(!first.takeover, "a lone verdict is never a takeover yet");
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
        assert!(
            second.takeover,
            "a confirmed Free is the genuine takeover the cycle counter needs"
        );
        assert_eq!(tracker.scheduled(), None);
    }

    #[test]
    fn a_dead_receiver_or_a_fail_open_probe_is_a_reclaim_but_not_a_takeover() {
        for result in [
            Ok(report(ProbeOutcome::Gone)),
            Ok(report(ProbeOutcome::Unknown)),
            Err(anyhow!("probe timed out")),
        ] {
            let mut tracker = Tracker::default();
            let note = tracker.note_result(&result, Instant::now());
            assert_eq!(note.action, ProbeAction::Reclaim);
            assert!(
                !note.takeover,
                "a dead device and an unreadable probe must never feed the takeover counter"
            );
            assert_eq!(tracker.scheduled(), None);
        }
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
            assert!(!note.takeover);
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
        let note = tracker.note_result(&busy(), at(base, 0) + CONFIRM_DELAY);
        assert_eq!(
            note.action,
            ProbeAction::Requeue,
            "a disagreement must not reclaim: prefer the non-destructive outcome"
        );
        assert!(!note.takeover);
        assert_eq!(
            note.disagreement,
            Some((ProbeOutcome::Free, ProbeOutcome::BusyElsewhere))
        );
        assert_eq!(
            tracker.scheduled(),
            Some(at(base, 0) + CONFIRM_DELAY + requeue_delay(0)),
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
        assert!(!note.takeover);
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
    fn queueing_backs_off_forever_and_never_forces_a_reclaim() {
        // Q34 §四: a confirmed busy receiver is polled with exponential backoff
        // and never reclaimed for being busy. The sequence is exactly
        // 3, 6, 12, 24, 60, 60, … seconds.
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::ConnectionClose, at(base, 0));
        let expected = [
            Duration::from_secs(3),
            Duration::from_secs(6),
            Duration::from_secs(12),
            Duration::from_secs(24),
            Duration::from_secs(60),
            Duration::from_secs(60),
            Duration::from_secs(60),
        ];
        let mut now = at(base, 0);
        for (index, delay) in expected.into_iter().enumerate() {
            let (note, confirmation) = confirm_busy(&mut tracker, now);
            assert_eq!(
                note.action,
                ProbeAction::Requeue,
                "confirmed busy #{index} queues, it never reclaims"
            );
            assert!(!note.takeover, "queueing is not a takeover");
            assert_eq!(
                tracker.scheduled(),
                Some(confirmation + delay),
                "requeue #{index} must wait {delay:?}"
            );
            assert!(!tracker.due(confirmation + delay - Duration::from_millis(1)));
            assert!(tracker.due(confirmation + delay));
            now = confirmation + delay;
        }
        // Well past an hour: still queueing, still no reclaim.
        now += Duration::from_secs(3600);
        let (note, confirmation) = confirm_busy(&mut tracker, now);
        assert_eq!(note.action, ProbeAction::Requeue);
        assert_eq!(
            tracker.scheduled(),
            Some(confirmation + REPROBE_MAX_DELAY),
            "the tracked interval stays capped at 60 s forever"
        );
    }

    #[test]
    fn requeue_delay_is_the_exact_backoff_sequence() {
        assert_eq!(requeue_delay(0), Duration::from_secs(3));
        assert_eq!(requeue_delay(1), Duration::from_secs(6));
        assert_eq!(requeue_delay(2), Duration::from_secs(12));
        assert_eq!(requeue_delay(3), Duration::from_secs(24));
        for later in [4u32, 5, 100, u32::MAX] {
            assert_eq!(requeue_delay(later), Duration::from_secs(60));
        }
        assert_eq!(REPROBE_BACKOFF.len(), 5);
        assert_eq!(REPROBE_MAX_DELAY, Duration::from_secs(60));
    }

    #[test]
    fn a_confirmed_busy_queue_does_not_self_heal_into_ours_active() {
        // Without this, the 2 s ended-trust window would dissolve the queue on
        // its first backoff round and "poll once a minute forever" would never
        // happen. Two agreeing probes are stronger evidence than a stale
        // `ended_at`; only renewed healthy media clears it.
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::UnexpectedTeardown, at(base, 0));
        assert!(
            !tracker.we_hold_session(false, at(base, 1_000)),
            "inside the ended-trust window the session is not ours"
        );
        assert!(
            tracker.we_hold_session(false, at(base, 3_000)),
            "without a confirmed busy queue the stale conclusion self-heals (Q32)"
        );
        let (note, confirmation) = confirm_busy(&mut tracker, at(base, 0));
        assert_eq!(note.action, ProbeAction::Requeue);
        assert!(tracker.queueing());
        assert!(!tracker.we_hold_session(false, confirmation));
        assert!(
            !tracker.we_hold_session(false, confirmation + Duration::from_secs(3_600)),
            "an hour into the queue the receiver is still another sender's"
        );
        assert!(
            tracker.we_hold_session(true, confirmation + Duration::from_secs(3_600)),
            "renewed healthy media proves the session is ours again"
        );
        // A Free verdict ends the busy run and restores the normal derivation.
        tracker.note_result(&free(), confirmation);
        assert!(!tracker.queueing());
    }

    #[test]
    fn a_non_busy_verdict_resets_the_queue_backoff() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        let (first, confirmation) = confirm_busy(&mut tracker, at(base, 0));
        assert_eq!(first.action, ProbeAction::Requeue);
        let (second, confirmation) = confirm_busy(&mut tracker, confirmation + requeue_delay(0));
        assert_eq!(second.action, ProbeAction::Requeue);
        // Our own session appears: no queue, and the backoff run is broken.
        assert_eq!(
            tracker
                .note_result(&Ok(report(ProbeOutcome::OursActive)), confirmation)
                .action,
            ProbeAction::FallThrough
        );
        // A fresh busy run starts again at the 3 s interval; it must not inherit
        // the second step from minutes ago.
        let (next, next_confirmation) =
            confirm_busy(&mut tracker, confirmation + Duration::from_secs(10));
        assert_eq!(next.action, ProbeAction::Requeue);
        assert_eq!(
            tracker.scheduled(),
            Some(next_confirmation + requeue_delay(0))
        );
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
            Some(confirmation + requeue_delay(0)),
            "busy is a queue, not a retry storm"
        );
        assert!(!tracker.due(confirmation + requeue_delay(0) - Duration::from_millis(1)));
        assert!(tracker.due(confirmation + requeue_delay(0)));
        // A repeated busy verdict does not change the log line, so it stays
        // quiet, but it is still confirmed before it is queued.
        let repeated = tracker.note_result(&busy(), confirmation + requeue_delay(0));
        assert!(!repeated.log_changed);
        assert_eq!(
            repeated.action,
            ProbeAction::Confirm,
            "each verdict is confirmed"
        );
        // Fresh evidence while queued must not cut the interval short.
        tracker.observe(
            Signal::ConnectionClose,
            confirmation + requeue_delay(0) + Duration::from_millis(500),
        );
        assert_eq!(
            tracker.scheduled(),
            Some(confirmation + requeue_delay(0) + CONFIRM_DELAY),
            "a signal while a confirmation is pending must not reschedule it"
        );
    }

    #[test]
    fn yielding_is_terminal_and_only_an_explicit_trigger_revives_it() {
        // Q34: `yielding` is not a timer. When the takeover limit is hit the
        // tracker drops every queued probe; nothing is ever due again until an
        // explicit external trigger (the user pressing play) asks for one.
        let base = Instant::now();
        let mut tracker = Tracker::default();
        tracker.observe(Signal::UnexpectedTeardown, at(base, 0));
        // Queue once first, so a stale requeue really exists at yield time.
        let (note, confirmation) = confirm_busy(&mut tracker, at(base, 0));
        assert_eq!(note.action, ProbeAction::Requeue);
        assert!(tracker.scheduled().is_some());

        tracker.suspend();
        assert!(tracker.suspended());
        assert_eq!(
            tracker.scheduled(),
            None,
            "yielding must drop the queued probe"
        );
        // A long simulated interval: nothing is due, ever. One hour, one day,
        // one year — the tracker is terminal, not on a long timer.
        for elapsed in [1u64, 60, 3_600, 86_400, 31_536_000] {
            assert!(
                !tracker.due(base + Duration::from_secs(elapsed)),
                "a yielded tracker must never self-arm after {elapsed}s"
            );
        }
        // New control-plane evidence must not re-arm it either.
        tracker.observe(
            Signal::UnexpectedTeardown,
            base + Duration::from_secs(3_600),
        );
        assert_eq!(tracker.scheduled(), None);
        assert!(!tracker.due(base + Duration::from_secs(3_601)));
        // Only the explicit trigger (user pressed play) revives it.
        let trigger = base + Duration::from_secs(86_400);
        assert!(tracker.trigger(trigger));
        assert!(!tracker.suspended());
        assert_eq!(tracker.scheduled(), Some(trigger));
        assert!(tracker.due(trigger));
        // Triggering a live tracker is a no-op, not a second probe.
        assert!(!tracker.trigger(trigger + Duration::from_secs(1)));
        assert_eq!(
            tracker.scheduled(),
            Some(trigger),
            "the already scheduled probe is not rescheduled"
        );
        let _ = confirmation;
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
        // No successful reclaim yet: the first takeover starts a new run at 1.
        assert_eq!(
            counter.takeover(at(base, 0)),
            TakeoverVerdict::Reclaim { consecutive: 1 }
        );
        assert_eq!(counter.consecutive(), 1);
    }

    #[test]
    fn a_takeover_without_a_successful_reclaim_is_never_a_cycle() {
        // "each within <n>s of a reclaim": without a reclaim in between, two
        // quick takeovers are not two turns of the same run. Repeated probes of
        // one displacement cannot build up to a yield.
        let base = Instant::now();
        let mut counter = TakeoverCounter::default();
        assert_eq!(
            counter.takeover(at(base, 0)),
            TakeoverVerdict::Reclaim { consecutive: 1 }
        );
        for step in 1..=5 {
            assert_eq!(
                counter.takeover(at(base, step * 100)),
                TakeoverVerdict::Reclaim { consecutive: 1 }
            );
        }
        assert_eq!(counter.consecutive(), 1);
    }

    #[test]
    fn takeovers_after_successful_reclaims_increment_in_a_row() {
        let base = Instant::now();
        let mut counter = TakeoverCounter::default();
        assert_eq!(
            counter.takeover(at(base, 0)),
            TakeoverVerdict::Reclaim { consecutive: 1 }
        );
        counter.reclaim_succeeded(at(base, 1_000));
        assert_eq!(
            counter.takeover(at(base, 2_000)),
            TakeoverVerdict::Reclaim { consecutive: 2 }
        );
        // Exactly on the boundary is still "within the window" of the reclaim.
        counter.reclaim_succeeded(at(base, 3_000));
        assert_eq!(
            counter.takeover(at(base, 3_000) + TAKEOVER_WINDOW),
            TakeoverVerdict::Yield { consecutive: 3 }
        );
    }

    #[test]
    fn a_takeover_after_the_window_resets_the_count_to_one() {
        let base = Instant::now();
        let mut counter = TakeoverCounter::default();
        counter.takeover(at(base, 0));
        counter.reclaim_succeeded(at(base, 1_000));
        counter.takeover(at(base, 2_000));
        assert_eq!(counter.consecutive(), 2);
        // The next takeover is outside the window of the last successful
        // reclaim: a new run, not another cycle.
        counter.reclaim_succeeded(at(base, 3_000));
        assert_eq!(
            counter.takeover(at(base, 3_000) + TAKEOVER_WINDOW + Duration::from_millis(1)),
            TakeoverVerdict::Reclaim { consecutive: 1 }
        );
    }

    #[test]
    fn hitting_the_limit_yields_and_a_later_takeover_starts_over() {
        let base = Instant::now();
        let mut counter = TakeoverCounter::default();
        counter.takeover(at(base, 0));
        counter.reclaim_succeeded(at(base, 100));
        counter.takeover(at(base, 200));
        counter.reclaim_succeeded(at(base, 300));
        assert_eq!(
            counter.takeover(at(base, 400)),
            TakeoverVerdict::Yield {
                consecutive: TAKEOVER_LIMIT
            }
        );
        // Even after a yield a much later takeover starts a new run at 1 (the
        // runtime never gets here: `yielding` is terminal for automatic retries).
        counter.reclaim_succeeded(at(base, 400));
        assert_eq!(
            counter.takeover(at(base, 400) + TAKEOVER_WINDOW + Duration::from_millis(1)),
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
            counters.takeover(left, at(base, 0)),
            TakeoverVerdict::Reclaim { consecutive: 1 }
        );
        assert_eq!(
            counters.takeover(right, at(base, 0)),
            TakeoverVerdict::Reclaim { consecutive: 1 }
        );
        counters.reclaimed(left, at(base, 1_000));
        assert_eq!(
            counters.takeover(left, at(base, 2_000)),
            TakeoverVerdict::Reclaim { consecutive: 2 }
        );
        assert_eq!(
            counters.consecutive(right),
            1,
            "the other member's count is untouched"
        );
    }

    #[test]
    fn a_reclaim_cycle_only_counts_when_the_reclaim_succeeded() {
        // Q34's correction: `BUSY_LIMIT` counted how long a sender queued; the
        // reference counter counts reclaim → takeover cycles. The `busy` path
        // never feeds a takeover, and a takeover needs an armed successful
        // reclaim to continue a run.
        let base = Instant::now();
        let mut counter = TakeoverCounter::default();
        counter.reclaim_succeeded(at(base, 0));
        // Armed by a successful reclaim but no takeover happened: the first
        // takeover is still 1.
        assert_eq!(
            counter.takeover(at(base, 1_000)),
            TakeoverVerdict::Reclaim { consecutive: 1 }
        );
        // The marker is consumed: a second takeover with no new successful
        // reclaim does not count as another cycle.
        assert_eq!(
            counter.takeover(at(base, 2_000)),
            TakeoverVerdict::Reclaim { consecutive: 1 }
        );
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
