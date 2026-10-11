//! Monotonic media clock and a bounded unicast PTPv2 probe master.
//! Implements the AirPlay PTP wire profile; no system clock is changed.
use crate::rtsp::Cancellation;
use anyhow::{Context, Result};
use std::{
    collections::HashMap,
    net::{IpAddr, UdpSocket},
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, Ordering},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
#[derive(Clone)]
pub struct Clock {
    origin: Instant,
    epoch_ns: u64,
}
impl Default for Clock {
    fn default() -> Self {
        Self::new()
    }
}
impl Clock {
    pub fn new() -> Self {
        Self {
            origin: Instant::now(),
            epoch_ns: SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos() as u64,
        }
    }
    pub fn now_ns(&self) -> u64 {
        self.epoch_ns + self.origin.elapsed().as_nanos() as u64
    }
}
fn header(kind: u8, len: usize, clock: u64, seq: u16, flags: u16, interval: i8) -> Vec<u8> {
    let mut p = vec![0; len];
    p[0] = 0x10 | kind;
    p[1] = 2;
    p[2..4].copy_from_slice(&(len as u16).to_be_bytes());
    p[6..8].copy_from_slice(&flags.to_be_bytes());
    p[20..28].copy_from_slice(&clock.to_be_bytes());
    p[28..30].copy_from_slice(&0x8005u16.to_be_bytes());
    p[30..32].copy_from_slice(&seq.to_be_bytes());
    p[33] = interval as u8;
    p
}
fn timestamp(out: &mut [u8], ns: u64) {
    out[..6].copy_from_slice(&(ns / 1_000_000_000).to_be_bytes()[2..]);
    out[6..10].copy_from_slice(&((ns % 1_000_000_000) as u32).to_be_bytes());
}
fn tlv(data: &[u8]) -> Vec<u8> {
    let mut out = vec![0, 3];
    out.extend((data.len() as u16).to_be_bytes());
    out.extend(data);
    out
}
pub fn announce(clock: u64, seq: u16, ns: u64) -> Vec<u8> {
    let mut p = header(11, 76, clock, seq, 0x0408, 0);
    timestamp(&mut p[34..44], ns);
    // Local oscillator; priority is a preference, not a claim of GPS accuracy.
    p[47] = 96;
    p[48] = 248;
    p[49] = 0xfe;
    p[50..52].copy_from_slice(&0xffffu16.to_be_bytes());
    p[52] = 128;
    p[53..61].copy_from_slice(&clock.to_be_bytes());
    p[63] = 0xa0;
    p[64..68].copy_from_slice(&[0, 8, 0, 8]);
    p[68..].copy_from_slice(&clock.to_be_bytes());
    p
}
pub fn sync_pair(clock: u64, seq: u16, ns: u64) -> (Vec<u8>, Vec<u8>) {
    let mut sync = header(0, 44, clock, seq, 0x0608, -3);
    timestamp(&mut sync[34..], ns);
    let mut follow = header(8, 96, clock, seq, 0x0408, -3);
    timestamp(&mut follow[34..44], ns);
    let mut rate = vec![0; 28];
    rate[..6].copy_from_slice(&[0, 0x80, 0xc2, 0, 0, 1]);
    let mut cid = vec![0; 16];
    cid[..6].copy_from_slice(&[0, 0x0d, 0x93, 0, 0, 4]);
    cid[6..14].copy_from_slice(&clock.to_be_bytes());
    follow[44..76].copy_from_slice(&tlv(&rate));
    follow[76..].copy_from_slice(&tlv(&cid));
    (sync, follow)
}
pub struct PtpMaster {
    pub clock_id: u64,
    pub received: Arc<AtomicU64>,
    states: Arc<Mutex<HashMap<IpAddr, PeerState>>>,
    pub mmcss: Arc<Mutex<(bool, bool)>>,
    cancel: Cancellation,
    worker: Option<JoinHandle<()>>,
}
#[derive(Default)]
struct PeerState {
    follow: bool,
    exchanges: u64,
    last: Option<Instant>,
    first: Option<Instant>,
    clock_id: Option<u64>,
    sync: Option<(u16, u64)>,
    sync_pair: Option<(u64, u64)>,
    delay: Option<(u16, u64)>,
    offset: Option<f64>,
    sync_age: Option<Instant>,
    delay_age: Option<Instant>,
    sync_correction: f64,
    source_id: Option<u64>,
    source_port: Option<u16>,
    subscriptions: [Option<Subscription>; 3],
    granted_until: [Option<Instant>; 3],
    received_grants: u64,
    last_request: Option<(u8, u16)>,
    grants: u64,
    round_trip_ns: Option<f64>,
    jitter_ns: Option<f64>,
}
#[derive(Clone, serde::Serialize)]
pub struct PeerSnapshot {
    pub host: IpAddr,
    pub mode: &'static str,
    pub exchanges: u64,
    pub ready: bool,
    pub last_exchange_age_ms: Option<u128>,
    pub clock_id: Option<u64>,
    pub offset_ns: Option<f64>,
    pub grants: u64,
    pub received_grants: u64,
    pub active_subscriptions: usize,
    pub round_trip_ns: Option<f64>,
    pub jitter_ns: Option<f64>,
}
#[derive(Clone, Copy)]
struct Subscription {
    sequence: u16,
    interval: i8,
    sent: Instant,
}
fn subscription_index(kind: u8) -> Option<usize> {
    [0, 9, 11].iter().position(|value| *value == kind)
}
impl PeerState {
    fn exchanged(&mut self, now: Instant) {
        if self
            .last
            .is_none_or(|last| now.duration_since(last) > Duration::from_secs(3))
        {
            self.exchanges = 0;
            self.first = Some(now);
        }
        self.last = Some(now);
        self.exchanges += 1;
    }
    fn ready(&self, now: Instant) -> bool {
        self.exchanges >= 3
            && self
                .first
                .is_some_and(|first| now.duration_since(first) >= Duration::from_millis(250))
            && self
                .last
                .is_some_and(|last| now.duration_since(last) < Duration::from_secs(3))
            && (!self.follow || self.offset.is_some())
    }
}
fn read_timestamp(data: &[u8]) -> Option<u64> {
    if data.len() < 10 {
        return None;
    }
    let mut secs = [0; 8];
    secs[2..].copy_from_slice(&data[..6]);
    let ns = u32::from_be_bytes(data[6..10].try_into().ok()?);
    if ns >= 1_000_000_000 {
        return None;
    }
    u64::from_be_bytes(secs)
        .checked_mul(1_000_000_000)?
        .checked_add(u64::from(ns))
}
fn unicast_grants(request: &[u8], clock_id: u64, seq: u16) -> Vec<Vec<u8>> {
    if request.len() < 44
        || !((request[34..42] == clock_id.to_be_bytes()
            && request[42..44] == 0x8005u16.to_be_bytes())
            || request[34..44].iter().all(|b| *b == 255))
    {
        return Vec::new();
    }
    let mut grants = Vec::new();
    let mut at = 44;
    while at + 4 <= request.len() {
        let kind = u16::from_be_bytes([request[at], request[at + 1]]);
        let len = u16::from_be_bytes([request[at + 2], request[at + 3]]) as usize;
        if at + 4 + len > request.len() {
            break;
        }
        if kind == 4 && len == 6 {
            let body = &request[at + 4..at + 10];
            let msg = body[0] >> 4;
            if [0, 9, 11].contains(&msg) && u32::from_be_bytes(body[2..6].try_into().unwrap()) > 0 {
                let mut out = header(12, 56, clock_id, seq, 0x0408, 0);
                out[34..44].copy_from_slice(&request[20..30]);
                out[44..48].copy_from_slice(&[0, 5, 0, 8]);
                out[48..54].copy_from_slice(body);
                let seconds = u32::from_be_bytes(body[2..6].try_into().unwrap()).min(30);
                out[50..54].copy_from_slice(&seconds.to_be_bytes());
                grants.push(out);
            }
        }
        at += 4 + len;
    }
    grants
}
fn valid_packet(data: &[u8]) -> bool {
    data.len() >= 34
        && data[0] >> 4 == 1
        && data[1] & 0xf == 2
        && data[4] == 0
        && u16::from_be_bytes([data[2], data[3]]) as usize == data.len()
}
fn correction(data: &[u8]) -> f64 {
    i64::from_be_bytes(data[8..16].try_into().unwrap()) as f64 / 65536.0
}
fn followed_packet(s: &mut PeerState, data: &[u8], local_ns: u64, now: Instant, own_id: u64) {
    if !valid_packet(data) {
        return;
    }
    let kind = data[0] & 15;
    let seq = u16::from_be_bytes([data[30], data[31]]);
    let source = u64::from_be_bytes(data[20..28].try_into().unwrap());
    let source_port = u16::from_be_bytes(data[28..30].try_into().unwrap());
    if kind == 11 && data.len() >= 64 {
        let id = u64::from_be_bytes(data[53..61].try_into().unwrap());
        if s.clock_id != Some(id)
            || s.source_id != Some(source)
            || s.source_port != Some(source_port)
        {
            let subscriptions = if s.source_id.is_none() {
                s.subscriptions
            } else {
                [None; 3]
            };
            *s = PeerState {
                follow: true,
                clock_id: Some(id),
                source_id: Some(source),
                source_port: Some(source_port),
                subscriptions,
                ..Default::default()
            };
        }
    }
    if s.source_id != Some(source) || s.source_port != Some(source_port) {
        return;
    }
    if kind == 12 {
        receive_grants(s, data, own_id, now);
    }
    if kind == 0 && data.len() >= 44 {
        s.sync = Some((seq, local_ns));
        s.sync_age = Some(now);
        s.sync_correction = correction(data);
        if data[6] & 2 == 0 {
            // A one-step clock carries its origin directly.
            if let Some(origin) = read_timestamp(&data[34..44]) {
                s.sync_pair = Some((origin, local_ns));
            }
        }
    }
    if kind == 8
        && data.len() >= 44
        && s.sync_age
            .is_some_and(|at| now.duration_since(at) < Duration::from_secs(2))
    {
        if let (Some((sequence, received)), Some(origin)) = (s.sync, read_timestamp(&data[34..44]))
        {
            if sequence == seq {
                s.sync_pair = Some((origin, received));
                s.sync_correction += correction(data);
                s.sync = None;
            }
        }
    }
    if kind == 9
        && data.len() >= 54
        && data[44..52] == own_id.to_be_bytes()
        && data[52..54] == 0x8005u16.to_be_bytes()
        && s.sync_age
            .is_some_and(|at| now.duration_since(at) < Duration::from_secs(2))
        && s.delay_age
            .is_some_and(|at| now.duration_since(at) < Duration::from_secs(2))
    {
        if let (Some((sequence, sent)), Some((origin, received)), Some(remote_received)) =
            (s.delay, s.sync_pair, read_timestamp(&data[34..44]))
        {
            if sequence == seq {
                let outward = (origin as i128 - received as i128) as f64 + s.sync_correction;
                let inward = (remote_received as i128 - sent as i128) as f64 - correction(data);
                let rtt = inward - outward;
                if !(0.0..=250_000_000.0).contains(&rtt) {
                    return;
                }
                let offset = (outward + inward) / 2.0;
                s.jitter_ns = s.offset.map(|old| (old - offset).abs());
                s.round_trip_ns = Some(rtt);
                if s.offset
                    .is_some_and(|old| (old - offset).abs() > 5_000_000.0)
                {
                    s.exchanges = 0;
                    s.first = None;
                    s.last = None;
                    s.offset = Some(offset);
                }
                s.offset = Some(s.offset.map_or(offset, |old| old * 0.875 + offset * 0.125));
                s.exchanged(now);
                s.delay = None;
                s.delay_age = None;
            }
        }
    }
}
fn receive_grants(s: &mut PeerState, data: &[u8], own_id: u64, now: Instant) {
    if data.len() < 44
        || data[34..42] != own_id.to_be_bytes()
        || data[42..44] != 0x8005u16.to_be_bytes()
    {
        return;
    }
    let sequence = u16::from_be_bytes([data[30], data[31]]);
    let mut at = 44;
    while at + 4 <= data.len() {
        let kind = u16::from_be_bytes([data[at], data[at + 1]]);
        let length = u16::from_be_bytes([data[at + 2], data[at + 3]]) as usize;
        if at + 4 + length > data.len() {
            return;
        }
        if kind == 5 && length == 8 {
            let body = &data[at + 4..at + 12];
            if let Some(index) = subscription_index(body[0] >> 4) {
                if let Some(pending) = s.subscriptions[index] {
                    let interval = body[1] as i8;
                    let seconds = u32::from_be_bytes(body[2..6].try_into().unwrap());
                    if sequence == pending.sequence
                        && now.duration_since(pending.sent) < Duration::from_secs(2)
                        && body[0] & 15 == 0
                        && body[6] & !1 == 0
                        && body[7] == 0
                        && interval >= pending.interval
                        && interval <= 4
                        && (1..=30).contains(&seconds)
                    {
                        s.granted_until[index] =
                            Some(now + Duration::from_secs(u64::from(seconds)));
                        s.received_grants += 1;
                        s.subscriptions[index] = None;
                    }
                }
            }
        }
        at += 4 + length;
    }
}
fn subscription(clock: u64, remote: Option<(u64, u16)>, seq: u16, kind: u8) -> Vec<u8> {
    let mut request = header(12, 54, clock, seq, 0x0408, 0);
    request[34..44].fill(255);
    if let Some((id, port)) = remote {
        request[34..42].copy_from_slice(&id.to_be_bytes());
        request[42..44].copy_from_slice(&port.to_be_bytes());
    }
    request[44..48].copy_from_slice(&[0, 4, 0, 6]);
    request[48] = kind << 4;
    request[49] = if kind == 0 { (-3i8) as u8 } else { 0 };
    request[50..54].copy_from_slice(&30u32.to_be_bytes());
    request
}
impl PtpMaster {
    pub fn start(peers: Vec<IpAddr>, clock: Clock) -> Result<Self> {
        Self::start_with_followers(peers, Vec::new(), clock)
    }
    pub fn start_with_followers(
        peers: Vec<IpAddr>,
        followers: Vec<IpAddr>,
        clock: Clock,
    ) -> Result<Self> {
        let event = UdpSocket::bind("0.0.0.0:319").context("PTP event port 319 unavailable")?;
        let general = UdpSocket::bind("0.0.0.0:320").context("PTP general port 320 unavailable")?;
        Self::from_sockets(peers, followers, clock, event, general)
    }
    pub(crate) fn from_sockets(
        peers: Vec<IpAddr>,
        followers: Vec<IpAddr>,
        clock: Clock,
        event: UdpSocket,
        general: UdpSocket,
    ) -> Result<Self> {
        event.set_nonblocking(true)?;
        general.set_nonblocking(true)?;
        let clock_id = rand::random::<u64>() & 0x7fff_ffff_ffff_ffff;
        let received = Arc::new(AtomicU64::new(0));
        let rx = received.clone();
        let cancel = Cancellation::default();
        let stop = cancel.clone();
        let states = Arc::new(Mutex::new(
            peers
                .iter()
                .map(|ip| {
                    (
                        *ip,
                        PeerState {
                            follow: followers.contains(ip),
                            ..Default::default()
                        },
                    )
                })
                .collect::<HashMap<_, _>>(),
        ));
        let state = states.clone();
        let mmcss = Arc::new(Mutex::new((false, false)));
        let priority_state = mmcss.clone();
        let worker = thread::Builder::new()
            .name("airplay-ptp".into())
            .spawn(move || {
                #[cfg(windows)]
                {
                    let priority = crate::wasapi::Mmcss::clock();
                    *priority_state.lock().unwrap() =
                        (priority.registered(), priority.priority_set());
                    // The guard belongs to this worker until it exits.
                    let _priority = priority;
                    ptp_loop(
                        &event, &general, &peers, &followers, &clock, clock_id, &rx, &stop, &state,
                    );
                }
                #[cfg(not(windows))]
                {
                    let _ = priority_state;
                    ptp_loop(
                        &event, &general, &peers, &followers, &clock, clock_id, &rx, &stop, &state,
                    );
                }
            })?;
        Ok(Self {
            clock_id,
            received,
            states,
            mmcss,
            cancel,
            worker: Some(worker),
        })
    }
    pub fn snapshots(&self) -> Vec<PeerSnapshot> {
        let now = Instant::now();
        let states = self.states.lock().unwrap();
        let mut result: Vec<_> = states
            .iter()
            .map(|(ip, s)| PeerSnapshot {
                host: *ip,
                mode: if s.follow { "receiver" } else { "sender" },
                exchanges: s.exchanges,
                ready: s.ready(now),
                last_exchange_age_ms: s.last.map(|last| now.duration_since(last).as_millis()),
                clock_id: s.clock_id,
                offset_ns: s.offset,
                grants: s.grants,
                received_grants: s.received_grants,
                active_subscriptions: s
                    .granted_until
                    .iter()
                    .flatten()
                    .filter(|until| **until > now)
                    .count(),
                round_trip_ns: s.round_trip_ns,
                jitter_ns: s.jitter_ns,
            })
            .collect();
        result.sort_by_key(|s| s.host);
        result
    }
    pub fn ready(&self, host: IpAddr) -> bool {
        self.states
            .lock()
            .unwrap()
            .get(&host)
            .is_some_and(|s| s.ready(Instant::now()))
    }
    pub fn timeline(&self, host: IpAddr, local_ns: u64) -> Result<(u64, u64)> {
        let states = self.states.lock().unwrap();
        let s = states.get(&host).context("unknown timing peer")?;
        if !s.follow {
            return Ok((self.clock_id, local_ns));
        }
        anyhow::ensure!(s.ready(Instant::now()), "receiver clock is not ready");
        let mapped = local_ns as i128 + s.offset.unwrap().round() as i128;
        Ok((
            s.clock_id.context("missing receiver clock identity")?,
            u64::try_from(mapped)?,
        ))
    }
}
#[allow(clippy::too_many_arguments)]
fn ptp_loop(
    event: &UdpSocket,
    general: &UdpSocket,
    peers: &[IpAddr],
    followers: &[IpAddr],
    clock: &Clock,
    clock_id: u64,
    rx: &AtomicU64,
    stop: &Cancellation,
    state: &Mutex<HashMap<IpAddr, PeerState>>,
) {
    let mut seq = 0u16;
    let mut last_announce = Instant::now() - Duration::from_secs(2);
    let mut next_sync = Instant::now();
    let mut next_subscription = Instant::now();
    let mut buf = [0u8; 2048];
    while !stop.is_cancelled() {
        let now = Instant::now();
        if now >= next_sync {
            for peer in peers {
                if followers.contains(peer) {
                    if now >= next_subscription {
                        let remote = state
                            .lock()
                            .unwrap()
                            .get(peer)
                            .and_then(|s| s.source_id.zip(s.source_port));
                        for kind in [0, 9, 11] {
                            let request = subscription(clock_id, remote, seq, kind);
                            if general.send_to(&request, (*peer, 320)).is_ok() {
                                state.lock().unwrap().get_mut(peer).unwrap().subscriptions
                                    [subscription_index(kind).unwrap()] = Some(Subscription {
                                    sequence: seq,
                                    interval: request[49] as i8,
                                    sent: now,
                                });
                            }
                        }
                    }
                    if now.duration_since(last_announce) >= Duration::from_secs(1) {
                        let mut req = header(1, 44, clock_id, seq, 0x0408, 0);
                        let sent = clock.now_ns();
                        timestamp(&mut req[34..44], sent);
                        if event.send_to(&req, (*peer, 319)).is_ok() {
                            let mut states = state.lock().unwrap();
                            let s = states.get_mut(peer).unwrap();
                            s.delay = Some((seq, sent));
                            s.delay_age = Some(now);
                        }
                    }
                    continue;
                }
                let (sync, follow) = sync_pair(clock_id, seq, clock.now_ns());
                let _ = event.send_to(&sync, (*peer, 319));
                let _ = general.send_to(&follow, (*peer, 320));
                if now.duration_since(last_announce) >= Duration::from_secs(1) {
                    let _ = general.send_to(&announce(clock_id, seq, clock.now_ns()), (*peer, 320));
                }
            }
            if now.duration_since(last_announce) >= Duration::from_secs(1) {
                last_announce = now;
            }
            seq = seq.wrapping_add(1);
            if now >= next_subscription {
                next_subscription = now + Duration::from_secs(10);
            }
            next_sync = now + Duration::from_millis(125);
        }
        for socket in [&event, &general] {
            let until = Instant::now() + Duration::from_millis(1);
            for _ in 0..128 {
                if Instant::now() >= until {
                    break;
                }
                let Ok((n, addr)) = socket.recv_from(&mut buf) else {
                    break;
                };
                if !peers.contains(&addr.ip())
                    || !valid_packet(&buf[..n])
                    || ![319, 320].contains(&addr.port())
                {
                    continue;
                }
                let length = u16::from_be_bytes([buf[2], buf[3]]) as usize;
                if length > n || length < 34 {
                    continue;
                }
                rx.fetch_add(1, Ordering::Relaxed);
                let kind = buf[0] & 0xf;
                let request_seq = u16::from_be_bytes([buf[30], buf[31]]);
                let local_received = clock.now_ns();
                let mut states = state.lock().unwrap();
                let s = states.get_mut(&addr.ip()).unwrap();
                if s.follow {
                    followed_packet(s, &buf[..length], local_received, now, clock_id);
                }
                if kind == 12 && length >= 44 {
                    for grant in unicast_grants(&buf[..length], clock_id, request_seq) {
                        if general.send_to(&grant, (addr.ip(), 320)).is_ok() {
                            s.grants += 1;
                        }
                    }
                }
                if !s.follow
                    && (kind == 1 || kind == 2)
                    && length >= if kind == 1 { 44 } else { 54 }
                {
                    if s.last_request == Some((kind, request_seq))
                        && s.last
                            .is_some_and(|at| now.duration_since(at) < Duration::from_secs(3))
                    {
                        continue;
                    }
                    s.last_request = Some((kind, request_seq));
                    let response_kind = if kind == 1 { 9 } else { 3 };
                    let mut response = header(
                        response_kind,
                        54,
                        clock_id,
                        request_seq,
                        if kind == 1 { 0x0408 } else { 0x0608 },
                        -3,
                    );
                    timestamp(&mut response[34..44], local_received);
                    response[44..54].copy_from_slice(&buf[20..30]);
                    if kind == 1 {
                        if general.send_to(&response, (addr.ip(), 320)).is_ok() {
                            s.exchanged(now);
                        }
                    } else {
                        let _ = event.send_to(&response, (addr.ip(), 319));
                        let mut follow = header(10, 54, clock_id, request_seq, 0x0408, -3);
                        timestamp(&mut follow[34..44], clock.now_ns());
                        follow[44..54].copy_from_slice(&buf[20..30]);
                        if general.send_to(&follow, (addr.ip(), 320)).is_ok() {
                            s.exchanged(now);
                        }
                    }
                }
            }
        }
        thread::sleep(Duration::from_millis(2));
    }
}
impl Drop for PtpMaster {
    fn drop(&mut self) {
        self.cancel.cancel();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn readiness_requires_exchange_streak_and_expires() {
        let now = Instant::now();
        let mut peer = PeerState::default();
        assert!(!peer.ready(now));
        peer.exchanged(now);
        peer.exchanged(now + Duration::from_millis(125));
        assert!(!peer.ready(now + Duration::from_millis(250)));
        peer.exchanged(now + Duration::from_millis(250));
        assert!(peer.ready(now + Duration::from_millis(250)));
        assert!(!peer.ready(now + Duration::from_secs(4)));
        peer.exchanged(now + Duration::from_secs(4));
        assert_eq!(peer.exchanges, 1);
        peer.follow = true;
        peer.exchanged(now + Duration::from_millis(4125));
        peer.exchanged(now + Duration::from_millis(4250));
        assert!(!peer.ready(now + Duration::from_millis(4250)));
    }
    #[test]
    fn unicast_grants_validate_target_length_type_and_duration() {
        let request = subscription(7, Some((8, 0x8005)), 3, 0);
        let grants = unicast_grants(&request, 8, 3);
        assert_eq!(grants.len(), 1);
        assert_eq!(&grants[0][34..42], &7u64.to_be_bytes());
        assert_eq!(&grants[0][44..48], &[0, 5, 0, 8]);
        assert!(unicast_grants(&request, 9, 3).is_empty());
        assert!(unicast_grants(&request[..50], 8, 3).is_empty());
        let mut invalid = request.clone();
        invalid[48] = 7 << 4;
        assert!(unicast_grants(&invalid, 8, 3).is_empty());
        let mut invalid = request;
        invalid[50..54].fill(0);
        assert!(unicast_grants(&invalid, 8, 3).is_empty());
    }
    #[test]
    fn follower_pairs_sequences_and_rejects_wrong_or_stale_responses() {
        let now = Instant::now();
        let mut s = PeerState {
            follow: true,
            ..Default::default()
        };
        followed_packet(&mut s, &announce(8, 0, 0), 0, now, 7);
        for seq in 0..3u16 {
            let at = now + Duration::from_millis(u64::from(seq) * 1000);
            let base = 1_000_000_000u64 + u64::from(seq) * 1_000_000_000;
            let (sync, follow) = sync_pair(8, seq, base + 1_000_000);
            followed_packet(&mut s, &sync, base + 100_000, at, 7);
            followed_packet(&mut s, &follow, base + 110_000, at, 7);
            s.delay = Some((seq, base + 200_000));
            s.delay_age = Some(at);
            let mut response = header(9, 54, 8, seq, 0x0408, 0);
            timestamp(&mut response[34..44], base + 1_300_000);
            response[44..52].copy_from_slice(&7u64.to_be_bytes());
            response[52..54].copy_from_slice(&0x8005u16.to_be_bytes());
            let mut bad = response.clone();
            bad[44] = 1;
            followed_packet(&mut s, &bad, base + 400_000, at, 7);
            assert!(s.delay.is_some());
            followed_packet(&mut s, &response, base + 400_000, at, 7);
            followed_packet(&mut s, &response, base + 400_000, at, 7);
            assert_eq!(s.exchanges, u64::from(seq) + 1);
        }
        assert!(s.ready(now + Duration::from_secs(2)));
        assert_eq!(s.offset, Some(1_000_000.0));
        assert_eq!(s.round_trip_ns, Some(200_000.0));
        let mut bad = announce(8, 0, 0);
        bad[4] = 1;
        assert!(!valid_packet(&bad));
        assert!(read_timestamp(&[255; 10]).is_none());
    }
    #[test]
    fn follower_grants_require_pending_sequence_source_target_and_bounded_lease() {
        let now = Instant::now();
        let mut s = PeerState {
            follow: true,
            ..Default::default()
        };
        followed_packet(&mut s, &announce(8, 0, 0), 0, now, 7);
        s.subscriptions[0] = Some(Subscription {
            sequence: 3,
            interval: -3,
            sent: now,
        });
        let request = subscription(7, Some((8, 0x8005)), 3, 0);
        let grant = unicast_grants(&request, 8, 3).remove(0);
        for (index, value) in [
            (20, 1),
            (29, 1),
            (41, 1),
            (43, 1),
            (31, 4),
            (47, 7),
            (48, 0x90),
            (49, (-4i8) as u8),
            (54, 2),
            (55, 1),
        ] {
            let mut bad = grant.clone();
            bad[index] = value;
            followed_packet(&mut s, &bad, 0, now, 7);
            assert_eq!(s.received_grants, 0);
        }
        let mut bad = grant.clone();
        bad[50..54].copy_from_slice(&31u32.to_be_bytes());
        followed_packet(&mut s, &bad, 0, now, 7);
        followed_packet(&mut s, &grant, 0, now + Duration::from_secs(3), 7);
        assert_eq!(s.received_grants, 0);
        followed_packet(&mut s, &grant, 0, now, 7);
        followed_packet(&mut s, &grant, 0, now, 7);
        assert_eq!(s.received_grants, 1);
        assert_eq!(s.granted_until[0], Some(now + Duration::from_secs(30)));
        assert!(!s.ready(now)); // A lease is not a clock offset exchange.
        let mut bad = request;
        bad[43] = 1;
        assert!(unicast_grants(&bad, 8, 3).is_empty());
    }
    #[test]
    fn wire_clock_and_lengths() {
        let id = 0x1234567812345678;
        let (s, f) = sync_pair(id, 65535, 1234567890);
        assert_eq!(&s[20..28], &id.to_be_bytes());
        assert_eq!(s.len(), 44);
        assert_eq!(f.len(), 96);
        assert_eq!(&f[34..44], &[0, 0, 0, 0, 0, 1, 13, 251, 56, 210]);
        assert_eq!(announce(id, 0, 0).len(), 76);
    }
}
