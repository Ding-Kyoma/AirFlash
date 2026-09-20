//! Thirty minute wall-clock test; localhost UDP only, never a real receiver.
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
