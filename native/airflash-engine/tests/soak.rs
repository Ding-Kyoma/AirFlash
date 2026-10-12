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
    for member in 0..2 {
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
                    if member == 0 {assert_eq!(n, PCM_BYTES + 36);}else{assert!(n>36 && n<1500);}
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
    streams[1].enable_alac();
    #[cfg(windows)]
    let _priority=airflash_engine::wasapi::Mmcss::new();
    let mut load=Vec::new();
    let busy=std::env::var("AIRFLASH_SOAK_LOAD_THREADS").ok().and_then(|s|s.parse::<usize>().ok()).unwrap_or(0).min(4);
    for _ in 0..busy {let done=stop.clone();load.push(thread::spawn(move || {let mut n=1u64;while !done.load(Ordering::Relaxed) {for _ in 0..50000 {n=n.wrapping_mul(6364136223846793005).wrapping_add(1);std::hint::black_box(n);}thread::yield_now();}}));}
    let start = Instant::now();
    let seconds=std::env::var("AIRFLASH_SOAK_SECONDS").ok().and_then(|s|s.parse::<u64>().ok()).filter(|s|(30..=1800).contains(s)).unwrap_or(1800);
    let duration = Duration::from_secs(seconds);
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
    for w in load {w.join().unwrap();}
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
