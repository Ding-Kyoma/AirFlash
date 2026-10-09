//! Local protocol peers enforce the wire contract independently of session setup.
use crate::{
    auth::protocol_tests::{respond, server_srp},
    crypto::{Cipher, derive},
    rtsp::{Cancellation, Connection, Message},
    session::{ProbeOptions, probe_local as probe},
    transport::Fault,
};
use plist::{Dictionary, Value};
use serde_json::json;
use std::{
    io::Read,
    net::{TcpListener, UdpSocket},
    path::PathBuf,
    sync::{Arc, Mutex, atomic::AtomicU32},
    thread,
    time::Duration,
};
static RECEIVER_TEST: Mutex<()> = Mutex::new(());
fn plist(items: &[(&str, Value)]) -> Value {
    Value::Dictionary(
        items
            .iter()
            .map(|(k, v)| (k.to_string(), v.clone()))
            .collect::<Dictionary>(),
    )
}
fn reply(c: &mut Connection, request: &Message, value: &Value) {
    let mut bytes = Vec::new();
    value.to_writer_binary(&mut bytes).unwrap();
    respond(c, request, &bytes);
}
fn reject(c: &mut Connection, request: &Message, status: u16) {
    c.write(
        format!(
            "RTSP/1.0 {status} Rejected\r\nCSeq: {}\r\nContent-Length: 0\r\n\r\n",
            request.headers["cseq"]
        )
        .as_bytes(),
    )
    .unwrap();
}
struct Wav(PathBuf);
impl Wav {
    fn new(rate: u32) -> Self {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("target")
            .join(format!("probe-{}.wav", uuid::Uuid::new_v4()));
        let mut wav = hound::WavWriter::create(
            &path,
            hound::WavSpec {
                channels: 2,
                sample_rate: rate,
                bits_per_sample: 16,
                sample_format: hound::SampleFormat::Int,
            },
        )
        .unwrap();
        for _ in 0..rate / 2 {
            wav.write_sample(100i16).unwrap();
            wav.write_sample(-50i16).unwrap();
        }
        wav.finalize().unwrap();
        Self(path)
    }
}
impl Drop for Wav {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.0);
    }
}

#[test]
fn third_party_wire_contract_and_fresh_connection_fallback() {
    let _serial = RECEIVER_TEST.lock().unwrap();
    for (buffered, reject_first, reject_rate) in [
        (false, false, false),
        (true, false, false),
        (true, true, false),
        (false, true, true),
    ] {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let event = TcpListener::bind("127.0.0.1:0").unwrap();
        let udp = UdpSocket::bind("127.0.0.1:0").unwrap();
        udp.set_read_timeout(Some(Duration::from_secs(8))).unwrap();
        let tcp = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let event_port = event.local_addr().unwrap().port();
        let udp_port = udp.local_addr().unwrap().port();
        let tcp_port = tcp.local_addr().unwrap().port();
        let packets = Arc::new(Mutex::new(Vec::<Vec<u8>>::new()));
        let captured = packets.clone();
        let tcp_mode = buffered && !reject_first;
        let reader = thread::spawn(move || {
            if tcp_mode {
                let (mut socket, _) = tcp.accept().unwrap();
                socket
                    .set_read_timeout(Some(Duration::from_secs(8)))
                    .unwrap();
                loop {
                    let mut prefix = [0; 2];
                    if socket.read_exact(&mut prefix).is_err() {
                        break;
                    }
                    let len = u16::from_be_bytes(prefix) as usize;
                    assert!(len > 2 && len < 4096);
                    let mut packet = vec![0; len - 2];
                    socket.read_exact(&mut packet).unwrap();
                    captured.lock().unwrap().push(packet);
                }
            } else {
                let mut bytes = [0; 4096];
                while let Ok((len, _)) = udp.recv_from(&mut bytes) {
                    if len >= 12 && bytes[1] & 0x7f == 96 {
                        captured.lock().unwrap().push(bytes[..len].to_vec());
                    }
                    udp.set_read_timeout(Some(Duration::from_secs(1))).unwrap();
                }
            }
        });
        let key = Arc::new(Mutex::new([0u8; 32]));
        let media_key = key.clone();
        let server = thread::spawn(move || {
            for attempt in 0..=usize::from(reject_first) {
                let (socket, _) = listener.accept().unwrap();
                let mut c = Connection::from_stream(socket, Cancellation::default()).unwrap();
                let request = c.read().unwrap();
                assert!(request.first.starts_with("GET /info"));
                let features = if buffered {
                    (1u64 << 9) | (1u64 << 40) | (1u64 << 41)
                } else {
                    1u64 << 9
                };
                let mut info = plist(&[
                    ("model", Value::String("SimulatedSpeaker".into())),
                    ("features", Value::Integer(features.into())),
                ]);
                if reject_rate {
                    info.as_dictionary_mut().unwrap().insert(
                        "audioFormats".into(),
                        Value::Integer(((1u64 << 18) | (1u64 << 20)).into()),
                    );
                }
                reply(&mut c, &request, &info);
                let secret = server_srp(&mut c, "3939");
                c.encrypt(
                    derive(&secret, "Control-Salt", "Control-Read-Encryption-Key"),
                    derive(&secret, "Control-Salt", "Control-Write-Encryption-Key"),
                );
                let request = c.read().unwrap();
                assert!(request.first.starts_with("SETUP"));
                let session = request.plist().unwrap();
                assert_eq!(
                    session.as_dictionary().unwrap()["timingProtocol"].as_string(),
                    Some(if buffered { "PTP" } else { "NTP" })
                );
                reply(
                    &mut c,
                    &request,
                    &plist(&[("eventPort", Value::Integer(event_port.into()))]),
                );
                let (_event_socket, _) = event.accept().unwrap();
                let request = c.read().unwrap();
                assert!(request.first.starts_with("RECORD"));
                assert!(request.body.is_empty());
                respond(&mut c, &request, &[]);
                if buffered {
                    let request = c.read().unwrap();
                    assert!(request.first.starts_with("SETPEERS"));
                    respond(&mut c, &request, &[]);
                }
                let request = c.read().unwrap();
                assert!(request.first.starts_with("SETUP"));
                let stream = request.plist().unwrap();
                let stream = stream.as_dictionary().unwrap()["streams"]
                    .as_array()
                    .unwrap()[0]
                    .as_dictionary()
                    .unwrap();
                let stream_buffered = stream["type"].as_unsigned_integer() == Some(103);
                assert_eq!(stream_buffered, buffered && attempt == 0);
                assert_eq!(
                    stream["sr"].as_unsigned_integer(),
                    Some(if reject_rate && attempt == 0 {
                        48000
                    } else {
                        44100
                    })
                );
                assert_eq!(stream["ct"].as_unsigned_integer(), Some(2));
                if stream_buffered {
                    assert!(!stream.contains_key("latencyMin"));
                    assert!(!stream.contains_key("latencyMax"));
                } else {
                    assert!(
                        stream["latencyMax"].as_unsigned_integer()
                            > stream["latencyMin"].as_unsigned_integer()
                    );
                }
                media_key
                    .lock()
                    .unwrap()
                    .copy_from_slice(stream["shk"].as_data().unwrap());
                if reject_first && attempt == 0 {
                    reject(&mut c, &request, 415);
                } else {
                    let response = plist(&[(
                        "streams",
                        Value::Array(vec![plist(&[
                            (
                                "dataPort",
                                Value::Integer(
                                    if stream_buffered { tcp_port } else { udp_port }.into(),
                                ),
                            ),
                            ("controlPort", Value::Integer(udp_port.into())),
                        ])]),
                    )]);
                    reply(&mut c, &request, &response);
                }
                let mut anchor_attempts = 0;
                loop {
                    let request = c.read().unwrap();
                    if request.first.starts_with("SETRATEANCHORTIME") {
                        let anchor = request.plist().unwrap();
                        assert!(
                            anchor.as_dictionary().unwrap()["networkTimeSecs"]
                                .as_unsigned_integer()
                                .unwrap()
                                > 1_000_000_000
                        );
                        anchor_attempts += 1;
                        if anchor_attempts == 1 {
                            reject(&mut c, &request, 400);
                        } else {
                            respond(&mut c, &request, &[]);
                        }
                    } else if request.first.starts_with("GET /info") {
                        reply(&mut c, &request, &info);
                    } else if request.first.starts_with("POST /feedback") {
                        reject(&mut c, &request, 404);
                    } else {
                        let done = request.first.starts_with("TEARDOWN");
                        assert!(done || request.first.starts_with("FLUSHBUFFERED"));
                        respond(&mut c, &request, &[]);
                        if done {
                            break;
                        }
                    }
                }
                if tcp_mode {
                    assert!(anchor_attempts >= 2);
                }
            }
        });
        let wav = Wav::new(48000);
        let options: ProbeOptions = serde_json::from_value(json!({"peers":[{"host":"127.0.0.1","port":address.port()}],"wav_path":wav.0,"source":"wav","duration_ms":500,"latency_ms":200,"gain":0.1,"sample_rate":48000,"timing":"auto","transport":"auto"})).unwrap();
        let events = Mutex::new(Vec::new());
        let result = probe(
            options,
            Cancellation::default(),
            Arc::new(AtomicU32::new(0.1f32.to_bits())),
            |v| events.lock().unwrap().push(v),
        );
        assert!(result.is_ok(), "{result:?}");
        server.join().unwrap();
        reader.join().unwrap();
        let events = events.into_inner().unwrap();
        let negotiated = events.iter().rfind(|v| v["event"] == "negotiated").unwrap();
        assert_eq!(
            negotiated["transport"],
            if tcp_mode { "buffered" } else { "realtime" }
        );
        assert_eq!(negotiated["effective_latency_ms"], 3000);
        assert_eq!(negotiated["sample_rate"], 44100);
        if reject_first {
            assert!(
                negotiated["fallback_reasons"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .any(|v| v
                        == if reject_rate {
                            "sample_rate_rejected_fallback"
                        } else {
                            "buffered_rejected_realtime_fallback"
                        })
            );
        }
        let key = *key.lock().unwrap();
        let mut cipher = Cipher::new(key);
        let encoder =
            alac_encoder::AlacEncoder::new(&alac_encoder::FormatDescription::alac(44100.0, 352, 2));
        let mut decoder =
            alac::Decoder::new(alac::StreamInfo::from_cookie(&encoder.magic_cookie()).unwrap());
        let packets = packets.lock().unwrap();
        assert!(!packets.is_empty());
        let mut decoded = vec![0i16; 704];
        for packet in packets.iter().take(3) {
            assert_eq!(packet[1] & 0x7f, if tcp_mode { 103 } else { 96 });
            let payload = cipher
                .decrypt(&packet[12..packet.len() - 8], &packet[4..12])
                .unwrap();
            let samples = decoder.decode_packet(&payload, &mut decoded).unwrap();
            assert_eq!(&samples[..4], &[10, -5, 10, -5]);
        }
    }
}

#[test]
fn authentication_restrictions_are_terminal_before_pair_setup() {
    let _serial = RECEIVER_TEST.lock().unwrap();
    for (flag, code) in [(0x8u64, "pairing_required"), (0x80, "password_required")] {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = thread::spawn(move || {
            let (socket, _) = listener.accept().unwrap();
            let mut c = Connection::from_stream(socket, Cancellation::default()).unwrap();
            let request = c.read().unwrap();
            reply(
                &mut c,
                &request,
                &plist(&[("statusFlags", Value::Integer(flag.into()))]),
            );
            assert!(c.read().is_err());
        });
        let wav = Wav::new(44100);
        let options = serde_json::from_value(json!({"peers":[{"host":"127.0.0.1","port":port}],"wav_path":wav.0,"source":"wav","duration_ms":500,"latency_ms":200,"gain":0.1,"timing":"auto","transport":"auto","handshake_only":true})).unwrap();
        let error = probe(
            options,
            Cancellation::default(),
            Arc::new(AtomicU32::new(0.1f32.to_bits())),
            |_| {},
        )
        .unwrap_err();
        let fault = error.downcast_ref::<Fault>().unwrap();
        assert_eq!(fault.code, code);
        assert!(!fault.retryable);
        server.join().unwrap();
    }
}

#[test]
fn transient_hap_authentication_rejection_requires_pairing() {
    let _serial = RECEIVER_TEST.lock().unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let server = thread::spawn(move || {
        let (socket, _) = listener.accept().unwrap();
        let mut c = Connection::from_stream(socket, Cancellation::default()).unwrap();
        let request = c.read().unwrap();
        reply(&mut c, &request, &plist(&[]));
        let request = c.read().unwrap();
        assert!(request.first.starts_with("POST /pair-setup"));
        respond(
            &mut c,
            &request,
            &crate::crypto::tlv_encode(&[(6, &[2]), (7, &[2])]),
        );
        assert!(c.read().is_err());
    });
    let wav = Wav::new(44100);
    let options = serde_json::from_value(json!({"peers":[{"host":"127.0.0.1","port":port}],"wav_path":wav.0,"duration_ms":100,"latency_ms":200,"gain":0.1,"timing":"auto","transport":"auto"})).unwrap();
    let error = probe(
        options,
        Cancellation::default(),
        Arc::new(AtomicU32::new(0.1f32.to_bits())),
        |_| {},
    )
    .unwrap_err();
    assert_eq!(
        error.downcast_ref::<Fault>().unwrap().code,
        "pairing_required"
    );
    server.join().unwrap();
}

#[test]
fn homepod_stereo_preserves_pcm_and_legacy_record_order() {
    let _serial = RECEIVER_TEST.lock().unwrap();
    let mut peers = Vec::new();
    let mut servers = Vec::new();
    for host in ["127.0.0.1", "127.0.0.2"] {
        let listener = TcpListener::bind((host, 0)).unwrap();
        let event = TcpListener::bind((host, 0)).unwrap();
        let udp = UdpSocket::bind((host, 0)).unwrap();
        let port = listener.local_addr().unwrap().port();
        let event_port = event.local_addr().unwrap().port();
        let udp_port = udp.local_addr().unwrap().port();
        peers.push(json!({"host":host,"port":port}));
        servers.push(thread::spawn(move || {
            let (socket, _) = listener.accept().unwrap();
            let mut c = Connection::from_stream(socket, Cancellation::default()).unwrap();
            let info = plist(&[("model", Value::String("AudioAccessory6,1".into()))]);
            let request = c.read().unwrap();
            reply(&mut c, &request, &info);
            let secret = server_srp(&mut c, "3939");
            c.encrypt(
                derive(&secret, "Control-Salt", "Control-Read-Encryption-Key"),
                derive(&secret, "Control-Salt", "Control-Write-Encryption-Key"),
            );
            let request = c.read().unwrap();
            assert!(request.first.starts_with("SETUP"));
            assert_eq!(
                request.plist().unwrap().as_dictionary().unwrap()["timingProtocol"].as_string(),
                Some("PTP")
            );
            reply(
                &mut c,
                &request,
                &plist(&[("eventPort", Value::Integer(event_port.into()))]),
            );
            let (_event_socket, _) = event.accept().unwrap();
            let request = c.read().unwrap();
            assert!(request.first.starts_with("SETPEERS"));
            respond(&mut c, &request, &[]);
            let request = c.read().unwrap();
            assert!(request.first.starts_with("SETUP"));
            let setup = request.plist().unwrap();
            let stream = setup.as_dictionary().unwrap()["streams"]
                .as_array()
                .unwrap()[0]
                .as_dictionary()
                .unwrap();
            assert_eq!(stream["ct"].as_unsigned_integer(), Some(1));
            assert_eq!(stream["sr"].as_unsigned_integer(), Some(48000));
            assert_eq!(stream["latencyMin"], stream["latencyMax"]);
            reply(
                &mut c,
                &request,
                &plist(&[(
                    "streams",
                    Value::Array(vec![plist(&[
                        ("dataPort", Value::Integer(udp_port.into())),
                        ("controlPort", Value::Integer(udp_port.into())),
                    ])]),
                )]),
            );
            for method in ["RECORD", "FLUSH"] {
                let request = c.read().unwrap();
                assert!(request.first.starts_with(method));
                assert!(request.headers.contains_key("rtp-info"));
                respond(&mut c, &request, &[]);
            }
            loop {
                let request = c.read().unwrap();
                if request.first.starts_with("GET /info") {
                    reply(&mut c, &request, &info);
                } else if request.first.starts_with("POST /feedback") {
                    reject(&mut c, &request, 404);
                } else {
                    assert!(request.first.starts_with("TEARDOWN"));
                    respond(&mut c, &request, &[]);
                    break;
                }
            }
        }));
    }
    let wav = Wav::new(48000);
    let options = serde_json::from_value(json!({"peers":peers,"wav_path":wav.0,"duration_ms":100,"latency_ms":120,"gain":0.1,"sample_rate":48000,"timing":"auto","transport":"auto"})).unwrap();
    probe(
        options,
        Cancellation::default(),
        Arc::new(AtomicU32::new(0.1f32.to_bits())),
        |_| {},
    )
    .unwrap();
    for server in servers {
        server.join().unwrap();
    }
}
