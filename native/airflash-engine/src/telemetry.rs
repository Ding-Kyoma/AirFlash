//! Counters and histograms that explain a session without a debugger.
//!
//! The engine already reports per-second `capture_metrics` and `transport_metrics`.
//! What was missing (roadmap P0-5) is the ability to answer "why did this session
//! stutter": lateness distribution, capture age, retransmission gaps, an explicit
//! underflow classification, timeline recovery counters and a session epoch.
use serde::Serialize;
use serde_json::{Value, json};
use std::{
    collections::VecDeque,
    io::Write,
    path::{Path, PathBuf},
    sync::{
        Mutex, OnceLock,
        atomic::{AtomicU64, Ordering},
    },
    time::{Duration, Instant},
};

/// Log-spaced buckets covering 0.05 ms .. 200 ms, which brackets everything a
/// scheduler, the network or the receiver can do to a packet.
pub const BUCKETS: usize = 64;
const LOW_MS: f64 = 0.05;
const HIGH_MS: f64 = 200.0;

#[derive(Clone)]
pub struct Histogram {
    buckets: [u32; BUCKETS],
    count: u64,
    below: u64,
    above: u64,
    sum_ms: f64,
    max_ms: f64,
}
impl Default for Histogram {
    fn default() -> Self {
        Self {
            buckets: [0; BUCKETS],
            count: 0,
            below: 0,
            above: 0,
            sum_ms: 0.0,
            max_ms: 0.0,
        }
    }
}
impl Histogram {
    fn index(ms: f64) -> usize {
        let t = (ms / LOW_MS).ln() / (HIGH_MS / LOW_MS).ln();
        ((t * BUCKETS as f64) as usize).min(BUCKETS - 1)
    }
    fn upper_ms(bucket: usize) -> f64 {
        LOW_MS * (HIGH_MS / LOW_MS).powf((bucket + 1) as f64 / BUCKETS as f64)
    }
    /// Negative or non-finite samples are clamped into the first bucket; callers
    /// that need a sign keep their own counters.
    pub fn observe(&mut self, ms: f64) {
        self.count += 1;
        let ms = if ms.is_finite() { ms.max(0.0) } else { 0.0 };
        self.sum_ms += ms;
        self.max_ms = self.max_ms.max(ms);
        if ms < LOW_MS {
            self.below += 1;
        } else if ms > HIGH_MS {
            self.above += 1;
            self.buckets[BUCKETS - 1] += 1;
        } else {
            self.buckets[Self::index(ms)] += 1;
        }
    }
    pub fn count(&self) -> u64 {
        self.count
    }
    pub fn mean_ms(&self) -> f64 {
        if self.count == 0 {
            0.0
        } else {
            self.sum_ms / self.count as f64
        }
    }
    /// Conservative quantile: the upper edge of the bucket the sample falls in,
    /// so the answer never under-reports lateness. Bucket granularity is about
    /// 13.6% (4000x range over 64 buckets). `fraction >= 1` returns the true max.
    pub fn percentile_ms(&self, fraction: f64) -> f64 {
        if self.count == 0 {
            return 0.0;
        }
        if fraction >= 1.0 {
            return self.max_ms;
        }
        if self.below as f64 / self.count as f64 >= fraction {
            return LOW_MS;
        }
        let target = (fraction * self.count as f64).ceil() as u64;
        let mut seen = self.below;
        for (bucket, hits) in self.buckets.iter().enumerate() {
            seen += u64::from(*hits);
            if seen >= target {
                return Self::upper_ms(bucket);
            }
        }
        self.max_ms
    }
    pub fn snapshot(&self) -> Value {
        json!({
            "count": self.count,
            "mean_ms": round3(self.mean_ms()),
            "p50_ms": round3(self.percentile_ms(0.50)),
            "p95_ms": round3(self.percentile_ms(0.95)),
            "p99_ms": round3(self.percentile_ms(0.99)),
            "max_ms": round3(self.max_ms),
            "below_floor": self.below,
            "above_ceiling": self.above,
            "buckets": self.buckets.to_vec(),
        })
    }
}

fn round3(value: f64) -> f64 {
    (value * 1000.0).round() / 1000.0
}

/// Why a packet had to be padded with silence.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum UnderflowCause {
    /// A packet had to be padded while real media was expected. These are the
    /// underruns that need musical concealment (decay instead of a hard cut).
    ActiveMedia,
    /// The source is genuinely idle (standby/keepalive), so plain silence is the
    /// correct output and must not be treated as a failure.
    IdleKeepalive,
}

/// Why the timeline was reset.
///
/// The reference implementation logs `[TIMELINE] reset reason=<reason>` and
/// exposes the last one as the `last_timeline_reset_reason` diagnostic field; its
/// nine names are the first nine below. AirFlash adds two of its own (Q43):
/// `source_restart` for a reopened capture source and `sender_stall` for a local
/// deadline miss that a successful re-anchor absorbed. They are also the CSV
/// column suffixes, so renaming a variant is a format change.
///
/// Only [`TimelineResetReason::ConfirmedTransportFailure`] is a drift signal.
/// `track_change`, `user_seek`, `flush` and `silence_resume` are neutral or
/// user-initiated, `latency_change` is our own lead adjustment, and
/// `receiver_rejected_flush` / `session_restart` / `source_restart` are their own
/// diagnoses. `sender_stall` is local (the sender thread missed a deadline), so it
/// must never be folded into the confirmed bucket (Q43 §三).
///
/// `start` keeps the reference implementation's "session start" meaning and is
/// deliberately never recorded: the first timeline of a row is its baseline, so a
/// normal stop stays all-zero (Q43 §四). It stays in the enum as a tripwire.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum TimelineResetReason {
    /// A fresh playback timeline was established for a new start, not a repair.
    Start,
    TrackChange,
    UserSeek,
    ReceiverRejectedFlush,
    SessionRestart,
    /// The only reliable drift signal: delivery was confirmed to have failed.
    ConfirmedTransportFailure,
    SilenceResume,
    /// Our own adaptive lead adjustment; must stay distinguishable from a fault.
    LatencyChange,
    Flush,
    /// A capture source ended and was reopened: a new timeline for the same run.
    SourceRestart,
    /// The sender thread missed its deadline with clean RTP counters; a local
    /// scheduling problem, not proof that the network failed.
    SenderStall,
}

impl TimelineResetReason {
    /// Declaration order, which is also the CSV column order.
    pub const ALL: [Self; 11] = [
        Self::Start,
        Self::TrackChange,
        Self::UserSeek,
        Self::ReceiverRejectedFlush,
        Self::SessionRestart,
        Self::ConfirmedTransportFailure,
        Self::SilenceResume,
        Self::LatencyChange,
        Self::Flush,
        Self::SourceRestart,
        Self::SenderStall,
    ];

    /// The wire/CSV label, identical to the reference enum's spelling.
    pub const fn name(self) -> &'static str {
        match self {
            Self::Start => "start",
            Self::TrackChange => "track_change",
            Self::UserSeek => "user_seek",
            Self::ReceiverRejectedFlush => "receiver_rejected_flush",
            Self::SessionRestart => "session_restart",
            Self::ConfirmedTransportFailure => "confirmed_transport_failure",
            Self::SilenceResume => "silence_resume",
            Self::LatencyChange => "latency_change",
            Self::Flush => "flush",
            Self::SourceRestart => "source_restart",
            Self::SenderStall => "sender_stall",
        }
    }
}

/// Timeline resets by cause. There is deliberately no bare total: a single number
/// would make a user pressing "next" or our own lead change look like drift.
#[derive(Default, Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub struct TimelineResets {
    pub start: u32,
    pub track_change: u32,
    pub user_seek: u32,
    pub receiver_rejected_flush: u32,
    pub session_restart: u32,
    pub confirmed_transport_failure: u32,
    pub silence_resume: u32,
    pub latency_change: u32,
    pub flush: u32,
    pub source_restart: u32,
    pub sender_stall: u32,
}

impl TimelineResets {
    pub fn record(&mut self, reason: TimelineResetReason) {
        let slot = match reason {
            TimelineResetReason::Start => &mut self.start,
            TimelineResetReason::TrackChange => &mut self.track_change,
            TimelineResetReason::UserSeek => &mut self.user_seek,
            TimelineResetReason::ReceiverRejectedFlush => &mut self.receiver_rejected_flush,
            TimelineResetReason::SessionRestart => &mut self.session_restart,
            TimelineResetReason::ConfirmedTransportFailure => {
                &mut self.confirmed_transport_failure
            }
            TimelineResetReason::SilenceResume => &mut self.silence_resume,
            TimelineResetReason::LatencyChange => &mut self.latency_change,
            TimelineResetReason::Flush => &mut self.flush,
            TimelineResetReason::SourceRestart => &mut self.source_restart,
            TimelineResetReason::SenderStall => &mut self.sender_stall,
        };
        *slot = slot.saturating_add(1);
    }

    pub const fn field(&self, reason: TimelineResetReason) -> u32 {
        match reason {
            TimelineResetReason::Start => self.start,
            TimelineResetReason::TrackChange => self.track_change,
            TimelineResetReason::UserSeek => self.user_seek,
            TimelineResetReason::ReceiverRejectedFlush => self.receiver_rejected_flush,
            TimelineResetReason::SessionRestart => self.session_restart,
            TimelineResetReason::ConfirmedTransportFailure => self.confirmed_transport_failure,
            TimelineResetReason::SilenceResume => self.silence_resume,
            TimelineResetReason::LatencyChange => self.latency_change,
            TimelineResetReason::Flush => self.flush,
            TimelineResetReason::SourceRestart => self.source_restart,
            TimelineResetReason::SenderStall => self.sender_stall,
        }
    }

    pub const fn total(&self) -> u32 {
        self.start
            + self.track_change
            + self.user_seek
            + self.receiver_rejected_flush
            + self.session_restart
            + self.confirmed_transport_failure
            + self.silence_resume
            + self.latency_change
            + self.flush
            + self.source_restart
            + self.sender_stall
    }
}

/// A lead shift of at least this many microseconds is a timeline reset. Smaller
/// adjustments only move the lead-shift counters (Q44 §二).
///
/// The number is the length of AirFlash's own continuity splice ramp:
/// `continuity::Splice` ramps over `rate / 200` frames, about 5 ms (5.0 ms at
/// 48 kHz, 4.99 ms at 44.1 kHz). A shift the ramp can cover is inaudible, so it
/// is activity rather than a timeline event.
/// It is deliberately not sub-millisecond: the IPC lead request is whole
/// milliseconds, so a 1 ms threshold always fires and makes `lead_shift_count`
/// and `latency_change` the same number. Microsecond precision is kept as an
/// internal invariant, not as a product capability.
pub const LEAD_SHIFT_RESET_US: u64 = 5_000;

/// The rate-ratio inputs for one successful re-anchor (Q44 §一).
///
/// A binary "did anything move" test turns one incidental Wi-Fi loss into a
/// transport fault. Comparing against the sender's own steady state instead makes
/// a link that always loses a couple of packets per second normal for that link,
/// and only a spike above it a fault.
///
/// Retransmit requests are a weaker signal than underruns: a retransmitted packet
/// was lost but probably recovered, while an underrun means the playout buffer ran
/// dry and the listener heard it. So one underrun is enough evidence, while
/// retransmits also need an absolute minimum.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RtpBaseline {
    /// Steady-state retransmit-request rate over the long window, events/second.
    pub steady_rtx_per_s: f64,
    /// Steady-state concealed-underflow rate over the long window, events/second.
    pub steady_underrun_per_s: f64,
    /// Actual retransmit-request increment inside the pre-stall window.
    pub pre_stall_rtx: u32,
    /// Actual concealed-underflow increment inside the pre-stall window.
    pub pre_stall_underrun: u32,
}

impl RtpBaseline {
    /// How far above the steady baseline counts as transport degradation.
    ///
    /// Three times is a starting point, not a constant to defend: once there are
    /// enough session-summary rows, calibrate it from the observed distribution
    /// (Q44 §一).
    pub const RATIO_THRESHOLD: f64 = 3.0;
    /// Floor for the denominator, in events/second. A clean link has a steady rate
    /// of zero; without the floor one lost packet would divide by zero and look
    /// like an infinite spike.
    pub const RATE_FLOOR_PER_S: f64 = 0.05;
    /// Minimum retransmit requests in the pre-stall window. Together with the
    /// ratio this stops a single incidental loss on an otherwise clean link from
    /// filling the bucket.
    pub const MIN_RTX: u32 = 3;
    /// One concealed underrun is already audible, so its absolute minimum is one.
    pub const MIN_UNDERRUN: u32 = 1;

    pub fn rtx_ratio(&self) -> f64 {
        f64::from(self.pre_stall_rtx) / self.steady_rtx_per_s.max(Self::RATE_FLOOR_PER_S)
    }

    pub fn underrun_ratio(&self) -> f64 {
        f64::from(self.pre_stall_underrun) / self.steady_underrun_per_s.max(Self::RATE_FLOOR_PER_S)
    }

    /// Whether the RTP side degraded enough to call the stall a transport fault.
    /// Underruns have their own line because one is enough to be audible.
    pub fn transport_bad(&self) -> bool {
        (self.rtx_ratio() > Self::RATIO_THRESHOLD && self.pre_stall_rtx >= Self::MIN_RTX)
            || (self.underrun_ratio() > Self::RATIO_THRESHOLD
                && self.pre_stall_underrun >= Self::MIN_UNDERRUN)
    }
}

/// RTP-side evidence used to separate a transport failure from a local sender
/// stall when a successful re-anchor absorbs a missed deadline (Q43 §三, Q44 §一).
///
/// The send thread can miss a deadline because the network stalled or because the
/// thread itself was descheduled. The cheap discriminator is whether the RTP
/// counters spiked in the second leading into the stall, compared with the
/// sender's own steady state over the last
/// [`STEADY_WINDOW`](Self::STEADY_WINDOW): retransmit requests (receivers asking
/// for missing packets) and concealed capture underruns.
///
/// The send loop samples the cumulative counters at most once per
/// [`SAMPLE_INTERVAL`](Self::SAMPLE_INTERVAL). At a re-anchor the oldest sample
/// still inside [`WINDOW`](Self::WINDOW) is the pre-stall baseline, and the oldest
/// sample still inside the steady window is the long baseline. The steady rate is
/// measured up to the start of the pre-stall window, not up to now: a spike must
/// not be diluted by its own baseline (3x over the last second of a 30 s window
/// would otherwise read as 2.8x).
///
/// Why "before": retransmit requests are only drained when the send loop runs, so
/// requests that arrive *during* the stall are counted after it ends. They are not
/// evidence for this re-anchor. [`reset`](Self::reset) starts a new pre-stall
/// window after every re-anchor, so the backlog drained on resume cannot be blamed
/// on the next stall either. The steady samples survive a re-anchor: a stall must
/// not throw away the baseline that says what normal loss looks like for this link.
///
/// This is a heuristic, not a proof: loss up to a second before a purely local
/// stall still counts as pre-stall movement, and a stall longer than the window
/// leaves no pre-stall baseline at all. The fallback is
/// [`TimelineResetReason::SenderStall`], the conservative direction — a missed
/// transport failure costs one stall sample, while a false confirmed failure would
/// poison the only drift signal.
#[derive(Debug, Default)]
pub struct RtpEvidence {
    /// Cumulative-counter samples, oldest first.
    samples: VecDeque<(Instant, u64, u64)>,
    /// Start of the current pre-stall window; `None` before the first re-anchor.
    pre_stall_since: Option<Instant>,
}

impl RtpEvidence {
    /// How far back a successful re-anchor looks for the pre-stall increment.
    pub const WINDOW: Duration = Duration::from_secs(1);
    /// How far back the steady baseline is measured.
    pub const STEADY_WINDOW: Duration = Duration::from_secs(30);
    /// Minimum spacing between samples; the send loop iterates every packet.
    pub const SAMPLE_INTERVAL: Duration = Duration::from_millis(250);
    /// [`STEADY_WINDOW`](Self::STEADY_WINDOW) at `SAMPLE_INTERVAL`, plus the
    /// sample that starts the window.
    const MAX_SAMPLES: usize = 121;

    /// Record the cumulative counters, throttled to `SAMPLE_INTERVAL`.
    pub fn sample(&mut self, now: Instant, rtx_req: u64, underruns: u64) {
        if self
            .samples
            .back()
            .is_some_and(|(at, _, _)| now.saturating_duration_since(*at) < Self::SAMPLE_INTERVAL)
        {
            return;
        }
        self.samples.push_back((now, rtx_req, underruns));
        while self.samples.len() > Self::MAX_SAMPLES {
            self.samples.pop_front();
        }
    }

    /// The pre-stall and steady-state windows for `now`, or `None` when there is
    /// no sample inside the pre-stall window: a stall longer than the window, or
    /// the very first loop iteration. The caller then falls back to
    /// [`TimelineResetReason::SenderStall`].
    pub fn baseline(&self, now: Instant, rtx_req: u64, underruns: u64) -> Option<RtpBaseline> {
        let pre = self.samples.iter().find(|(at, _, _)| {
            self.pre_stall_since.is_none_or(|since| *at > since)
                && now.saturating_duration_since(*at) <= Self::WINDOW
        })?;
        let steady = self
            .samples
            .iter()
            .find(|(at, _, _)| now.saturating_duration_since(*at) <= Self::STEADY_WINDOW)?;
        let steady_seconds = pre
            .0
            .saturating_duration_since(steady.0)
            .as_secs_f64()
            .max(0.001);
        Some(RtpBaseline {
            steady_rtx_per_s: pre.1.saturating_sub(steady.1) as f64 / steady_seconds,
            steady_underrun_per_s: pre.2.saturating_sub(steady.2) as f64 / steady_seconds,
            pre_stall_rtx: u32::try_from(rtx_req.saturating_sub(pre.1)).unwrap_or(u32::MAX),
            pre_stall_underrun: u32::try_from(underruns.saturating_sub(pre.2))
                .unwrap_or(u32::MAX),
        })
    }

    /// The bucket a successful re-anchor belongs to (Q43 §三, Q44 §一).
    pub fn reset_reason(&self, now: Instant, rtx_req: u64, underruns: u64) -> TimelineResetReason {
        match self.baseline(now, rtx_req, underruns) {
            Some(baseline) if baseline.transport_bad() => {
                TimelineResetReason::ConfirmedTransportFailure
            }
            _ => TimelineResetReason::SenderStall,
        }
    }

    /// Start a new pre-stall window after a re-anchor, without throwing away the
    /// long steady baseline. The retransmit requests that piled up during this
    /// stall are drained on the same send-loop iteration, right after the
    /// re-anchor; the next sample (taken after that drain) becomes the new
    /// baseline, so the backlog cannot make the next re-anchor look like a
    /// transport failure. A stall inside the next sampling interval has no
    /// pre-stall baseline and therefore falls back to `sender_stall`.
    ///
    /// The drain from an earlier stall stays in the steady window. It is genuine
    /// observed retransmission pressure, and counting it can only raise the
    /// denominator, i.e. make the discriminator more conservative.
    pub fn reset(&mut self, now: Instant) {
        self.pre_stall_since = Some(now);
    }
}

/// One counter block per streaming session. Owned by the send loop; readers build
/// a JSON snapshot through [`SessionTelemetry::snapshot`].
#[derive(Default)]
pub struct SessionTelemetry {
    pub session_epoch: u64,
    pub packets: u64,
    /// Packets that were on time (within 0.5 ms of their deadline).
    pub on_time_packets: u64,
    /// Packets that woke after their deadline; the histogram keeps the magnitude.
    pub late_packets: u64,
    /// Timeline slots filled with encoded silence to keep seq/rtptime linear.
    pub gap_filled: u64,
    /// Times the schedule was re-anchored instead of expiring the session.
    pub reanchored: u64,
    /// Signed shift applied by the most recent re-anchor (positive = timeline advanced).
    pub reanchor_shift_us: i64,
    /// Retransmit requests drained on the first send-loop iteration after a
    /// stall: how loud the stalls were, not how many there were. A high
    /// `sender_stall` with a near-zero drained count means the receiver's buffer
    /// absorbed them and nothing was audible (Q44 §一).
    pub stall_rtx_drained: u64,
    /// Largest single-stall [`stall_rtx_drained`](Self::stall_rtx_drained).
    pub stall_rtx_drained_max: u64,
    /// Timeline expiries that still require a fresh session (should stay near zero).
    pub expired: u64,
    /// Underruns while real media was playing; these are audible without concealment.
    pub underruns_active: u64,
    /// Underruns while the source was idle; these must be plain silence.
    pub underruns_idle: u64,
    /// Capture frames discarded because they were stale (input drops).
    pub input_drops: u64,
    /// Frames dropped between decode and the sender.
    pub decode_drops: u64,
    /// WASAPI discontinuity flags that were not ignored as benign wraparound.
    pub capture_glitches: u64,
    /// Event-channel messages that could not be parsed into a command.
    pub events_dropped: u64,
    pub servo_engagements: u64,
    pub servo_releases: u64,
    pub servo_step_resets: u64,
    pub servo_correction_ms: f64,
    pub servo_filtered_ms: f64,
    pub servo_drift_ppb: f64,
    pub lead_extra_ms: f64,
    /// Applied adaptive-lead adjustments of any size. These are deliberately
    /// *not* reset buckets: they describe the ladder twitching, which is not a
    /// timeline reset. Only shifts at least as long as the splice ramp also move
    /// `latency_change` (Q44 §二).
    pub lead_shift_count: u64,
    /// Signed sum of every applied lead shift, in microseconds. Positive means
    /// the timeline was delayed (more lead held at the receiver).
    pub lead_shift_total_us: i64,
    /// Timeline resets by cause; never collapsed into a single count.
    pub timeline_resets: TimelineResets,
    /// The most recent cause, `None` when the timeline was never reset.
    pub last_timeline_reset_reason: Option<TimelineResetReason>,
    pub lateness: Histogram,
    pub capture_age: Histogram,
    pub rtx_gap: Histogram,
}
impl SessionTelemetry {
    pub fn new() -> Self {
        Self {
            session_epoch: next_session_epoch(),
            ..Default::default()
        }
    }
    pub fn observe_lateness(&mut self, late_ms: f64) {
        self.packets += 1;
        self.lateness.observe(late_ms);
        if late_ms > 0.5 {
            self.late_packets += 1;
        } else {
            self.on_time_packets += 1;
        }
    }
    pub fn underflow(&mut self, cause: UnderflowCause) {
        match cause {
            UnderflowCause::ActiveMedia => self.underruns_active += 1,
            UnderflowCause::IdleKeepalive => self.underruns_idle += 1,
        }
    }
    /// Record one timeline reset. Callers pass the cause explicitly; a site that
    /// does not fit one of the reasons must not stay untagged.
    pub fn record_timeline_reset(&mut self, reason: TimelineResetReason) {
        self.timeline_resets.record(reason);
        self.last_timeline_reset_reason = Some(reason);
    }
    /// Account for one applied adaptive-lead shift (Q44 §二).
    ///
    /// Every shift moves `lead_shift_count` / `lead_shift_total_us`, but only a
    /// shift of at least [`LEAD_SHIFT_RESET_US`] (the splice ramp) is a
    /// `latency_change` reset. The adaptive ladder nudges the lead by whole
    /// milliseconds; counting all of them as resets would make `latency_change`
    /// equal to `lead_shift_count` and drown `confirmed_transport_failure`.
    pub fn record_lead_shift(&mut self, applied_us: i64) {
        self.lead_shift_count = self.lead_shift_count.saturating_add(1);
        self.lead_shift_total_us = self.lead_shift_total_us.saturating_add(applied_us);
        if applied_us.unsigned_abs() >= LEAD_SHIFT_RESET_US {
            self.record_timeline_reset(TimelineResetReason::LatencyChange);
        }
    }
    /// Account for the retransmit drain a stall caused (Q44 §一).
    ///
    /// `drained` is the increment the first `service_retransmits` call after the
    /// re-anchor observed. A zero drain is healthy: the receiver's buffer covered
    /// the gap. The per-stall maximum is kept separately because "fifty stalls of
    /// one packet" and "two stalls of five hundred" are different problems.
    pub fn record_stall_drain(&mut self, drained: u64) {
        self.stall_rtx_drained = self.stall_rtx_drained.saturating_add(drained);
        self.stall_rtx_drained_max = self.stall_rtx_drained_max.max(drained);
    }
    pub fn snapshot(&self) -> Value {
        json!({
            "session_epoch": self.session_epoch,
            "packets": self.packets,
            "on_time_packets": self.on_time_packets,
            "late_packets": self.late_packets,
            "timeline_gap_filled": self.gap_filled,
            "timeline_reanchored": self.reanchored,
            "timeline_reanchor_shift_us": self.reanchor_shift_us,
            "stall_rtx_drained": self.stall_rtx_drained,
            "stall_rtx_drained_max": self.stall_rtx_drained_max,
            "timeline_expired": self.expired,
            "underruns_active": self.underruns_active,
            "underruns_idle": self.underruns_idle,
            "input_drops": self.input_drops,
            "decode_drops": self.decode_drops,
            "capture_glitches": self.capture_glitches,
            "events_dropped": self.events_dropped,
            "lead_extra_ms": round3(self.lead_extra_ms),
            "lead_shift_count": self.lead_shift_count,
            "lead_shift_total_us": self.lead_shift_total_us,
            "timeline_resets": self.timeline_resets,
            "last_timeline_reset_reason": self
                .last_timeline_reset_reason
                .map_or("none", TimelineResetReason::name),
            "servo": {
                "engagements": self.servo_engagements,
                "releases": self.servo_releases,
                "step_resets": self.servo_step_resets,
                "correction_ms": round3(self.servo_correction_ms),
                "filtered_error_ms": round3(self.servo_filtered_ms),
                "drift_ppb": round3(self.servo_drift_ppb),
            },
            "lateness": self.lateness.snapshot(),
            "capture_age": self.capture_age.snapshot(),
            "retransmit_gap": self.rtx_gap.snapshot(),
        })
    }
}

static EPOCH: AtomicU64 = AtomicU64::new(0);
pub fn next_session_epoch() -> u64 {
    EPOCH.fetch_add(1, Ordering::Relaxed) + 1
}

/// The roadmap §12.3 field list, in order. The CSV header is generated from this
/// array, so changing the order is a format change and must be deliberate.
///
/// `rebuild_count` is gone: it was a bare count that mixed user track changes and
/// our own lead adjustments into what looked like drift. The eleven
/// `timeline_resets_<reason>` columns are flat so a spreadsheet `SUM` keeps per
/// cause, and `last_timeline_reset_reason` records the most recent one.
///
/// Layout (33 columns): the 21 scalar fields, then the eleven reset buckets in
/// [`TimelineResetReason::ALL`] declaration order, then the last-reason label.
/// The two lead-shift fields sit next to `applied_lead_ms` because they describe
/// the same adaptive ladder; they are counters, not reset buckets, and every
/// shift moves them while only a >= 5 ms shift (the splice ramp) increments
/// `timeline_resets_latency_change`. The two stall-severity fields sit next to
/// `reanchored` for the same reason: `reanchored` says how often the sender
/// stalled, they say how loud each stall was.
pub const SUMMARY_COLUMNS: [&str; 33] = [
    "session_id",
    "topology",
    "codec",
    "timing",
    "applied_lead_ms",
    "lead_shift_count",
    "lead_shift_total_us",
    "packets",
    "rtx_req",
    "rtx_resent",
    "underruns",
    "gap_filled",
    "reanchored",
    "stall_rtx_drained",
    "stall_rtx_drained_max",
    "servo_engagements",
    "baseline_resets",
    "mean_lateness_ms",
    "p99_lateness_ms",
    "max_lateness_ms",
    "duration_s",
    "timeline_resets_start",
    "timeline_resets_track_change",
    "timeline_resets_user_seek",
    "timeline_resets_receiver_rejected_flush",
    "timeline_resets_session_restart",
    "timeline_resets_confirmed_transport_failure",
    "timeline_resets_silence_resume",
    "timeline_resets_latency_change",
    "timeline_resets_flush",
    "timeline_resets_source_restart",
    "timeline_resets_sender_stall",
    "last_timeline_reset_reason",
];

/// The CSV/JSON label for "no timeline reset in this session".
pub const NO_TIMELINE_RESET: &str = "none";

/// One finished streaming session, in the shape roadmap §12.3 asks for.
///
/// Every value is copied from a counter that already existed before this struct:
/// the scheduler telemetry, `MediaMetrics`, the servo counters and the negotiated
/// `CompatProfile`. Nothing here is sampled on the audio path.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct SessionSummary {
    pub session_id: String,
    pub topology: &'static str,
    pub codec: &'static str,
    pub timing: &'static str,
    pub applied_lead_ms: i64,
    /// Applied lead adjustments of any size (the adaptive ladder's twitching).
    pub lead_shift_count: u64,
    /// Signed sum of those adjustments in microseconds.
    pub lead_shift_total_us: i64,
    pub packets: u64,
    pub rtx_req: u64,
    pub rtx_resent: u64,
    pub underruns: u64,
    pub gap_filled: u64,
    pub reanchored: u64,
    /// Retransmit requests drained after stalls: how loud they were.
    pub stall_rtx_drained: u64,
    /// Largest single-stall drain.
    pub stall_rtx_drained_max: u64,
    pub servo_engagements: u64,
    pub baseline_resets: u64,
    pub mean_lateness_ms: f64,
    pub p99_lateness_ms: f64,
    pub max_lateness_ms: f64,
    pub duration_s: f64,
    pub timeline_resets: TimelineResets,
    /// One of the nine reason labels, or [`NO_TIMELINE_RESET`].
    pub last_timeline_reset_reason: &'static str,
}
impl SessionSummary {
    fn last_reason(&self) -> &'static str {
        if self.last_timeline_reset_reason.is_empty() {
            NO_TIMELINE_RESET
        } else {
            self.last_timeline_reset_reason
        }
    }
    /// The additive IPC event. Consumers of `transport_metrics`/`capture_metrics`
    /// never see a changed shape; this is a new `event` value they can ignore.
    pub fn event(&self) -> Value {
        json!({
            "event": "session_summary",
            "session_id": self.session_id,
            "topology": self.topology,
            "codec": self.codec,
            "timing": self.timing,
            "applied_lead_ms": self.applied_lead_ms,
            "lead_shift_count": self.lead_shift_count,
            "lead_shift_total_us": self.lead_shift_total_us,
            "packets": self.packets,
            "rtx_req": self.rtx_req,
            "rtx_resent": self.rtx_resent,
            "underruns": self.underruns,
            "gap_filled": self.gap_filled,
            "reanchored": self.reanchored,
            "stall_rtx_drained": self.stall_rtx_drained,
            "stall_rtx_drained_max": self.stall_rtx_drained_max,
            "servo_engagements": self.servo_engagements,
            "baseline_resets": self.baseline_resets,
            "mean_lateness_ms": self.mean_lateness_ms,
            "p99_lateness_ms": self.p99_lateness_ms,
            "max_lateness_ms": self.max_lateness_ms,
            "duration_s": self.duration_s,
            "timeline_resets": self.timeline_resets,
            "last_timeline_reset_reason": self.last_reason(),
        })
    }
    pub fn csv_header() -> String {
        SUMMARY_COLUMNS.join(",")
    }
    /// One CSV row. Only the opaque `session_id` can ever need quoting; every
    /// other field is a number or a fixed enum label.
    ///
    /// The eleven reset counters are flat columns rather than one object because a
    /// spreadsheet can then `SUM` a single cause across sessions without parsing
    /// JSON. `TimelineResetReason::ALL` and [`SUMMARY_COLUMNS`] must stay in the
    /// same declared order; the header test locks that down.
    pub fn csv_row(&self) -> String {
        let mut fields = vec![
            csv_field(&self.session_id),
            csv_field(self.topology),
            csv_field(self.codec),
            csv_field(self.timing),
            self.applied_lead_ms.to_string(),
            self.lead_shift_count.to_string(),
            self.lead_shift_total_us.to_string(),
            self.packets.to_string(),
            self.rtx_req.to_string(),
            self.rtx_resent.to_string(),
            self.underruns.to_string(),
            self.gap_filled.to_string(),
            self.reanchored.to_string(),
            self.stall_rtx_drained.to_string(),
            self.stall_rtx_drained_max.to_string(),
            self.servo_engagements.to_string(),
            self.baseline_resets.to_string(),
            format!("{:.3}", self.mean_lateness_ms),
            format!("{:.3}", self.p99_lateness_ms),
            format!("{:.3}", self.max_lateness_ms),
            format!("{:.3}", self.duration_s),
        ];
        for reason in TimelineResetReason::ALL {
            fields.push(self.timeline_resets.field(reason).to_string());
        }
        fields.push(csv_field(self.last_reason()));
        fields.join(",")
    }
}

fn csv_field(value: &str) -> String {
    if value.contains([',', '"', '\n', '\r']) {
        format!("\"{}\"", value.replace('"', "\"\""))
    } else {
        value.to_owned()
    }
}

/// Where the per-user summary lives: next to the WPF logs, which is the same
/// `%APPDATA%\AirFlash` root the engine already uses for credentials. This
/// mirrors `AppPaths.LogDirectory` on the engine side instead of inventing a
/// second location.
pub fn summary_path() -> PathBuf {
    std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_default()
        .join("AirFlash")
        .join("logs")
        .join("session-summary.csv")
}

/// Append one row, writing the header first when the file is new. Callers treat
/// every error as a warning: a bad profile must never fail a session.
pub fn append_summary_row(path: &Path, summary: &SessionSummary) -> std::io::Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let mut file = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(path)?;
    let mut line = String::new();
    if file.metadata()?.len() == 0 {
        line.push_str(&SessionSummary::csv_header());
        line.push('\n');
    }
    line.push_str(&summary.csv_row());
    line.push('\n');
    file.write_all(line.as_bytes())?;
    file.flush()
}

static LATEST: OnceLock<Mutex<Value>> = OnceLock::new();
/// The most recent session snapshot, readable through the `diag_summary` command
/// after (or while) a session runs.
pub fn publish_diagnostics(value: Value) {
    let slot = LATEST.get_or_init(|| Mutex::new(Value::Null));
    *slot.lock().unwrap() = value;
}
pub fn diagnostics() -> Value {
    LATEST
        .get_or_init(|| Mutex::new(Value::Null))
        .lock()
        .unwrap()
        .clone()
}

/// Replace everything that identifies a network or a machine, keeping the shape and
/// the numbers.
///
/// A diagnostic dump is only useful if it can be attached to an issue, so hosts,
/// MAC addresses, paths and credential-looking keys are replaced by stable
/// pseudonyms (`host-1`, `host-2`, ...) rather than dropped: two members keep two
/// different names, and the same host keeps the same name across the document.
pub fn redact(value: &Value) -> Value {
    let mut names = std::collections::BTreeMap::new();
    redact_into(value, None, &mut names)
}
fn redact_into(
    value: &Value,
    key: Option<&str>,
    names: &mut std::collections::BTreeMap<String, String>,
) -> Value {
    match value {
        Value::Object(fields) => Value::Object(
            fields
                .iter()
                .map(|(name, inner)| {
                    let lowered = name.to_ascii_lowercase();
                    if matches!(
                        lowered.as_str(),
                        "secret" | "password" | "key" | "authorization" | "token"
                    ) {
                        (name.clone(), Value::String("[redacted]".into()))
                    } else {
                        (name.clone(), redact_into(inner, Some(&lowered), names))
                    }
                })
                .collect(),
        ),
        Value::Array(items) => Value::Array(
            items
                .iter()
                .map(|item| redact_into(item, key, names))
                .collect(),
        ),
        Value::String(text) if key.is_some_and(is_identity_key) => {
            Value::String(pseudonym(text, names))
        }
        Value::String(text) if looks_like_path(text) => Value::String(pseudonym(text, names)),
        Value::String(text) => Value::String(scrub_text(text, names)),
        other => other.clone(),
    }
}
/// Replace addresses that appear inside free text (log lines, messages).
fn scrub_text(text: &str, names: &mut std::collections::BTreeMap<String, String>) -> String {
    let mut out = String::with_capacity(text.len());
    let mut token = String::new();
    for ch in text.chars() {
        if ch.is_ascii_alphanumeric() || matches!(ch, '.' | ':' | '-') {
            token.push(ch);
        } else {
            flush_token(&mut token, &mut out, names);
            out.push(ch);
        }
    }
    flush_token(&mut token, &mut out, names);
    out
}
fn flush_token(
    token: &mut String,
    out: &mut String,
    names: &mut std::collections::BTreeMap<String, String>,
) {
    if token.is_empty() {
        return;
    }
    if is_ipv4(token) || is_mac(token) {
        out.push_str(&pseudonym(token, names));
    } else {
        out.push_str(token);
    }
    token.clear();
}
fn is_ipv4(token: &str) -> bool {
    let parts: Vec<_> = token.split('.').collect();
    parts.len() == 4
        && parts.iter().all(|part| {
            !part.is_empty()
                && part.len() <= 3
                && part.bytes().all(|byte| byte.is_ascii_digit())
                && part.parse::<u16>().is_ok_and(|value| value <= 255)
        })
}
fn is_mac(token: &str) -> bool {
    let separator = if token.contains(':') {
        ':'
    } else if token.contains('-') {
        '-'
    } else {
        return false;
    };
    let parts: Vec<_> = token.split(separator).collect();
    parts.len() == 6
        && parts
            .iter()
            .all(|part| part.len() == 2 && part.bytes().all(|byte| byte.is_ascii_hexdigit()))
}
fn is_identity_key(key: &str) -> bool {
    matches!(
        key,
        "host" | "ip" | "address" | "addr" | "mac" | "deviceid" | "receiver_id" | "id" | "path"
    )
}
fn looks_like_path(text: &str) -> bool {
    text.len() > 3 && (text.contains(":\\") || text.starts_with("\\\\") || text.starts_with("/home/") || text.starts_with("/Users/"))
}
fn pseudonym(text: &str, names: &mut std::collections::BTreeMap<String, String>) -> String {
    if let Some(existing) = names.get(text) {
        return existing.clone();
    }
    let label = format!("host-{}", names.len() + 1);
    names.insert(text.to_string(), label.clone());
    label
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn redaction_keeps_the_numbers_and_hides_the_identities() {
        let snapshot = json!({
            "session_epoch": 4,
            "packets": 1200,
            "members": [
                {"host": "192.168.1.20", "ip": "192.168.1.20", "mac": "02:57:32:41:50:01", "send_errors": 0},
                {"host": "192.168.1.21", "ip": "192.168.1.21", "send_errors": 3},
            ],
            "feedback": {"path": "C:\\Users\\alice\\AppData\\Local\\AirFlash\\log.txt", "rtt_ms": 4.5},
            "secret": "Events-Write-Encryption-Key",
            "nested": {"authorization": "Bearer abc", "note": "192.168.1.20 said hello"},
        });
        let redacted = redact(&snapshot);
        let text = redacted.to_string();
        assert!(!text.contains("192.168.1.20"));
        assert!(!text.contains("192.168.1.21"));
        assert!(!text.contains("alice"));
        assert!(!text.contains("Bearer"));
        assert_eq!(redacted["packets"], json!(1200));
        assert_eq!(redacted["feedback"]["rtt_ms"], json!(4.5));
        assert_eq!(redacted["secret"], json!("[redacted]"));
        // The same host keeps one pseudonym; a second host gets a different one.
        assert_eq!(redacted["members"][0]["host"], redacted["members"][0]["ip"]);
        assert_ne!(redacted["members"][0]["host"], redacted["members"][1]["host"]);
        assert_eq!(redacted["members"][1]["send_errors"], json!(3));
    }

    #[test]
    fn histogram_tracks_percentiles_and_bounds() {
        let mut h = Histogram::default();
        for i in 0..100 {
            h.observe(i as f64 / 10.0);
        }
        assert_eq!(h.count(), 100);
        assert!((h.mean_ms() - 4.95).abs() < 1e-9);
        // 13.6% bucket granularity, rounded up, so 5.0 ms may read as 5.4 ms.
        let p50 = h.percentile_ms(0.5);
        assert!((4.5..5.5).contains(&p50), "p50 {p50}");
        assert!(h.percentile_ms(0.99) >= 9.0);
        assert!((h.percentile_ms(1.0) - 9.9).abs() < 1e-9);
        h.observe(1000.0);
        assert_eq!(h.snapshot()["above_ceiling"], json!(1));
        assert!((h.percentile_ms(1.0) - 1000.0).abs() < 1e-9);
        h.observe(-5.0);
        assert!(h.snapshot()["below_floor"].as_u64().unwrap() >= 1);
    }
    #[test]
    fn telemetry_snapshot_reports_recovery_counters() {
        let mut t = SessionTelemetry::new();
        t.observe_lateness(0.2);
        t.observe_lateness(12.0);
        t.gap_filled = 3;
        t.reanchored = 1;
        t.underflow(UnderflowCause::ActiveMedia);
        t.underflow(UnderflowCause::IdleKeepalive);
        let value = t.snapshot();
        assert_eq!(value["packets"], json!(2));
        assert_eq!(value["late_packets"], json!(1));
        assert_eq!(value["timeline_gap_filled"], json!(3));
        assert_eq!(value["underruns_active"], json!(1));
        assert_eq!(value["underruns_idle"], json!(1));
        assert!(t.session_epoch > 0);
    }
    #[test]
    fn session_epoch_is_monotonic() {
        let first = next_session_epoch();
        let second = next_session_epoch();
        assert!(second > first);
    }
    #[test]
    fn published_diagnostics_round_trip() {
        publish_diagnostics(json!({"session_epoch": 7}));
        assert_eq!(diagnostics()["session_epoch"], json!(7));
    }

    fn known_summary() -> SessionSummary {
        let mut timeline_resets = TimelineResets::default();
        timeline_resets.record(TimelineResetReason::ConfirmedTransportFailure);
        timeline_resets.record(TimelineResetReason::ConfirmedTransportFailure);
        timeline_resets.record(TimelineResetReason::LatencyChange);
        timeline_resets.record(TimelineResetReason::SourceRestart);
        timeline_resets.record(TimelineResetReason::SenderStall);
        SessionSummary {
            session_id: "session-42".into(),
            topology: "software_stereo",
            codec: "alac",
            timing: "ptp",
            applied_lead_ms: 45,
            lead_shift_count: 7,
            lead_shift_total_us: -1500,
            packets: 1500,
            rtx_req: 7,
            rtx_resent: 5,
            underruns: 2,
            gap_filled: 11,
            reanchored: 3,
            stall_rtx_drained: 12,
            stall_rtx_drained_max: 7,
            servo_engagements: 4,
            baseline_resets: 1,
            mean_lateness_ms: 1.234,
            p99_lateness_ms: 9.876,
            max_lateness_ms: 12.345,
            duration_s: 30.5,
            timeline_resets,
            last_timeline_reset_reason: TimelineResetReason::SenderStall.name(),
        }
    }

    #[test]
    fn each_reason_lands_in_its_own_bucket_and_no_bucket_leaks() {
        for reason in TimelineResetReason::ALL {
            let mut resets = TimelineResets::default();
            resets.record(reason);
            assert_eq!(resets.field(reason), 1, "{reason:?} must count once");
            assert_eq!(resets.total(), 1, "{reason:?} must not count twice");
            for other in TimelineResetReason::ALL {
                if other != reason {
                    assert_eq!(
                        resets.field(other),
                        0,
                        "{reason:?} leaked into {other:?}"
                    );
                }
            }
            assert_eq!(resets.field(reason), 1);
        }
        // Every reason once: eleven, and each bucket holds exactly one.
        assert_eq!(TimelineResetReason::ALL.len(), 11);
        let mut all = TimelineResets::default();
        for reason in TimelineResetReason::ALL {
            all.record(reason);
        }
        assert_eq!(all.total(), 11);
        for reason in TimelineResetReason::ALL {
            assert_eq!(all.field(reason), 1, "{reason:?}");
        }
        // A repeated cause only grows its own bucket.
        all.record(TimelineResetReason::LatencyChange);
        assert_eq!(all.latency_change, 2);
        assert_eq!(all.total(), 12);
        // The two AirFlash-only buckets are their own reasons, not aliases for the
        // confirmed transport failure (Q43 §三/§四).
        assert_eq!(all.confirmed_transport_failure, 1);
        assert_eq!(all.source_restart, 1);
        assert_eq!(all.sender_stall, 1);
        // `start` stays a declared tripwire and is never produced by the engine.
        assert_eq!(all.start, 1, "the bucket is addressable even if unused");
    }

    #[test]
    fn latency_change_and_silence_resume_are_not_transport_failures() {
        let mut resets = TimelineResets::default();
        resets.record(TimelineResetReason::LatencyChange);
        resets.record(TimelineResetReason::SilenceResume);
        resets.record(TimelineResetReason::LatencyChange);
        assert_eq!(resets.latency_change, 2);
        assert_eq!(resets.silence_resume, 1);
        assert_eq!(
            resets.confirmed_transport_failure, 0,
            "our own lead change and a silence resume must never look like drift"
        );
        resets.record(TimelineResetReason::ConfirmedTransportFailure);
        assert_eq!(resets.confirmed_transport_failure, 1);
        assert_eq!(resets.latency_change, 2);
        assert_eq!(resets.silence_resume, 1);

        let mut telemetry = SessionTelemetry::new();
        telemetry.record_timeline_reset(TimelineResetReason::SilenceResume);
        telemetry.record_timeline_reset(TimelineResetReason::LatencyChange);
        assert_eq!(
            telemetry.last_timeline_reset_reason,
            Some(TimelineResetReason::LatencyChange)
        );
        assert_eq!(
            telemetry.snapshot()["last_timeline_reset_reason"],
            json!("latency_change")
        );
        assert_eq!(
            telemetry.snapshot()["timeline_resets"]["confirmed_transport_failure"],
            json!(0)
        );
    }

    /// Feed `seconds` of a steady rate at the sampling interval. One counter tick
    /// per `ticks_per_second` is spread over the four samples of each second.
    fn feed_steady(evidence: &mut RtpEvidence, t0: Instant, seconds: u64, per_second: u64) {
        for i in 0..=seconds * 4 {
            evidence.sample(
                t0 + Duration::from_millis(250 * i),
                i * per_second / 4,
                0,
            );
        }
    }

    #[test]
    fn a_steady_baseline_absorbs_a_small_pre_stall_increment() {
        let t0 = Instant::now();
        // 2 rtx/s for 30 s is this link's normal loss; a binary "did anything
        // move" test would have called the next stall a transport failure.
        let mut evidence = RtpEvidence::default();
        feed_steady(&mut evidence, t0, 30, 2);
        let now = t0 + Duration::from_millis(30_500);
        evidence.sample(now, 63, 0);
        let baseline = evidence.baseline(now, 63, 0).expect("window exists");
        assert!(
            baseline.pre_stall_rtx >= 1,
            "the old binary test would have fired: {baseline:?}"
        );
        assert!(
            (baseline.steady_rtx_per_s - 2.0).abs() < 0.2,
            "steady rate must describe the link, got {baseline:?}"
        );
        assert!(
            baseline.rtx_ratio() <= RtpBaseline::RATIO_THRESHOLD,
            "a normal increment must stay inside the ratio: {baseline:?}"
        );
        assert!(!baseline.transport_bad(), "{baseline:?}");
        assert_eq!(
            evidence.reset_reason(now, 63, 0),
            TimelineResetReason::SenderStall
        );
    }

    #[test]
    fn a_clear_spike_above_the_steady_rate_is_a_transport_failure() {
        let t0 = Instant::now();
        let mut evidence = RtpEvidence::default();
        feed_steady(&mut evidence, t0, 30, 1);
        let now = t0 + Duration::from_millis(30_750);
        // Eleven requests in the pre-stall window against a 1/s steady rate.
        evidence.sample(now, 40, 0);
        let baseline = evidence.baseline(now, 40, 0).expect("window exists");
        assert_eq!(baseline.pre_stall_rtx, 11);
        assert!(baseline.steady_rtx_per_s < 1.5, "{baseline:?}");
        assert!(baseline.rtx_ratio() > RtpBaseline::RATIO_THRESHOLD);
        assert!(baseline.transport_bad(), "{baseline:?}");
        assert_eq!(
            evidence.reset_reason(now, 40, 0),
            TimelineResetReason::ConfirmedTransportFailure
        );
    }

    #[test]
    fn one_underrun_is_transport_evidence_while_one_rtx_is_not() {
        let t0 = Instant::now();
        // A clean link: the denominator is zero, so the ratio floor applies and
        // the absolute minimums decide.
        let mut underruns = RtpEvidence::default();
        feed_steady(&mut underruns, t0, 30, 0);
        let now = t0 + Duration::from_millis(30_250);
        underruns.sample(now, 0, 1);
        let baseline = underruns.baseline(now, 0, 1).expect("window exists");
        assert_eq!(baseline.pre_stall_rtx, 0);
        assert_eq!(baseline.pre_stall_underrun, 1);
        assert!(
            baseline.underrun_ratio() > RtpBaseline::RATIO_THRESHOLD,
            "{baseline:?}"
        );
        assert!(baseline.transport_bad(), "{baseline:?}");
        assert_eq!(
            underruns.reset_reason(now, 0, 1),
            TimelineResetReason::ConfirmedTransportFailure
        );
        // The same single event on the retransmit side is only a lost packet:
        // rtx needs an absolute minimum of three as well as the ratio.
        let mut rtx = RtpEvidence::default();
        feed_steady(&mut rtx, t0, 30, 0);
        rtx.sample(now, 1, 0);
        let baseline = rtx.baseline(now, 1, 0).expect("window exists");
        assert_eq!(baseline.pre_stall_rtx, 1);
        assert!(baseline.rtx_ratio() > RtpBaseline::RATIO_THRESHOLD);
        assert!(!baseline.transport_bad(), "{baseline:?}");
        assert_eq!(
            rtx.reset_reason(now, 1, 0),
            TimelineResetReason::SenderStall
        );
    }

    #[test]
    fn rtp_evidence_needs_a_pre_stall_sample_and_keeps_the_steady_window() {
        let t0 = Instant::now();
        let mut evidence = RtpEvidence::default();
        // No sample at all: the conservative answer is a local stall.
        assert_eq!(
            evidence.reset_reason(t0, 9, 9),
            TimelineResetReason::SenderStall
        );
        feed_steady(&mut evidence, t0, 30, 1);
        // A stall longer than the pre-stall window leaves no baseline.
        let far = t0 + Duration::from_millis(31_600);
        assert!(evidence.baseline(far, 40, 0).is_none());
        assert_eq!(
            evidence.reset_reason(far, 40, 0),
            TimelineResetReason::SenderStall
        );
        // A re-anchor starts a new pre-stall window: samples at or before it are
        // not a baseline, so a stall inside the next sample interval has none.
        let now = t0 + Duration::from_millis(30_000);
        evidence.reset(now);
        assert!(
            evidence
                .baseline(now + Duration::from_millis(100), 31, 0)
                .is_none()
        );
        // The long steady samples survive the re-anchor: once a post-reset sample
        // exists, the link's normal rate is still known.
        let later = t0 + Duration::from_millis(30_500);
        evidence.sample(later, 31, 0);
        let baseline = evidence.baseline(later, 31, 0).expect("window exists");
        assert!(
            (baseline.steady_rtx_per_s - 1.0).abs() < 0.2,
            "{baseline:?}"
        );
    }

    #[test]
    fn stall_severity_counts_the_total_and_the_worst_single_stall() {
        let mut telemetry = SessionTelemetry::new();
        assert_eq!(telemetry.stall_rtx_drained, 0);
        assert_eq!(telemetry.stall_rtx_drained_max, 0);
        telemetry.record_stall_drain(3);
        telemetry.record_stall_drain(9);
        // A stall the receiver's buffer absorbed adds nothing but a count.
        telemetry.record_stall_drain(0);
        assert_eq!(telemetry.stall_rtx_drained, 12);
        assert_eq!(telemetry.stall_rtx_drained_max, 9);
        let snapshot = telemetry.snapshot();
        assert_eq!(snapshot["stall_rtx_drained"], json!(12));
        assert_eq!(snapshot["stall_rtx_drained_max"], json!(9));
    }

    #[test]
    fn only_a_shift_at_least_as_long_as_the_splice_ramp_is_a_latency_change_reset() {
        // The ramp is 5 ms (`continuity::Splice` uses `rate / 200`), so a 3 ms
        // shift is inaudible and must not be a timeline reset.
        assert_eq!(LEAD_SHIFT_RESET_US, 5_000);
        let mut telemetry = SessionTelemetry::new();
        telemetry.record_lead_shift(3_000);
        assert_eq!(telemetry.lead_shift_count, 1);
        assert_eq!(telemetry.lead_shift_total_us, 3_000);
        assert_eq!(
            telemetry.timeline_resets.latency_change, 0,
            "3 ms is inside the 5 ms splice ramp"
        );
        telemetry.record_lead_shift(5_000);
        assert_eq!(telemetry.timeline_resets.latency_change, 1);
        telemetry.record_lead_shift(-7_000);
        assert_eq!(telemetry.timeline_resets.latency_change, 2);
        assert_eq!(telemetry.lead_shift_count, 3);
        assert_eq!(telemetry.lead_shift_total_us, 1_000);
        assert_eq!(
            telemetry.last_timeline_reset_reason,
            Some(TimelineResetReason::LatencyChange)
        );
        let snapshot = telemetry.snapshot();
        assert_eq!(snapshot["lead_shift_count"], json!(3));
        assert_eq!(snapshot["lead_shift_total_us"], json!(1_000));
    }

    #[test]
    fn a_session_without_resets_is_all_zero_and_still_emits_one_summary() {
        let summary = SessionSummary {
            session_id: "session-clean".into(),
            ..Default::default()
        };
        assert_eq!(summary.timeline_resets, TimelineResets::default());
        assert_eq!(summary.timeline_resets.total(), 0);
        assert_eq!(summary.last_reason(), NO_TIMELINE_RESET);
        let row = summary.csv_row();
        let fields: Vec<_> = row.split(',').collect();
        assert_eq!(fields.len(), SUMMARY_COLUMNS.len());
        for (column, value) in SUMMARY_COLUMNS.iter().zip(&fields) {
            if column.starts_with("timeline_resets_")
                || matches!(
                    *column,
                    "lead_shift_count"
                        | "lead_shift_total_us"
                        | "stall_rtx_drained"
                        | "stall_rtx_drained_max"
                )
            {
                assert_eq!(*value, "0", "{column} must be zero");
            }
        }
        assert_eq!(fields[SUMMARY_COLUMNS.len() - 1], NO_TIMELINE_RESET);

        let root = std::env::temp_dir().join(format!("airflash-clean-{}", uuid::Uuid::new_v4()));
        let path = root.join("session-summary.csv");
        let events = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let sink = events.clone();
        crate::session::emit_session_summary(&summary, Some(path.as_path()), &|event| {
            sink.lock().unwrap().push(event)
        });
        let emitted = events.lock().unwrap();
        assert_eq!(emitted.len(), 1, "a clean session emits exactly one summary");
        assert_eq!(emitted[0]["event"], json!("session_summary"));
        assert_eq!(
            emitted[0]["last_timeline_reset_reason"],
            json!(NO_TIMELINE_RESET)
        );
        assert!(emitted[0].get("rebuild_count").is_none());
        drop(emitted);
        let text = std::fs::read_to_string(&path).unwrap();
        assert_eq!(text.lines().count(), 2, "header plus one clean row");
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn summary_event_carries_the_declared_fields_and_redaction_keeps_its_shape() {
        let summary = known_summary();
        let event = summary.event();
        assert_eq!(event["event"], json!("session_summary"));
        // The plain columns are top-level; the nine reset buckets are one nested
        // object in JSON (the CSV keeps them flat for aggregation).
        for column in SUMMARY_COLUMNS {
            if column.starts_with("timeline_resets_") || column == "last_timeline_reset_reason" {
                continue;
            }
            assert!(event.get(column).is_some(), "missing field {column}");
        }
        assert!(event.get("rebuild_count").is_none());
        let resets = event["timeline_resets"].as_object().unwrap();
        assert_eq!(resets.len(), TimelineResetReason::ALL.len());
        for reason in TimelineResetReason::ALL {
            assert_eq!(
                resets[reason.name()],
                json!(summary.timeline_resets.field(reason)),
                "missing reset bucket {}",
                reason.name()
            );
        }
        assert_eq!(
            event["last_timeline_reset_reason"],
            json!(TimelineResetReason::SenderStall.name())
        );
        // Redaction never changes the shape of an already identity-free record.
        assert_eq!(redact(&event), event);
    }

    #[test]
    fn summary_csv_uses_the_declared_column_order_and_hides_identities() {
        let summary = known_summary();
        assert_eq!(SessionSummary::csv_header(), SUMMARY_COLUMNS.join(","));
        // The nine reason columns are present, in the enum's declared order, and
        // the retired bare count is gone.
        let header: Vec<_> = SessionSummary::csv_header().split(',').map(str::to_owned).collect();
        let expected: Vec<_> = TimelineResetReason::ALL
            .iter()
            .map(|reason| format!("timeline_resets_{}", reason.name()))
            .collect();
        let start = SUMMARY_COLUMNS.len() - 1 - expected.len();
        assert_eq!(&header[start..start + expected.len()], expected.as_slice());
        assert_eq!(header[SUMMARY_COLUMNS.len() - 1], "last_timeline_reset_reason");
        assert!(!header.iter().any(|column| column == "rebuild_count"));

        let row = summary.csv_row();
        let fields: Vec<_> = row.split(',').collect();
        assert_eq!(fields.len(), SUMMARY_COLUMNS.len(), "row: {row}");
        for (index, column) in SUMMARY_COLUMNS.iter().enumerate() {
            match *column {
                "session_id" => assert_eq!(fields[index], "session-42"),
                "topology" => assert_eq!(fields[index], "software_stereo"),
                "codec" => assert_eq!(fields[index], "alac"),
                "timing" => assert_eq!(fields[index], "ptp"),
                "applied_lead_ms" => assert_eq!(fields[index], "45"),
                "lead_shift_count" => assert_eq!(fields[index], "7"),
                "lead_shift_total_us" => assert_eq!(fields[index], "-1500"),
                "packets" => assert_eq!(fields[index], "1500"),
                "rtx_req" => assert_eq!(fields[index], "7"),
                "rtx_resent" => assert_eq!(fields[index], "5"),
                "underruns" => assert_eq!(fields[index], "2"),
                "gap_filled" => assert_eq!(fields[index], "11"),
                "reanchored" => assert_eq!(fields[index], "3"),
                "stall_rtx_drained" => assert_eq!(fields[index], "12"),
                "stall_rtx_drained_max" => assert_eq!(fields[index], "7"),
                "servo_engagements" => assert_eq!(fields[index], "4"),
                "baseline_resets" => assert_eq!(fields[index], "1"),
                "mean_lateness_ms" => assert_eq!(fields[index], "1.234"),
                "p99_lateness_ms" => assert_eq!(fields[index], "9.876"),
                "max_lateness_ms" => assert_eq!(fields[index], "12.345"),
                "duration_s" => assert_eq!(fields[index], "30.500"),
                "timeline_resets_start" => assert_eq!(fields[index], "0"),
                "timeline_resets_track_change" => assert_eq!(fields[index], "0"),
                "timeline_resets_user_seek" => assert_eq!(fields[index], "0"),
                "timeline_resets_receiver_rejected_flush" => assert_eq!(fields[index], "0"),
                "timeline_resets_session_restart" => assert_eq!(fields[index], "0"),
                "timeline_resets_confirmed_transport_failure" => {
                    assert_eq!(fields[index], "2")
                }
                "timeline_resets_silence_resume" => assert_eq!(fields[index], "0"),
                "timeline_resets_latency_change" => assert_eq!(fields[index], "1"),
                "timeline_resets_flush" => assert_eq!(fields[index], "0"),
                "timeline_resets_source_restart" => assert_eq!(fields[index], "1"),
                "timeline_resets_sender_stall" => assert_eq!(fields[index], "1"),
                "last_timeline_reset_reason" => assert_eq!(fields[index], "sender_stall"),
                other => panic!("unexpected column {other}"),
            }
        }
        // No host, MAC, path or secret can survive the numeric/enumerated shape.
        for forbidden in ["127.0.0.1", "192.168.", "C:\\", "/home/", "02:57:32", "secret"] {
            assert!(!row.contains(forbidden), "CSV leaked {forbidden}: {row}");
        }
    }

    #[test]
    fn summary_csv_append_writes_one_header_and_one_row_per_session() {
        let root = std::env::temp_dir().join(format!("airflash-summary-{}", uuid::Uuid::new_v4()));
        let path = root.join("logs").join("session-summary.csv");
        let first = known_summary();
        append_summary_row(&path, &first).unwrap();
        let mut second = first.clone();
        second.session_id = "session-43".into();
        append_summary_row(&path, &second).unwrap();
        let text = std::fs::read_to_string(&path).unwrap();
        let lines: Vec<_> = text.lines().collect();
        assert_eq!(lines.len(), 3, "header plus one row per session: {text}");
        assert_eq!(lines[0], SessionSummary::csv_header());
        assert!(lines[1].starts_with("session-42,"));
        assert!(lines[2].starts_with("session-43,"));
        let _ = std::fs::remove_dir_all(root);
    }
}
