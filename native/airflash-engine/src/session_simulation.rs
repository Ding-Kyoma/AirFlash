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
    stop: Arc<AtomicBool>,
    worker: thread::JoinHandle<()>,
}
fn plist_bytes(value: &plist::Value) -> Vec<u8> {
    let mut bytes = Vec::new();
    value.to_writer_binary(&mut bytes).unwrap();
    bytes
}
fn mock(ip: &str, buffered: bool, alac: bool, channel: Channel, offset: i32) -> Mock {
    mock_with_cancel(ip, buffered, alac, channel, offset, None)
}
fn mock_with_cancel(
    ip: &str,
    buffered: bool,
    alac: bool,
    channel: Channel,
    offset: i32,
    cancel_on_stream: Option<Cancellation>,
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
    let stop = Arc::new(AtomicBool::new(false));
    let (out, key_out, anchor_out, method_out, done) = (
        packets.clone(),
        key.clone(),
        anchor.clone(),
        methods.clone(),
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
            plist::Value::Integer((if buffered { 1u64 << 41 } else { 0 }).into()),
        );
        info.insert("initialVolume".into(), plist::Value::Real(-15.0));
        let info = plist::Value::Dictionary(info);
        let request = c.read().unwrap();
        assert!(request.first.starts_with("GET /info"));
        respond(&mut c, &request, &plist_bytes(&info));
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
                                    } else if buf[1] & 127 == 0x54 && n >= 20 {
                                        let sec = u32::from_be_bytes(buf[8..12].try_into().unwrap())
                                            as u64
                                            - 2_208_988_800;
                                        let frac =
                                            u32::from_be_bytes(buf[12..16].try_into().unwrap())
                                                as u64;
                                        let ns = sec * 1_000_000_000
                                            + ((frac as u128 * 1_000_000_000) >> 32) as u64;
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
        stop,
        worker,
    }
}
#[test]
fn cancelling_second_member_stream_setup_tears_down_both_without_sending_audio() {
    let cancel = Cancellation::default();
    let left = mock("127.0.0.2", false, false, Channel::Left, 0);
    let right = mock_with_cancel(
        "127.0.0.3",
        false,
        false,
        Channel::Right,
        0,
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
#[test]
fn mixed_transports_share_sample_timeline_route_channels_and_close_together() {
    let left = mock("127.0.0.2", true, true, Channel::Left, -80);
    let right = mock("127.0.0.3", false, false, Channel::Right, 25);
    let ptp_event = UdpSocket::bind("127.0.0.2:319").unwrap();
    ptp_event
        .set_read_timeout(Some(Duration::from_millis(10)))
        .unwrap();
    let ptp_general = UdpSocket::bind("127.0.0.2:320").unwrap();
    ptp_general
        .set_read_timeout(Some(Duration::from_millis(10)))
        .unwrap();
    let done = left.stop.clone();
    let ptp_receiver = thread::spawn(move || {
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
    });
    let clock = crate::clock::Clock::new();
    let timer = crate::clock::PtpMaster::from_sockets(
        vec![left.peer.host],
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
    ptp_receiver.join().unwrap();
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
        &right.methods.lock().unwrap()[..3],
        &["SETUP", "RECORD", "SETUP"]
    );
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
}
