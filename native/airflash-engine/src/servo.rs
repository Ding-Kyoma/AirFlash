//! Playout-lead servo and drift estimate (roadmap P0-3).
//!
//! AirFlash already had a proportional loop that pins the *capture queue depth* to
//! a target. That keeps the capture side fed, but it never looks at whether packets
//! arrive in time for their playout deadline, has no integral term (so a constant
//! bias cannot be removed) and saturates at ±500 ppm without saying so.
//!
//! This module adds two slow, well-bounded controllers on top:
//!
//! * [`LatenessServo`] filters observed send lateness (one sample per packet),
//!   decides whether the sender is genuinely behind, and emits a small ppm bias for
//!   the capture resampler.
//! * [`DriftEstimator`] turns the queue-depth slope over a long window into a
//!   part-per-billion estimate of the capture clock versus the RTP rate, with a
//!   plausibility ceiling so a single glitch cannot be mistaken for drift.
//!
//! Both are deliberately slow: the fast queue-depth loop stays in charge of
//! avoiding underruns, and these only remove the steady-state error it cannot.

use std::collections::VecDeque;
use std::time::{Duration, Instant};

#[derive(Clone, Copy, Debug)]
pub struct ServoConfig {
    /// Low-pass coefficient for the lateness sample (0.2 ≈ 50 ms at 5 ms/packet).
    pub alpha: f64,
    /// A single sample this far from the filtered error is a discontinuity, not drift.
    pub step_ms: f64,
    /// Inside this band the sender is considered on time; the integral freezes.
    pub dead_band_ms: f64,
    /// Filtered error that engages the integral term.
    pub engage_ms: f64,
    /// Filtered error that releases it again (hysteresis, lower than `engage_ms`).
    pub release_ms: f64,
    /// Proportional gain in ppm per ms of filtered lateness.
    pub kp_ppm_per_ms: f64,
    /// Integral gain in ppm per (ms · s).
    pub ki_ppm_per_ms_s: f64,
    /// Absolute correction ceiling; the capture loop clamps again at ±500 ppm.
    pub max_ppm: f64,
    /// Anti-windup clamp on the integral term.
    pub integral_limit_ms_s: f64,
}
impl Default for ServoConfig {
    fn default() -> Self {
        Self {
            alpha: 0.2,
            step_ms: 250.0,
            dead_band_ms: 2.0,
            engage_ms: 5.0,
            release_ms: 2.0,
            kp_ppm_per_ms: 20.0,
            ki_ppm_per_ms_s: 25.0,
            max_ppm: 300.0,
            integral_limit_ms_s: 60.0,
        }
    }
}

/// Positive output means "produce frames faster" (the sender is late and the
/// capture path must catch up), expressed as a relative ratio in parts per million.
#[derive(Clone, Debug)]
pub struct LatenessServo {
    cfg: ServoConfig,
    filtered_ms: f64,
    integral_ms_s: f64,
    correction_ppm: f64,
    engaged: bool,
    last: Option<Instant>,
    samples: u64,
    steps: u64,
    engagements: u64,
    releases: u64,
    saturated: u64,
}
impl Default for LatenessServo {
    fn default() -> Self {
        Self::new(ServoConfig::default())
    }
}
impl LatenessServo {
    pub fn new(cfg: ServoConfig) -> Self {
        Self {
            cfg,
            filtered_ms: 0.0,
            integral_ms_s: 0.0,
            correction_ppm: 0.0,
            engaged: false,
            last: None,
            samples: 0,
            steps: 0,
            engagements: 0,
            releases: 0,
            saturated: 0,
        }
    }
    pub fn config(&self) -> ServoConfig {
        self.cfg
    }
    /// Feed one lateness sample (positive = the packet woke after its deadline).
    pub fn update(&mut self, sample_ms: f64, now: Instant) {
        if !sample_ms.is_finite() {
            return;
        }
        let dt = self
            .last
            .map_or(Duration::ZERO, |last| now.saturating_duration_since(last));
        self.last = Some(now);
        self.samples += 1;
        // A step is a discontinuity, not drift: reset the baseline instead of
        // integrating a 300 ms stall into a slow pitch change.
        if (sample_ms - self.filtered_ms).abs() > self.cfg.step_ms {
            self.filtered_ms = sample_ms;
            self.integral_ms_s = 0.0;
            self.correction_ppm = 0.0;
            self.steps += 1;
            return;
        }
        self.filtered_ms += self.cfg.alpha * (sample_ms - self.filtered_ms);
        if !self.engaged && self.filtered_ms >= self.cfg.engage_ms {
            self.engaged = true;
            self.engagements += 1;
        } else if self.engaged && self.filtered_ms <= self.cfg.release_ms {
            self.engaged = false;
            self.integral_ms_s *= 0.5;
            self.releases += 1;
        }
        if !self.engaged {
            // Bleed any residual bias away rather than holding it forever.
            self.correction_ppm *= 0.9;
            if self.correction_ppm.abs() < 0.5 {
                self.correction_ppm = 0.0;
            }
            return;
        }
        let error = (self.filtered_ms - self.cfg.dead_band_ms).max(0.0);
        let seconds = dt.as_secs_f64().min(0.25); // ignore implausible gaps
        self.integral_ms_s = (self.integral_ms_s + error * seconds)
            .clamp(-self.cfg.integral_limit_ms_s, self.cfg.integral_limit_ms_s);
        let raw = self.cfg.kp_ppm_per_ms * error + self.cfg.ki_ppm_per_ms_s * self.integral_ms_s;
        let clamped = raw.clamp(-self.cfg.max_ppm, self.cfg.max_ppm);
        if (clamped - raw).abs() > f64::EPSILON {
            self.saturated += 1;
        }
        self.correction_ppm = clamped;
    }
    pub fn correction_ppm(&self) -> f64 {
        self.correction_ppm
    }
    pub fn filtered_ms(&self) -> f64 {
        self.filtered_ms
    }
    pub fn engaged(&self) -> bool {
        self.engaged
    }
    pub fn counters(&self) -> ServoCounters {
        ServoCounters {
            samples: self.samples,
            steps: self.steps,
            engagements: self.engagements,
            releases: self.releases,
            saturated: self.saturated,
            filtered_ms: self.filtered_ms,
            correction_ppm: self.correction_ppm,
        }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct ServoCounters {
    pub samples: u64,
    pub steps: u64,
    pub engagements: u64,
    pub releases: u64,
    pub saturated: u64,
    pub filtered_ms: f64,
    pub correction_ppm: f64,
}

/// Inner loop: hold the capture queue at its target depth.
///
/// The original loop was proportional only (`(target - have) / rate * 0.01`), so a
/// constant capture-clock offset settled at a constant depth error of
/// `offset / gain` — tens of milliseconds away from the target with a bad clock,
/// which is exactly the deficit that later turns into underruns. The integral term
/// removes that steady-state error; anti-windup keeps it from fighting the clamp.
#[derive(Clone, Copy, Debug)]
pub struct QueueConfig {
    /// Proportional gain in ppm per frame of depth error.
    pub kp_ppm_per_frame: f64,
    /// Integral gain in ppm per (frame · s).
    pub ki_ppm_per_frame_s: f64,
    /// Absolute output ceiling; the capture loop also clamps at ±500 ppm.
    pub limit_ppm: f64,
}
impl Default for QueueConfig {
    fn default() -> Self {
        // 0.25 ppm/frame reproduces the original 0.01-per-tick gain at 44.1 kHz.
        Self {
            kp_ppm_per_frame: 0.25,
            ki_ppm_per_frame_s: 0.5,
            limit_ppm: 400.0,
        }
    }
}

#[derive(Clone, Debug)]
pub struct QueueRegulator {
    cfg: QueueConfig,
    integral_ppm: f64,
}
impl Default for QueueRegulator {
    fn default() -> Self {
        Self::new(QueueConfig::default())
    }
}
impl QueueRegulator {
    pub fn new(cfg: QueueConfig) -> Self {
        Self {
            cfg,
            integral_ppm: 0.0,
        }
    }
    /// `error_frames` is positive when the queue is shorter than its target, so a
    /// positive result means "produce more frames".
    pub fn update(&mut self, error_frames: f64, dt: Duration) -> f64 {
        if !error_frames.is_finite() {
            return self.integral_ppm;
        }
        let seconds = dt.as_secs_f64().min(0.5);
        let proportional = self.cfg.kp_ppm_per_frame * error_frames;
        let candidate = self.integral_ppm + self.cfg.ki_ppm_per_frame_s * error_frames * seconds;
        let suggested = proportional + candidate;
        // Freeze the integral while the output is pinned at the ceiling in the
        // same direction: otherwise it winds up and overshoots on recovery.
        let saturating = suggested.abs() > self.cfg.limit_ppm
            && suggested.signum() == self.integral_ppm.signum();
        if !saturating {
            self.integral_ppm = candidate.clamp(-self.cfg.limit_ppm, self.cfg.limit_ppm);
        }
        (proportional + self.integral_ppm).clamp(-self.cfg.limit_ppm, self.cfg.limit_ppm)
    }
    pub fn integral_ppm(&self) -> f64 {
        self.integral_ppm
    }
    pub fn reset(&mut self) {
        self.integral_ppm = 0.0;
    }
}

/// Capture-clock versus RTP-rate estimate from the queue-depth slope.
#[derive(Clone, Debug)]
pub struct DriftEstimator {
    window: Duration,
    samples: VecDeque<(Instant, f64)>,
    ppm: f64,
    /// Samples that produced an implausible slope and were discarded.
    rejected: u64,
    /// Plausibility ceiling: anything beyond this is a glitch, not a clock.
    max_ppm: f64,
    rate: f64,
}
impl DriftEstimator {
    pub fn new(window: Duration, rate: f64) -> Self {
        Self {
            window,
            samples: VecDeque::new(),
            ppm: 0.0,
            rejected: 0,
            max_ppm: 1000.0,
            rate,
        }
    }
    /// Record the current queue depth in frames and return the drift estimate.
    pub fn observe(&mut self, now: Instant, depth_frames: f64) -> f64 {
        if !depth_frames.is_finite() {
            return self.ppm;
        }
        self.samples.push_back((now, depth_frames));
        while self
            .samples
            .front()
            .is_some_and(|(at, _)| now.saturating_duration_since(*at) > self.window)
        {
            self.samples.pop_front();
        }
        if self.samples.len() < 8 {
            return self.ppm;
        }
        let (Some(first), Some(last)) = (self.samples.front(), self.samples.back()) else {
            return self.ppm;
        };
        let seconds = last.0.saturating_duration_since(first.0).as_secs_f64();
        if seconds < self.window.as_secs_f64() * 0.5 {
            return self.ppm;
        }
        // Least-squares slope of depth over time, in frames per second.
        let n = self.samples.len() as f64;
        let mean_t = self
            .samples
            .iter()
            .map(|(at, _)| at.saturating_duration_since(first.0).as_secs_f64())
            .sum::<f64>()
            / n;
        let mean_d = self.samples.iter().map(|(_, d)| *d).sum::<f64>() / n;
        let mut cov = 0.0;
        let mut var = 0.0;
        for (at, depth) in &self.samples {
            let dt = at.saturating_duration_since(first.0).as_secs_f64() - mean_t;
            cov += dt * (depth - mean_d);
            var += dt * dt;
        }
        if var <= f64::EPSILON {
            return self.ppm;
        }
        let slope = cov / var;
        let ppm = slope / self.rate * 1e6;
        if !ppm.is_finite() || ppm.abs() > self.max_ppm {
            self.rejected += 1;
            return self.ppm;
        }
        self.ppm = ppm;
        self.ppm
    }
    pub fn ppm(&self) -> f64 {
        self.ppm
    }
    pub fn ppb(&self) -> f64 {
        self.ppm * 1000.0
    }
    pub fn rejected(&self) -> u64 {
        self.rejected
    }
    pub fn reset(&mut self) {
        self.samples.clear();
        self.ppm = 0.0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn at(start: Instant, ms: u64) -> Instant {
        start + Duration::from_millis(ms)
    }

    #[test]
    fn on_time_traffic_never_engages() {
        let start = Instant::now();
        let mut servo = LatenessServo::default();
        for step in 0..500 {
            servo.update(0.3, at(start, step * 8));
        }
        assert!(!servo.engaged());
        assert_eq!(servo.correction_ppm(), 0.0);
        assert_eq!(servo.counters().engagements, 0);
    }

    #[test]
    fn sustained_lateness_engages_and_saturates_safely() {
        let start = Instant::now();
        let mut servo = LatenessServo::default();
        for step in 0..400 {
            servo.update(30.0, at(start, step * 8));
        }
        let counters = servo.counters();
        assert!(servo.engaged(), "30 ms of sustained lateness must engage");
        assert_eq!(counters.engagements, 1);
        assert!(servo.correction_ppm() > 0.0, "correction speeds the source up");
        assert!(servo.correction_ppm() <= servo.config().max_ppm);
        assert!(counters.saturated > 0, "the integral will reach the ceiling");
    }

    #[test]
    fn the_servo_releases_with_hysteresis_and_bleeds_the_bias() {
        let start = Instant::now();
        let mut servo = LatenessServo::default();
        for step in 0..300 {
            servo.update(30.0, at(start, step * 8));
        }
        assert!(servo.engaged());
        let engaged_correction = servo.correction_ppm();
        assert!(engaged_correction > 0.0);
        // Back on time: filtered error falls through release_ms, then the bias decays.
        for step in 300..900 {
            servo.update(0.0, at(start, step * 8));
        }
        assert!(!servo.engaged());
        assert_eq!(servo.counters().releases, 1);
        assert_eq!(servo.correction_ppm(), 0.0);
    }

    #[test]
    fn a_step_resets_the_baseline_instead_of_integrating() {
        let start = Instant::now();
        let mut servo = LatenessServo::default();
        for step in 0..200 {
            servo.update(1.0, at(start, step * 8));
        }
        // A 300 ms stall is a discontinuity, not drift.
        servo.update(300.0, at(start, 200 * 8));
        assert_eq!(servo.counters().steps, 1);
        assert_eq!(servo.correction_ppm(), 0.0);
        assert!(!servo.engaged(), "a single step must not engage the loop");
        assert!((servo.filtered_ms() - 300.0).abs() < 1e-9);
    }

    #[test]
    fn non_finite_samples_are_ignored() {
        let start = Instant::now();
        let mut servo = LatenessServo::default();
        servo.update(f64::NAN, at(start, 0));
        servo.update(f64::INFINITY, at(start, 8));
        assert_eq!(servo.counters().samples, 0);
    }

    #[test]
    fn queue_regulator_removes_a_constant_offset_in_steady_state() {
        // Proportional-only, a 100 ppm capture-clock offset parks the queue 400
        // frames (≈9 ms) away from its target; with the integral term the depth
        // error decays to zero and the correction alone carries the offset.
        for offset_ppm in [100.0, -100.0, 300.0, -300.0] {
            let rate = 44_100.0;
            let dt = Duration::from_millis(10);
            let mut regulator = QueueRegulator::default();
            let mut error_frames = 0.0f64;
            let mut correction = 0.0f64;
            for _ in 0..20_000 {
                correction = regulator.update(error_frames, dt);
                // Capture clock offset plus the resampler bias move the depth.
                error_frames -= (offset_ppm + correction) / 1e6 * rate * dt.as_secs_f64();
            }
            // Proportional-only parks at `offset / kp` (400–1200 frames here);
            // the integral cuts that by an order of magnitude or better.
            let proportional_only = offset_ppm.abs() / 0.25;
            assert!(
                error_frames.abs() < proportional_only / 10.0,
                "depth error must shrink well inside the P-only band: offset {offset_ppm} left {error_frames} frames"
            );
            assert!(
                (correction + offset_ppm).abs() < 60.0,
                "the integral must carry the offset: offset {offset_ppm} correction {correction}"
            );
        }
    }

    #[test]
    fn queue_regulator_antwindup_holds_at_the_ceiling() {
        let mut regulator = QueueRegulator::default();
        let dt = Duration::from_millis(10);
        for _ in 0..100_000 {
            let out = regulator.update(1000.0, dt);
            assert!(out <= 400.0);
        }
        // Releasing the error must not produce a long overshoot tail.
        let released = regulator.update(0.0, dt);
        assert!((0.0..=400.0).contains(&released));
    }

    #[test]
    fn queue_regulator_resets_cleanly() {
        let mut regulator = QueueRegulator::default();
        for _ in 0..100 {
            regulator.update(50.0, Duration::from_millis(10));
        }
        assert!(regulator.integral_ppm() > 0.0);
        regulator.reset();
        assert_eq!(regulator.integral_ppm(), 0.0);
    }

    #[test]
    fn drift_estimate_is_signed_and_rejects_glitches() {
        let start = Instant::now();
        let mut drift = DriftEstimator::new(Duration::from_secs(2), 44_100.0);
        // 100 ppm fast capture clock: the queue grows 4.41 frames per second.
        for step in 0..=400 {
            let seconds = step as f64 * 0.005;
            drift.observe(at(start, step * 5), 4.41 * seconds);
        }
        assert!((drift.ppm() - 100.0).abs() < 2.0, "ppm {}", drift.ppm());
        assert!((drift.ppb() - 100_000.0).abs() < 2_000.0);
        // A sudden 5000 ppm slope is not a clock.
        let settled = drift.ppm();
        let mut glitch = DriftEstimator::new(Duration::from_secs(2), 44_100.0);
        for step in 0..=400 {
            glitch.observe(at(start, step * 5), 220.5 * step as f64 * 0.005);
        }
        assert_eq!(glitch.ppm(), 0.0);
        assert!(glitch.rejected() > 0);
        assert_eq!(settled, drift.ppm());
    }

    #[test]
    fn short_windows_do_not_publish_a_drift_estimate() {
        let start = Instant::now();
        let mut drift = DriftEstimator::new(Duration::from_secs(30), 44_100.0);
        for step in 0..20 {
            drift.observe(at(start, step * 5), step as f64);
        }
        assert_eq!(drift.ppm(), 0.0);
    }
}
