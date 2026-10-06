//! Fixed stereo graphic EQ. Preparation stays off the media thread; updates use
//! a latest-value mailbox and a 20 ms crossfade without delaying audio.
use anyhow::{Result, ensure};
use serde::{Deserialize, Serialize};
use std::sync::{Arc, Mutex};

pub const FREQUENCIES: [f64; 10] = [
    31.25, 62.5, 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0,
];

#[derive(Clone, Copy, Debug, Default, PartialEq, Deserialize, Serialize)]
#[serde(default, deny_unknown_fields)]
pub struct Settings {
    pub enabled: bool,
    pub preamp_db: f64,
    pub band_gains_db: [f64; 10],
}
impl Settings {
    pub fn validate(&self) -> Result<()> {
        ensure!(
            std::iter::once(&self.preamp_db)
                .chain(&self.band_gains_db)
                .all(|v| v.is_finite() && (-12.0..=12.0).contains(v)),
            "equalizer gains must be finite and between -12 and +12 dB"
        );
        Ok(())
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct Coefficients {
    b: [f64; 3],
    a: [f64; 2],
}
impl Coefficients {
    fn new(frequency: f64, gain: f64, rate: u32) -> Self {
        if gain == 0.0 {
            return Self {
                b: [1.0, 0.0, 0.0],
                a: [0.0, 0.0],
            };
        }
        let amplitude = 10f64.powf(gain / 40.0);
        let w = 2.0 * std::f64::consts::PI * frequency / f64::from(rate);
        let alpha = w.sin() / (2.0 * 1.4);
        let a0 = 1.0 + alpha / amplitude;
        Self {
            b: [
                (1.0 + alpha * amplitude) / a0,
                -2.0 * w.cos() / a0,
                (1.0 - alpha * amplitude) / a0,
            ],
            a: [-2.0 * w.cos() / a0, (1.0 - alpha / amplitude) / a0],
        }
    }
    fn response_db(self, frequency: f64, rate: u32) -> f64 {
        let w = 2.0 * std::f64::consts::PI * frequency / f64::from(rate);
        let (s, c) = w.sin_cos();
        let (s2, c2) = (2.0 * w).sin_cos();
        let nr = self.b[0] + self.b[1] * c + self.b[2] * c2;
        let ni = -self.b[1] * s - self.b[2] * s2;
        let dr = 1.0 + self.a[0] * c + self.a[1] * c2;
        let di = -self.a[0] * s - self.a[1] * s2;
        10.0 * ((nr * nr + ni * ni) / (dr * dr + di * di)).log10()
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Prepared {
    coefficients: [Coefficients; 10],
    gain: f64,
    bypass: bool,
    pub auto_attenuation_db: f64,
    pub effective_preamp_db: f64,
}
impl Prepared {
    pub fn new(settings: Settings, rate: u32) -> Result<Self> {
        settings.validate()?;
        ensure!(
            [44100, 48000].contains(&rate),
            "unsupported equalizer sample rate"
        );
        let coefficients = std::array::from_fn(|i| {
            Coefficients::new(FREQUENCIES[i], settings.band_gains_db[i], rate)
        });
        let response = |f| {
            coefficients
                .iter()
                .map(|c| c.response_db(f, rate))
                .sum::<f64>()
        };
        let mut peak = 0f64.max(response(0.0)).max(response(f64::from(rate) / 2.0));
        if settings.enabled {
            for f in FREQUENCIES {
                peak = peak.max(response(f));
            }
            for i in 0..4096 {
                peak = peak.max(response(
                    10.0 * (f64::from(rate) / 20.0).powf(f64::from(i) / 4095.0),
                ));
            }
        }
        let auto_attenuation_db = if settings.enabled {
            -(settings.preamp_db + peak).max(0.0)
        } else {
            0.0
        };
        let effective_preamp_db = if settings.enabled {
            settings.preamp_db + auto_attenuation_db
        } else {
            0.0
        };
        Ok(Self {
            coefficients,
            gain: 10f64.powf(effective_preamp_db / 20.0),
            bypass: !settings.enabled
                || (settings.preamp_db == 0.0 && settings.band_gains_db.iter().all(|v| *v == 0.0)),
            auto_attenuation_db,
            effective_preamp_db,
        })
    }
}

#[derive(Clone)]
pub struct Control(Arc<Mutex<(u64, Prepared)>>);
impl Control {
    pub fn new(settings: Settings, rate: u32) -> Result<Self> {
        Ok(Self(Arc::new(Mutex::new((
            0,
            Prepared::new(settings, rate)?,
        )))))
    }
    pub fn set(&self, sequence: u64, prepared: Prepared) {
        let mut latest = self.0.lock().unwrap();
        if sequence > latest.0 {
            *latest = (sequence, prepared);
        }
    }
    // No waiting for the IPC thread on the audio path.
    pub fn latest(&self) -> Option<(u64, Prepared)> {
        self.0.try_lock().ok().map(|value| *value)
    }
    pub fn initial(&self) -> Prepared {
        self.0.lock().unwrap().1
    }
}

#[derive(Clone)]
struct Chain {
    prepared: Prepared,
    state: [[[f64; 2]; 10]; 2],
}
impl Chain {
    fn new(prepared: Prepared) -> Self {
        Self {
            prepared,
            state: [[[0.0; 2]; 10]; 2],
        }
    }
    fn frame(&mut self, input: [f32; 2]) -> [f32; 2] {
        if self.prepared.bypass {
            return input;
        }
        std::array::from_fn(|channel| {
            let mut x = f64::from(input[channel]);
            for (c, state) in self
                .prepared
                .coefficients
                .iter()
                .zip(&mut self.state[channel])
            {
                let y = c.b[0] * x + state[0];
                state[0] = c.b[1] * x - c.a[0] * y + state[1];
                state[1] = c.b[2] * x - c.a[1] * y;
                x = y;
            }
            (x * self.prepared.gain) as f32
        })
    }
}

pub struct Processor {
    active: Chain,
    next: Option<Chain>,
    pending: Option<Prepared>,
    fade_frames: usize,
    position: usize,
    sequence: u64,
}
impl Processor {
    pub fn new(prepared: Prepared, rate: u32) -> Self {
        Self {
            active: Chain::new(prepared),
            next: None,
            pending: None,
            fade_frames: rate as usize / 50,
            position: 0,
            sequence: 0,
        }
    }
    pub fn update(&mut self, sequence: u64, prepared: Prepared) {
        if sequence <= self.sequence {
            return;
        }
        self.sequence = sequence;
        if self.next.is_none() && self.active.prepared == prepared {
            return;
        }
        if self
            .next
            .as_ref()
            .is_some_and(|next| next.prepared == prepared)
        {
            self.pending = None;
            return;
        }
        if self.next.is_some() {
            self.pending = Some(prepared);
        } else {
            self.begin(prepared);
        }
    }
    fn begin(&mut self, prepared: Prepared) {
        self.next = Some(Chain::new(prepared));
        self.position = 0;
    }
    pub fn frame(&mut self, input: [f32; 2]) -> [f32; 2] {
        let old = self.active.frame(input);
        let Some(next) = &mut self.next else {
            return old;
        };
        let new = next.frame(input);
        self.position += 1;
        let weight = self.position as f32 / self.fade_frames as f32;
        let result = std::array::from_fn(|i| old[i] + (new[i] - old[i]) * weight);
        if self.position >= self.fade_frames {
            self.active = self.next.take().unwrap();
            if let Some(prepared) = self.pending.take() {
                self.begin(prepared);
            }
        }
        result
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn flat_and_disabled_are_bit_exact() {
        for rate in [44100, 48000] {
            for settings in [
                Settings::default(),
                Settings {
                    enabled: true,
                    ..Settings::default()
                },
            ] {
                let mut p = Processor::new(Prepared::new(settings, rate).unwrap(), rate);
                for x in [0.0, -0.75, 0.1234567, 1.0] {
                    assert_eq!(p.frame([x, -x]), [x, -x]);
                }
            }
        }
    }
    #[test]
    fn unchanged_settings_keep_filter_history() {
        let mut settings = Settings {
            enabled: true,
            ..Default::default()
        };
        settings.band_gains_db[0] = 6.0;
        let prepared = Prepared::new(settings, 44100).unwrap();
        let mut p = Processor::new(prepared, 44100);
        for _ in 0..100 {
            p.frame([0.1, 0.0]);
        }
        let state = p.active.state;
        p.update(1, prepared);
        assert!(p.next.is_none());
        assert_eq!(p.active.state, state);
    }
    #[test]
    fn response_and_headroom_cover_both_rates() {
        for rate in [44100, 48000] {
            let mut settings = Settings {
                enabled: true,
                preamp_db: 3.0,
                ..Settings::default()
            };
            settings.band_gains_db[5] = 6.0;
            let prepared = Prepared::new(settings, rate).unwrap();
            assert!((prepared.coefficients[5].response_db(1000.0, rate) - 6.0).abs() < 1e-8);
            assert!((prepared.auto_attenuation_db + 9.0).abs() < 1e-7);
            assert!((prepared.effective_preamp_db + 6.0).abs() < 1e-7);
            for f in FREQUENCIES {
                assert!(
                    prepared
                        .coefficients
                        .iter()
                        .map(|c| c.response_db(f, rate))
                        .sum::<f64>()
                        + prepared.effective_preamp_db
                        < 1e-6
                );
            }
            let mut p = Processor::new(prepared, rate);
            let mut peak = 0f32;
            for i in 0..rate {
                let x = (2.0 * std::f64::consts::PI * 1000.0 * f64::from(i) / f64::from(rate)).sin()
                    as f32
                    * 0.5;
                let y = p.frame([x, 0.0]);
                assert_eq!(y[1], 0.0);
                if i > rate / 2 {
                    peak = peak.max(y[0].abs());
                }
            }
            assert!((peak - 0.5).abs() < 0.001);
        }
    }
    #[test]
    fn transitions_finish_and_latest_update_wins() {
        for rate in [44100, 48000] {
            let flat = Prepared::new(Settings::default(), rate).unwrap();
            let quiet = Prepared::new(
                Settings {
                    enabled: true,
                    preamp_db: -12.0,
                    ..Settings::default()
                },
                rate,
            )
            .unwrap();
            let mut p = Processor::new(flat, rate);
            p.update(1, quiet);
            let first = p.frame([0.5, -0.5]);
            assert!((first[0] - 0.5).abs() < 0.001);
            p.update(2, quiet);
            p.update(3, flat);
            p.update(2, quiet);
            for _ in 0..rate / 25 {
                assert!(
                    p.frame([0.5, -0.5])
                        .iter()
                        .all(|v| v.is_finite() && v.abs() <= 0.5)
                );
            }
            assert_eq!(p.frame([0.5, -0.5]), [0.5, -0.5]);
            assert!(p.next.is_none());
        }
    }
    #[test]
    fn invalid_settings_and_old_mailbox_updates_are_rejected() {
        for gain in [f64::NAN, f64::INFINITY, -12.5, 12.5] {
            assert!(
                Prepared::new(
                    Settings {
                        preamp_db: gain,
                        ..Settings::default()
                    },
                    44100
                )
                .is_err()
            );
        }
        assert!(serde_json::from_str::<Settings>(r#"{"band_gains_db":[1,2]}"#).is_err());
        let c = Control::new(Settings::default(), 44100).unwrap();
        let p = Prepared::new(Settings::default(), 44100).unwrap();
        c.set(3, p);
        c.set(2, p);
        assert_eq!(c.latest().unwrap().0, 3);
    }
}
