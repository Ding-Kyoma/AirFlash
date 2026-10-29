//! Read-only /info queries and the same profile negotiation used by playback.
use crate::{
    profile::{Codec, CompatFingerprint, Formats, Profile, Timing, TimingPlan, Transport},
    rtsp::{Cancellation, Connection},
    session::Peer,
};
use anyhow::Result;
use serde::Serialize;

#[derive(Clone, Debug, Serialize)]
pub struct ProfileChoice {
    pub codec: &'static str,
    pub transport: &'static str,
    pub timing: &'static str,
    pub sample_rate: u32,
    pub selected_codec: &'static str,
    pub selected_transport: &'static str,
    pub selected_timing: &'static str,
}
#[derive(Serialize)]
pub struct Capabilities {
    pub receiver_id: String,
    pub host: std::net::IpAddr,
    pub port: u16,
    pub known: bool,
    pub profiles: Vec<ProfileChoice>,
}
/// Enumerate the codec/transport/timing combinations this receiver accepts.
///
/// The fingerprint comes from the same `/info` parse playback uses, and the
/// timing answer comes from the same shared `select_timing` decision the session
/// runtime uses. The fingerprint carries no model/firmware evidence here on
/// purpose: capability prediction has always been made from formats and the
/// peer's own feature word alone, and adding the model would change the values
/// reported over IPC.
///
/// This is a per-receiver answer, so every plan is a one-member plan and the
/// output stays the characterized solo timing. The session runtime may override
/// it for a group: Q33 makes every multi-device AirPlay 2 group PTP, including
/// an Apple TV whose solo prediction is NTP. Group timing is not part of this
/// per-receiver IPC response; it is decided once in `session::run_stream`.
pub fn choices(fingerprint: &CompatFingerprint, evidence: &Profile) -> Vec<ProfileChoice> {
    let mut result = Vec::new();
    for rate in [44100, 48000] {
        for (codec_name, codec) in [
            ("auto", Codec::Auto),
            ("pcm", Codec::Pcm),
            ("alac", Codec::Alac),
        ] {
            let plan = TimingPlan::single(fingerprint, codec);
            for (transport_name, transport) in [
                ("auto", Transport::Auto),
                ("realtime", Transport::Realtime),
                ("buffered", Transport::Buffered),
            ] {
                for (timing_name, timing) in [
                    ("auto", Timing::Auto),
                    ("ptp", Timing::Ptp),
                    ("ntp", Timing::Ntp),
                    ("follow", Timing::Ptp),
                ] {
                    if timing_name == "follow" && !evidence.follow_receiver_clock {
                        continue;
                    }
                    let profile = Profile {
                        codec,
                        transport,
                        timing,
                        // Explicit sender PTP/NTP override automatic following,
                        // matching the profile sent by SessionController.
                        follow_receiver_clock: timing_name == "follow"
                            || timing_name == "auto" && evidence.follow_receiver_clock,
                        ..evidence.clone()
                    };
                    if let Ok(compat) = fingerprint.negotiate_planned(plan, &profile, rate, false) {
                        result.push(ProfileChoice {
                            codec: codec_name,
                            transport: transport_name,
                            timing: timing_name,
                            sample_rate: rate,
                            selected_codec: compat.codec_name(),
                            selected_transport: if compat.uses_buffered_stream() {
                                "buffered"
                            } else {
                                "realtime"
                            },
                            selected_timing: compat.timing_label(),
                        });
                    }
                }
            }
        }
    }
    result
}
pub fn query(peer: &Peer, cancellation: Cancellation) -> Result<Capabilities> {
    let mut connection = Connection::connect(
        std::net::SocketAddr::new(peer.host, peer.port),
        cancellation,
    )?;
    let info = connection.request("GET", "/info", &[], &[])?.plist()?;
    let fingerprint =
        CompatFingerprint::from_formats(Formats::parse(&info), &peer.profile, &peer.codecs);
    Ok(Capabilities {
        receiver_id: peer.receiver_id.clone(),
        host: peer.host,
        port: peer.port,
        known: fingerprint.advertises_audio(),
        profiles: choices(&fingerprint, &peer.profile),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Capability prediction works from the parsed formats plus the peer's own
    /// evidence, exactly like `query` does.
    fn fingerprint(formats: Formats, evidence: &Profile) -> CompatFingerprint {
        CompatFingerprint::from_formats(formats, evidence, &[])
    }

    #[test]
    fn capability_query_only_reads_info_and_closes_without_pairing_or_setup() {
        use std::io::{Read, Write};
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let server = std::thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            socket
                .set_read_timeout(Some(std::time::Duration::from_secs(2)))
                .unwrap();
            let mut buffer = [0; 4096];
            let received = socket.read(&mut buffer).unwrap();
            assert!(
                std::str::from_utf8(&buffer[..received])
                    .unwrap()
                    .starts_with("GET /info RTSP/1.0")
            );
            let mut dictionary = plist::Dictionary::new();
            let mut formats = plist::Dictionary::new();
            formats.insert(
                "audioStream".into(),
                plist::Value::Integer(crate::rtp::audio_format(false, 44100).into()),
            );
            dictionary.insert("supportedFormats".into(), plist::Value::Dictionary(formats));
            let mut body = Vec::new();
            plist::Value::Dictionary(dictionary)
                .to_writer_binary(&mut body)
                .unwrap();
            write!(
                socket,
                "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: {}\r\n\r\n",
                body.len()
            )
            .unwrap();
            socket.write_all(&body).unwrap();
            assert_eq!(socket.read(&mut buffer).unwrap(), 0);
        });
        let peer = Peer {
            receiver_id: "a".into(),
            host: address.ip(),
            port: address.port(),
            codecs: vec![],
            profile: Profile::default(),
        };
        let capabilities = query(&peer, Cancellation::default()).unwrap();
        assert!(capabilities.known);
        assert_eq!(capabilities.receiver_id, "a");
        server.join().unwrap();
    }
    #[test]
    fn advertised_combinations_and_predictions_match_playback() {
        let formats = Formats {
            realtime: Some(crate::rtp::audio_format(false, 44100)),
            buffered: Some(crate::rtp::audio_format(true, 48000)),
            features: Some(1 << 41),
        };
        let evidence = Profile {
            follow_receiver_clock: true,
            ..Profile::default()
        };
        let items = choices(&fingerprint(formats, &evidence), &evidence);
        assert!(items.iter().any(|p| p.sample_rate == 48000
            && p.codec == "auto"
            && p.transport == "auto"
            && p.selected_transport == "buffered"
            && p.selected_timing == "follow"));
        assert!(
            !items
                .iter()
                .any(|p| p.transport == "buffered" && (p.codec == "pcm" || p.timing == "ntp"))
        );
        assert!(
            !items
                .iter()
                .any(|p| p.sample_rate == 48000 && p.transport == "realtime")
        );
        assert!(
            !items
                .iter()
                .any(|p| p.sample_rate == 44100 && p.codec == "alac")
        );
    }
    #[test]
    fn follow_requires_receiver_evidence_and_ptp_requires_advertised_support() {
        let formats = Formats {
            realtime: Some(crate::rtp::audio_format(false, 44100)),
            features: Some(0),
            ..Formats::default()
        };
        let items = choices(
            &fingerprint(formats, &Profile::default()),
            &Profile::default(),
        );
        assert!(
            items
                .iter()
                .any(|p| p.timing == "auto" && p.selected_timing == "ntp")
        );
        assert!(
            !items
                .iter()
                .any(|p| p.timing == "ptp" || p.timing == "follow")
        );
    }

    #[test]
    fn follow_evidence_does_not_enable_sender_ptp_or_hide_explicit_ntp() {
        let formats = Formats {
            realtime: Some(crate::rtp::audio_format(false, 44100)),
            features: Some(0),
            ..Formats::default()
        };
        let evidence = Profile {
            follow_receiver_clock: true,
            ..Profile::default()
        };
        let items = choices(&fingerprint(formats, &evidence), &evidence);
        assert!(
            items
                .iter()
                .any(|p| p.timing == "auto" && p.selected_timing == "follow")
        );
        assert!(
            items
                .iter()
                .any(|p| p.timing == "follow" && p.selected_timing == "follow")
        );
        assert!(
            !items.iter().any(|p| p.timing == "ptp"),
            "explicit sender PTP must still require advertised support"
        );
        assert!(
            items
                .iter()
                .any(|p| p.timing == "ntp" && p.selected_timing == "ntp"),
            "explicit NTP disables following, just as it does during playback"
        );
    }

    #[test]
    fn clock_capability_predictions_match_playback_roles_and_feature_sources() {
        for features in [None, Some(0), Some(1 << 41)] {
            for evidence_features in [None, Some(0), Some(1 << 41)] {
                for follow_receiver_clock in [false, true] {
                    let formats = Formats {
                        realtime: Some(crate::rtp::audio_format(false, 44100)),
                        buffered: Some(crate::rtp::audio_format(true, 48000)),
                        features,
                    };
                    let evidence = Profile {
                        features: evidence_features,
                        follow_receiver_clock,
                        ..Profile::default()
                    };
                    let items = choices(&fingerprint(formats, &evidence), &evidence);
                    assert!(!items.is_empty());
                    for item in items {
                        // Reconstruct the wire profile used by SessionController:
                        // explicit sender PTP/NTP turn off automatic following.
                        let profile = Profile {
                            codec: match item.codec {
                                "auto" => Codec::Auto,
                                "pcm" => Codec::Pcm,
                                "alac" => Codec::Alac,
                                _ => unreachable!(),
                            },
                            transport: match item.transport {
                                "auto" => Transport::Auto,
                                "realtime" => Transport::Realtime,
                                "buffered" => Transport::Buffered,
                                _ => unreachable!(),
                            },
                            timing: match item.timing {
                                "auto" => Timing::Auto,
                                "ptp" | "follow" => Timing::Ptp,
                                "ntp" => Timing::Ntp,
                                _ => unreachable!(),
                            },
                            follow_receiver_clock: item.timing == "follow"
                                || item.timing == "auto" && follow_receiver_clock,
                            ..evidence.clone()
                        };
                        let selected = fingerprint(formats, &evidence)
                            .negotiate(&profile, item.sample_rate, true)
                            .unwrap_or_else(|error| {
                                panic!(
                                    "offered {item:?} failed playback: {error}; features={features:?}, evidence={evidence:?}"
                                )
                            });
                        assert_eq!(item.selected_codec, selected.codec_name());
                        assert_eq!(
                            item.selected_transport,
                            if selected.uses_buffered_stream() {
                                "buffered"
                            } else {
                                "realtime"
                            }
                        );
                        assert_eq!(item.selected_timing, selected.timing_label());
                    }
                }
            }
        }
    }
}
