//! Read-only /info queries and the same profile negotiation used by playback.
use crate::{
    profile::{Codec, Formats, Profile, Timing, Transport},
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
pub fn choices(formats: Formats, evidence: &Profile, codecs: &[u8]) -> Vec<ProfileChoice> {
    let mut result = Vec::new();
    for rate in [44100, 48000] {
        for (codec_name, codec) in [
            ("auto", Codec::Auto),
            ("pcm", Codec::Pcm),
            ("alac", Codec::Alac),
        ] {
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
                        ..evidence.clone()
                    };
                    if let Ok(selected) = formats.negotiate(&profile, codecs, rate, true) {
                        result.push(ProfileChoice {
                            codec: codec_name,
                            transport: transport_name,
                            timing: timing_name,
                            sample_rate: rate,
                            selected_codec: if selected.alac { "alac" } else { "pcm" },
                            selected_transport: if selected.transport == Transport::Buffered {
                                "buffered"
                            } else {
                                "realtime"
                            },
                            selected_timing: if !selected.ptp {
                                "ntp"
                            } else if timing_name == "follow"
                                || timing_name == "auto" && evidence.follow_receiver_clock
                            {
                                "follow"
                            } else {
                                "ptp"
                            },
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
    let formats = Formats::parse(&info);
    Ok(Capabilities {
        receiver_id: peer.receiver_id.clone(),
        host: peer.host,
        port: peer.port,
        known: formats.realtime.is_some() || formats.buffered.is_some(),
        profiles: choices(formats, &peer.profile, &peer.codecs),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
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
        let items = choices(formats, &evidence, &[]);
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
        let items = choices(formats, &Profile::default(), &[]);
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
}
