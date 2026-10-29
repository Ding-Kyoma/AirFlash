//! Exercises real encrypted RTSP, PTP, RTP/TCP and mixed group routing on loopback.
use super::run_session;
use crate::{
    auth::protocol_tests::{respond, server_srp},
    crypto::{Cipher, derive},
    profile::*,
    rtsp::{Cancellation, Connection},
    session::*,
};
use std::{
    io::Read,
    net::{TcpListener, UdpSocket},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU32, Ordering},
    },
    thread,
    time::{Duration, Instant},
};

struct Mock {
    peer: Peer,
    packets: Arc<Mutex<Vec<Vec<u8>>>>,
    key: Arc<Mutex<Option<[u8; 32]>>>,
    anchor: Arc<Mutex<Option<plist::Value>>>,
    methods: Arc<Mutex<Vec<String>>>,
    /// `timingProtocol` of every session SETUP, in order.
    setups: Arc<Mutex<Vec<String>>>,
    /// Whether that SETUP carried `timingPeerInfo` (i.e. the member joined the
    /// sender's PTP domain) instead of a `timingPort` (plain NTP).
    timing_peers: Arc<Mutex<Vec<bool>>>,
    stop: Arc<AtomicBool>,
    worker: thread::JoinHandle<()>,
}
fn plist_bytes(value: &plist::Value) -> Vec<u8> {
    let mut bytes = Vec::new();
    value.to_writer_binary(&mut bytes).unwrap();
    bytes
}
fn quiet_wav() -> std::path::PathBuf {
    let path = std::env::temp_dir().join(format!("airflash-mock-{}.wav", uuid::Uuid::new_v4()));
    let mut wav = hound::WavWriter::create(
        &path,
        hound::WavSpec {
            channels: 2,
            sample_rate: 44100,
            bits_per_sample: 16,
            sample_format: hound::SampleFormat::Int,
        },
    )
    .unwrap();
    for _ in 0..704 {
        wav.write_sample(0i16).unwrap();
    }
    wav.finalize().unwrap();
    path
}
fn mock(ip: &str, buffered: bool, alac: bool, channel: Channel, offset: i32, ap2: bool) -> Mock {
    mock_with_model(
        ip,
        buffered,
        alac,
        channel,
        offset,
        ap2,
        Some("AudioAccessory5,1"),
    )
}
/// Same as [`mock`], but `/info` reports the given model (or none).
fn mock_with_model(
    ip: &str,
    buffered: bool,
    alac: bool,
    channel: Channel,
    offset: i32,
    ap2: bool,
    model: Option<&'static str>,
) -> Mock {
    mock_with_cancel_and_model(ip, buffered, alac, channel, offset, ap2, model, None, true)
}
fn mock_with_cancel(
    ip: &str,
    buffered: bool,
    alac: bool,
    channel: Channel,
    offset: i32,
    ap2: bool,
    cancel_on_stream: Option<Cancellation>,
) -> Mock {
    mock_with_cancel_and_model(
        ip,
        buffered,
        alac,
        channel,
        offset,
        ap2,
        Some("AudioAccessory5,1"),
        cancel_on_stream,
        true,
    )
}
/// A receiver that is only probed with `/info`: the test expects the group to
/// be refused before pairing or SETUP.
fn mock_probe_only(
    ip: &str,
    alac: bool,
    channel: Channel,
    offset: i32,
    ap2: bool,
) -> Mock {
    mock_with_cancel_and_model(
        ip,
        false,
        alac,
        channel,
        offset,
        ap2,
        Some("AudioAccessory5,1"),
        None,
        false,
    )
}
/// `pair` = false models a receiver that is only probed: the session must be
/// refused between `/info` and pairing, so the worker records any request after
/// `/info` (there must be none) and then closes.
#[allow(clippy::too_many_arguments)]
fn mock_with_cancel_and_model(
    ip: &str,
    buffered: bool,
    alac: bool,
    channel: Channel,
    offset: i32,
    ap2: bool,
    model: Option<&'static str>,
    cancel_on_stream: Option<Cancellation>,
    pair: bool,
) -> Mock {
    let control = TcpListener::bind((ip, 0)).unwrap();
    let event = TcpListener::bind((ip, 0)).unwrap();
    let udp = UdpSocket::bind((ip, 0)).unwrap();
    udp.set_read_timeout(Some(Duration::from_millis(30)))
        .unwrap();
    let tcp = TcpListener::bind((ip, 0)).unwrap();
    let data_port = if buffered {
        tcp.local_addr().unwrap().port()
    } else {
        udp.local_addr().unwrap().port()
    };
    let peer = Peer {
        receiver_id: String::new(),
        host: ip.parse().unwrap(),
        port: control.local_addr().unwrap().port(),
        codecs: vec![0, 1],
        profile: Profile {
            codec: Codec::Auto,
            transport: if buffered {
                Transport::Buffered
            } else {
                Transport::Realtime
            },
            timing: if buffered { Timing::Ptp } else { Timing::Ntp },
            channel,
            sync_offset_ms: offset,
            latency_ms: Some(if buffered { 120 } else { 200 }),
            ..Default::default()
        },
    };
    let event_port = event.local_addr().unwrap().port();
    let packets = Arc::new(Mutex::new(Vec::new()));
    let key = Arc::new(Mutex::new(None));
    let anchor = Arc::new(Mutex::new(None));
    let methods = Arc::new(Mutex::new(Vec::new()));
    let setups = Arc::new(Mutex::new(Vec::new()));
    let timing_peers = Arc::new(Mutex::new(Vec::new()));
    let stop = Arc::new(AtomicBool::new(false));
    let (out, key_out, anchor_out, method_out, setup_out, timing_peer_out, done) = (
        packets.clone(),
        key.clone(),
        anchor.clone(),
        methods.clone(),
        setups.clone(),
        timing_peers.clone(),
        stop.clone(),
    );
    let worker = thread::spawn(move || {
        let (socket, _) = control.accept().unwrap();
        let mut c = Connection::from_stream(socket, Cancellation::default()).unwrap();
        let mut formats = plist::Dictionary::new();
        formats.insert(
            if buffered {
                "bufferStream"
            } else {
                "audioStream"
            }
            .into(),
            plist::Value::Integer(crate::rtp::audio_format(alac, 44100).into()),
        );
        let mut info = plist::Dictionary::new();
        info.insert("supportedFormats".into(), plist::Value::Dictionary(formats));
        info.insert(
            "features".into(),
            plist::Value::Integer(
                (if ap2 {
                    1u64 << 40
                } else if buffered {
                    1u64 << 41
                } else {
                    0
                })
                .into(),
            ),
        );
        if ap2 {
            // AirPlay 2 needs bit 40 *and* a modern firmware; the model decides
            // the solo timing answer and can be overridden per mock member.
            info.insert(
                "sourceVersion".into(),
                plist::Value::String("366.0".into()),
            );
            if let Some(model) = model {
                info.insert("model".into(), plist::Value::String(model.into()));
            }
        }
        info.insert("initialVolume".into(), plist::Value::Real(-15.0));
        let info = plist::Value::Dictionary(info);
        let request = c.read().unwrap();
        assert!(request.first.starts_with("GET /info"));
        respond(&mut c, &request, &plist_bytes(&info));
        if !pair {
            // The group is refused after the `/info` probes: the client must
            // close this connection without sending anything else. Record a
            // stray request (a SETUP here would be the bug this test guards)
            // and stop instead of answering it.
            if let Ok(request) = c.read() {
                method_out.lock().unwrap().push(
                    request
                        .first
                        .split_whitespace()
                        .next()
                        .unwrap_or("")
                        .to_owned(),
                );
            }
            return;
        }
        let secret = server_srp(&mut c, "3939");
        c.encrypt(
            derive(&secret, "Control-Salt", "Control-Read-Encryption-Key"),
            derive(&secret, "Control-Salt", "Control-Write-Encryption-Key"),
        );
        let event_thread = thread::spawn(move || {
            let (mut socket, _) = event.accept().unwrap();
            let mut bytes = [0; 32];
            while socket.read(&mut bytes).is_ok_and(|n| n > 0) {}
        });
        let mut media_thread = None;
        loop {
            let request = c.read().unwrap();
            let method = request.first.split_whitespace().next().unwrap().to_owned();
            method_out.lock().unwrap().push(method.clone());
            let mut response = plist::Dictionary::new();
            if method == "SETUP" {
                let body = request.plist().unwrap();
                let d = body.as_dictionary().unwrap();
                if let Some(timing) = d.get("timingProtocol").and_then(plist::Value::as_string) {
                    setup_out.lock().unwrap().push(timing.to_owned());
                    timing_peer_out
                        .lock()
                        .unwrap()
                        .push(d.contains_key("timingPeerInfo"));
                }
                if let Some(streams) = d.get("streams") {
                    if let Some(cancel) = &cancel_on_stream {
                        cancel.cancel();
                        continue; // No response: cancellation must still issue TEARDOWN.
                    }
                    let stream = streams.as_array().unwrap()[0].as_dictionary().unwrap();
                    assert_eq!(
                        stream["type"].as_unsigned_integer(),
                        Some(if buffered { 103 } else { 96 })
                    );
                    assert_eq!(
                        stream["audioFormat"].as_unsigned_integer(),
                        Some(crate::rtp::audio_format(alac, 44100))
                    );
                    assert_eq!(stream.contains_key("latencyMin"), !buffered);
                    *key_out.lock().unwrap() =
                        Some(stream["shk"].as_data().unwrap().try_into().unwrap());
                    let mut reply = plist::Dictionary::new();
                    reply.insert(
                        "dataPort".into(),
                        plist::Value::Integer(u64::from(data_port).into()),
                    );
                    reply.insert(
                        "controlPort".into(),
                        plist::Value::Integer(u64::from(udp.local_addr().unwrap().port()).into()),
                    );
                    if !buffered {
                        reply.insert("latencyMin".into(), plist::Value::Integer(8820u64.into()));
                    }
                    response.insert(
                        "streams".into(),
                        plist::Value::Array(vec![plist::Value::Dictionary(reply)]),
                    );
                    let out = out.clone();
                    let done = done.clone();
                    let udp = udp.try_clone().unwrap();
                    let timing = anchor_out.clone();
                    let tcp = tcp.try_clone().unwrap();
                    media_thread = Some(thread::spawn(move || {
                        if buffered {
                            let (mut socket, _) = tcp.accept().unwrap();
                            socket
                                .set_read_timeout(Some(Duration::from_millis(30)))
                                .unwrap();
                            let mut stash = Vec::new();
                            let mut buf = [0; 4096];
                            while !done.load(Ordering::Relaxed) {
                                match socket.read(&mut buf) {
                                    Ok(0) => break,
                                    Ok(n) => stash.extend_from_slice(&buf[..n]),
                                    Err(e)
                                        if matches!(
                                            e.kind(),
                                            std::io::ErrorKind::WouldBlock
                                                | std::io::ErrorKind::TimedOut
                                        ) => {}
                                    Err(e) => panic!("{e}"),
                                }
                                while stash.len() >= 2 {
                                    let length = u16::from_be_bytes([stash[0], stash[1]]) as usize;
                                    assert!(length >= 38);
                                    if stash.len() < length {
                                        break;
                                    }
                                    out.lock().unwrap().push(stash[2..length].to_vec());
                                    stash.drain(..length);
                                }
                            }
                        } else {
                            let mut buf = [0; 2048];
                            while !done.load(Ordering::Relaxed) {
                                if let Ok((n, _)) = udp.recv_from(&mut buf) {
                                    if buf[1] & 127 == 96 {
                                        out.lock().unwrap().push(buf[..n].to_vec());
                                    } else if (buf[1] & 127 == 0x54 || buf[1] & 127 == 0x57)
                                        && n >= 20
                                    {
                                        // 0x54 carries an NTP timestamp; 0x57 carries a
                                        // big-endian PTP-mapped nanosecond timestamp.
                                        let ns = if buf[1] & 127 == 0x54 {
                                            let sec =
                                                u32::from_be_bytes(buf[8..12].try_into().unwrap())
                                                    as u64
                                                    - 2_208_988_800;
                                            let frac =
                                                u32::from_be_bytes(buf[12..16].try_into().unwrap())
                                                    as u64;
                                            sec * 1_000_000_000
                                                + ((frac as u128 * 1_000_000_000) >> 32) as u64
                                        } else {
                                            u64::from_be_bytes(buf[8..16].try_into().unwrap())
                                        };
                                        let mut sync = plist::Dictionary::new();
                                        sync.insert(
                                            "network_ns".into(),
                                            plist::Value::Integer(ns.into()),
                                        );
                                        sync.insert(
                                            "rtpTime".into(),
                                            plist::Value::Integer(
                                                u64::from(u32::from_be_bytes(
                                                    buf[4..8].try_into().unwrap(),
                                                ))
                                                .into(),
                                            ),
                                        );
                                        *timing.lock().unwrap() =
                                            Some(plist::Value::Dictionary(sync));
                                    }
                                }
                            }
                        }
                    }));
                } else {
                    response.insert(
                        "eventPort".into(),
                        plist::Value::Integer(u64::from(event_port).into()),
                    );
                }
            } else if method == "SETRATEANCHORTIME" {
                *anchor_out.lock().unwrap() = Some(request.plist().unwrap());
            }
            if method == "GET" {
                respond(&mut c, &request, &plist_bytes(&info));
            } else {
                respond(
                    &mut c,
                    &request,
                    &plist_bytes(&plist::Value::Dictionary(response)),
                );
            }
            if method == "TEARDOWN" {
                done.store(true, Ordering::Relaxed);
                break;
            }
        }
        if let Some(media) = media_thread {
            media.join().unwrap();
        }
        event_thread.join().unwrap();
    });
    Mock {
        peer,
        packets,
        key,
        anchor,
        methods,
        setups,
        timing_peers,
        stop,
        worker,
    }
}
#[test]
fn cancelling_second_member_stream_setup_tears_down_both_without_sending_audio() {
    let cancel = Cancellation::default();
    let left = mock("127.0.0.2", false, false, Channel::Left, 0, false);
    let right = mock_with_cancel(
        "127.0.0.3",
        false,
        false,
        Channel::Right,
        0,
        false,
        Some(cancel.clone()),
    );
    let path = std::env::temp_dir().join(format!("airflash-cancel-{}.wav", uuid::Uuid::new_v4()));
    let mut wav = hound::WavWriter::create(
        &path,
        hound::WavSpec {
            channels: 2,
            sample_rate: 44100,
            bits_per_sample: 16,
            sample_format: hound::SampleFormat::Int,
        },
    )
    .unwrap();
    for _ in 0..704 {
        wav.write_sample(0i16).unwrap();
    }
    wav.finalize().unwrap();
    let options: ProbeOptions = serde_json::from_value(serde_json::json!({"peers":[],"source":"wav","duration_ms":400,"gain":0.1,"latency_ms":200,"wav_path":path,"group_mode":"software_stereo","handshake_only":true})).unwrap();
    let options = ProbeOptions {
        peers: vec![left.peer.clone(), right.peer.clone()],
        ..options
    };
    let equalizer = crate::equalizer::Control::new(options.equalizer, 44100).unwrap();
    let started = Instant::now();
    let outcome = run_session(
        options,
        cancel.clone(),
        Arc::new(AtomicU32::new(0.1f32.to_bits())),
        crate::volume::Control::default(),
        equalizer,
        |_| {},
        (crate::clock::Clock::new(), None),
    );
    std::fs::remove_file(path).unwrap();
    assert!(outcome.is_err());
    assert!(cancel.is_cancelled());
    assert!(started.elapsed() < Duration::from_secs(3));
    for member in [&left, &right] {
        assert_eq!(member.methods.lock().unwrap().last().unwrap(), "TEARDOWN");
        assert!(member.packets.lock().unwrap().is_empty());
    }
    left.worker.join().unwrap();
    right.worker.join().unwrap();
}
fn ptp_receiver(ip: &'static str, done: Arc<AtomicBool>) -> thread::JoinHandle<()> {
    let ptp_event = UdpSocket::bind((ip, 319)).unwrap();
    ptp_event
        .set_read_timeout(Some(Duration::from_millis(10)))
        .unwrap();
    let ptp_general = UdpSocket::bind((ip, 320)).unwrap();
    ptp_general
        .set_read_timeout(Some(Duration::from_millis(10)))
        .unwrap();
    thread::spawn(move || {
        let mut seq = 0u16;
        let mut next = Instant::now();
        let mut buffer = [0; 2048];
        while !done.load(Ordering::Relaxed) {
            if Instant::now() >= next {
                let mut request = vec![0; 44];
                request[0] = 0x11;
                request[1] = 2;
                request[2..4].copy_from_slice(&44u16.to_be_bytes());
                request[20..28].copy_from_slice(&42u64.to_be_bytes());
                request[28..30].copy_from_slice(&1u16.to_be_bytes());
                request[30..32].copy_from_slice(&seq.to_be_bytes());
                ptp_event.send_to(&request, "127.0.0.1:319").unwrap();
                seq = seq.wrapping_add(1);
                next = Instant::now() + Duration::from_millis(100);
            }
            let _ = ptp_event.recv_from(&mut buffer);
            let _ = ptp_general.recv_from(&mut buffer);
        }
    })
}
#[test]
fn mixed_transports_share_sample_timeline_route_channels_and_close_together() {
    let left = mock("127.0.0.2", true, true, Channel::Left, -80, true);
    // One clock domain: the realtime member uses the same PTP timing as the
    // buffered member; mixed transports are still allowed.
    let mut right = mock("127.0.0.3", false, false, Channel::Right, 25, true);
    right.peer.profile.timing = Timing::Ptp;
    let done = left.stop.clone();
    let left_ptp = ptp_receiver("127.0.0.2", done.clone());
    let right_ptp = ptp_receiver("127.0.0.3", done);
    let clock = crate::clock::Clock::new();
    let timer = crate::clock::PtpMaster::from_sockets(
        vec![left.peer.host, right.peer.host],
        vec![],
        clock.clone(),
        UdpSocket::bind("127.0.0.1:319").unwrap(),
        UdpSocket::bind("127.0.0.1:320").unwrap(),
    )
    .unwrap();
    let path = std::env::temp_dir().join(format!("airflash-mixed-{}.wav", uuid::Uuid::new_v4()));
    let mut wav = hound::WavWriter::create(
        &path,
        hound::WavSpec {
            channels: 2,
            sample_rate: 44100,
            bits_per_sample: 16,
            sample_format: hound::SampleFormat::Int,
        },
    )
    .unwrap();
    for _ in 0..44100 {
        wav.write_sample(1200i16).unwrap();
        wav.write_sample(-800i16).unwrap();
    }
    wav.finalize().unwrap();
    let options:ProbeOptions=serde_json::from_value(serde_json::json!({"peers":[],"source":"wav","duration_ms":400,"gain":0.1,"latency_ms":200,"wav_path":path,"group_mode":"software_stereo"})).unwrap();
    let options = ProbeOptions {
        peers: vec![left.peer.clone(), right.peer.clone()],
        ..options
    };
    let events = Mutex::new(Vec::new());
    let eq = crate::equalizer::Control::new(options.equalizer, 44100).unwrap();
    let outcome = run_session(
        options,
        Cancellation::default(),
        Arc::new(AtomicU32::new(0.1f32.to_bits())),
        crate::volume::Control::default(),
        eq,
        |event| events.lock().unwrap().push(event),
        (clock, Some(timer)),
    );
    std::fs::remove_file(path).unwrap();
    left.stop.store(true, Ordering::Relaxed);
    right.stop.store(true, Ordering::Relaxed);
    left_ptp.join().unwrap();
    right_ptp.join().unwrap();
    left.worker.join().unwrap();
    right.worker.join().unwrap();
    outcome.unwrap();
    let l = left.packets.lock().unwrap();
    let r = right.packets.lock().unwrap();
    assert!(l.len() > 20);
    assert_eq!(l.len(), r.len());
    assert_eq!(&l[0][4..8], &r[0][4..8]);
    for (mock, packets, alac, expected, pt) in [
        (&left.key, &*l, true, 120i16, 103),
        (&right.key, &*r, false, -80i16, 96),
    ] {
        let mut cipher = Cipher::new(mock.lock().unwrap().unwrap());
        let encoder =
            alac_encoder::AlacEncoder::new(&alac_encoder::FormatDescription::alac(44100.0, 352, 2));
        let mut decoder =
            alac::Decoder::new(alac::StreamInfo::from_cookie(&encoder.magic_cookie()).unwrap());
        for (i, packet) in packets.iter().enumerate() {
            assert_eq!(packet[1] & 127, pt);
            assert_eq!(
                u64::from_le_bytes(packet[packet.len() - 8..].try_into().unwrap()),
                i as u64
            );
            let clear = cipher
                .decrypt(&packet[12..packet.len() - 8], &packet[4..12])
                .unwrap();
            let mut decoded = vec![0i16; 704];
            if alac {
                decoder.decode_packet(&clear, &mut decoded).unwrap();
            } else {
                decoded = clear
                    .chunks_exact(2)
                    .map(|b| i16::from_be_bytes([b[0], b[1]]))
                    .collect();
            }
            assert!(decoded.iter().all(|sample| *sample == expected));
        }
    }
    assert_eq!(
        &left.methods.lock().unwrap()[..4],
        &["SETUP", "RECORD", "SETPEERS", "SETUP"]
    );
    assert_eq!(
        &right.methods.lock().unwrap()[..4],
        &["SETUP", "RECORD", "SETPEERS", "SETUP"]
    );
    // One clock domain even though the transports differ.
    assert_eq!(*left.setups.lock().unwrap(), ["PTP"]);
    assert_eq!(*right.setups.lock().unwrap(), ["PTP"]);
    for methods in [&left.methods, &right.methods] {
        let m = methods.lock().unwrap();
        assert_eq!(m.last().unwrap(), "TEARDOWN");
        assert!(!m.iter().any(|m| m == "FLUSH"));
    }
    let left_anchor = left.anchor.lock().unwrap();
    let d = left_anchor.as_ref().unwrap().as_dictionary().unwrap();
    let left_ns = d["networkTimeSecs"].as_unsigned_integer().unwrap() * 1_000_000_000
        + ((d["networkTimeFrac"].as_unsigned_integer().unwrap() as u128 * 1_000_000_000) >> 64)
            as u64;
    let right_anchor = right.anchor.lock().unwrap();
    let d = right_anchor.as_ref().unwrap().as_dictionary().unwrap();
    let first = u32::from_be_bytes(r[0][4..8].try_into().unwrap());
    let frames = (d["rtpTime"].as_unsigned_integer().unwrap() as u32).wrapping_sub(first);
    let right_ns = d["network_ns"].as_unsigned_integer().unwrap()
        - u64::from(frames) * 1_000_000_000 / 44100
        + 200_000_000;
    assert!((right_ns as i128 - left_ns as i128 - 105_000_000).abs() < 10);
    assert_eq!(
        events
            .lock()
            .unwrap()
            .iter()
            .filter(|e| e["event"] == "negotiated")
            .count(),
        2
    );
    let events = events.lock().unwrap();
    assert!(
        events
            .iter()
            .filter(|e| e["event"] == "negotiated")
            .all(|e| e["timing"] == "ptp"),
        "every member must stay on the group's PTP clock"
    );
    assert!(
        !events.iter().any(|e| e["code"] == "group_timing_plan"),
        "identical fingerprints must not warn"
    );
}
/// All members advertise AirPlay 2: the group keeps today's PTP decision.
#[test]
fn an_airplay2_group_keeps_ptp_on_every_member() {
    let mut left = mock("127.0.0.2", false, false, Channel::Left, 0, true);
    let mut right = mock("127.0.0.3", false, false, Channel::Right, 0, true);
    left.peer.receiver_id = "left".into();
    right.peer.receiver_id = "right".into();
    left.peer.profile.timing = Timing::Auto;
    right.peer.profile.timing = Timing::Auto;
    let path = quiet_wav();
    let options:ProbeOptions=serde_json::from_value(serde_json::json!({"peers":[],"source":"wav","duration_ms":400,"gain":0.1,"latency_ms":200,"wav_path":path,"group_mode":"software_stereo","handshake_only":true})).unwrap();
    let options = ProbeOptions {
        peers: vec![left.peer.clone(), right.peer.clone()],
        ..options
    };
    let clock = crate::clock::Clock::new();
    let timer = crate::clock::PtpMaster::from_sockets(
        vec![left.peer.host, right.peer.host],
        vec![],
        clock.clone(),
        UdpSocket::bind("127.0.0.1:0").unwrap(),
        UdpSocket::bind("127.0.0.1:0").unwrap(),
    )
    .unwrap();
    let events = Mutex::new(Vec::new());
    let eq = crate::equalizer::Control::new(options.equalizer, 44100).unwrap();
    let outcome = run_session(
        options,
        Cancellation::default(),
        Arc::new(AtomicU32::new(0.1f32.to_bits())),
        crate::volume::Control::default(),
        eq,
        |event| events.lock().unwrap().push(event),
        (clock, Some(timer)),
    );
    std::fs::remove_file(path).unwrap();
    left.stop.store(true, Ordering::Relaxed);
    right.stop.store(true, Ordering::Relaxed);
    left.worker.join().unwrap();
    right.worker.join().unwrap();
    outcome.unwrap();
    assert_eq!(*left.setups.lock().unwrap(), ["PTP"]);
    assert_eq!(*right.setups.lock().unwrap(), ["PTP"]);
    let events = events.lock().unwrap();
    let negotiated: Vec<_> = events
        .iter()
        .filter(|event| event["event"] == "negotiated")
        .collect();
    assert_eq!(negotiated.len(), 2);
    assert!(
        negotiated.iter().all(|event| event["timing"] == "ptp"),
        "every member must stay on the group's PTP clock"
    );
    assert!(
        !events
            .iter()
            .any(|event| event["code"] == "group_timing_plan")
    );
}
/// Q33: a mixed HomePod + Apple TV group is PTP, and the Apple TV joins the
/// sender's PTP domain even though its solo answer is NTP.
#[test]
fn a_mixed_homepod_and_apple_tv_group_runs_one_ptp_clock_domain() {
    let mut left = mock("127.0.0.2", false, false, Channel::Left, 0, true);
    let mut right = mock_with_model(
        "127.0.0.3",
        false,
        false,
        Channel::Right,
        0,
        true,
        Some("AppleTV6,2"),
    );    left.peer.receiver_id = "homepod".into();
    right.peer.receiver_id = "appletv".into();
    left.peer.profile.timing = Timing::Auto;
    right.peer.profile.timing = Timing::Auto;
    let path = quiet_wav();
    let options:ProbeOptions=serde_json::from_value(serde_json::json!({"peers":[],"source":"wav","duration_ms":400,"gain":0.1,"latency_ms":200,"wav_path":path,"group_mode":"software_stereo","handshake_only":true})).unwrap();
    let options = ProbeOptions {
        peers: vec![left.peer.clone(), right.peer.clone()],
        ..options
    };
    let clock = crate::clock::Clock::new();
    let timer = crate::clock::PtpMaster::from_sockets(
        vec![left.peer.host, right.peer.host],
        vec![],
        clock.clone(),
        UdpSocket::bind("127.0.0.1:0").unwrap(),
        UdpSocket::bind("127.0.0.1:0").unwrap(),
    )
    .unwrap();
    let events = Mutex::new(Vec::new());
    let eq = crate::equalizer::Control::new(options.equalizer, 44100).unwrap();
    let outcome = run_session(
        options,
        Cancellation::default(),
        Arc::new(AtomicU32::new(0.1f32.to_bits())),
        crate::volume::Control::default(),
        eq,
        |event| events.lock().unwrap().push(event),
        (clock, Some(timer)),
    );
    std::fs::remove_file(path).unwrap();
    left.stop.store(true, Ordering::Relaxed);
    right.stop.store(true, Ordering::Relaxed);
    left.worker.join().unwrap();
    right.worker.join().unwrap();
    outcome.unwrap();
    // Both members are configured with the group PTP decision. The Apple TV
    // also receives timingPeerInfo (the sender's clock), not an NTP port.
    assert_eq!(*left.setups.lock().unwrap(), ["PTP"]);
    assert_eq!(*right.setups.lock().unwrap(), ["PTP"]);
    assert_eq!(*left.timing_peers.lock().unwrap(), [true]);
    assert_eq!(*right.timing_peers.lock().unwrap(), [true]);
    let events = events.lock().unwrap();
    let negotiated: Vec<_> = events
        .iter()
        .filter(|event| event["event"] == "negotiated")
        .collect();
    assert_eq!(negotiated.len(), 2);
    assert!(
        negotiated.iter().all(|event| event["timing"] == "ptp"),
        "the Apple TV must join the group PTP clock, not stay on NTP"
    );
    assert!(
        !events
            .iter()
            .any(|event| event["event"] == "warning" && event["code"] == "group_timing_plan"),
        "a uniform group PTP plan must not emit a user-facing warning"
    );
}
/// An all-classic multi-member group still runs NTP on every member.
#[test]
fn an_all_classic_group_keeps_ntp_on_every_member() {
    let mut left = mock("127.0.0.2", false, false, Channel::Left, 0, false);
    let mut right = mock("127.0.0.3", false, false, Channel::Right, 0, false);
    left.peer.receiver_id = "left".into();
    right.peer.receiver_id = "right".into();
    left.peer.profile.timing = Timing::Auto;
    right.peer.profile.timing = Timing::Auto;
    let path = quiet_wav();
    let options:ProbeOptions=serde_json::from_value(serde_json::json!({"peers":[],"source":"wav","duration_ms":400,"gain":0.1,"latency_ms":200,"wav_path":path,"group_mode":"software_stereo","handshake_only":true})).unwrap();
    let options = ProbeOptions {
        peers: vec![left.peer.clone(), right.peer.clone()],
        ..options
    };
    let events = Mutex::new(Vec::new());
    let eq = crate::equalizer::Control::new(options.equalizer, 44100).unwrap();
    let outcome = run_session(
        options,
        Cancellation::default(),
        Arc::new(AtomicU32::new(0.1f32.to_bits())),
        crate::volume::Control::default(),
        eq,
        |event| events.lock().unwrap().push(event),
        (crate::clock::Clock::new(), None),
    );
    std::fs::remove_file(path).unwrap();
    left.stop.store(true, Ordering::Relaxed);
    right.stop.store(true, Ordering::Relaxed);
    left.worker.join().unwrap();
    right.worker.join().unwrap();
    outcome.unwrap();
    // Both members are configured with the group decision, not their own.
    assert_eq!(*left.setups.lock().unwrap(), ["NTP"]);
    assert_eq!(*right.setups.lock().unwrap(), ["NTP"]);
    let events = events.lock().unwrap();
    let negotiated: Vec<_> = events
        .iter()
        .filter(|event| event["event"] == "negotiated")
        .collect();
    assert_eq!(negotiated.len(), 2);
    assert!(
        negotiated.iter().all(|event| event["timing"] == "ntp"),
        "an all-classic group must stay on NTP"
    );
    // The plan is still a valid group decision, not a user-facing warning.
    assert!(
        !events
            .iter()
            .any(|event| event["event"] == "warning" && event["code"] == "group_timing_plan"),
        "the group timing diagnostic must not use the user-facing warning channel"
    );
}

/// Q39: a group whose members disagree on AirPlay 2 evidence is refused after
/// the `/info` probes and before any SETUP -- never downgraded to NTP.
#[test]
fn a_mixed_group_is_refused_before_any_setup() {
    let mut left = mock_probe_only("127.0.0.2", false, Channel::Left, 0, true);
    let mut right = mock_probe_only("127.0.0.3", false, Channel::Right, 0, false);
    left.peer.receiver_id = "left".into();
    right.peer.receiver_id = "right".into();
    left.peer.profile.timing = Timing::Auto;
    right.peer.profile.timing = Timing::Auto;
    let path = quiet_wav();
    let options:ProbeOptions=serde_json::from_value(serde_json::json!({"peers":[],"source":"wav","duration_ms":400,"gain":0.1,"latency_ms":200,"wav_path":path,"group_mode":"software_stereo","handshake_only":true})).unwrap();
    let options = ProbeOptions {
        peers: vec![left.peer.clone(), right.peer.clone()],
        ..options
    };
    let events = Mutex::new(Vec::new());
    let eq = crate::equalizer::Control::new(options.equalizer, 44100).unwrap();
    let outcome = run_session(
        options,
        Cancellation::default(),
        Arc::new(AtomicU32::new(0.1f32.to_bits())),
        crate::volume::Control::default(),
        eq,
        |event| events.lock().unwrap().push(event),
        (crate::clock::Clock::new(), None),
    );
    std::fs::remove_file(path).unwrap();
    // The probe-only mocks record every request after `/info`; nothing may have
    // arrived, and in particular no SETUP was ever sent to either receiver.
    for (mock, name) in [(&left, "left"), (&right, "right")] {
        assert!(
            mock.methods.lock().unwrap().is_empty(),
            "{name} received a request after /info"
        );
        assert!(
            mock.setups.lock().unwrap().is_empty(),
            "{name} received a session SETUP"
        );
        assert!(mock.packets.lock().unwrap().is_empty());
    }
    left.worker.join().unwrap();
    right.worker.join().unwrap();
    let fault = outcome
        .expect_err("a mixed group must not start")
        .downcast::<crate::transport::Fault>()
        .expect("the refusal must be a structured engine fault");
    assert_eq!(fault.code, "mixed_timing_group");
    assert_eq!(fault.code, crate::profile::MixedTimingGroup::CODE);
    assert_eq!(fault.channel, "setup");
    assert!(!fault.retryable, "a mixed group will not become legal on retry");
    assert!(
        fault.message.contains("Please connect them separately."),
        "{}",
        fault.message
    );
}
