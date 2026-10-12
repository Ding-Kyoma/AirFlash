//! Five millisecond, sample-domain boundaries. No lookahead and no music gating.
pub struct Splice {
    length: u32,
    remaining: u32,
    origin: [f32; 2],
    last: [f32; 2],
    gain: f32,
    gain_from: f32,
    gain_target: f32,
    gain_remaining: u32,
    missing: bool,
    silent_frames: u32,
    rate: u32,
    pub transitions: u64,
    gain_initialized: bool,
}
impl Splice {
    pub fn new(rate: u32) -> Self {
        Self {
            length: (rate / 200).max(1),
            remaining: rate / 200,
            origin: [0.0; 2],
            last: [0.0; 2],
            gain: 1.0,
            gain_from: 1.0,
            gain_target: 1.0,
            gain_remaining: 0,
            missing: false,
            silent_frames: 0,
            rate,
            transitions: 1,
            gain_initialized: false,
        }
    }
    pub fn boundary(&mut self) {
        self.origin = self.last;
        self.remaining = self.length;
        self.transitions += 1;
    }
    pub fn frame(&mut self, input: [f32; 2], gain: f32, missing: bool) -> [f32; 2] {
        if !self.gain_initialized {
            self.gain = gain;
            self.gain_from = gain;
            self.gain_target = gain;
            self.gain_initialized = true;
        }
        if missing != self.missing {
            self.boundary();
            self.missing = missing;
        }
        let silent = input.iter().all(|s| *s == 0.0);
        if !silent && self.silent_frames >= self.rate {
            self.boundary();
        }
        self.silent_frames = if silent {
            self.silent_frames.saturating_add(1)
        } else {
            0
        };
        if gain != self.gain_target {
            self.transitions += 1;
            self.gain_from = self.gain;
            self.gain_target = gain;
            self.gain_remaining = self.length;
        }
        if self.gain_remaining > 0 {
            let t = 1.0 - self.gain_remaining as f32 / self.length as f32;
            self.gain = self.gain_from + (self.gain_target - self.gain_from) * t;
            self.gain_remaining -= 1;
        } else {
            self.gain = self.gain_target;
        }
        let mut out = input.map(|s| (s * self.gain).clamp(-1.0, 1.0));
        if self.remaining > 0 {
            let t = 1.0 - self.remaining as f32 / self.length as f32;
            let w = 0.5 - 0.5 * (std::f32::consts::PI * t).cos();
            for (i, sample) in out.iter_mut().enumerate() {
                *sample = self.origin[i] * (1.0 - w) + *sample * w;
            }
            self.remaining -= 1;
        }
        self.last = out;
        out
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn startup_respects_initial_gain_and_silence_resume_has_no_peak() {
        for rate in [44100, 48000] {
            let mut s = Splice::new(rate);
            for _ in 0..rate / 200 + 2 {
                assert!(
                    s.frame([0.8; 2], 0.1, false)
                        .iter()
                        .all(|v| v.abs() <= 0.080001)
                );
            }
            for _ in 0..rate + 1 {
                s.frame([0.0; 2], 0.1, false);
            }
            assert_eq!(s.frame([0.8; 2], 0.1, false), [0.0; 2]);
        }
    }
    #[test]
    fn transitions_do_not_overshoot_and_continuous_samples_are_untouched() {
        for rate in [44100, 48000] {
            let mut s = Splice::new(rate);
            for _ in 0..rate / 200 + 1 {
                s.frame([0.8, -0.8], 1.0, false);
            }
            assert_eq!(s.frame([0.25, -0.5], 1.0, false), [0.25, -0.5]);
            s.boundary();
            assert_eq!(s.frame([-0.9, 0.9], 1.0, false), [0.25, -0.5]);
            for _ in 0..rate / 200 + 1 {
                assert!(
                    s.frame([-0.9, 0.9], 1.0, false)
                        .iter()
                        .all(|v| v.abs() <= 0.9)
                );
            }
            let first = s.frame([0.0; 2], 1.0, true);
            assert_eq!(first, [-0.9, 0.9]);
            for _ in 0..rate {
                s.frame([0.0; 2], 1.0, true);
            }
            assert_eq!(s.frame([0.8; 2], 1.0, false), [0.0; 2]);
        }
    }
    #[test]
    fn gain_changes_ramp_without_resetting_music() {
        let mut s = Splice::new(48000);
        for _ in 0..241 {
            s.frame([1.0; 2], 1.0, false);
        }
        assert_eq!(s.frame([1.0; 2], 0.0, false), [1.0; 2]);
        for _ in 0..240 {
            s.frame([1.0; 2], 0.0, false);
        }
        assert_eq!(s.frame([1.0; 2], 0.0, false), [0.0; 2]);
    }
}
