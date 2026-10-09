//! Localhost stability checks; never connect to a real receiver or open audio hardware.
use airflash_engine::rtp::{FRAMES, PCM_BYTES, Packetizer, RATE};
use std::{
    net::UdpSocket,
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, Ordering},
    },
    thread,
    time::{Duration, Instant},
};

#[test]
fn buffered_localhost_continuity() {
    buffered_soak(Duration::from_secs(2));
}

#[test]
#[ignore = "30 minute wall-clock localhost TCP soak"]
fn thirty_minute_buffered_receiver() {
    buffered_soak(Duration::from_secs(1800));
}

fn buffered_soak(duration: Duration) {
    use airflash_engine::{
        buffered::{BufferedTransport, Media},
        crypto::Cipher,
        rtsp::Cancellation,
        transport::Health,
    };
    use std::{collections::HashSet, io::Read, net::TcpListener};
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let key = [33; 32];
    let mut transport = BufferedTransport::new(Packetizer::new(key, 65530, u32::MAX - 500, 7));
    transport.connect(listener.local_addr().unwrap()).unwrap();
    let (mut socket, _) = listener.accept().unwrap();
    socket
        .set_read_timeout(Some(Duration::from_secs(12)))
        .unwrap();
    let receiver = thread::spawn(move || {
        let mut count = 0u64;
        let mut cipher = Cipher::new(key);
        let mut nonces = HashSet::new();
        let mut sequence = 65530u16;
        let mut timestamp = u32::MAX - 500;
        loop {
            let mut prefix = [0; 2];
            if let Err(error) = socket.read_exact(&mut prefix) {
                assert_eq!(error.kind(), std::io::ErrorKind::UnexpectedEof);
                break;
            }
            let length = u16::from_be_bytes(prefix) as usize;
            assert!(length > 2 && length < 4096);
            let mut packet = vec![0; length - 2];
            // Deliberately split reads across RTP headers, payload, tag and nonce.
            for chunk in packet.chunks_mut(17) {
                socket.read_exact(chunk).unwrap();
            }
            assert_eq!(packet[1] & 0x7f, 103);
            assert_eq!(
                u16::from_be_bytes(packet[2..4].try_into().unwrap()),
                sequence
            );
            assert_eq!(
                u32::from_be_bytes(packet[4..8].try_into().unwrap()),
                timestamp
            );
            assert!(nonces.insert(u64::from_le_bytes(
                packet[packet.len() - 8..].try_into().unwrap()
            )));
            assert_eq!(
                cipher
                    .decrypt(&packet[12..packet.len() - 8], &packet[4..12])
                    .unwrap(),
                vec![0; PCM_BYTES]
            );
            sequence = sequence.wrapping_add(1);
            timestamp = timestamp.wrapping_add(FRAMES as u32);
            count += 1;
            if count % 13 == 0 {
                thread::sleep(Duration::from_millis(20));
            }
        }
        count
    });
    let mut media = Media::Buffered(Box::new(transport));
    let pcm = vec![0; PCM_BYTES];
    let start = Instant::now();
    let mut sent = 0u64;
    while start.elapsed() < duration {
        let deadline =
            start + Duration::from_nanos(sent * FRAMES as u64 * 1_000_000_000 / RATE as u64);
        if let Some(wait) = deadline.checked_duration_since(Instant::now()) {
            thread::sleep(wait);
        }
        media
            .send(
                &pcm,
                sent == 0,
                Instant::now(),
                deadline,
                &Health::default(),
            )
            .unwrap();
        sent += 1;
    }
    media
        .drain(&Cancellation::default(), Duration::ZERO)
        .unwrap();
    media.stop_buffered();
    assert_eq!(receiver.join().unwrap(), sent);
    assert!(sent > 100);
}
#[test]
#[ignore = "30 minute wall-clock localhost soak"]
fn thirty_minute_local_receivers() {
    let stop = Arc::new(AtomicBool::new(false));
    let received = Arc::new(AtomicU64::new(0));
    let mut endpoints = Vec::new();
    let mut workers = Vec::new();
    for _ in 0..2 {
        let socket = UdpSocket::bind("127.0.0.1:0").unwrap();
        socket
            .set_read_timeout(Some(Duration::from_millis(100)))
            .unwrap();
        endpoints.push(socket.local_addr().unwrap());
        let done = stop.clone();
        let count = received.clone();
        workers.push(thread::spawn(move || {
            let mut buf = [0u8; 2048];
            while !done.load(Ordering::Acquire) {
                if let Ok((n, _)) = socket.recv_from(&mut buf) {
                    assert_eq!(n, PCM_BYTES + 36);
                    assert_eq!(buf[0], 0x80);
                    count.fetch_add(1, Ordering::Relaxed);
                }
            }
        }));
    }
    let sender = UdpSocket::bind("127.0.0.1:0").unwrap();
    let pcm = vec![0; PCM_BYTES];
    let mut streams = [
        Packetizer::new([1; 32], 65530, u32::MAX - 500, 1),
        Packetizer::new([2; 32], 65530, u32::MAX - 500, 2),
    ];
    let start = Instant::now();
    let duration = Duration::from_secs(1800);
    let mut packets = 0u64;
    let mut late = 0u128;
    while start.elapsed() < duration {
        let deadline =
            start + Duration::from_nanos(packets * FRAMES as u64 * 1_000_000_000 / RATE as u64);
        if let Some(wait) = deadline.checked_duration_since(Instant::now()) {
            thread::sleep(wait);
        }
        late = late.max(
            Instant::now()
                .saturating_duration_since(deadline)
                .as_micros(),
        );
        for (stream, address) in streams.iter_mut().zip(&endpoints) {
            sender
                .send_to(&stream.packet(&pcm, packets == 0).unwrap(), address)
                .unwrap();
        }
        packets += 1;
    }
    thread::sleep(Duration::from_millis(100));
    stop.store(true, Ordering::Release);
    for w in workers {
        w.join().unwrap();
    }
    let received = received.load(Ordering::Relaxed);
    eprintln!(
        "SOAK elapsed_s={} sent={} received={} max_late_us={}",
        start.elapsed().as_secs_f64(),
        packets * 2,
        received,
        late
    );
    assert!(
        received >= packets * 2 * 999 / 1000,
        "unexpected localhost packet loss"
    );
}
