//! Monotonic media clock and a bounded unicast PTPv2 probe master.
//! Implements the AirPlay PTP wire profile; no system clock is changed.
use crate::rtsp::Cancellation;
use anyhow::{Context, Result};
use std::{
    collections::{HashMap, VecDeque},
    io,
    net::{IpAddr, SocketAddr, UdpSocket},
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
    network: Arc<Mutex<NetworkSnapshot>>,
    cancel: Cancellation,
    worker: Option<JoinHandle<()>>,
}
#[derive(Default)]
struct PeerState {
    follow: bool,
    active: bool,
    local_address: Option<IpAddr>,
    exchanges: u64,
    last: Option<Instant>,
    first: Option<Instant>,
    clock_id: Option<u64>,
    pending_syncs: VecDeque<PendingSync>,
    sync_pair: Option<SyncSample>,
    delay: Option<(u16, u64)>,
    /// Offset refined by a validated delay response. This removes the path
    /// delay from the estimate but is not required to establish one.
    offset: Option<f64>,
    /// Offset derived from complete Sync/Follow_Up pairs alone.
    sync_offset: Option<f64>,
    delay_age: Option<Instant>,
    source_id: Option<u64>,
    source_port: Option<u16>,
    subscriptions: [VecDeque<Subscription>; 3],
    granted_until: [Option<Instant>; 3],
    received_grants: u64,
    last_request: Option<(u8, u16, Instant)>,
    grants: u64,
    round_trip_ns: Option<f64>,
    jitter_ns: Option<f64>,
    next_subscription: Option<Instant>,
    exchange_interval: Option<Duration>,
    packets: PacketSnapshot,
}
const SAMPLE_LIFETIME: Duration = Duration::from_secs(2);
const MAX_PENDING_SYNCS: usize = 32;
const SUBSCRIPTION_RETRY: Duration = Duration::from_millis(500);

#[derive(Clone, Copy)]
struct SyncSample {
    origin: u64,
    received: u64,
    correction: f64,
    at: Instant,
}
struct PendingSync {
    sequence: u16,
    at: Instant,
    sync: Option<(u64, f64)>,
    follow: Option<(u64, f64)>,
    complete: bool,
}
#[derive(Clone, Default, serde::Serialize)]
pub struct SocketFailure {
    operation: &'static str,
    kind: String,
    os_code: Option<i32>,
}
impl SocketFailure {
    fn new(operation: &'static str, error: &io::Error) -> Self {
        Self {
            operation,
            kind: format!("{:?}", error.kind()),
            os_code: error.raw_os_error(),
        }
    }
}
#[derive(Clone, Default, serde::Serialize)]
pub struct NetworkSnapshot {
    event_received: u64,
    general_received: u64,
    unknown_peer: u64,
    receive_errors: u64,
    last_receive_error: Option<SocketFailure>,
}
#[derive(Clone, Default, serde::Serialize)]
pub struct PacketSnapshot {
    raw_received: u64,
    received_by_type: [u64; 16],
    rejected: u64,
    last_rejection: Option<&'static str>,
    last_round_trip_ns: Option<f64>,
    send_errors: u64,
    last_send_error: Option<SocketFailure>,
    pdelay_responses: u64,
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
    /// `delay` when a validated delay response refined the offset, `sync` when
    /// only complete Sync/Follow_Up pairs have supplied one.
    pub offset_source: Option<&'static str>,
    pub grants: u64,
    pub received_grants: u64,
    pub active_subscriptions: usize,
    pub round_trip_ns: Option<f64>,
    pub jitter_ns: Option<f64>,
    pub local_address: Option<IpAddr>,
    pub phase: &'static str,
    pub packets: PacketSnapshot,
    pub exchange_timeout_ms: u128,
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
    fn reject(&mut self, reason: &'static str) {
        self.packets.rejected += 1;
        self.packets.last_rejection = Some(reason);
    }
    fn exchange_timeout(&self) -> Duration {
        self.exchange_interval
            .map(|interval| interval.saturating_mul(3))
            .unwrap_or(Duration::from_secs(3))
            .clamp(Duration::from_secs(3), Duration::from_secs(12))
    }
    fn startup_timeout(&self) -> Duration {
        (self.exchange_timeout() + Duration::from_secs(2)).max(Duration::from_secs(8))
    }
    /// Best available receiver-to-local offset. A validated delay response wins
    /// because it also removes the path delay; the Sync pair estimate remains
    /// usable when the receiver never supplies a matching delay response.
    fn effective_offset(&self) -> Option<f64> {
        self.offset.or(self.sync_offset)
    }
    fn offset_source(&self) -> Option<&'static str> {
        if self.offset.is_some() {
            Some("delay")
        } else if self.sync_offset.is_some() {
            Some("sync")
        } else {
            None
        }
    }
    fn phase(&self, now: Instant) -> &'static str {
        if !self.active {
            "waiting_session"
        } else if self.ready(now) {
            "ready"
        } else if self.packets.raw_received == 0 {
            "no_rx"
        } else if self.packets.received_by_type.iter().sum::<u64>() == 0 {
            "rx_rejected"
        } else if self.follow && self.clock_id.is_none() {
            "waiting_announce"
        } else if self.follow
            && self
                .sync_pair
                .is_none_or(|sample| now.duration_since(sample.at) >= SAMPLE_LIFETIME)
        {
            "waiting_sync_pair"
        } else if self.follow && self.effective_offset().is_none() {
            "waiting_delay_response"
        } else {
            "waiting_exchanges"
        }
    }
    fn subscription_due(&self, now: Instant) -> bool {
        self.next_subscription.is_none_or(|at| now >= at)
    }
    fn subscription_needed(&self, index: usize, now: Instant) -> bool {
        // An unready peer may have lost the negotiated stream despite a live lease.
        !self.ready(now)
            || self.granted_until[index].is_none_or(|until| until <= now + Duration::from_secs(5))
    }
    fn remember_sync(&mut self, data: &[u8], local_ns: u64, now: Instant) {
        let sequence = u16::from_be_bytes([data[30], data[31]]);
        let kind = data[0] & 15;
        let origin = if kind == 8 || data[6] & 2 == 0 {
            match read_timestamp(&data[34..44]) {
                Some(value) => Some(value),
                None => {
                    self.reject("timestamp");
                    return;
                }
            }
        } else {
            None
        };
        self.pending_syncs
            .retain(|entry| now.duration_since(entry.at) < SAMPLE_LIFETIME);
        let index = match self
            .pending_syncs
            .iter()
            .position(|e| e.sequence == sequence)
        {
            Some(index) => index,
            None => {
                if self.pending_syncs.len() == MAX_PENDING_SYNCS {
                    self.pending_syncs.pop_front();
                }
                self.pending_syncs.push_back(PendingSync {
                    sequence,
                    at: now,
                    sync: None,
                    follow: None,
                    complete: false,
                });
                self.pending_syncs.len() - 1
            }
        };
        let complete = {
            let entry = &mut self.pending_syncs[index];
            if entry.complete {
                return;
            }
            if kind == 0 && entry.sync.is_none() {
                entry.sync = Some((local_ns, correction(data)));
                if let Some(origin) = origin {
                    entry.follow = Some((origin, 0.0));
                }
            } else if kind == 8 && entry.follow.is_none() {
                entry.follow = Some((origin.unwrap(), correction(data)));
            }
            match (entry.sync, entry.follow) {
                (Some((received, sync_correction)), Some((origin, follow_correction))) => {
                    entry.complete = true;
                    Some((
                        origin,
                        received,
                        sync_correction + follow_correction,
                        entry.at,
                    ))
                }
                _ => None,
            }
        };
        let Some((origin, received, correction_ns, at)) = complete else {
            return;
        };
        // A late older pair must not replace the newest complete sample.
        if self
            .sync_pair
            .is_none_or(|sample| received > sample.received)
        {
            self.sync_pair = Some(SyncSample {
                origin,
                received,
                correction: correction_ns,
                at,
            });
        }
        if self.follow {
            // A complete pair already yields the receiver-to-local offset. The
            // omitted path delay is sub-millisecond on a local network and far
            // below any AirPlay latency in use, so a receiver that answers the
            // sync exchange but never supplies a usable delay response can still
            // provide a valid timeline. A validated response refines `offset`
            // without being required for readiness.
            let sample = (origin as i128 - received as i128) as f64 + correction_ns;
            self.sync_offset = Some(
                self.sync_offset
                    .map_or(sample, |old| old * 0.875 + sample * 0.125),
            );
            self.exchanged(at);
        }
    }
    fn exchanged(&mut self, now: Instant) {
        if self
            .last
            .is_none_or(|last| now.duration_since(last) >= self.exchange_timeout())
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
                .is_some_and(|last| now.duration_since(last) < self.exchange_timeout())
            && (!self.follow || self.effective_offset().is_some())
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
    packet_rejection(data).is_none()
}
fn packet_rejection(data: &[u8]) -> Option<&'static str> {
    if data.len() < 34 {
        return Some("short_header");
    }
    if data[0] >> 4 != 1 || data[1] & 15 != 2 {
        return Some("profile_or_version");
    }
    if data[4] != 0 {
        return Some("domain");
    }
    let minimum = match data[0] & 15 {
        0 | 1 | 8 | 12 => 44,
        2 | 3 | 9 | 10 => 54,
        11 => 64,
        _ => 34,
    };
    if data.len() < minimum || usize::from(u16::from_be_bytes([data[2], data[3]])) != data.len() {
        return Some("length");
    }
    None
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
                std::mem::take(&mut s.subscriptions)
            } else {
                Default::default()
            };
            *s = PeerState {
                follow: true,
                active: s.active,
                local_address: s.local_address,
                packets: std::mem::take(&mut s.packets),
                clock_id: Some(id),
                source_id: Some(source),
                source_port: Some(source_port),
                subscriptions,
                ..Default::default()
            };
        }
    }
    if s.source_id != Some(source) || s.source_port != Some(source_port) {
        s.reject("source_identity");
        return;
    }
    if kind == 12 {
        receive_grants(s, data, own_id, now);
    }
    if (kind == 0 || kind == 8) && data.len() >= 44 {
        s.remember_sync(data, local_ns, now);
    }
    if kind == 9
        && data.len() >= 54
        && data[44..52] == own_id.to_be_bytes()
        && data[52..54] == 0x8005u16.to_be_bytes()
        && s.sync_pair
            .is_some_and(|sample| now.duration_since(sample.at) < SAMPLE_LIFETIME)
        && s.delay_age
            .is_some_and(|at| now.duration_since(at) < Duration::from_secs(2))
    {
        if let (Some((sequence, sent)), Some(sample), Some(remote_received)) =
            (s.delay, s.sync_pair, read_timestamp(&data[34..44]))
        {
            if sequence == seq {
                let outward =
                    (sample.origin as i128 - sample.received as i128) as f64 + sample.correction;
                let inward = (remote_received as i128 - sent as i128) as f64 - correction(data);
                let rtt = inward - outward;
                if !(0.0..=250_000_000.0).contains(&rtt) {
                    // Keep the number for diagnostics: a receiver whose delay
                    // timestamps disagree with its sync timestamps is otherwise
                    // indistinguishable from one that never answered.
                    s.packets.last_round_trip_ns = Some(rtt);
                    s.reject("round_trip");
                    s.delay = None;
                    s.delay_age = None;
                    return;
                }
                let offset = (outward + inward) / 2.0;
                s.jitter_ns = s.offset.map(|old| (old - offset).abs());
                s.round_trip_ns = Some(rtt);
                if s.offset
                    .is_some_and(|old| (old - offset).abs() > 5_000_000.0)
                {
                    s.reject("offset_jump");
                    s.exchanges = 0;
                    s.first = None;
                    s.last = None;
                    s.offset = Some(offset);
                }
                s.offset = Some(s.offset.map_or(offset, |old| old * 0.875 + offset * 0.125));
                s.delay = None;
                s.delay_age = None;
            } else {
                s.reject("delay_sequence");
            }
        }
    } else if kind == 9 {
        s.reject("delay_identity_or_stale_sample");
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
                if let Some(pending) = s.subscriptions[index]
                    .iter()
                    .find(|p| p.sequence == sequence)
                {
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
                        s.subscriptions[index].clear();
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
        let network = Arc::new(Mutex::new(NetworkSnapshot::default()));
        let worker_network = network.clone();
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
                        &event,
                        &general,
                        &peers,
                        &clock,
                        clock_id,
                        &rx,
                        &stop,
                        &state,
                        &worker_network,
                    );
                }
                #[cfg(not(windows))]
                {
                    let _ = priority_state;
                    ptp_loop(
                        &event,
                        &general,
                        &peers,
                        &clock,
                        clock_id,
                        &rx,
                        &stop,
                        &state,
                        &worker_network,
                    );
                }
            })?;
        Ok(Self {
            clock_id,
            received,
            states,
            mmcss,
            network,
            cancel,
            worker: Some(worker),
        })
    }
    /// SETPEERS establishes the session's timing relationship. Send fresh timing
    /// requests afterwards even if the receiver ignored pre-session traffic.
    pub fn activate(&self, host: IpAddr, local_address: IpAddr) {
        if let Some(peer) = self.states.lock().unwrap().get_mut(&host) {
            peer.active = true;
            peer.local_address = Some(local_address);
            peer.next_subscription = None;
        }
    }
    pub fn network_snapshot(&self) -> NetworkSnapshot {
        self.network.lock().unwrap().clone()
    }
    pub fn startup_timeout(&self, host: IpAddr) -> Duration {
        self.states
            .lock()
            .unwrap()
            .get(&host)
            .map_or(Duration::from_secs(8), PeerState::startup_timeout)
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
                offset_ns: s.effective_offset(),
                offset_source: s.offset_source(),
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
                local_address: s.local_address,
                phase: s.phase(now),
                packets: s.packets.clone(),
                exchange_timeout_ms: s.exchange_timeout().as_millis(),
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
        let offset = s.effective_offset().context("missing clock offset")?;
        let mapped = local_ns as i128 + offset.round() as i128;
        Ok((
            s.clock_id.context("missing receiver clock identity")?,
            u64::try_from(mapped)?,
        ))
    }
}
fn send_ptp(
    socket: &UdpSocket,
    packet: &[u8],
    destination: SocketAddr,
    peer: &mut PeerState,
    operation: &'static str,
) -> bool {
    match socket.send_to(packet, destination) {
        Ok(sent) if sent == packet.len() => true,
        result => {
            let error = result.err().unwrap_or_else(|| {
                io::Error::new(io::ErrorKind::WriteZero, "partial PTP datagram")
            });
            peer.packets.send_errors += 1;
            peer.packets.last_send_error = Some(SocketFailure::new(operation, &error));
            false
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn respond_to_delay(
    event: &UdpSocket,
    general: &UdpSocket,
    destination: IpAddr,
    data: &[u8],
    received_ns: u64,
    clock: &Clock,
    clock_id: u64,
    s: &mut PeerState,
    now: Instant,
) {
    let kind = data[0] & 15;
    // Peer delay measures the link, regardless of who supplies its timeline.
    if kind != 2 && (kind != 1 || s.follow) {
        return;
    }
    let sequence = u16::from_be_bytes([data[30], data[31]]);
    if s.last_request.is_some_and(|(old_kind, old_sequence, at)| {
        old_kind == kind
            && old_sequence == sequence
            && now.duration_since(at) < s.exchange_timeout()
    }) {
        return;
    }
    let mut response = header(
        if kind == 1 { 9 } else { 3 },
        54,
        clock_id,
        sequence,
        if kind == 1 { 0x0408 } else { 0x0608 },
        -3,
    );
    timestamp(&mut response[34..44], received_ns);
    response[44..54].copy_from_slice(&data[20..30]);
    let sent = if kind == 1 {
        send_ptp(
            general,
            &response,
            (destination, 320).into(),
            s,
            "delay_response",
        )
    } else {
        let sent = send_ptp(
            event,
            &response,
            (destination, 319).into(),
            s,
            "pdelay_response",
        );
        let mut follow = header(10, 54, clock_id, sequence, 0x0408, -3);
        timestamp(&mut follow[34..44], clock.now_ns());
        follow[44..54].copy_from_slice(&data[20..30]);
        let followed = send_ptp(
            general,
            &follow,
            (destination, 320).into(),
            s,
            "pdelay_follow_up",
        );
        if sent && followed {
            s.packets.pdelay_responses += 1;
        }
        sent && followed
    };
    if sent {
        s.last_request = Some((kind, sequence, now));
        if !s.follow {
            // Only use bounded, explicit intervals. 0x7f/0x80 are unspecified.
            let interval = data[33] as i8;
            if (-7..=2).contains(&interval) {
                s.exchange_interval = Some(Duration::from_secs_f64(2f64.powi(i32::from(interval))));
            }
            s.exchanged(now);
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn ptp_loop(
    event: &UdpSocket,
    general: &UdpSocket,
    peers: &[IpAddr],
    clock: &Clock,
    clock_id: u64,
    rx: &AtomicU64,
    stop: &Cancellation,
    state: &Mutex<HashMap<IpAddr, PeerState>>,
    network: &Mutex<NetworkSnapshot>,
) {
    let mut seq = 0u16;
    let mut last_announce = Instant::now() - Duration::from_secs(2);
    let mut next_sync = Instant::now();
    let mut buf = [0u8; 2048];
    while !stop.is_cancelled() {
        let now = Instant::now();
        if now >= next_sync {
            for peer in peers {
                let mut states = state.lock().unwrap();
                let s = states.get_mut(peer).unwrap();
                if !s.active {
                    continue;
                }
                if s.follow {
                    if s.subscription_due(now) {
                        let remote = s.source_id.zip(s.source_port);
                        for (index, kind) in [0, 9, 11].into_iter().enumerate() {
                            if !s.subscription_needed(index, now) {
                                continue;
                            }
                            let request = subscription(clock_id, remote, seq, kind);
                            if send_ptp(general, &request, (*peer, 320).into(), s, "subscribe") {
                                let pending = &mut s.subscriptions[index];
                                pending.retain(|p| now.duration_since(p.sent) < SAMPLE_LIFETIME);
                                if pending.len() == 4 {
                                    pending.pop_front();
                                }
                                pending.push_back(Subscription {
                                    sequence: seq,
                                    interval: request[49] as i8,
                                    sent: now,
                                });
                            }
                        }
                        s.next_subscription = Some(now + SUBSCRIPTION_RETRY);
                    }
                    if now.duration_since(last_announce) >= Duration::from_secs(1) {
                        let mut req = header(1, 44, clock_id, seq, 0x0408, 0);
                        let sent = clock.now_ns();
                        timestamp(&mut req[34..44], sent);
                        if send_ptp(event, &req, (*peer, 319).into(), s, "delay_request") {
                            s.delay = Some((seq, sent));
                            s.delay_age = Some(now);
                        }
                    }
                    continue;
                }
                let (sync, follow) = sync_pair(clock_id, seq, clock.now_ns());
                send_ptp(event, &sync, (*peer, 319).into(), s, "sync");
                send_ptp(general, &follow, (*peer, 320).into(), s, "follow_up");
                if now.duration_since(last_announce) >= Duration::from_secs(1) {
                    send_ptp(
                        general,
                        &announce(clock_id, seq, clock.now_ns()),
                        (*peer, 320).into(),
                        s,
                        "announce",
                    );
                }
            }
            if now.duration_since(last_announce) >= Duration::from_secs(1) {
                last_announce = now;
            }
            seq = seq.wrapping_add(1);
            next_sync = now + Duration::from_millis(125);
        }
        for (socket_index, socket) in [event, general].into_iter().enumerate() {
            let until = Instant::now() + Duration::from_millis(1);
            for _ in 0..128 {
                if Instant::now() >= until {
                    break;
                }
                let (n, addr) = match socket.recv_from(&mut buf) {
                    Ok(packet) => packet,
                    Err(error) if error.kind() == io::ErrorKind::WouldBlock => break,
                    Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
                    Err(error) => {
                        let mut counters = network.lock().unwrap();
                        counters.receive_errors += 1;
                        counters.last_receive_error = Some(SocketFailure::new(
                            if socket_index == 0 {
                                "receive_event"
                            } else {
                                "receive_general"
                            },
                            &error,
                        ));
                        break;
                    }
                };
                {
                    let mut counters = network.lock().unwrap();
                    if socket_index == 0 {
                        counters.event_received += 1;
                    } else {
                        counters.general_received += 1;
                    }
                    if !peers.contains(&addr.ip()) {
                        counters.unknown_peer += 1;
                        continue;
                    }
                }
                let local_received = clock.now_ns();
                let received_at = Instant::now();
                let mut states = state.lock().unwrap();
                let s = states.get_mut(&addr.ip()).unwrap();
                s.packets.raw_received += 1;
                let rejection = if ![319, 320].contains(&addr.port()) {
                    Some("source_port")
                } else {
                    packet_rejection(&buf[..n])
                };
                if let Some(reason) = rejection {
                    s.reject(reason);
                    continue;
                }
                rx.fetch_add(1, Ordering::Relaxed);
                let kind = buf[0] & 0xf;
                s.packets.received_by_type[usize::from(kind)] += 1;
                let request_seq = u16::from_be_bytes([buf[30], buf[31]]);
                if !s.active {
                    continue;
                }
                if s.follow && [0, 8, 9, 11, 12].contains(&kind) {
                    followed_packet(s, &buf[..n], local_received, received_at, clock_id);
                }
                if kind == 12 {
                    for grant in unicast_grants(&buf[..n], clock_id, request_seq) {
                        if send_ptp(general, &grant, (addr.ip(), 320).into(), s, "grant") {
                            s.grants += 1;
                        }
                    }
                }
                respond_to_delay(
                    event,
                    general,
                    addr.ip(),
                    &buf[..n],
                    local_received,
                    clock,
                    clock_id,
                    s,
                    received_at,
                );
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
        s.subscriptions[0].push_back(Subscription {
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
#[cfg(test)]
#[path = "clock_tests.rs"]
mod regression_tests;
