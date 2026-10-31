//! Read-only /info queries and the same profile negotiation used by playback.
use crate::{
    profile::{Codec, CompatFingerprint, Profile, Timing, TimingPlan, Transport},
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
/// runtime uses. Solo choices use the full model/firmware evidence; group queries
/// enumerate every member under the shared group timing plan.
pub fn choices(fingerprint: &CompatFingerprint, evidence: &Profile) -> Vec<ProfileChoice> {
    choices_planned(fingerprint, evidence, None)
}
fn choices_planned(
    fingerprint: &CompatFingerprint,
    evidence: &Profile,
    group: Option<TimingPlan>,
) -> Vec<ProfileChoice> {
    let mut result = Vec::new();
    for rate in [44100, 48000] {
        for (codec_name, codec) in [
            ("auto", Codec::Auto),
            ("pcm", Codec::Pcm),
            ("alac", Codec::Alac),
        ] {
            let plan = group.unwrap_or_else(|| TimingPlan::single(fingerprint, codec));
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
    Ok(query_group(std::slice::from_ref(peer), cancellation)?.remove(0))
}
pub fn query_group(peers: &[Peer], cancellation: Cancellation) -> Result<Vec<Capabilities>> {
    let mut fingerprints = Vec::new();
    for peer in peers {
        let mut connection = Connection::connect(
            std::net::SocketAddr::new(peer.host, peer.port),
            cancellation.clone(),
        )?;
        let info = connection.request("GET", "/info", &[], &[])?.plist()?;
        fingerprints.push(CompatFingerprint::from_info(
            &info,
            &peer.profile,
            &peer.codecs,
        ));
    }
    let evidence: Vec<_> = fingerprints
        .iter()
        .map(CompatFingerprint::timing_evidence)
        .collect();
    let plan = TimingPlan::select(&evidence, Codec::Auto).map_err(|error| {
        crate::transport::Fault::new(
            "",
            "setup",
            crate::profile::MixedTimingGroup::CODE,
            false,
            error,
        )
    })?;
    Ok(peers
        .iter()
        .zip(&fingerprints)
        .map(|(peer, fingerprint)| Capabilities {
            receiver_id: peer.receiver_id.clone(),
            host: peer.host,
            port: peer.port,
            known: fingerprint.advertises_audio(),
            profiles: choices_planned(fingerprint, &peer.profile, Some(plan)),
        })
        .collect())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::profile::Formats;

    fn info_peer(model: &str, version: Option<&str>) -> (Peer, std::thread::JoinHandle<()>) {
        use std::io::{Read, Write};
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let mut info = plist::Dictionary::new();
        info.insert("model".into(), plist::Value::String(model.into()));
        info.insert(
            "features".into(),
            plist::Value::Integer(crate::profile::AIRPLAY2_BIT.into()),
        );
        if let Some(version) = version {
            info.insert("sourceVersion".into(), plist::Value::String(version.into()));
        }
        let mut formats = plist::Dictionary::new();
        formats.insert(
            "audioStream".into(),
            plist::Value::Integer(
                (crate::rtp::audio_format(true, 44100) | crate::rtp::audio_format(false, 44100))
                    .into(),
            ),
        );
        info.insert("supportedFormats".into(), plist::Value::Dictionary(formats));
        let mut body = Vec::new();
        plist::Value::Dictionary(info)
            .to_writer_binary(&mut body)
            .unwrap();
        let worker = std::thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            socket
                .set_read_timeout(Some(std::time::Duration::from_secs(3)))
                .unwrap();
            let mut request = [0; 4096];
            assert!(socket.read(&mut request).unwrap() > 0);
            write!(
                socket,
                "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: {}\r\n\r\n",
                body.len()
            )
            .unwrap();
            socket.write_all(&body).unwrap();
        });
        (
            Peer {
                receiver_id: model.into(),
                host: address.ip(),
                port: address.port(),
                codecs: vec![],
                profile: Profile {
                    codec: Codec::Auto,
                    timing: Timing::Auto,
                    transport: Transport::Auto,
                    ..Default::default()
                },
            },
            worker,
        )
    }
    #[test]
    fn queries_keep_full_model_firmware_and_group_evidence() {
        let (tv, server) = info_peer("AppleTV6,2", Some("366.0"));
        let solo = query(&tv, Cancellation::default()).unwrap();
        server.join().unwrap();
        assert!(
            solo.profiles
                .iter()
                .any(|p| p.codec == "auto" && p.timing == "auto" && p.selected_timing == "ntp")
        );
        assert!(!solo.profiles.iter().any(|p| p.timing == "ptp"));
        let (pod, pod_server) = info_peer("AudioAccessory5,1", Some("366.0"));
        let (tv, tv_server) = info_peer("AppleTV6,2", Some("366.0"));
        let group = query_group(&[pod, tv], Cancellation::default()).unwrap();
        for member in group {
            assert!(
                member
                    .profiles
                    .iter()
                    .any(|p| p.codec == "auto" && p.timing == "auto" && p.selected_timing == "ptp")
            );
            assert!(member.profiles.iter().any(|p| p.timing == "ptp"));
        }
        pod_server.join().unwrap();
        tv_server.join().unwrap();
        let (known, a) = info_peer("AudioAccessory5,1", Some("366.0"));
        let (unknown, b) = info_peer("AudioAccessory6,1", None);
        assert_eq!(
            query_group(&[known, unknown], Cancellation::default())
                .unwrap()
                .len(),
            2
        );
        a.join().unwrap();
        b.join().unwrap();
    }

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
