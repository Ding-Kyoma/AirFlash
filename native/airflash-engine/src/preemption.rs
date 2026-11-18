//! Receiver ownership checks under `receiver_ownership_guard`.
//!
//! [`Tracker`] classifies probe results and schedules confirmations. The
//! session applies the verdict. `Requeue` and `Reclaim` are historical action
//! names. Both end the current attempt.
//!
//! | Tracker action | Session outcome |
//! | --- | --- |
//! | [`ProbeAction::Confirm`] | Keep streaming until the confirmation probe. |
//! | [`ProbeAction::FallThrough`] | Keep streaming. Re-probe `Unknown` liveness after [`LIVENESS_RETRY`]. |
//! | [`ProbeAction::Requeue`] | Yield `preempted` with reason `takeover` when the receiver ended our session and is still busy. Otherwise yield `ownership_unconfirmed`. |
//! | [`ProbeAction::Reclaim`] | Yield retryable `peer_closed` only when the confirmation probe itself read `Free`. Otherwise yield `ownership_unconfirmed`. |
//!
//! A disagreeing confirmation or an ownership check still open 15 s after
//! feedback loss, clock loss, or an unexpected `TEARDOWN` also yields
//! `ownership_unconfirmed`. A yield stops the whole group and suppresses its
//! old `TEARDOWN` requests. The affected member suspends its tracker, including
//! any busy re-probe scheduled by [`REPROBE_BACKOFF`].
//!
//! `preempted` and `ownership_unconfirmed` require manual reconnect. When a
//! clock fault is pending, the sender reports retryable `clock_lost` in place
//! of the confirmed-free `peer_closed`. Every automatic start, including
//! desktop retry, standby resume, and capture-source restart, must first get
//! an inactive session from every member in two read-only `/info` rounds
//! [`CONFIRM_DELAY`] apart before SETUP. See `docs/RECEIVER-OWNERSHIP.md`.
//!
//! ## Probe inputs
//!
//! Control-plane symptoms schedule a probe. The background probe combines a
//! TCP liveness check on the receiver's control port and a fresh `GET /info`
//! with the session's local evidence. A successful UDP write only shows local
//! socket acceptance. A receiver-initiated `TEARDOWN`, feedback loss, or clock
//! loss invalidates the corresponding healthy-media assumption. Authentication
//! faults on the feedback channel follow the authentication fault path.
//!
//! [`classify`] interprets the 64-bit `statusFlags` field. Its session-active
//! bit can describe our session or another controller's, so the classifier
//! also takes the caller's association context. [`combine`] preserves the
//! distinction between `Refused` and `Dead` in the liveness evidence while
//! classifying both as `Gone`. [`LivenessTracker`] bounds consecutive unknown
//! samples. The first `Free` or `BusyElsewhere` verdict schedules a confirmation
//! after [`CONFIRM_DELAY`].
//!
//! The group-wide gateway reference model ([`judge_all_failed`]) is exercised
//! by tests. The live session does not run it. Each member's background probe
//! completes independently, and the sender applies its verdict without
//! waiting for a synchronized group round or connecting to the gateway.
use crate::rtsp::{Cancellation, Connection, is_teardown_request};
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

/// Consecutive non-definitive liveness probes before the tracker reports
/// `Dead`. Five samples at [`LIVENESS_RETRY`] leave 12 s between the first and
/// fifth nominal probe times. The session's separate ownership-check deadline
/// can end the attempt sooner.
pub const LIVENESS_PATIENCE: u32 = 5;

/// The wall-clock half of Q42's escalation timer: a receiver that has not been
/// proved alive for this long is treated as gone once a **second** consecutive
/// non-definitive probe confirms it is not a one-off. Q46 confirmed the narrowed
/// shape and left the number to us: 60 s matches the longest busy-queue
/// interval, and it is deliberately **not** the power-off budget — that is
/// [`LIVENESS_PATIENCE`] × [`LIVENESS_RETRY`]. This clause only catches
/// *sparse* failures (one every 40 s never reaches the consecutive limit) while
/// leaving a single anomalous error after a quiet hour alone.
pub const LIVENESS_PATIENCE_WINDOW: Duration = Duration::from_secs(60);

/// How long to wait before re-sampling a receiver whose liveness probe was
/// non-definitive. Q42's timer needs repeated samples: without this, a single
/// `Unknown` would be the only sample the tracker ever sees (the next probe is
/// only scheduled by fresh control-plane evidence, and a dead feedback worker
/// produces no more), so the patience limit could never be reached. It equals
/// the first [`REPROBE_BACKOFF`] step; the loop is bounded — the fifth unknown
/// escalates to `Dead`, which the session turns into a yield.
///
/// Q46 floats a growing interval (3 → 4 → 6 → 9 → 13 s) as a cheaper way to
/// spend the same patience. Not taken: it would spread five samples over ~35 s
/// and blow the 15 s power-off budget the same answer asks for. Probe cost is
/// already bounded by the sample count, not by the interval.
pub const LIVENESS_RETRY: Duration = Duration::from_secs(3);

/// The ports Q46's gateway reference probe tries, in order (the reference is
/// not consulted by the session loop; see the module docs). The reference only
/// needs **any** answer — a completed handshake and a returned RST are equally
/// good proof that the gateway's stack is on our path — so the first common
/// port with a listener (or a reject) settles it. 443 comes first because a
/// router that serves HTTPS has it open; 80 is the fallback for routers whose
/// admin page is plain HTTP.
pub const GATEWAY_PROBE_PORTS: [u16; 2] = [443, 80];

/// How long each gateway reference probe may take. It is half
/// [`LIVENESS_TIMEOUT`]: a gateway is one hop away, and the reference probe must
/// not dominate the all-failed round it is part of.
pub const GATEWAY_PROBE_TIMEOUT: Duration = Duration::from_millis(1_000);

/// Q46's last resort when no default route can be discovered at all: keep
/// suppressing the all-failed round for this long, then stop. The gateway is the
/// judgement; time is only the fallback for "there is no reference to probe".
/// 60 s matches the busy-queue ceiling and is long enough that a transient
/// routing-table hole does not reclaim every receiver.
pub const GATEWAY_FALLBACK_WINDOW: Duration = Duration::from_secs(60);

/// The tracker's re-probe backoff after a confirmed `BusyElsewhere` verdict:
/// 3 s, 6 s, 12 s, 24 s, then 60 s. It is a queue, never a failure retry, and
/// a busy receiver is never reclaimed for being busy (Q34 §四). The engine does
/// not wait it out: under `receiver_ownership_guard` the member yields on the
/// same verdict and suspends its tracker, which drops the scheduled probe. The growth factor and the cap are our
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
/// 60 s.
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

/// Consecutive takeovers that cause the standalone counter to yield.
pub const TAKEOVER_LIMIT: u32 = 3;

/// Maximum time after a successful reclaim for a takeover to count in the same run.
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
    /// The established feedback connection stopped answering. Ownership is unknown.
    FeedbackLost,
    /// PTP stopped beyond its grace period; verify ownership before rebuilding.
    ClockLost,
}

impl Signal {
    pub fn name(self) -> &'static str {
        match self {
            Self::UnexpectedTeardown => "unexpected_teardown",
            Self::ConnectionClose => "connection_close",
            Self::FeedbackLost => "feedback_lost",
            Self::ClockLost => "clock_lost",
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
    method
        .filter(|method| is_teardown_request(method))
        .map(|_| Signal::UnexpectedTeardown)
}

/// Result of one TCP connect to the receiver's control port. `Refused` and
/// `Dead` lead to the same ownership verdict while remaining distinct evidence
/// in diagnostics.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ReceiverLiveness {
    /// `TcpStream::connect_timeout` succeeded: the receiver is alive.
    Alive,
    /// The connection was refused. A host answered, but the control port did
    /// not accept the connection needed to read `statusFlags`.
    Refused,
    /// The connect timed out or the host or network was reported unreachable.
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
    /// Every receiver of the group failed in the same round **and the gateway
    /// reference did not answer**, so the common cause is our own network rather
    /// than N devices dying at once (Q42 + Q46). The session loop never sends
    /// this today (the group-wide rule is not wired; see the module docs). This is not evidence about this
    /// receiver: the verdict is `Unknown`, the escalation counter is neither
    /// advanced nor reset. The caller re-probes after [`LIVENESS_RETRY`] to
    /// re-evaluate the reference — a recovered gateway must end the suppression
    /// (and, with no gateway at all, the fallback clock has to keep ticking).
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

/// Q42's group-wide premise: in a multi-receiver session, all failing probes at
/// once is a symptom of our own side, not of several devices dying in the same
/// instant.
///
/// It is the one observation that needs no errno knowledge. It deliberately
/// requires evidence about **every** receiver: a single receiver failing is that
/// receiver's problem, and a member that was not probed this round leaves the
/// round inconclusive (its own verdict then stands). A `Refused` probe proves
/// the host answered, so an all-refused round is *not* a local network failure —
/// the devices are alive and simply not serving.
///
/// Q46 adds the control group this observation was missing: see
/// [`judge_all_failed`].
pub fn all_receivers_failed(probes: &[ReceiverLiveness], receivers: usize) -> bool {
    receivers > 1
        && probes.len() == receivers
        && probes.iter().all(|probed| !probed.host_answered())
}

/// Whether the reference host (the default gateway) answered.
///
/// Q46's control group is deliberately about *answering*, not about serving
/// anything: a completed TCP handshake and an RST both prove the gateway's
/// network stack is reachable from this machine, which is exactly the
/// independent evidence the all-failed round needs. A gateway probe is never
/// turned into a [`ReceiverLiveness`] and never reaches a [`Tracker`]: "the
/// reference did not answer" says something about our network, not about a
/// receiver.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GatewayReachability {
    /// The gateway answered (a completed connect or a returned RST).
    Answered,
    /// Nothing answered on any probed port: either the gateway is off or our
    /// own interface/route is. Both are "our network", never a dead receiver.
    Silent,
}

impl GatewayReachability {
    pub fn name(self) -> &'static str {
        match self {
            Self::Answered => "reachable",
            Self::Silent => "unreachable",
        }
    }
}

/// The verdict of the test-only gateway model for one full polling round.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AllFailed {
    /// Not every receiver of a multi-receiver session failed this round: each
    /// receiver's own verdict stands.
    NotAllFailed,
    /// Every receiver failed and the gateway answered. The model leaves the
    /// per-receiver `Dead` verdicts in place.
    DevicesGone,
    /// Every receiver failed and the gateway did not answer. The model treats
    /// a local network issue as possible and suppresses the `Dead` verdicts.
    LocalNetwork,
    /// Every receiver failed and there is no default route to probe. Suppress,
    /// but only until [`GATEWAY_FALLBACK_WINDOW`] elapses (Q46's last resort).
    LocalNetworkUnproven,
    /// Every receiver failed, no default route was discoverable, and the
    /// fallback window elapsed: stop suppressing so a genuine power-off cannot
    /// keep a stream alive forever.
    FallbackExpired,
}

impl AllFailed {
    /// Whether the group-wide rule suppresses the per-receiver `Dead` verdicts.
    pub fn suppress(self) -> bool {
        matches!(self, Self::LocalNetwork | Self::LocalNetworkUnproven)
    }

    pub fn name(self) -> &'static str {
        match self {
            Self::NotAllFailed => "not_all_failed",
            Self::DevicesGone => "devices_gone",
            Self::LocalNetwork => "local_network",
            Self::LocalNetworkUnproven => "local_network_unproven",
            Self::FallbackExpired => "fallback_expired",
        }
    }
}

/// Q46's judgement with a control group. When every receiver failed, the same
/// round probes the default gateway, which shares our local path but not the
/// receivers' power state:
///
/// ```text
/// all receivers failed + gateway answered  => DevicesGone       (no suppression)
/// all receivers failed + gateway silent    => LocalNetwork     (suppress)
/// all receivers failed + no default route  => LocalNetworkUnproven
///                                            until GATEWAY_FALLBACK_WINDOW,
///                                            then FallbackExpired (no suppression)
/// ```
///
/// `all_failed_for` is how long the current all-failed episode has lasted. It is
/// only consulted when no gateway could be discovered; when the gateway answers,
/// time is irrelevant — the reference is the judgement, the clock is the
/// fallback.
///
/// Not called by the session loop; see the module docs for why.
pub fn judge_all_failed(
    probes: &[ReceiverLiveness],
    receivers: usize,
    gateway: Option<GatewayReachability>,
    all_failed_for: Option<Duration>,
) -> AllFailed {
    if !all_receivers_failed(probes, receivers) {
        return AllFailed::NotAllFailed;
    }
    match gateway {
        Some(GatewayReachability::Answered) => AllFailed::DevicesGone,
        Some(GatewayReachability::Silent) => AllFailed::LocalNetwork,
        None if all_failed_for.is_some_and(|held| held >= GATEWAY_FALLBACK_WINDOW) => {
            AllFailed::FallbackExpired
        }
        None => AllFailed::LocalNetworkUnproven,
    }
}

/// The default route's next hop (IPv4), read from the OS routing table — no
/// configuration knob, no guessed `192.168.1.1`.
///
/// Q46 suggests `GetAdaptersAddresses(FirstGatewayAddress)` or
/// `GetIpForwardTable`. The engine does not enable the `windows` crate's
/// `Win32_NetworkManagement_IpHelper` feature, so this calls the documented
/// `GetIpForwardTable` export of `iphlpapi.dll` directly: one syscall, and it
/// answers exactly "which next hop would Windows use for a destination that is
/// not on-link". It is the same interface the receivers are reached over, which
/// is what makes it a control group rather than a second guess.
///
/// Returns `None` when there is no IPv4 default route at all (Wi-Fi
/// disconnected, IPv6-only, IPv4 stack disabled, or a non-Windows build).
/// Q46's time-boxed fallback then applies — it never invents a default.
///
/// The table is caller-allocated: the null-buffer call only reports the required
/// size, and the second call fills our buffer, so it is released by dropping the
/// `Vec`. (`FreeIpForwardTable` exists for buffers the API allocated itself; it
/// is deliberately not used here.)
#[cfg(windows)]
pub fn default_gateway() -> Option<IpAddr> {
    // `MIB_IPFORWARDROW` (ipmib.h): fourteen DWORDs. IPv4 addresses are stored
    // in network byte order, i.e. their in-memory bytes are already the octets.
    const ROW_BYTES: usize = 14 * 4;
    const DEST: usize = 0;
    const MASK: usize = 4;
    const NEXT_HOP: usize = 12;
    const METRIC: usize = 36;
    const NO_ERROR: u32 = 0;
    const ERROR_INSUFFICIENT_BUFFER: u32 = 122;

    #[link(name = "iphlpapi")]
    unsafe extern "system" {
        fn GetIpForwardTable(table: *mut u8, size: *mut u32, order: i32) -> u32;
    }

    let mut size = 0u32;
    // SAFETY: the null buffer + size out-parameter is the documented sizing
    // call; the API writes nothing else.
    let status = unsafe { GetIpForwardTable(std::ptr::null_mut(), &mut size, 0) };
    if status != ERROR_INSUFFICIENT_BUFFER || size < 4 {
        return None;
    }
    let mut buffer = vec![0u8; size as usize];
    // SAFETY: `buffer` is at least `size` bytes, which the sizing call reported
    // as enough for the whole table.
    let status = unsafe { GetIpForwardTable(buffer.as_mut_ptr(), &mut size, 0) };
    if status != NO_ERROR {
        return None;
    }
    let rows = u32::from_ne_bytes([buffer[0], buffer[1], buffer[2], buffer[3]]) as usize;
    let fits = (buffer.len() - 4) / ROW_BYTES;
    let mut best: Option<(u32, [u8; 4])> = None;
    for index in 0..rows.min(fits) {
        let row = &buffer[4 + index * ROW_BYTES..4 + (index + 1) * ROW_BYTES];
        let is_zero = |field: usize| row[field..field + 4].iter().all(|byte| *byte == 0);
        // The default route is destination 0.0.0.0/0. A zero next hop is an
        // on-link route, not a gateway.
        if !is_zero(DEST) || !is_zero(MASK) || is_zero(NEXT_HOP) {
            continue;
        }
        let metric = u32::from_ne_bytes([
            row[METRIC],
            row[METRIC + 1],
            row[METRIC + 2],
            row[METRIC + 3],
        ]);
        let next_hop = [
            row[NEXT_HOP],
            row[NEXT_HOP + 1],
            row[NEXT_HOP + 2],
            row[NEXT_HOP + 3],
        ];
        // Several default routes only happen on a multi-homed machine; the one
        // with the lowest route metric is the one the OS prefers.
        if best.is_none_or(|(best_metric, _)| metric < best_metric) {
            best = Some((metric, next_hop));
        }
    }
    best.map(|(_, octets)| IpAddr::V4(std::net::Ipv4Addr::from(octets)))
}

/// No routing-table lookup outside Windows: the engine only runs on Windows,
/// and inventing a default gateway here would be the fake the rule must never
/// use. Q46's time-boxed fallback applies instead.
#[cfg(not(windows))]
pub fn default_gateway() -> Option<IpAddr> {
    None
}

/// Probe the reference host on the given ports: the first answer wins, and only
/// "nothing answered at all" is [`GatewayReachability::Silent`]. A refused
/// connection is an answer (the RST proves the stack is there), which is why
/// this reuses the same [`ReceiverLiveness::host_answered`] dimension as the
/// receiver probe instead of a boolean "connected".
pub fn reference_reachability(ip: IpAddr, ports: &[u16], timeout: Duration) -> GatewayReachability {
    for port in ports {
        if receiver_liveness(ip, *port, timeout).host_answered() {
            return GatewayReachability::Answered;
        }
    }
    GatewayReachability::Silent
}

/// Discover the default gateway and probe it once. `None` means there is no
/// default route to use as a reference (Q46's fallback then applies); it is
/// deliberately not the same value as a silent gateway.
pub fn probe_default_gateway(timeout: Duration) -> Option<GatewayReachability> {
    default_gateway().map(|gateway| reference_reachability(gateway, &GATEWAY_PROBE_PORTS, timeout))
}

/// What a live `GET /info` probe could tell us.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProbeOutcome {
    /// No active session on the receiver ([`RECEIVER_SESSION_ACTIVE`] is clear),
    /// whatever the other bits say.
    Free,
    /// The association is treated as ours based on the active bit and the
    /// caller's local context, or on healthy media and non-terminal liveness.
    OursActive,
    /// The receiver reports an active session while the caller does not hold an
    /// association. A first verdict schedules a confirmation probe.
    BusyElsewhere,
    /// The probe could not classify ownership, for example because `statusFlags`
    /// was missing or invalid.
    Unknown,
    /// The receiver's control port was unreachable or refused the connection.
    /// The diagnostic line keeps `dead` and `refused` distinct.
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
/// `we_hold_session` is the caller's local association context. A live member
/// clears it when the receiver ended the association or ownership became
/// uncertain. The automatic-start preflight passes `false` because it has no
/// association yet. The session bit alone cannot distinguish senders.
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
/// (!media healthy, Alive or Unknown) => consult statusFlags
/// ```
///
/// The status-flags consultation is [`classify`] (and `Unknown` when the field
/// is missing). `Dead` and `Refused` win over the flags in every case so they can
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
    info.as_dictionary()?
        .get("statusFlags")?
        .as_unsigned_integer()
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
    /// Historical label for the free, gone, or unreadable path. The member
    /// yields `peer_closed` only when the confirmation itself read `Free`, and
    /// `ownership_unconfirmed` otherwise.
    Reclaim,
    /// Historical label for the settled `BusyElsewhere` path. The tracker can
    /// schedule another probe, which the member discards when it yields.
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

/// Classify a completed probe or a failed probe. An error takes the historical
/// [`ProbeAction::Reclaim`] branch, which yields `ownership_unconfirmed` in the
/// session because no successful `Free` result was read.
pub fn action_for_result(result: &Result<ProbeReport>) -> ProbeAction {
    match result {
        Ok(report) => action_for(report.outcome),
        Err(_) => ProbeAction::Reclaim,
    }
}

/// Adjudicate a state-changing first verdict against its confirmation probe.
///
/// A failed or `Unknown` second probe leaves the first tracker verdict in
/// place. The session still requires the second probe itself to read `Free`
/// before allowing automatic recovery. Two different usable outcomes return a
/// disagreement, which the session turns into `ownership_unconfirmed`.
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
    let mut connection = Connection::connect(addr, cancel.clone())
        .with_context(|| format!("status probe {addr}"))?;
    connection.set_timeout(timeout)?;
    let message = connection
        .request("GET", "/info", &[], &[])
        .context("status probe GET /info")?;
    let info = message.plist().context("status probe /info plist")?;
    Ok(report_from_info(&info, we_hold_session))
}

/// Probe the receiver's live status on a fresh RTSP connection.
///
/// `we_hold_session` is the caller's local association context used by
/// [`classify`]. The probe only reads the receiver's status.
///
/// The caller's existing control connection is deliberately not used: after a
/// takeover it belongs to a session the receiver already ended, and encrypted
/// RTSP cannot be re-keyed mid-session (Q16). Any TCP/RTSP/plist error or read
/// timeout is returned as `Err` for the caller to classify.
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

/// The `reason` attached to a `preempted` fault.
///
/// The guarded engine yields on the first confirmed takeover. The cycle limit
/// reason is available to callers of the standalone takeover counter.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ReclaimReason {
    /// Another controller took the receiver over: the member yields and the
    /// engine does not reconnect automatically.
    Takeover,
    /// The standalone counter reached its consecutive takeover limit.
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
/// instance per receiver. The guarded engine does not consult this counter.
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
        self.counters
            .get(&host)
            .map_or(0, TakeoverCounter::consecutive)
    }
}

/// A reclaim identifier for library callers managing their own playback.
/// The guarded engine yields without using this error to retry in-session.
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
    /// The tracker settled on `Free` and [`ProbeAction::Reclaim`]. The session
    /// uses the actual confirmation result to decide whether the fault is
    /// retryable.
    pub takeover: bool,
}

/// Per-member evidence and probe scheduling. Only the session thread drives it
/// (the probes themselves run on a background worker so the audio loop never
/// waits for a connect); feedback workers merely append [`Signal`]s to `Health`.
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
    /// Yielding drops scheduled probes. Signals cannot re-arm one. Library
    /// callers can explicitly trigger a new probe. The guarded engine builds
    /// a fresh tracker with each new member.
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
    /// An explicit receiver TEARDOWN ends this association. Only a new Member
    /// can establish another one; local UDP writes cannot renew ownership.
    pub fn session_ended(&self) -> bool {
        self.ended_at.is_some()
    }
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
        // evidence stays recorded for the log, nothing resumes it.
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

    /// Signals collected since the last call (at most the newest eight), for
    /// the one evidence log line that carries
    /// `policy`/`rtp_healthy`/`packets_prepared`/`tcp_reachable`. The session
    /// drains it when it starts a probe, so signals that arrive while a
    /// confirmation or retry is pending ride on that probe's evidence line.
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

    /// Q42's timer needs repeated samples. After an undecided (`Unknown`) or
    /// locally suppressed round ask for the next liveness probe at
    /// [`LIVENESS_RETRY`]; otherwise a single sample would be the only one this
    /// tracker ever sees (the next probe is otherwise only scheduled by fresh
    /// control-plane evidence, and a dead feedback worker produces no more), so
    /// the patience limit could never be reached. It never overrides a busy
    /// queue or the terminal yielded state, and it is bounded: the fifth
    /// unknown escalates to `Dead`.
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

    /// Enter the terminal yielded state. The session calls this on every yield
    /// (`preempted`, `ownership_unconfirmed` or `peer_closed`): every queued
    /// probe is dropped and signals cannot re-arm it. The guarded engine
    /// starts another tracker in a new playback run. Automatic starts must
    /// first pass the free-receiver preflight.
    pub fn suspend(&mut self) {
        self.suspended = true;
        self.pending = None;
        self.next_probe = None;
    }

    /// Whether the tracker is in the terminal yielded state.
    pub fn suspended(&self) -> bool {
        self.suspended
    }

    /// Re-arm one immediate probe and reset queue backoff after an explicit
    /// request from a library caller. Returns false if the tracker is active.
    /// The guarded engine starts a fresh tracker for each playback attempt.
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
    /// A first `Free` or `BusyElsewhere` verdict schedules a confirmation
    /// probe after [`CONFIRM_DELAY`]. `OursActive`, `Unknown`, `Gone`, and errors
    /// settle immediately. The tracker schedules a confirmed `BusyElsewhere`
    /// re-probe after the next [`REPROBE_BACKOFF`] interval, which the session
    /// drops when it yields.
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
            // `OursActive`, `Unknown`, `Gone`, and errors settle without a
            // confirmation. The session applies the resulting action.
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
        assert_eq!(combine(false, L::Alive, Some(0), false), ProbeOutcome::Free);
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
        assert_eq!(combine(false, L::Alive, None, false), ProbeOutcome::Unknown);
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
                    let outcome =
                        combine(media_healthy, liveness, Some(RECEIVER_SESSION_ACTIVE), hold);
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
        for step in 1..LIVENESS_PATIENCE {
            assert_eq!(
                liveness.observe(ReceiverLiveness::Unknown, at(base, u64::from(step) * 1_000)),
                Verdict::Unknown,
                "failure {step} is still only 'temporarily unknown'"
            );
            assert_eq!(liveness.consecutive_failures(), step);
        }
        assert_eq!(
            liveness.observe(
                ReceiverLiveness::Unknown,
                at(base, u64::from(LIVENESS_PATIENCE) * 1_000)
            ),
            Verdict::Dead,
            "the patience limit is LIVENESS_PATIENCE consecutive unknowns"
        );
        assert_eq!(liveness.consecutive_failures(), LIVENESS_PATIENCE);
        // One successful probe resets the counter and the clock...
        let alive = u64::from(LIVENESS_PATIENCE + 1) * 1_000;
        assert_eq!(
            liveness.observe(ReceiverLiveness::Alive, at(base, alive)),
            Verdict::Alive
        );
        assert_eq!(liveness.consecutive_failures(), 0);
        assert_eq!(liveness.last_ok(), at(base, alive));
        // ...so the chain has to start over.
        for extra in 1..LIVENESS_PATIENCE {
            assert_eq!(
                liveness.observe(
                    ReceiverLiveness::Unknown,
                    at(base, alive + u64::from(extra) * 1_000)
                ),
                Verdict::Unknown
            );
        }
        assert_eq!(
            liveness.observe(
                ReceiverLiveness::Unknown,
                at(base, alive + u64::from(LIVENESS_PATIENCE) * 1_000)
            ),
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
    fn the_all_failed_rule_needs_the_gateway_reference() {
        use GatewayReachability as G;
        use ReceiverLiveness as L;
        // Every receiver produced no evidence of life.
        let all_failed = [L::Dead, L::Unknown];
        assert!(all_receivers_failed(&all_failed, 2));
        assert!(all_receivers_failed(&[L::Dead, L::Dead, L::Unknown], 3));
        // One alive receiver proves the local network works: the failure
        // belongs to the device that failed.
        assert!(!all_receivers_failed(&[L::Dead, L::Alive], 2));
        // A returned RST proves the host answered, so an all-refused round is
        // not a local routing failure.
        assert!(!all_receivers_failed(&[L::Refused, L::Refused], 2));
        assert!(!all_receivers_failed(&[L::Refused, L::Dead], 2));
        // A single receiver can never be "all failed": that is its own problem.
        assert!(!all_receivers_failed(&[L::Dead], 1));
        // A member that was not probed this round leaves the round inconclusive.
        assert!(!all_receivers_failed(&[L::Dead], 2));
        assert!(!all_receivers_failed(&[], 2));

        // Q46: the gateway decides what "all failed" means.
        assert_eq!(
            judge_all_failed(&all_failed, 2, Some(G::Answered), None),
            AllFailed::DevicesGone,
            "a reachable gateway + all receivers failed = the devices really are gone"
        );
        assert!(!AllFailed::DevicesGone.suppress());
        assert_eq!(
            judge_all_failed(&all_failed, 2, Some(G::Silent), None),
            AllFailed::LocalNetwork,
            "a silent gateway + all receivers failed = our own network"
        );
        assert!(AllFailed::LocalNetwork.suppress());
        // No reference at all: Q46's fallback is time-boxed, not a guess.
        assert_eq!(
            judge_all_failed(&all_failed, 2, None, Some(Duration::from_secs(1))),
            AllFailed::LocalNetworkUnproven
        );
        assert!(AllFailed::LocalNetworkUnproven.suppress());
        assert_eq!(
            judge_all_failed(
                &all_failed,
                2,
                None,
                Some(GATEWAY_FALLBACK_WINDOW - Duration::from_millis(1))
            ),
            AllFailed::LocalNetworkUnproven,
            "the fallback window has not elapsed yet"
        );
        assert_eq!(
            judge_all_failed(&all_failed, 2, None, Some(GATEWAY_FALLBACK_WINDOW)),
            AllFailed::FallbackExpired,
            "no reference and the window elapsed: suppression must end"
        );
        assert!(!AllFailed::FallbackExpired.suppress());
        // The rule never fires when it should not, whatever the gateway said.
        assert_eq!(
            judge_all_failed(&[L::Dead, L::Alive], 2, Some(G::Silent), None),
            AllFailed::NotAllFailed,
            "one reachable receiver proves the network path to the group"
        );
        assert_eq!(
            judge_all_failed(&[L::Dead], 1, Some(G::Silent), None),
            AllFailed::NotAllFailed
        );
        assert_eq!(
            judge_all_failed(&[L::Dead], 2, Some(G::Silent), None),
            AllFailed::NotAllFailed,
            "a partially probed round is inconclusive"
        );
        // Two receivers failing a *refused* probe are alive; the gateway state
        // is irrelevant because the round is not "all failed" at all.
        assert_eq!(
            judge_all_failed(&[L::Refused, L::Dead], 2, Some(G::Silent), None),
            AllFailed::NotAllFailed
        );
        assert_eq!(AllFailed::NotAllFailed.name(), "not_all_failed");
        assert_eq!(AllFailed::DevicesGone.name(), "devices_gone");
        assert_eq!(AllFailed::LocalNetwork.name(), "local_network");
        assert_eq!(
            AllFailed::LocalNetworkUnproven.name(),
            "local_network_unproven"
        );
        assert_eq!(AllFailed::FallbackExpired.name(), "fallback_expired");
        assert_eq!(GatewayReachability::Answered.name(), "reachable");
        assert_eq!(GatewayReachability::Silent.name(), "unreachable");
    }

    #[test]
    fn a_gateway_probe_is_judged_by_answering_not_by_serving() {
        // A listening port answers (the connect completes)...
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        assert_eq!(
            reference_reachability(address.ip(), &[address.port()], PROBE_TIMEOUT),
            GatewayReachability::Answered
        );
        let (socket, _) = listener.accept().unwrap();
        drop(socket);
        drop(listener);
        // ...and a closed port still answers (the RST proves the stack is
        // there), which is what makes "refused" usable as a reference.
        assert_eq!(
            reference_reachability(address.ip(), &[address.port()], Duration::from_secs(10)),
            GatewayReachability::Answered
        );
        // A silent address answers on no port: only then is the reference
        // "unreachable". TEST-NET-1 is never routed, so this is a timeout on
        // every stack that honours its routing table.
        assert_eq!(
            reference_reachability(
                "192.0.2.1".parse().unwrap(),
                &[9, 10],
                Duration::from_millis(200)
            ),
            GatewayReachability::Silent
        );
        // The discovery itself never invents a gateway: it is either a real
        // next hop from the routing table or nothing at all.
        if let Some(gateway) = default_gateway() {
            assert!(gateway.is_ipv4(), "the IPv4 table yields an IPv4 hop");
            assert!(!gateway.is_unspecified());
            assert!(!gateway.is_loopback());
        }
    }

    #[test]
    fn a_local_suspect_round_neither_counts_nor_resets_the_device_failures() {
        let base = Instant::now();
        let mut tracker = Tracker::default();
        assert_eq!(
            tracker
                .observe_liveness(
                    LivenessSample::Probe(ReceiverLiveness::Unknown),
                    at(base, 0)
                )
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
        // The device's own chain still needs its remaining failures.
        for step in 2..LIVENESS_PATIENCE {
            assert_eq!(
                tracker
                    .observe_liveness(
                        LivenessSample::Probe(ReceiverLiveness::Unknown),
                        at(base, u64::from(step) * 1_000)
                    )
                    .verdict,
                Verdict::Unknown,
                "failure {step} is below the patience limit"
            );
        }
        assert_eq!(
            tracker
                .observe_liveness(
                    LivenessSample::Probe(ReceiverLiveness::Unknown),
                    at(base, u64::from(LIVENESS_PATIENCE) * 1_000)
                )
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
                .observe_liveness(
                    LivenessSample::Probe(ReceiverLiveness::Alive),
                    at(base, 12_000)
                )
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
        assert!(!tracker.due(at(base, 0) + CONFIRM_DELAY - Duration::from_millis(1)));

        let second = tracker.note_result(&free(), at(base, 0) + CONFIRM_DELAY);
        assert_eq!(
            second.action,
            ProbeAction::Reclaim,
            "two agreeing Free probes reclaim"
        );
        assert!(second.takeover, "a confirmed Free settles as a takeover");
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
        // Yielding drops every queued probe. A library caller can explicitly
        // re-arm the tracker. The guarded runtime instead creates a new one.
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
        assert!(tracker.suspended());

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
            tracker.note_result(&busy(), at(base, 4_000)).log_changed,
            "a different outcome is logged"
        );
        // The busy count is irrelevant to the log dedup.
        assert!(
            !tracker
                .note_result(&busy(), at(base, 4_000) + CONFIRM_DELAY)
                .log_changed
        );
        assert!(
            tracker.note_result(&free(), at(base, 8_000)).log_changed,
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
