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
/// How a SETUP-time BMCA negotiation ended. Reported to the desktop so a session
/// that follows the receiver says why.
#[derive(Clone, Copy, Debug, serde::Serialize)]
pub struct Verdict {
    /// True when the receiver outranks us and we become the slave.
    pub yield_master: bool,
    pub our_p1: u8,
    pub remote_p1: Option<u8>,
    pub remote_is_master: bool,
    /// Announces we asked for before deciding (bounded).
    pub probes: u8,
}
/// The single key WAC's "Mac-style negotiation" compares. It does not use
/// `priority2` (absent from the binary) or `stepsRemoved` (parsed, never compared),
/// so neither does this: a lower `priority1` from a peer that announces as master
/// wins, and anything else — including silence — leaves us as master.
pub fn decide(our_p1: u8, remote: Option<BmcaDataset>, probes: u8) -> Verdict {
    let remote_is_master = remote.is_some_and(|dataset| dataset.grandmaster != 0);
    let remote_p1 = remote.map(|dataset| dataset.priority1);
    Verdict {
        yield_master: remote_is_master && remote_p1.is_some_and(|p1| p1 < our_p1),
        our_p1,
        remote_p1,
        remote_is_master,
        probes,
    }
}
/// Our advertised priority1. Deliberately lax: preferring the receiver's clock is
/// the whole point of the negotiation, and a tie keeps us as master.
pub const OUR_PRIORITY1: u8 = 96;
/// Announces asked for before giving up and staying master.
const BMCA_PROBE_ATTEMPTS: u8 = 3;
const BMCA_PROBE_INTERVAL: Duration = Duration::from_millis(300);
/// The grandfather dataset an Announce carries, in the fields BMCA compares first.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct BmcaDataset {
    pub priority1: u8,
    pub clock_class: u8,
    pub accuracy: u8,
    pub variance: u16,
    pub priority2: u8,
    pub grandmaster: u64,
    pub steps_removed: u16,
}
impl BmcaDataset {
    /// IEEE 1588 orders datasets by priority1, class, accuracy, variance,
    /// priority2 and grandmaster identity; `steps_removed` only breaks ties among
    /// paths to the same grandmaster.
    pub fn compare(&self, other: &Self) -> std::cmp::Ordering {
        self.priority1
            .cmp(&other.priority1)
            .then(self.clock_class.cmp(&other.clock_class))
            .then(self.accuracy.cmp(&other.accuracy))
            .then(self.variance.cmp(&other.variance))
            .then(self.priority2.cmp(&other.priority2))
            .then(self.grandmaster.cmp(&other.grandmaster))
            .then(self.steps_removed.cmp(&other.steps_removed))
    }
    /// Our own dataset: a locally generated clock with a lax preference so a real
    /// grandmaster wins, but a peer of equal rank does not cause a role flip.
    pub fn local(clock_id: u64) -> Self {
        Self {
            priority1: 96,
            clock_class: 248,
            accuracy: 0xfe,
            variance: 0xffff,
            priority2: 128,
            grandmaster: clock_id,
            steps_removed: 0,
        }
    }
}
/// Parse the grandfather fields of an Announce message. Returns `None` for any
/// other message type or a packet too short to contain them.
pub fn parse_announce(packet: &[u8]) -> Option<BmcaDataset> {
    // byte 0 low nibble is the message type, byte 1 is the PTP version (2).
    if packet.len() < 64 || packet[0] & 0xf != 11 || packet[1] & 0xf != 2 {
        return None;
    }
    Some(BmcaDataset {
        priority1: packet[47],
        clock_class: packet[48],
        accuracy: packet[49],
        variance: u16::from_be_bytes([packet[50], packet[51]]),
        priority2: packet[52],
        grandmaster: u64::from_be_bytes(packet[53..61].try_into().ok()?),
        steps_removed: u16::from_be_bytes([packet[61], packet[62]]),
    })
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
    offset_at: Option<Instant>,
    /// Last mapping actually published by this association, with its sample age.
    last_mapping: Option<(f64, Instant)>,
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
    /// Grandfather dataset from the last Announce this peer sent, plus whether it
    /// beats our own.
    bmca: Option<BmcaDataset>,
    better_master: bool,
    /// Announces requested while deciding the clock role at SETUP.
    bmca_probes: u8,
    next_probe: Option<Instant>,
    /// Clock identity from this peer's last Announce, tracked regardless of the
    /// role we hold. A takeover commonly restarts the receiver's PTP association.
    announced_clock_id: Option<u64>,
    /// How many times the announced identity changed after the first sighting.
    identity_changes: u64,
    last_identity_change: Option<Instant>,
}
const SAMPLE_LIFETIME: Duration = Duration::from_secs(2);
const MAX_PENDING_SYNCS: usize = 32;
const SUBSCRIPTION_RETRY: Duration = Duration::from_millis(500);

#[derive(Clone, Copy)]
struct SyncSample {
    sequence: u16,
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
    /// The peer's BMCA dataset, when it has sent an Announce.
    pub bmca_priority1: Option<u8>,
    pub bmca_clock_class: Option<u8>,
    pub bmca_priority2: Option<u8>,
    pub bmca_grandmaster: Option<u64>,
    pub bmca_steps_removed: Option<u16>,
    /// True when the peer's dataset outranks ours (IEEE 1588 comparison order).
    pub better_master: bool,
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
    /// When this peer last restarted its PTP association, if it ever did.
    fn identity_change_at(&self) -> Option<Instant> {
        self.last_identity_change
    }
    /// The last exchange time once the peer has been silent for longer than its
    /// own advertised exchange timeout. `None` while it is still following.
    fn stalled_at(&self, now: Instant) -> Option<Instant> {
        let last = self.last?;
        (now.saturating_duration_since(last) >= self.exchange_timeout()).then_some(last)
    }
    fn delay_offset(&self, now: Instant) -> Option<f64> {
        self.offset.filter(|_| {
            self.offset_at
                .is_some_and(|at| now.duration_since(at) < SAMPLE_LIFETIME)
        })
    }
    /// The last offset, kept alive for a grace window after the exchanges stop.
    ///
    /// The reference implementation freezes drift correction when PTP goes quiet
    /// rather than ending the session, so the timeline keeps using the final
    /// estimate until the caller's grace expires.
    fn frozen_offset(&self, now: Instant, grace: Duration) -> Option<f64> {
        let (offset, at) = self.last_mapping?;
        if now.saturating_duration_since(at) > grace {
            return None;
        }
        Some(offset)
    }
    fn stale_for(&self, now: Instant) -> Option<Duration> {
        self.last.map(|last| now.duration_since(last))
    }
    fn map_offset(&mut self, now: Instant, grace: Duration) -> Option<(f64, bool)> {
        if self.ready(now) {
            let offset = self.effective_offset(now)?;
            self.last_mapping = Some((offset, self.last?));
            Some((offset, false))
        } else if !grace.is_zero() {
            self.frozen_offset(now, grace).map(|offset| (offset, true))
        } else {
            None
        }
    }
    /// Prefer a fresh delay measurement, then follow the ongoing Sync estimates
    /// when usable delay responses stop arriving.
    fn effective_offset(&self, now: Instant) -> Option<f64> {
        self.delay_offset(now).or(self.sync_offset)
    }
    fn offset_source(&self, now: Instant) -> Option<&'static str> {
        if self.delay_offset(now).is_some() {
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
        } else if self.follow && self.effective_offset(now).is_none() {
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
        if let Some(sample) = self.sync_pair {
            // Compare source sequence numbers as well as local arrival times:
            // an old Sync can arrive after a newer pair or its own Follow_Up.
            // Serial arithmetic permits u16 wraparound. After an exchange
            // timeout, allow the receiver to restart its sequence counter.
            let advance = sequence.wrapping_sub(sample.sequence);
            let newer_sequence = (1..0x8000).contains(&advance);
            if received <= sample.received
                || at < sample.at
                || (now.duration_since(sample.at) < self.exchange_timeout() && !newer_sequence)
            {
                return;
            }
        }
        self.sync_pair = Some(SyncSample {
            sequence,
            origin,
            received,
            correction: correction_ns,
            at,
        });
        if self.follow {
            // A complete pair estimates the receiver-to-local offset without
            // the path delay. Keep tracking it even when delay responses are
            // unusable; a fresh validated response refines the estimate without
            // being required for readiness.
            let sample = (origin as i128 - received as i128) as f64 + correction_ns;
            self.sync_offset = Some(
                self.sync_offset
                    .map_or(sample, |old| old * 0.875 + sample * 0.125),
            );
            self.exchanged(at);
        }
    }
    fn exchanged(&mut self, now: Instant) {
        if self.last.is_some_and(|last| now < last) {
            return;
        }
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
            && (!self.follow || self.effective_offset(now).is_some())
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
/// Track the clock identity announced by a peer, even when we are the master and
/// do not follow it. A changed identity means the receiver restarted its PTP
/// association, which is exactly what another controller taking over looks like.
fn note_announce_identity(s: &mut PeerState, data: &[u8], now: Instant) {
    if data.len() < 64 || data[0] & 15 != 11 {
        return;
    }
    let id = u64::from_be_bytes(data[53..61].try_into().unwrap());
    if let Some(previous) = s.announced_clock_id
        && previous != id
    {
        s.identity_changes += 1;
        s.last_identity_change = Some(now);
    }
    s.announced_clock_id = Some(id);
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
            let mut identity_changes = s.identity_changes;
            let mut last_identity_change = s.last_identity_change;
            // `note_announce_identity` runs for every Announce before this path
            // and already counted a clock-identity change. Count a source-only
            // restart here so callers that feed `followed_packet` directly still
            // see one association change per restart.
            let identity_changed = s.clock_id.is_some() && s.announced_clock_id != Some(id);
            let source_changed = s.clock_id.is_some()
                && s.source_id.is_some()
                && (s.source_id != Some(source) || s.source_port != Some(source_port));
            if identity_changed || source_changed {
                if identity_changed {
                    identity_changes += 1;
                }
                last_identity_change = Some(now);
            }
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
                announced_clock_id: s.announced_clock_id.or(Some(id)),
                identity_changes,
                last_identity_change,
                ..Default::default()
            };
        }
        // Record the grandfather dataset so a better master is visible in the
        // snapshot even though the role itself is not switched here.
        if let Some(dataset) = parse_announce(data) {
            s.bmca = Some(dataset);
            s.better_master =
                dataset.compare(&BmcaDataset::local(own_id)) == std::cmp::Ordering::Less;
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
                let previous = s.delay_offset(now);
                s.jitter_ns = previous.map(|old| (old - offset).abs());
                s.round_trip_ns = Some(rtt);
                if previous.is_some_and(|old| (old - offset).abs() > 5_000_000.0) {
                    s.reject("offset_jump");
                    s.exchanges = 0;
                    s.first = None;
                    s.offset = None;
                } else {
                    s.offset = previous;
                }
                s.offset = Some(s.offset.map_or(offset, |old| old * 0.875 + offset * 0.125));
                s.offset_at = Some(now);
                s.last = Some(now);
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
    /// Wait for the receiver's Announce and decide the clock role, once, before
    /// RECORD. The worker sends the probes; this only waits, so no SETUP traffic
    /// can be delayed by more than `timeout`.
    pub fn negotiate(&self, host: IpAddr, timeout: Duration) -> Verdict {
        let deadline = Instant::now() + timeout;
        loop {
            let (remote, probes, following) = {
                let states = self.states.lock().unwrap();
                match states.get(&host) {
                    Some(peer) => (peer.bmca, peer.bmca_probes, peer.follow),
                    None => (None, 0, false),
                }
            };
            // Already following (configured to): the receiver is the master by
            // policy, so there is nothing to negotiate.
            if following {
                return decide(OUR_PRIORITY1, remote, probes);
            }
            if remote.is_some() || probes >= BMCA_PROBE_ATTEMPTS {
                return decide(OUR_PRIORITY1, remote, probes);
            }
            if Instant::now() >= deadline {
                return decide(OUR_PRIORITY1, remote, probes);
            }
            std::thread::sleep(Duration::from_millis(20));
        }
    }
    /// Become the slave of `host`: stop announcing and follow its Sync/Follow_Up.
    /// Only meaningful before RECORD, which is where `negotiate` is called.
    pub fn yield_to(&self, host: IpAddr) {
        let mut states = self.states.lock().unwrap();
        if let Some(peer) = states.get_mut(&host) {
            peer.follow = true;
            peer.next_subscription = None;
            // The Announce we already decoded stays as telemetry, but the packet
            // counters start clean so readiness is measured on the followed path.
            peer.packets = PacketSnapshot::default();
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
                offset_ns: s.effective_offset(now),
                offset_source: s.offset_source(now),
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
                bmca_priority1: s.bmca.map(|b| b.priority1),
                bmca_clock_class: s.bmca.map(|b| b.clock_class),
                bmca_priority2: s.bmca.map(|b| b.priority2),
                bmca_grandmaster: s.bmca.map(|b| b.grandmaster),
                bmca_steps_removed: s.bmca.map(|b| b.steps_removed),
                better_master: s.better_master,
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
        self.timeline_frozen(host, local_ns, Duration::ZERO)
            .map(|(id, network, _)| (id, network))
    }
    /// Same mapping as [`Self::timeline`], but a stale offset keeps working for
    /// `grace`. The flag reports whether the offset was frozen, which is what the
    /// session turns into a warning instead of a reconnect.
    pub fn timeline_frozen(
        &self,
        host: IpAddr,
        local_ns: u64,
        grace: Duration,
    ) -> Result<(u64, u64, bool)> {
        let mut states = self.states.lock().unwrap();
        let s = states.get_mut(&host).context("unknown timing peer")?;
        if !s.follow {
            return Ok((self.clock_id, local_ns, false));
        }
        let now = Instant::now();
        let (offset, frozen) = s
            .map_offset(now, grace)
            .context("receiver clock is not ready")?;
        let mapped = local_ns as i128 + offset.round() as i128;
        Ok((
            s.clock_id.context("missing receiver clock identity")?,
            u64::try_from(mapped)?,
            frozen,
        ))
    }
    /// How long the peer has been silent, or `None` when it never answered.
    pub fn stale_for(&self, host: IpAddr, now: Instant) -> Option<Duration> {
        self.states
            .lock()
            .unwrap()
            .get(&host)
            .and_then(|s| s.stale_for(now))
    }
    /// When the peer last restarted its PTP association (new clock identity or
    /// source), or `None` if it has not done so since the first Announce.
    /// Session code pairs this with a control-plane signal, so the timestamp is
    /// the actual change time rather than "still true" polling time.
    pub fn association_changed_at(&self, host: IpAddr) -> Option<Instant> {
        self.states
            .lock()
            .unwrap()
            .get(&host)
            .and_then(PeerState::identity_change_at)
    }
    /// When the peer's last PTP exchange happened, once it has been silent for
    /// longer than its own advertised exchange timeout. This is the "peer stopped
    /// following us" half of an association restart.
    pub fn association_stalled_at(&self, host: IpAddr, now: Instant) -> Option<Instant> {
        self.states
            .lock()
            .unwrap()
            .get(&host)
            .and_then(|s| s.stalled_at(now))
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
                // BMCA runs once, before RECORD: ask the receiver to announce so we
                // can compare priority1, with a bounded number of probes. A peer
                // that never announces simply leaves us as master.
                if s.bmca.is_none()
                    && s.bmca_probes < BMCA_PROBE_ATTEMPTS
                    && s.next_probe.is_none_or(|at| now >= at)
                {
                    let probe = subscription(clock_id, None, seq, 11);
                    if send_ptp(general, &probe, (*peer, 320).into(), s, "subscribe") {
                        s.bmca_probes += 1;
                        s.next_probe = Some(now + BMCA_PROBE_INTERVAL);
                    }
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
                if kind == 11 {
                    note_announce_identity(s, &buf[..n], received_at);
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
    fn a_stale_offset_survives_only_inside_the_grace_window() {
        let now = Instant::now();
        let peer = PeerState {
            offset: Some(1_500.0),
            offset_at: Some(now),
            last_mapping: Some((1_500.0, now)),
            ..PeerState::default()
        };
        // Fresh: the normal path returns it and nothing is frozen.
        assert_eq!(peer.delay_offset(now), Some(1_500.0));
        assert!(!peer.ready(now));
        // Three seconds later the offset is stale for the regular path but still
        // usable for a frozen timeline inside the grace window.
        let later = now + Duration::from_secs(3);
        assert_eq!(peer.delay_offset(later), None);
        assert_eq!(
            peer.frozen_offset(later, Duration::from_secs(10)),
            Some(1_500.0)
        );
        assert_eq!(peer.frozen_offset(later, Duration::from_secs(2)), None);
        // Without a measurement there is nothing to freeze.
        let empty = PeerState::default();
        assert_eq!(empty.frozen_offset(now, Duration::from_secs(10)), None);
    }
    #[test]
    fn staleness_is_measured_from_the_last_exchange() {
        let now = Instant::now();
        let mut peer = PeerState::default();
        assert_eq!(peer.stale_for(now), None);
        peer.exchanged(now);
        assert_eq!(
            peer.stale_for(now + Duration::from_millis(700)),
            Some(Duration::from_millis(700))
        );
    }
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
    fn offset_jump_preserves_last_exchange_and_recovers_within_grace() {
        let now = Instant::now();
        let mut s = PeerState {
            follow: true,
            ..Default::default()
        };
        followed_packet(&mut s, &announce(8, 0, 0), 0, now, 7);
        for seq in 0..3u16 {
            let at = now + Duration::from_millis(u64::from(seq) * 100);
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
            followed_packet(&mut s, &response, base + 400_000, at, 7);
        }
        assert!(s.ready(now + Duration::from_millis(300)));
        assert_eq!(s.offset, Some(1_000_000.0));

        // Inject an exchange with a > 5 ms offset jump (jump of 10 ms)
        let jump_at = now + Duration::from_millis(400);
        let jump_base = 4_000_000_000u64;
        let (sync, follow) = sync_pair(8, 3, jump_base + 11_000_000);
        followed_packet(&mut s, &sync, jump_base + 100_000, jump_at, 7);
        followed_packet(&mut s, &follow, jump_base + 110_000, jump_at, 7);
        s.delay = Some((3, jump_base + 200_000));
        s.delay_age = Some(jump_at);
        let mut response = header(9, 54, 8, 3, 0x0408, 0);
        timestamp(&mut response[34..44], jump_base + 11_300_000);
        response[44..52].copy_from_slice(&7u64.to_be_bytes());
        response[52..54].copy_from_slice(&0x8005u16.to_be_bytes());
        followed_packet(&mut s, &response, jump_base + 400_000, jump_at, 7);

        assert_eq!(s.packets.last_rejection, Some("offset_jump"));
        assert_eq!(s.exchanges, 0);
        assert!(!s.ready(jump_at));
        // Crucial invariant: s.last must NOT be None; it must record the packet exchange instant
        assert!(s.last.is_some());
        assert!(s.stale_for(jump_at).is_some_and(|stale| stale < Duration::from_millis(10)));
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
    fn announce_dataset_is_parsed_from_the_wire_and_matches_what_we_send() {
        let id = 0x0102030405060708;
        let packet = announce(id, 7, 0);
        let dataset = parse_announce(&packet).unwrap();
        // Our own Announce advertises a local oscillator with a lax preference.
        assert_eq!(dataset.priority1, 96);
        assert_eq!(dataset.clock_class, 248);
        assert_eq!(dataset.priority2, 128);
        assert_eq!(dataset.grandmaster, id);
        assert_eq!(dataset.steps_removed, 0);
        // Anything that is not an Announce is rejected rather than misparsed.
        assert!(parse_announce(&sync_pair(id, 1, 0).0).is_none());
        let mut short = packet.clone();
        short.truncate(50);
        assert!(parse_announce(&short).is_none());
    }
    #[test]
    fn bmca_orders_by_priority_then_class_then_identity() {
        let base = BmcaDataset::local(7);
        // Lower priority1 wins, before anything else.
        assert_eq!(
            BmcaDataset {
                priority1: 10,
                ..base
            }
            .compare(&base),
            std::cmp::Ordering::Less
        );
        // Equal priority1: a better clock class wins.
        assert_eq!(
            BmcaDataset {
                clock_class: 6,
                ..base
            }
            .compare(&base),
            std::cmp::Ordering::Less
        );
        // Class, accuracy, variance and priority2 all tie: identity decides.
        assert_eq!(
            BmcaDataset {
                grandmaster: 1,
                ..base
            }
            .compare(&base),
            std::cmp::Ordering::Less
        );
        // A dataset identical to ours is not "better", so no role flip.
        assert_eq!(base.compare(&base), std::cmp::Ordering::Equal);
        assert!(!(base.compare(&base) == std::cmp::Ordering::Less));
        // steps_removed only breaks ties on the same path.
        assert_eq!(
            BmcaDataset {
                steps_removed: 3,
                ..base
            }
            .compare(&base),
            std::cmp::Ordering::Greater
        );
    }
    #[test]
    fn a_receiver_announcing_a_grandmaster_outranks_our_local_clock() {
        let own = BmcaDataset::local(0xaaaa);
        // A HomePod advertising an Apple grandmaster (priority1 128) loses on the
        // first field, so it does not get to take the role.
        let homepod = BmcaDataset {
            priority1: 128,
            clock_class: 6,
            ..own
        };
        assert_eq!(homepod.compare(&own), std::cmp::Ordering::Greater);
        // A device with a better class and the same priority does.
        let appliance = BmcaDataset {
            clock_class: 20,
            ..own
        };
        assert_eq!(appliance.compare(&own), std::cmp::Ordering::Less);
    }

    #[test]
    fn bmca_decides_on_priority1_alone_and_stays_master_otherwise() {
        let ours = OUR_PRIORITY1;
        // A receiver that announces a lower priority1 as master wins the role.
        let better = BmcaDataset {
            priority1: ours - 1,
            grandmaster: 0x1234,
            ..BmcaDataset::local(0)
        };
        let verdict = decide(ours, Some(better), 1);
        assert!(verdict.yield_master);
        assert_eq!(verdict.remote_p1, Some(ours - 1));
        assert!(verdict.remote_is_master);

        // A worse priority1 never wins, even with a better clock class: the
        // negotiation WAC uses compares one key.
        let worse = BmcaDataset {
            priority1: ours + 1,
            clock_class: 6,
            grandmaster: 0x1234,
            ..BmcaDataset::local(0)
        };
        assert!(!decide(ours, Some(worse), 1).yield_master);

        // A tie keeps us as master, which is what makes the choice deterministic.
        let tie = BmcaDataset {
            grandmaster: 0x1234,
            ..BmcaDataset::local(0)
        };
        assert!(!decide(ours, Some(tie), 1).yield_master);
    }

    #[test]
    fn silence_or_an_empty_announce_leaves_us_as_master() {
        // No answer at all: fail open, and record how many probes went unanswered.
        let silent = decide(OUR_PRIORITY1, None, BMCA_PROBE_ATTEMPTS);
        assert!(!silent.yield_master);
        assert!(!silent.remote_is_master);
        assert_eq!(silent.remote_p1, None);
        assert_eq!(silent.probes, BMCA_PROBE_ATTEMPTS);

        // An Announce with no grandmaster identity does not claim the role.
        let anonymous = BmcaDataset {
            priority1: 10,
            grandmaster: 0,
            ..BmcaDataset::local(0)
        };
        let verdict = decide(OUR_PRIORITY1, Some(anonymous), 1);
        assert!(!verdict.yield_master);
        assert!(!verdict.remote_is_master);
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
