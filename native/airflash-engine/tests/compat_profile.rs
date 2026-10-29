//! One explicit characterization table for receiver compatibility.
//!
//! Every row is a `/info` fingerprint (model, feature word, firmware version,
//! legacy codec list, advertised stream formats) plus the session profile that
//! asks about it, together with the codec/transport/timing the sender must
//! choose. The table was written against the pre-refactor negotiation and must
//! keep passing unchanged: it is the proof that collecting the scattered
//! conditions into `CompatProfile` changed no outcome.

use airflash_engine::profile::{Codec, CompatFingerprint, CompatProfile, Profile, Timing, Transport};

/// Feature bit 40: "AirPlay 2 (including buffered audio)".
const BIT40: u64 = 1 << 40;
/// Feature bit 41: carries no meaning in the reference implementation.
const BIT41: u64 = 1 << 41;
/// PCM 44.1/48 and ALAC 44.1/48 advertised on the realtime stream.
const REALTIME: u64 = (1 << 11) | (1 << 15) | (1 << 18) | (1 << 20);
/// ALAC 44.1/48 advertised on the buffered stream.
const BUFFERED_ALAC: u64 = (1 << 18) | (1 << 20);
const PCM_44: u64 = 1 << 11;
const ALAC_48: u64 = 1 << 20;

#[derive(Clone, Copy, Debug)]
enum Expected {
    Ok {
        alac: bool,
        transport: Transport,
        ptp: bool,
        airplay2: bool,
    },
    Err(&'static str),
}

fn auto(ptp: bool, airplay2: bool) -> Expected {
    Expected::Ok {
        alac: true,
        transport: Transport::Realtime,
        ptp,
        airplay2,
    }
}

#[derive(Debug)]
struct Row {
    model: Option<&'static str>,
    features: Option<u64>,
    hint: Option<u64>,
    version: Option<&'static str>,
    legacy: &'static [u8],
    realtime: Option<u64>,
    buffered: Option<u64>,
    rate: u32,
    codec: Codec,
    transport: Transport,
    timing: Timing,
    follow: bool,
    offset_ms: i32,
    inherited_ptp: bool,
    expected: Expected,
}

impl Row {
    /// The common case: a HomePod-class receiver with modern AirPlay 2 evidence,
    /// both stream types advertised, and a plain auto session profile.
    fn base() -> Self {
        Self {
            model: Some("AudioAccessory5,1"),
            features: Some(BIT40),
            hint: None,
            version: Some("366.0"),
            legacy: &[],
            realtime: Some(REALTIME),
            buffered: Some(BUFFERED_ALAC),
            rate: 44100,
            codec: Codec::Auto,
            transport: Transport::Realtime,
            timing: Timing::Auto,
            follow: false,
            offset_ms: 0,
            inherited_ptp: false,
            expected: auto(true, true),
        }
    }
}

macro_rules! row {
    ($($field:ident: $value:expr),* $(,)?) => {
        Row { $($field: $value,)* ..Row::base() }
    };
}

fn info(
    model: Option<&str>,
    features: Option<u64>,
    version: Option<&str>,
    realtime: Option<u64>,
    buffered: Option<u64>,
) -> plist::Value {
    let mut formats = plist::Dictionary::new();
    if let Some(mask) = realtime {
        formats.insert("audioStream".into(), plist::Value::Integer(mask.into()));
    }
    if let Some(mask) = buffered {
        formats.insert("bufferStream".into(), plist::Value::Integer(mask.into()));
    }
    let mut root = plist::Dictionary::new();
    root.insert("supportedFormats".into(), plist::Value::Dictionary(formats));
    if let Some(features) = features {
        root.insert("features".into(), plist::Value::Integer(features.into()));
    }
    if let Some(model) = model {
        root.insert("model".into(), plist::Value::String(model.into()));
    }
    if let Some(version) = version {
        root.insert("sourceVersion".into(), plist::Value::String(version.into()));
    }
    plist::Value::Dictionary(root)
}

fn table() -> Vec<Row> {
    vec![
        // HomePod1,1: the model decides PTP; the feature word only decides
        // AirPlay 2 (bit 40 AND firmware >= 354.54.6), never timing.
        row!(model: Some("HomePod1,1"), features: None, version: None, legacy: &[], expected: auto(true, false)),
        row!(model: Some("HomePod1,1"), features: None, version: Some("354.54.5"), legacy: &[], expected: auto(true, false)),
        row!(model: Some("HomePod1,1"), features: None, version: Some("354.54.6"), legacy: &[], expected: auto(true, false)),
        row!(model: Some("HomePod1,1"), features: None, version: Some("366.0"), legacy: &[], expected: auto(true, false)),
        row!(model: Some("HomePod1,1"), features: Some(0), version: None, legacy: &[0], expected: auto(true, false)),
        row!(model: Some("HomePod1,1"), features: Some(0), version: Some("354.54.5"), legacy: &[0], expected: auto(true, false)),
        row!(model: Some("HomePod1,1"), features: Some(0), version: Some("354.54.6"), legacy: &[0], expected: auto(true, false)),
        row!(model: Some("HomePod1,1"), features: Some(0), version: Some("366.0"), legacy: &[0], expected: auto(true, false)),
        row!(model: Some("HomePod1,1"), features: Some(BIT40), version: None, legacy: &[0, 1], expected: auto(true, false)),
        row!(model: Some("HomePod1,1"), features: Some(BIT40), version: Some("354.54.5"), legacy: &[0, 1], expected: auto(true, false)),
        row!(model: Some("HomePod1,1"), features: Some(BIT40), version: Some("354.54.6"), legacy: &[0, 1], expected: auto(true, true)),
        row!(model: Some("HomePod1,1"), features: Some(BIT40), version: Some("366.0"), legacy: &[0, 1], expected: auto(true, true)),
        row!(model: Some("HomePod1,1"), features: Some(BIT40 | BIT41), version: None, legacy: &[1], expected: auto(true, false)),
        row!(model: Some("HomePod1,1"), features: Some(BIT40 | BIT41), version: Some("354.54.5"), legacy: &[1], expected: auto(true, false)),
        row!(model: Some("HomePod1,1"), features: Some(BIT40 | BIT41), version: Some("354.54.6"), legacy: &[1], expected: auto(true, true)),
        row!(model: Some("HomePod1,1"), features: Some(BIT40 | BIT41), version: Some("366.0"), legacy: &[1], expected: auto(true, true)),

        // AudioAccessory5,1: same PTP expectation as HomePod, same AirPlay 2 rule.
        row!(model: Some("AudioAccessory5,1"), features: None, version: None, legacy: &[], expected: auto(true, false)),
        row!(model: Some("AudioAccessory5,1"), features: None, version: Some("354.54.5"), legacy: &[], expected: auto(true, false)),
        row!(model: Some("AudioAccessory5,1"), features: None, version: Some("354.54.6"), legacy: &[], expected: auto(true, false)),
        row!(model: Some("AudioAccessory5,1"), features: None, version: Some("366.0"), legacy: &[], expected: auto(true, false)),
        row!(model: Some("AudioAccessory5,1"), features: Some(0), version: None, legacy: &[0], expected: auto(true, false)),
        row!(model: Some("AudioAccessory5,1"), features: Some(0), version: Some("354.54.5"), legacy: &[0], expected: auto(true, false)),
        row!(model: Some("AudioAccessory5,1"), features: Some(0), version: Some("354.54.6"), legacy: &[0], expected: auto(true, false)),
        row!(model: Some("AudioAccessory5,1"), features: Some(0), version: Some("366.0"), legacy: &[0], expected: auto(true, false)),
        row!(model: Some("AudioAccessory5,1"), features: Some(BIT40), version: None, legacy: &[0, 1], expected: auto(true, false)),
        row!(model: Some("AudioAccessory5,1"), features: Some(BIT40), version: Some("354.54.5"), legacy: &[0, 1], expected: auto(true, false)),
        row!(model: Some("AudioAccessory5,1"), features: Some(BIT40), version: Some("354.54.6"), legacy: &[0, 1], expected: auto(true, true)),
        row!(model: Some("AudioAccessory5,1"), features: Some(BIT40), version: Some("366.0"), legacy: &[0, 1], expected: auto(true, true)),
        row!(model: Some("AudioAccessory5,1"), features: Some(BIT40 | BIT41), version: None, legacy: &[1], expected: auto(true, false)),
        row!(model: Some("AudioAccessory5,1"), features: Some(BIT40 | BIT41), version: Some("354.54.5"), legacy: &[1], expected: auto(true, false)),
        row!(model: Some("AudioAccessory5,1"), features: Some(BIT40 | BIT41), version: Some("354.54.6"), legacy: &[1], expected: auto(true, true)),
        row!(model: Some("AudioAccessory5,1"), features: Some(BIT40 | BIT41), version: Some("366.0"), legacy: &[1], expected: auto(true, true)),

        // AppleTV6,2: a standalone Apple TV runs NTP even when it is fully
        // AirPlay 2 capable.
        row!(model: Some("AppleTV6,2"), features: None, version: None, legacy: &[], expected: auto(false, false)),
        row!(model: Some("AppleTV6,2"), features: None, version: Some("354.54.5"), legacy: &[], expected: auto(false, false)),
        row!(model: Some("AppleTV6,2"), features: None, version: Some("354.54.6"), legacy: &[], expected: auto(false, false)),
        row!(model: Some("AppleTV6,2"), features: None, version: Some("366.0"), legacy: &[], expected: auto(false, false)),
        row!(model: Some("AppleTV6,2"), features: Some(0), version: None, legacy: &[0], expected: auto(false, false)),
        row!(model: Some("AppleTV6,2"), features: Some(0), version: Some("354.54.5"), legacy: &[0], expected: auto(false, false)),
        row!(model: Some("AppleTV6,2"), features: Some(0), version: Some("354.54.6"), legacy: &[0], expected: auto(false, false)),
        row!(model: Some("AppleTV6,2"), features: Some(0), version: Some("366.0"), legacy: &[0], expected: auto(false, false)),
        row!(model: Some("AppleTV6,2"), features: Some(BIT40), version: None, legacy: &[0, 1], expected: auto(false, false)),
        row!(model: Some("AppleTV6,2"), features: Some(BIT40), version: Some("354.54.5"), legacy: &[0, 1], expected: auto(false, false)),
        row!(model: Some("AppleTV6,2"), features: Some(BIT40), version: Some("354.54.6"), legacy: &[0, 1], expected: auto(false, true)),
        row!(model: Some("AppleTV6,2"), features: Some(BIT40), version: Some("366.0"), legacy: &[0, 1], expected: auto(false, true)),
        row!(model: Some("AppleTV6,2"), features: Some(BIT40 | BIT41), version: None, legacy: &[1], expected: auto(false, false)),
        row!(model: Some("AppleTV6,2"), features: Some(BIT40 | BIT41), version: Some("354.54.5"), legacy: &[1], expected: auto(false, false)),
        row!(model: Some("AppleTV6,2"), features: Some(BIT40 | BIT41), version: Some("354.54.6"), legacy: &[1], expected: auto(false, true)),
        row!(model: Some("AppleTV6,2"), features: Some(BIT40 | BIT41), version: Some("366.0"), legacy: &[1], expected: auto(false, true)),

        // Unknown model: timing falls back to the AirPlay 2 evidence alone.
        row!(model: Some("ThirdPartySpeaker9,9"), features: None, version: None, legacy: &[], expected: auto(false, false)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: None, version: Some("354.54.5"), legacy: &[], expected: auto(false, false)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: None, version: Some("354.54.6"), legacy: &[], expected: auto(false, false)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: None, version: Some("366.0"), legacy: &[], expected: auto(false, false)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: Some(0), version: None, legacy: &[0], expected: auto(false, false)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: Some(0), version: Some("354.54.5"), legacy: &[0], expected: auto(false, false)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: Some(0), version: Some("354.54.6"), legacy: &[0], expected: auto(false, false)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: Some(0), version: Some("366.0"), legacy: &[0], expected: auto(false, false)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: Some(BIT40), version: None, legacy: &[0, 1], expected: auto(false, false)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: Some(BIT40), version: Some("354.54.5"), legacy: &[0, 1], expected: auto(false, false)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: Some(BIT40), version: Some("354.54.6"), legacy: &[0, 1], expected: auto(true, true)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: Some(BIT40), version: Some("366.0"), legacy: &[0, 1], expected: auto(true, true)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: Some(BIT40 | BIT41), version: None, legacy: &[1], expected: auto(false, false)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: Some(BIT40 | BIT41), version: Some("354.54.5"), legacy: &[1], expected: auto(false, false)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: Some(BIT40 | BIT41), version: Some("354.54.6"), legacy: &[1], expected: auto(true, true)),
        row!(model: Some("ThirdPartySpeaker9,9"), features: Some(BIT40 | BIT41), version: Some("366.0"), legacy: &[1], expected: auto(true, true)),

        // Stream selection: realtime first, buffered only when the realtime
        // stream cannot carry the asked-for codec/sample rate.
        row!(),
        row!(realtime: None, transport: Transport::Auto, expected: Expected::Ok { alac: false, transport: Transport::Realtime, ptp: true, airplay2: true }),
        row!(realtime: Some(0), transport: Transport::Auto, expected: Expected::Ok { alac: true, transport: Transport::Buffered, ptp: true, airplay2: true }),
        row!(realtime: Some(PCM_44), transport: Transport::Auto, expected: Expected::Ok { alac: false, transport: Transport::Realtime, ptp: true, airplay2: true }),
        row!(realtime: Some(PCM_44), transport: Transport::Auto, rate: 48000, expected: Expected::Ok { alac: true, transport: Transport::Buffered, ptp: true, airplay2: true }),
        row!(buffered: Some(PCM_44), codec: Codec::Pcm, transport: Transport::Buffered, expected: Expected::Err("buffered audio requires ALAC")),
        row!(codec: Codec::Alac, transport: Transport::Buffered, expected: Expected::Ok { alac: true, transport: Transport::Buffered, ptp: true, airplay2: true }),
        row!(buffered: Some(ALAC_48), transport: Transport::Buffered, expected: Expected::Err("requested codec/sample rate is not advertised for this stream")),
        row!(buffered: None, transport: Transport::Auto, expected: Expected::Ok { alac: true, transport: Transport::Realtime, ptp: true, airplay2: true }),

        // Legacy codec lists, with no supportedFormats at all.
        row!(model: None, features: None, version: None, legacy: &[0, 1], realtime: None, buffered: None, timing: Timing::Ntp, expected: Expected::Ok { alac: false, transport: Transport::Realtime, ptp: false, airplay2: false }),
        row!(model: None, features: None, version: None, legacy: &[1], realtime: None, buffered: None, timing: Timing::Ntp, expected: Expected::Ok { alac: true, transport: Transport::Realtime, ptp: false, airplay2: false }),
        row!(model: None, features: None, version: None, legacy: &[], realtime: None, buffered: None, timing: Timing::Ntp, expected: Expected::Ok { alac: false, transport: Transport::Realtime, ptp: false, airplay2: false }),
        row!(model: None, features: None, version: None, legacy: &[2], realtime: None, buffered: None, timing: Timing::Ntp, expected: Expected::Err("requested codec is not advertised")),
        row!(model: None, features: None, version: None, legacy: &[0], realtime: None, buffered: None, codec: Codec::Alac, timing: Timing::Ntp, expected: Expected::Err("requested codec is not advertised")),
        row!(model: None, features: None, version: None, legacy: &[1], realtime: None, buffered: None, codec: Codec::Pcm, timing: Timing::Ntp, expected: Expected::Err("requested codec is not advertised")),

        // The offset guard runs before any format decision.
        row!(offset_ms: 501, expected: Expected::Err("sync offset must be -500..500 ms")),

        // Following the receiver clock is PTP by definition.
        row!(follow: true, expected: Expected::Ok { alac: true, transport: Transport::Realtime, ptp: true, airplay2: true }),
        row!(model: None, features: None, version: None, follow: true, expected: Expected::Ok { alac: true, transport: Transport::Realtime, ptp: true, airplay2: false }),
        row!(model: Some("AppleTV6,2"), follow: true, expected: Expected::Ok { alac: true, transport: Transport::Realtime, ptp: true, airplay2: true }),
        row!(follow: true, timing: Timing::Ntp, expected: Expected::Err("following a receiver clock requires PTP timing")),

        // Inherit simply keeps whatever the session already chose.
        row!(timing: Timing::Inherit, expected: Expected::Ok { alac: true, transport: Transport::Realtime, ptp: false, airplay2: true }),
        row!(timing: Timing::Inherit, inherited_ptp: true, expected: Expected::Ok { alac: true, transport: Transport::Realtime, ptp: true, airplay2: true }),

        // Forced PTP still requires evidence, and never overrides a model that
        // says NTP.
        row!(model: Some("AppleTV6,2"), timing: Timing::Ptp, expected: Expected::Err("PTP timing is not advertised")),
        row!(model: None, features: None, version: None, timing: Timing::Ptp, expected: Expected::Err("PTP timing is not advertised")),
        row!(model: None, features: Some(BIT40), version: Some("366.0"), timing: Timing::Ptp, expected: Expected::Ok { alac: true, transport: Transport::Realtime, ptp: true, airplay2: true }),
        row!(model: None, features: Some(BIT40), version: Some("354.54.5"), timing: Timing::Ptp, inherited_ptp: true, expected: Expected::Ok { alac: true, transport: Transport::Realtime, ptp: true, airplay2: false }),

        // Buffered audio needs both ALAC and PTP.
        row!(timing: Timing::Ntp, transport: Transport::Buffered, codec: Codec::Alac, expected: Expected::Err("buffered audio requires PTP timing")),
        row!(model: Some("AppleTV6,2"), transport: Transport::Buffered, codec: Codec::Alac, expected: Expected::Err("buffered audio requires PTP timing")),
        row!(transport: Transport::Buffered, codec: Codec::Alac, expected: Expected::Ok { alac: true, transport: Transport::Buffered, ptp: true, airplay2: true }),

        // The discovery hint only fills a missing `/info` feature word.
        row!(model: None, features: None, hint: Some(BIT40), version: Some("366.0"), expected: Expected::Ok { alac: true, transport: Transport::Realtime, ptp: true, airplay2: true }),
        row!(model: None, features: Some(0), hint: Some(BIT40), version: Some("366.0"), expected: Expected::Ok { alac: true, transport: Transport::Realtime, ptp: false, airplay2: false }),
        row!(model: None, features: None, hint: Some(BIT40), version: None, expected: Expected::Ok { alac: true, transport: Transport::Realtime, ptp: false, airplay2: false }),
        row!(model: None, features: None, hint: Some(BIT40), version: Some("354.54.6"), expected: Expected::Ok { alac: true, transport: Transport::Realtime, ptp: true, airplay2: true }),
    ]
}

#[test]
fn info_fingerprints_map_to_the_characterized_codec_transport_timing() {
    let rows = table();
    // 64 fingerprint rows + 33 decision rows. The size is part of the oracle:
    // dropping a row must not silently shrink the characterization.
    assert_eq!(rows.len(), 97);
    for row in &rows {
        let profile = Profile {
            codec: row.codec,
            transport: row.transport,
            timing: row.timing,
            sync_offset_ms: row.offset_ms,
            features: row.hint,
            follow_receiver_clock: row.follow,
            ..Profile::default()
        };
        let info = info(
            row.model,
            row.features,
            row.version,
            row.realtime,
            row.buffered,
        );
        let result =
            CompatProfile::derive(&info, &profile, row.legacy, row.rate, row.inherited_ptp);
        match row.expected {
            Expected::Ok {
                alac,
                transport,
                ptp,
                airplay2,
            } => {
                let compat = result.unwrap_or_else(|error| panic!("{row:?} failed: {error:#}"));
                assert_eq!(compat.prefers_alac(), alac, "alac: {row:?}");
                assert_eq!(compat.transport(), transport, "transport: {row:?}");
                assert_eq!(compat.uses_ptp(), ptp, "ptp: {row:?}");
                assert_eq!(compat.airplay2(), airplay2, "airplay2: {row:?}");
                assert_eq!(
                    compat.codec_name(),
                    if alac { "alac" } else { "pcm" },
                    "codec name: {row:?}"
                );
                assert_eq!(
                    compat.timing_name(),
                    if ptp { "ptp" } else { "ntp" },
                    "timing name: {row:?}"
                );
            }
            Expected::Err(message) => {
                let error = result
                    .expect_err(&format!("{row:?} was accepted"))
                    .to_string();
                assert!(error.contains(message), "{row:?}: {error}");
            }
        }
    }
}

#[test]
fn model_fingerprint_maps_to_its_timing_expectation() {
    for (model, expected) in [
        (Some("HomePod1,1"), Some(true)),
        (Some("AudioAccessory5,1"), Some(true)),
        (Some("AppleTV6,2"), Some(false)),
        (Some("ThirdPartySpeaker9,9"), None),
        (None, None),
    ] {
        let info = info(model, None, None, Some(REALTIME), Some(BUFFERED_ALAC));
        let fingerprint = CompatFingerprint::from_info(&info, &Profile::default(), &[]);
        assert_eq!(fingerprint.ptp_expected(), expected, "{model:?}");
    }
}
