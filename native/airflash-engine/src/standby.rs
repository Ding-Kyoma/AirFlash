//! Silence policy is independent of transport lifetime and user playback gain.
use anyhow::{Result, ensure};
use serde::{Deserialize, Serialize};
use std::sync::{Arc, Mutex};

#[derive(Clone, Copy, Debug, Deserialize, Serialize)]
#[serde(default, deny_unknown_fields)]
pub struct Settings {
    pub enabled: bool,
    pub silence_seconds: f64,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            enabled: false,
            silence_seconds: 10.0,
        }
    }
}
impl Settings {
    pub fn validate(self) -> Result<()> {
        ensure!(
            self.silence_seconds.is_finite() && (5.0..=300.0).contains(&self.silence_seconds),
            "standby duration must be 5..300 seconds"
        );
        Ok(())
    }
}
#[derive(Clone, Default)]
pub struct Control(Arc<Mutex<Settings>>);
impl Control {
    pub fn new(settings: Settings) -> Self {
        Self(Arc::new(Mutex::new(settings)))
    }
    pub fn set(&self, settings: Settings) -> Result<()> {
        settings.validate()?;
        *self.0.lock().unwrap() = settings;
        Ok(())
    }
    pub fn get(&self) -> Settings {
        *self.0.lock().unwrap()
    }
    pub fn should_pause(&self, now_ns: u64, last_audio_ns: u64, started_ns: u64) -> bool {
        let settings = self.get();
        settings.enabled
            && now_ns.saturating_sub(last_audio_ns.max(started_ns)) as f64 / 1e9
                >= settings.silence_seconds
    }
    pub fn should_resume(&self, last_audio_ns: u64, paused_ns: u64) -> bool {
        !self.get().enabled || last_audio_ns > paused_ns
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn activity_and_live_edits_control_release_and_resume() {
        let control = Control::new(Settings {
            enabled: true,
            silence_seconds: 10.0,
        });
        assert!(!control.should_pause(9_000_000_000, 0, 0));
        assert!(control.should_pause(10_000_000_000, 0, 0));
        assert!(!control.should_pause(10_000_000_000, 8_000_000_000, 0));
        assert!(!control.should_resume(8_000_000_000, 10_000_000_000));
        assert!(control.should_resume(10_000_000_001, 10_000_000_000));
        control
            .set(Settings {
                enabled: false,
                silence_seconds: 5.0,
            })
            .unwrap();
        assert!(control.should_resume(0, 10_000_000_000));
        assert!(!control.should_pause(u64::MAX, 0, 0));
        assert!(
            control
                .set(Settings {
                    enabled: true,
                    silence_seconds: f64::NAN
                })
                .is_err()
        );
        assert!(!control.get().enabled);
    }
}
