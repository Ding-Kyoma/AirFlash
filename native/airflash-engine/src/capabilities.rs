//! Receiver advertisements are hints; /info refines them before session setup.
use crate::{rtp, transport::Fault};
use anyhow::{Result, ensure};
use plist::Value;
use serde::Deserialize;
use std::{collections::BTreeMap, net::IpAddr};

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Service {
    pub service_type: String,
    pub host: IpAddr,
    pub port: u16,
    #[serde(default)]
    pub txt: BTreeMap<String, String>,
}

pub fn parse_mask(text: &str) -> Option<u64> {
    let parts: Vec<_> = text.trim().split(',').collect();
    let parse = |s: &str| {
        let s = s.trim();
        if let Some(hex) = s.strip_prefix("0x").or_else(|| s.strip_prefix("0X")) {
            u64::from_str_radix(hex, 16).ok()
        } else {
            s.parse::<u64>().ok()
        }
    };
    match parts.as_slice() {
        [one] => parse(one),
        [low, high] => {
            let (low, high) = (parse(low)?, parse(high)?);
            (low <= u32::MAX as u64 && high <= u32::MAX as u64).then_some((high << 32) | low)
        }
        _ => None,
    }
}
fn integer(value: &Value) -> Option<u64> {
    value
        .as_unsigned_integer()
        .or_else(|| value.as_string().and_then(parse_mask))
}
fn parse_flags(value: &str) -> Option<u64> {
    let value = value.trim();
    u64::from_str_radix(value.trim_start_matches("0x").trim_start_matches("0X"), 16).ok()
}
#[derive(Clone, Debug, Default)]
pub struct Capabilities {
    pub model: String,
    pub device_id: String,
    pub features: Option<u64>,
    pub flags: Option<u64>,
    pub formats: Option<u64>,
    pub codecs: Vec<u8>,
    pub rates: Vec<u32>,
    pub timing: Vec<String>,
    pub password: Option<bool>,
    pub restricted: bool,
}
impl Capabilities {
    pub fn read(services: &[Service], codecs: &[u8], info: &Value) -> Result<Self> {
        let mut ordered: Vec<_> = services.iter().collect();
        ordered.sort_by_key(|s| !s.service_type.starts_with("_airplay"));
        let field = |names: &[&str]| {
            ordered.iter().find_map(|s| {
                names
                    .iter()
                    .find_map(|key| s.txt.get(*key).filter(|v| !v.trim().is_empty()))
            })
        };
        let mut cap = Self {
            model: field(&["model", "am"]).cloned().unwrap_or_default(),
            device_id: field(&["deviceid"]).cloned().unwrap_or_default(),
            features: field(&["features", "ft"]).and_then(|v| parse_mask(v)),
            flags: field(&["flags", "sf"]).and_then(|v| parse_flags(v)),
            codecs: field(&["cn"])
                .map(|s| s.split(',').filter_map(|n| n.trim().parse().ok()).collect())
                .unwrap_or_else(|| codecs.to_vec()),
            rates: field(&["sr"])
                .map(|s| s.split(',').filter_map(|n| n.trim().parse().ok()).collect())
                .unwrap_or_default(),
            password: field(&["pw"]).and_then(|s| match s.to_ascii_lowercase().as_str() {
                "true" | "1" => Some(true),
                "false" | "0" => Some(false),
                _ => None,
            }),
            restricted: field(&["acl"]).is_some_and(|s| s == "1")
                || field(&["act"]).is_some_and(|s| s == "2"),
            ..Self::default()
        };
        let d = info
            .as_dictionary()
            .ok_or_else(|| anyhow::anyhow!("info is not a dictionary"))?;
        if let Some(v) = d.get("model").and_then(Value::as_string) {
            cap.model = v.into();
        }
        if let Some(v) = d.get("deviceID").and_then(Value::as_string) {
            cap.device_id = v.into();
        }
        if let Some(v) = d.get("features").and_then(integer) {
            cap.features = Some(v);
        }
        if let Some(v) = d.get("statusFlags").and_then(integer) {
            cap.flags = Some(v);
        }
        if let Some(v) = d.get("passwordRequired").and_then(Value::as_boolean) {
            cap.password = Some(v);
        }
        // Only advertised input formats describe what the receiver can consume.
        if let Some(formats) = d.get("audioFormats") {
            cap.formats = integer(formats).or_else(|| {
                formats.as_array().and_then(|items| {
                    let masks: Vec<_> = items
                        .iter()
                        .filter_map(|item| {
                            item.as_dictionary()
                                .and_then(|d| d.get("audioInputFormats"))
                                .and_then(integer)
                        })
                        .collect();
                    (!masks.is_empty()).then(|| masks.into_iter().fold(0, |a, b| a | b))
                })
            });
        }
        if let Some(timing) = d.get("timingProtocols").and_then(Value::as_array) {
            cap.timing = timing
                .iter()
                .filter_map(Value::as_string)
                .map(str::to_ascii_lowercase)
                .collect();
        }
        Ok(cap)
    }
    pub fn feature(&self, bit: u8) -> Option<bool> {
        self.features.map(|v| v & (1u64 << bit) != 0)
    }
    pub fn ptp(&self) -> bool {
        if self.timing.is_empty() {
            self.feature(41) == Some(true)
        } else {
            self.timing.iter().any(|s| s == "ptp")
        }
    }
    pub fn ntp(&self) -> bool {
        if self.timing.is_empty() {
            self.feature(41) == Some(false)
        } else {
            self.timing.iter().any(|s| s == "ntp")
        }
    }
    pub fn buffered(&self) -> bool {
        self.feature(40) == Some(true) && self.ptp()
    }
    pub fn realtime(&self) -> bool {
        self.feature(9) == Some(true)
    }
    pub fn needs_pin(&self) -> bool {
        self.flags.is_some_and(|v| v & (0x8 | 0x200) != 0)
    }
    pub fn needs_password(&self) -> bool {
        self.password == Some(true) || self.flags.is_some_and(|v| v & 0x80 != 0)
    }
    pub fn homepod(&self) -> bool {
        self.model.starts_with("AudioAccessory") || self.model.starts_with("HomePod")
    }
    pub fn supports_format(&self, alac: bool, rate: u32) -> bool {
        if !self.codecs.is_empty() && !self.codecs.contains(&u8::from(alac)) {
            return false;
        }
        if !self.rates.is_empty() && !self.rates.contains(&rate) {
            return false;
        }
        self.formats
            .is_none_or(|v| v & rtp::audio_format(alac, rate) != 0)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Transport {
    Realtime,
    Buffered,
}
impl Transport {
    pub fn name(self) -> &'static str {
        match self {
            Self::Realtime => "realtime",
            Self::Buffered => "buffered",
        }
    }
}
#[derive(Clone, Debug)]
pub struct Profile {
    pub timing: String,
    pub transport: Transport,
    pub alac: bool,
    pub rate: u32,
    pub latency_ms: u32,
    pub legacy: bool,
    pub reasons: Vec<String>,
}
impl Profile {
    pub fn resolve(
        cap: &Capabilities,
        timing: &str,
        transport: &str,
        rate: u32,
        latency: u32,
        stereo: bool,
    ) -> Result<Self> {
        let legacy = transport == "legacy" || stereo;
        ensure!(
            !stereo || transport != "buffered",
            "buffered transport is limited to a single receiver"
        );
        let timing = if timing == "auto" {
            if legacy || cap.homepod() || cap.ptp() {
                "ptp"
            } else {
                "ntp"
            }
        } else {
            timing
        };
        let selected = match transport {
            "buffered" => Transport::Buffered,
            "auto"
                if !legacy
                    && !cap.homepod()
                    && cap.buffered()
                    && timing == "ptp"
                    && [44100, 48000]
                        .iter()
                        .any(|rate| cap.supports_format(true, *rate)) =>
            {
                Transport::Buffered
            }
            _ => Transport::Realtime,
        };
        if selected == Transport::Buffered && (timing != "ptp" || !cap.buffered()) {
            return Err(Fault::new(
                "",
                "capabilities",
                "unsupported_transport",
                false,
                "Buffered audio requires advertised buffered audio and PTP support",
            )
            .into());
        }
        let mut reasons = Vec::new();
        let candidates = if legacy || cap.homepod() {
            [false, true]
        } else {
            [true, false]
        };
        let rates = if rate == 48000 {
            [48000, 44100]
        } else {
            [44100, 44100]
        };
        let mut format = None;
        for candidate_rate in rates {
            for alac in candidates {
                if selected == Transport::Buffered && !alac {
                    continue;
                }
                // Unknown 48k support is not evidence of support on an experimental receiver.
                if !legacy
                    && !cap.homepod()
                    && candidate_rate == 48000
                    && cap.formats.is_none()
                    && !cap.rates.contains(&48000)
                {
                    continue;
                }
                if cap.supports_format(alac, candidate_rate) {
                    format = Some((alac, candidate_rate));
                    break;
                }
            }
            if format.is_some() {
                break;
            }
        }
        let (alac, negotiated_rate) = format.ok_or_else(|| {
            Fault::new(
                "",
                "capabilities",
                "unsupported_format",
                false,
                "Receiver supports no compatible 16-bit stereo PCM/ALAC format",
            )
        })?;
        if negotiated_rate != rate {
            reasons.push("sample_rate_fallback".into());
        }
        Ok(Self {
            timing: timing.into(),
            transport: selected,
            alac,
            rate: negotiated_rate,
            latency_ms: latency,
            legacy,
            reasons,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn masks_and_unknown_fields() {
        assert_eq!(parse_mask("0x1,0x102"), Some((0x102 << 32) | 1));
        assert_eq!(parse_mask("0X10"), Some(16));
        assert_eq!(parse_mask("0x100000000,0x1"), None);
        assert_eq!(parse_mask("bad"), None);
        assert_eq!(parse_flags("200"), Some(0x200));
        assert_eq!(parse_flags("0x80"), Some(0x80));
        let cap = Capabilities::read(&[], &[], &Value::Dictionary(Default::default())).unwrap();
        assert_eq!(cap.feature(41), None);
        let p = Profile::resolve(&cap, "auto", "auto", 48000, 3000, false).unwrap();
        assert_eq!(
            (p.timing.as_str(), p.alac, p.rate, p.latency_ms),
            ("ntp", true, 44100, 3000)
        );
    }
    #[test]
    fn buffered_and_pcm_only() {
        let mut cap = Capabilities {
            features: Some((1 << 40) | (1 << 41)),
            ..Default::default()
        };
        assert_eq!(
            Profile::resolve(&cap, "auto", "auto", 44100, 0, false)
                .unwrap()
                .transport,
            Transport::Buffered
        );
        cap.codecs = vec![0];
        assert!(Profile::resolve(&cap, "auto", "buffered", 44100, 0, false).is_err());
        assert!(
            !Profile::resolve(&cap, "ntp", "realtime", 44100, 3000, false)
                .unwrap()
                .alac
        );
        assert!(Profile::resolve(&cap, "ntp", "buffered", 44100, 3000, false).is_err());
    }

    #[test]
    fn info_refines_conflicting_advertisements_and_input_formats() {
        let services = vec![
            Service {
                service_type: "_raop._tcp".into(),
                host: "127.0.0.1".parse().unwrap(),
                port: 5000,
                txt: [
                    ("ft".into(), "0x200,0x300".into()),
                    ("cn".into(), "0,1".into()),
                ]
                .into(),
            },
            Service {
                service_type: "_airplay._tcp".into(),
                host: "127.0.0.1".parse().unwrap(),
                port: 7000,
                txt: [
                    ("features".into(), "0x200,0x0".into()),
                    ("pw".into(), "false".into()),
                ]
                .into(),
            },
        ];
        let input = rtp::audio_format(false, 48000);
        let info = Value::Dictionary(
            [
                (
                    "features".to_owned(),
                    Value::Integer(((1u64 << 40) | (1u64 << 41)).into()),
                ),
                (
                    "audioFormats".to_owned(),
                    Value::Array(vec![Value::Dictionary(
                        [
                            ("audioInputFormats".to_owned(), Value::Integer(input.into())),
                            (
                                "audioOutputFormats".to_owned(),
                                Value::Integer(rtp::audio_format(true, 44100).into()),
                            ),
                        ]
                        .into_iter()
                        .collect(),
                    )]),
                ),
                (
                    "timingProtocols".to_owned(),
                    Value::Array(vec![Value::String("NTP".into())]),
                ),
            ]
            .into_iter()
            .collect(),
        );
        let cap = Capabilities::read(&services, &[], &info).unwrap();
        assert_eq!(cap.features, Some((1 << 40) | (1 << 41)));
        assert!(cap.ntp());
        assert!(!cap.ptp());
        assert!(!cap.buffered());
        let p = Profile::resolve(&cap, "auto", "auto", 48000, 0, false).unwrap();
        assert_eq!(
            (p.transport, p.alac, p.rate, p.latency_ms),
            (Transport::Realtime, false, 48000, 0)
        );
        assert!(Profile::resolve(&cap, "auto", "auto", 44100, 3000, false).is_err());
    }
}
