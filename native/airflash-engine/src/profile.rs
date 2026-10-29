//! Receiver-advertised formats are evidence, not acoustic qualification.
use anyhow::{Result, ensure};
use plist::Value;
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Profile {
    #[serde(default)]
    pub codec: Codec,
    #[serde(default)]
    pub transport: Transport,
    #[serde(default)]
    pub timing: Timing,
    #[serde(default)]
    pub channel: Channel,
    #[serde(default)]
    pub sync_offset_ms: i32,
    #[serde(default)]
    pub features: Option<u64>,
    #[serde(default)]
    pub follow_receiver_clock: bool,
    #[serde(default)]
    pub latency_ms: Option<u32>,
}
#[derive(Clone, Copy, Debug, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum Codec {
    #[default]
    Legacy,
    Auto,
    Pcm,
    Alac,
}
#[derive(Clone, Copy, Debug, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum Transport {
    #[default]
    Realtime,
    Auto,
    Buffered,
}
#[derive(Clone, Copy, Debug, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum Timing {
    #[default]
    Inherit,
    Auto,
    Ptp,
    Ntp,
}
#[derive(Clone, Copy, Debug, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum Channel {
    #[default]
    Stereo,
    Left,
    Right,
}
impl Channel {
    pub fn map(self, pcm: &[u8]) -> Vec<u8> {
        if self == Self::Stereo {
            return pcm.to_vec();
        }
        pcm.chunks_exact(4)
            .flat_map(|f| {
                let i = if self == Self::Left { 0 } else { 2 };
                [f[i], f[i + 1], f[i], f[i + 1]]
            })
            .collect()
    }
}
#[derive(Clone, Copy, Debug, Default)]
pub struct Formats {
    pub realtime: Option<u64>,
    pub buffered: Option<u64>,
    pub features: Option<u64>,
}
/// AirPlay 2 (and therefore a buffered-audio capable peer) is advertised with
/// feature bit 40 *and* a firmware version. Bit 41 is not used by the reference
/// implementation at all, which is why it must not decide timing here.
pub const AIRPLAY2_BIT: u64 = 1 << 40;
const AIRPLAY2_MIN_VERSION: (u64, u64, u64) = (354, 54, 6);
pub fn is_airplay2(features: Option<u64>, source_version: Option<&str>) -> bool {
    let Some(features) = features else {
        return false;
    };
    // Both halves are required by the reference implementation: a device that sets
    // the bit but reports an old firmware is not treated as AirPlay 2.
    features & AIRPLAY2_BIT != 0 && version_at_least(source_version, AIRPLAY2_MIN_VERSION)
}
fn version_at_least(version: Option<&str>, minimum: (u64, u64, u64)) -> bool {
    let Some(version) = version else {
        return false;
    };
    let mut parts = version
        .split('.')
        .map(|part| part.parse::<u64>().unwrap_or(0));
    let parsed = (
        parts.next().unwrap_or(0),
        parts.next().unwrap_or(0),
        parts.next().unwrap_or(0),
    );
    parsed >= minimum
}
/// PTP is a topology decision, not a feature bit: a HomePod keeps its own
/// grandmaster and needs PTP, while a standalone Apple TV runs on NTP. `None`
/// means the model is unknown and the caller keeps its usual heuristic.
pub fn ptp_expected(model: Option<&str>) -> Option<bool> {
    let model = model?;
    if model.contains("AppleTV") {
        Some(false)
    } else if model.contains("AudioAccessory") || model.contains("HomePod") {
        Some(true)
    } else {
        None
    }
}
fn integer(v: &Value) -> Option<u64> {
    v.as_unsigned_integer()
        .or_else(|| v.as_signed_integer().map(|n| n as u64))
}
impl Formats {
    pub fn parse(info: &Value) -> Self {
        let get = |key: &str| {
            let root = info.as_dictionary()?;
            if let Some(values) = root
                .get("supportedAudioFormatsExtended")
                .and_then(Value::as_dictionary)
                .and_then(|d| d.get(key))
                .and_then(Value::as_array)
            {
                let mut mask = 0u64;
                for value in values {
                    let bit = integer(value)?;
                    if bit >= 64 {
                        continue;
                    }
                    mask |= 1u64 << bit;
                }
                Some(mask)
            } else {
                root.get("supportedFormats")
                    .and_then(Value::as_dictionary)
                    .and_then(|d| d.get(key))
                    .and_then(integer)
            }
        };
        Self {
            realtime: get("audioStream"),
            buffered: get("bufferStream"),
            features: info
                .as_dictionary()
                .and_then(|d| d.get("features"))
                .and_then(integer),
        }
    }
    pub fn negotiate(
        self,
        profile: &Profile,
        legacy_codecs: &[u8],
        rate: u32,
        inherited_ptp: bool,
    ) -> Result<Selection> {
        self.negotiate_with(profile, legacy_codecs, rate, inherited_ptp, None, None)
    }
    /// `model` and `source_version` come from `/info`; they let the timing choice
    /// follow the device instead of a feature bit that the reference implementation
    /// never reads.
    #[allow(clippy::too_many_arguments)]
    pub fn negotiate_with(
        self,
        profile: &Profile,
        legacy_codecs: &[u8],
        rate: u32,
        inherited_ptp: bool,
        model: Option<&str>,
        source_version: Option<&str>,
    ) -> Result<Selection> {
        ensure!(
            (-500..=500).contains(&profile.sync_offset_ms),
            "sync offset must be -500..500 ms"
        );
        let transport = match profile.transport {
            Transport::Auto
                if self.realtime.is_some_and(|f| {
                    f & (crate::rtp::audio_format(true, rate)
                        | crate::rtp::audio_format(false, rate))
                        == 0
                }) && self
                    .buffered
                    .is_some_and(|f| f & crate::rtp::audio_format(true, rate) != 0) =>
            {
                Transport::Buffered
            }
            Transport::Auto => Transport::Realtime,
            forced => forced,
        };
        let formats = if transport == Transport::Buffered {
            self.buffered
        } else {
            self.realtime
        };
        let supports =
            |alac| formats.is_some_and(|f| f & crate::rtp::audio_format(alac, rate) != 0);
        let legacy_alac =
            !legacy_codecs.is_empty() && !legacy_codecs.contains(&0) && legacy_codecs.contains(&1);
        let alac = match profile.codec {
            Codec::Auto if formats.is_some() => supports(true),
            Codec::Auto | Codec::Legacy => legacy_alac,
            Codec::Alac => true,
            Codec::Pcm => false,
        };
        if let Some(f) = formats {
            ensure!(
                f & crate::rtp::audio_format(alac, rate) != 0,
                "requested codec/sample rate is not advertised for this stream"
            );
        } else {
            ensure!(
                legacy_codecs.is_empty() || legacy_codecs.contains(&u8::from(alac)),
                "requested codec is not advertised"
            );
        }
        ensure!(
            transport != Transport::Buffered || alac,
            "buffered audio requires ALAC"
        );
        // A followed receiver supplies the timeline, so following it is PTP by
        // definition. A standalone HomePod on HomePod software 27 keeps its own
        // grandmaster and stays silent on a sender-owned or NTP-timed session;
        // an absent feature bit or an automatic timing choice must therefore
        // never downshift a followed receiver to NTP.
        let ptp = if profile.follow_receiver_clock {
            ensure!(
                profile.timing != Timing::Ntp,
                "following a receiver clock requires PTP timing"
            );
            true
        } else {
            match profile.timing {
                Timing::Inherit => inherited_ptp,
                Timing::Auto => match ptp_expected(model) {
                    Some(expectation) => expectation,
                    // Unknown model: rely on AirPlay 2 evidence alone. A peer that
                    // advertises nothing is an AirPlay 1 device and has no PTP.
                    None => is_airplay2(self.features.or(profile.features), source_version),
                },
                Timing::Ptp => {
                    let advertised = ptp_expected(model) == Some(true)
                        || is_airplay2(self.features.or(profile.features), source_version)
                        || inherited_ptp;
                    ensure!(
                        ptp_expected(model) != Some(false) && advertised,
                        "PTP timing is not advertised"
                    );
                    true
                }
                Timing::Ntp => false,
            }
        };
        ensure!(
            transport != Transport::Buffered || ptp,
            "buffered audio requires PTP timing"
        );
        Ok(Selection {
            alac,
            transport,
            ptp,
        })
    }
}
#[derive(Clone, Copy, Debug)]
pub struct Selection {
    pub alac: bool,
    pub transport: Transport,
    pub ptp: bool,
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn extended_formats_take_precedence_and_forced_profiles_never_fall_back() {
        let mut extended = plist::Dictionary::new();
        extended.insert(
            "audioStream".into(),
            Value::Array(vec![Value::Integer(18.into()), Value::Integer(20.into())]),
        );
        let mut legacy = plist::Dictionary::new();
        legacy.insert("audioStream".into(), Value::Integer((1u64 << 11).into()));
        let mut info = plist::Dictionary::new();
        info.insert(
            "supportedAudioFormatsExtended".into(),
            Value::Dictionary(extended),
        );
        info.insert("supportedFormats".into(), Value::Dictionary(legacy));
        let f = Formats::parse(&Value::Dictionary(info));
        assert_eq!(f.realtime, Some((1 << 18) | (1 << 20)));
        assert!(
            f.negotiate(
                &Profile {
                    codec: Codec::Auto,
                    ..Default::default()
                },
                &[0],
                44100,
                true
            )
            .unwrap()
            .alac
        );
        assert!(
            f.negotiate(
                &Profile {
                    codec: Codec::Pcm,
                    ..Default::default()
                },
                &[0, 1],
                44100,
                true
            )
            .is_err()
        );
        let f = Formats {
            realtime: Some(1 << 11),
            buffered: Some(1 << 20),
            features: Some(1 << 41),
        };
        assert_eq!(
            f.negotiate(
                &Profile {
                    codec: Codec::Auto,
                    transport: Transport::Auto,
                    ..Default::default()
                },
                &[],
                48000,
                true
            )
            .unwrap()
            .transport,
            Transport::Buffered
        );
    }
    #[test]
    fn selection_requires_matching_stream_format_and_keeps_unknown_legacy_behavior() {
        for rate in crate::rtp::SUPPORTED_RATES {
            let both = Formats {
                realtime: Some(
                    crate::rtp::audio_format(true, rate) | crate::rtp::audio_format(false, rate),
                ),
                ..Default::default()
            };
            let p = Profile {
                codec: Codec::Auto,
                ..Default::default()
            };
            assert!(both.negotiate(&p, &[0, 1], rate, true).unwrap().alac);
            assert!(
                !Formats::default()
                    .negotiate(&p, &[0, 1], rate, true)
                    .unwrap()
                    .alac
            );
            assert!(
                Formats {
                    realtime: Some(0),
                    ..Default::default()
                }
                .negotiate(&p, &[], rate, true)
                .is_err()
            );
            assert!(
                both.negotiate(
                    &Profile {
                        codec: Codec::Alac,
                        ..Default::default()
                    },
                    &[],
                    if rate == 44100 { 48000 } else { 44100 },
                    true
                )
                .is_err()
            );
        }
    }
    #[test]
    fn buffered_selection_and_ntp_are_independent_of_latency_preset() {
        let f = Formats {
            realtime: Some(0),
            buffered: Some(1 << 18),
            features: Some(AIRPLAY2_BIT),
        };
        let p = Profile {
            codec: Codec::Auto,
            transport: Transport::Auto,
            timing: Timing::Auto,
            ..Default::default()
        };
        assert_eq!(
            f.negotiate_with(&p, &[], 44100, false, None, Some("366.0"))
                .unwrap()
                .transport,
            Transport::Buffered
        );
        assert!(
            f.negotiate(
                &Profile {
                    timing: Timing::Ntp,
                    ..p
                },
                &[],
                44100,
                true
            )
            .is_err()
        );
    }

    #[test]
    fn airplay2_needs_the_bit_and_a_modern_firmware() {
        assert!(is_airplay2(Some(AIRPLAY2_BIT), Some("366.0")));
        assert!(is_airplay2(Some(AIRPLAY2_BIT | 1), Some("354.54.6")));
        assert!(!is_airplay2(Some(AIRPLAY2_BIT), Some("354.54.5")));
        assert!(!is_airplay2(Some(AIRPLAY2_BIT), None));
        assert!(!is_airplay2(Some(0), Some("366.0")));
        assert!(!is_airplay2(None, Some("366.0")));
        // Bit 41 carries no meaning in the reference implementation, so it must not
        // buy an AirPlay 2 verdict on its own.
        assert!(!is_airplay2(Some(1 << 41), Some("366.0")));
    }

    #[test]
    fn ptp_follows_the_model_before_any_feature_bit() {
        assert_eq!(ptp_expected(Some("AudioAccessory5,1")), Some(true));
        assert_eq!(ptp_expected(Some("HomePod1,1")), Some(true));
        assert_eq!(ptp_expected(Some("AppleTV6,2")), Some(false));
        assert_eq!(ptp_expected(Some("ThirdPartySpeaker")), None);
        assert_eq!(ptp_expected(None), None);
        // A HomePod runs PTP even when it advertises no feature bit at all.
        let formats = Formats {
            realtime: Some(1 << 18),
            ..Default::default()
        };
        let auto = Profile {
            codec: Codec::Auto,
            transport: Transport::Realtime,
            timing: Timing::Auto,
            ..Default::default()
        };
        assert!(
            formats
                .negotiate_with(&auto, &[], 44100, false, Some("AudioAccessory5,1"), None)
                .unwrap()
                .ptp
        );
        // An Apple TV on the same settings is NTP, and forcing PTP is rejected.
        assert!(
            !formats
                .negotiate_with(&auto, &[], 44100, false, Some("AppleTV6,2"), None)
                .unwrap()
                .ptp
        );
        assert!(
            formats
                .negotiate_with(
                    &Profile {
                        timing: Timing::Ptp,
                        ..auto
                    },
                    &[],
                    44100,
                    false,
                    Some("AppleTV6,2"),
                    None
                )
                .is_err()
        );
    }

    #[test]
    fn an_unknown_model_falls_back_to_airplay2_evidence() {
        let formats = Formats {
            realtime: Some(1 << 18),
            features: Some(AIRPLAY2_BIT),
            ..Default::default()
        };
        let auto = Profile {
            codec: Codec::Auto,
            transport: Transport::Realtime,
            timing: Timing::Auto,
            ..Default::default()
        };
        // Unknown model, but a modern AirPlay 2 firmware: PTP.
        assert!(
            formats
                .negotiate_with(&auto, &[], 44100, false, Some("Speaker9,9"), Some("366.0"))
                .unwrap()
                .ptp
        );
        // Unknown model without AirPlay 2 evidence: no PTP, whatever the session
        // asked for, because bit 41 alone means nothing.
        let quiet = Formats {
            realtime: Some(1 << 18),
            features: Some(1 << 41),
            ..Default::default()
        };
        assert!(
            !quiet
                .negotiate_with(&auto, &[], 44100, false, Some("Speaker9,9"), Some("366.0"))
                .unwrap()
                .ptp
        );
        assert!(
            !quiet
                .negotiate_with(&auto, &[], 44100, true, Some("Speaker9,9"), Some("366.0"))
                .unwrap()
                .ptp
        );
    }
    #[test]
    fn followed_receiver_keeps_ptp_without_an_advertised_feature_bit() {
        let formats = Formats {
            realtime: Some(1 << 18),
            features: Some(0),
            ..Default::default()
        };
        let followed = Profile {
            codec: Codec::Auto,
            transport: Transport::Auto,
            timing: Timing::Auto,
            follow_receiver_clock: true,
            ..Default::default()
        };
        let selection = formats
            .negotiate(&followed, &[0, 1], 44100, false)
            .unwrap();
        assert!(selection.ptp, "following a receiver clock must stay on PTP");
        assert!(selection.alac);
        assert_eq!(selection.transport, Transport::Realtime);
        assert!(
            formats
                .negotiate(
                    &Profile {
                        timing: Timing::Ntp,
                        ..followed.clone()
                    },
                    &[0, 1],
                    44100,
                    false
                )
                .is_err(),
            "an explicit NTP choice cannot follow a receiver clock"
        );
        // Without follow evidence the same receiver may legitimately advertise
        // no PTP support and use NTP.
        assert!(
            !formats
                .negotiate(
                    &Profile {
                        follow_receiver_clock: false,
                        ..followed
                    },
                    &[0, 1],
                    44100,
                    false
                )
                .unwrap()
                .ptp
        );
    }
    #[test]
    fn mono_routing_duplicates_only_the_assigned_channel() {
        let pcm = [0x12, 0x34, 0xfe, 0xdc, 0, 1, 0, 2];
        assert_eq!(
            Channel::Left.map(&pcm),
            [0x12, 0x34, 0x12, 0x34, 0, 1, 0, 1]
        );
        assert_eq!(
            Channel::Right.map(&pcm),
            [0xfe, 0xdc, 0xfe, 0xdc, 0, 2, 0, 2]
        );
        assert_eq!(Channel::Stereo.map(&pcm), pcm);
    }
}
