//! AirPlay member orchestration, live loopback and finite quiet qualification.
use crate::{
    auth,
    clock::{Clock, PtpMaster},
    crypto::derive,
    preemption,
    rtp::{self, FRAMES, PCM_BYTES, Packetizer},
    rtsp::{Cancellation, Connection},
    telemetry::TimelineResetReason,
    transport::{
        EventWorker, Fault, FeedbackTiming, FeedbackWorker, Health, MediaSchedule, MediaTransport,
    },
};
use anyhow::{Context, Result, ensure};
use plist::{Dictionary, Value};
use serde::Deserialize;
use serde_json::{Value as Json, json};
use std::{
    net::{IpAddr, SocketAddr, UdpSocket},
    path::PathBuf,
    sync::atomic::Ordering,
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use uuid::Uuid;

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Peer {
    #[serde(default)]
    pub receiver_id: String,
    pub host: IpAddr,
    #[serde(default = "default_port")]
    pub port: u16,
    #[serde(default)]
    pub codecs: Vec<u8>,
    #[serde(default)]
    pub profile: crate::profile::Profile,
}
fn default_port() -> u16 {
    7000
}
fn default_sample_rate() -> u32 {
    44100
}
#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ProbeOptions {
    pub peers: Vec<Peer>,
    #[serde(default)]
    pub intent: StartIntent,
    #[serde(default)]
    pub wav_path: PathBuf,
    #[serde(default = "default_source")]
    pub source: String,
    #[serde(default)]
    pub capture_endpoint: Option<String>,
    #[serde(default)]
    pub capture_application_path: Option<String>,
    #[serde(default)]
    pub capture_process_id: Option<u32>,
    #[serde(default)]
    pub capture_process_started_at: Option<u64>,
    #[serde(default)]
    pub standby: crate::standby::Settings,
    pub duration_ms: u32,
    pub latency_ms: u32,
    #[serde(default)]
    pub force_latency: bool,
    #[serde(default)]
    pub adaptive_latency: bool,
    #[serde(default)]
    pub adaptive_initial_latency_ms: Option<u32>,
    pub gain: f32,
    #[serde(default)]
    pub equalizer: crate::equalizer::Settings,
    #[serde(default = "default_sample_rate")]
    pub sample_rate: u32,
    #[serde(default = "default_timing")]
    pub timing: String,
    #[serde(default)]
    pub group_id: Option<String>,
    #[serde(default)]
    pub group_mode: String,
    #[serde(default)]
    pub handshake_only: bool,
    #[serde(default)]
    pub record_mic_path: Option<PathBuf>,
    /// Correlation id supplied by the IPC host (the request's `session_id`).
    /// `serde(skip)` keeps the wire parameters unchanged: the engine front end
    /// fills this in after deserializing, and standalone callers leave it empty.
    #[serde(skip)]
    pub report_session_id: String,
}
#[derive(Clone, Copy, Debug, Default, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum StartIntent {
    #[default]
    Manual,
    Automatic,
}
fn default_source() -> String {
    "wav".into()
}
fn default_timing() -> String {
    "ptp".into()
}
impl ProbeOptions {
    fn validate_forced_latency(&self) -> Result<()> {
        ensure!(
            self.adaptive_initial_latency_ms.is_none_or(|ms| ms <= 2000),
            "adaptive initial latency must be 0..2000 ms"
        );
        ensure!(
            !self.adaptive_latency || !self.force_latency,
            "adaptive and forced latency are mutually exclusive"
        );
        if self.force_latency {
            let mut targets = self
                .peers
                .iter()
                .map(|p| p.profile.latency_ms.unwrap_or(self.latency_ms));
            if let Some(first) = targets.next() {
                ensure!(
                    targets.all(|value| value == first),
                    "forced latency requires the same target on every group member"
                );
            }
        }
        Ok(())
    }
    pub fn validate_start(&self) -> Result<()> {
        self.validate_forced_latency()?;
        ensure!(
            matches!(self.source.as_str(), "loopback" | "application") && self.duration_ms == 0,
            "start requires unbounded live source"
        );
        ensure!(
            self.gain.is_finite() && (0.0..=1.0).contains(&self.gain),
            "gain must be 0..1"
        );
        self.standby.validate()?;
        if self.source == "application" {
            ensure!(
                self.capture_application_path
                    .as_ref()
                    .is_some_and(|p| !p.trim().is_empty()),
                "select an application"
            );
            ensure!(
                self.capture_endpoint.is_none(),
                "application capture cannot select an endpoint"
            );
            ensure!(
                self.capture_process_id != Some(0),
                "invalid application process ID"
            );
            ensure!(
                self.capture_process_id.is_some() == self.capture_process_started_at.is_some(),
                "application process ID and creation time must be supplied together"
            );
        }
        let mut bounded = self.clone();
        bounded.source = "loopback".into();
        bounded.capture_application_path = None;
        bounded.capture_process_id = None;
        bounded.capture_process_started_at = None;
        bounded.standby = crate::standby::Settings::default();
        bounded.duration_ms = 5000;
        bounded.gain = 0.1;
        self.equalizer.validate()?;
        bounded.equalizer = crate::equalizer::Settings::default();
        bounded.validate()
    }
    pub fn validate(&self) -> Result<()> {
        self.validate_forced_latency()?;
        self.equalizer.validate()?;
        ensure!(
            !self.standby.enabled
                && self.capture_application_path.is_none()
                && self.capture_process_id.is_none()
                && self.capture_process_started_at.is_none(),
            "finite probes do not accept application capture or standby"
        );
        ensure!(
            !self.equalizer.enabled,
            "finite probes require the equalizer to be disabled"
        );
        ensure!(
            self.source == "wav" || self.source == "loopback",
            "unknown audio source"
        );
        ensure!(
            !self.peers.is_empty() && self.peers.len() <= 2,
            "probe supports one device or one stereo pair"
        );
        ensure!(
            self.peers.iter().all(|p| p.host.is_ipv4() && p.port > 0),
            "IPv4 endpoint required"
        );
        if self.peers.len() == 2 {
            ensure!(self.peers[0].host != self.peers[1].host, "duplicate peer");
        }
        ensure!(
            ["", "single", "native_stereo", "software_stereo"].contains(&self.group_mode.as_str()),
            "invalid group mode"
        );
        for peer in &self.peers {
            ensure!(
                (-500..=500).contains(&peer.profile.sync_offset_ms),
                "sync offset must be -500..500 ms"
            );
            ensure!(
                peer.profile.latency_ms.is_none_or(|v| v <= 2000),
                "latency must be 0..2000 ms"
            );
        }
        if self.group_mode == "software_stereo" {
            ensure!(
                self.peers.len() == 2
                    && self
                        .peers
                        .iter()
                        .any(|p| p.profile.channel == crate::profile::Channel::Left)
                    && self
                        .peers
                        .iter()
                        .any(|p| p.profile.channel == crate::profile::Channel::Right),
                "software stereo requires one left and one right member"
            );
        } else {
            ensure!(
                self.peers
                    .iter()
                    .all(|p| p.profile.channel == crate::profile::Channel::Stereo),
                "channel routing requires a software stereo group"
            );
        }
        ensure!(
            (1..=5000).contains(&self.duration_ms),
            "probe duration must be 1..5000 ms"
        );
        ensure!(
            (0..=2000).contains(&self.latency_ms),
            "latency must be 0..2000 ms"
        );
        ensure!(
            self.gain.is_finite() && (0.0..=0.1).contains(&self.gain),
            "probe gain must be 0..0.1"
        );
        ensure!(
            self.timing == "ptp" || self.timing == "ntp",
            "timing must be ptp or ntp"
        );
        ensure!(
            crate::rtp::SUPPORTED_RATES.contains(&self.sample_rate),
            "sample rate must be 44100 or 48000"
        );
        Ok(())
    }
    /// WAC's topology plan takes one codec for the whole group. Software stereo
    /// may request different profiles per member, but the codec does not change
    /// the timing verdict, so the first member's request is the group's.
    fn profile_codec(&self) -> crate::profile::Codec {
        self.peers
            .first()
            .map_or(crate::profile::Codec::Auto, |peer| peer.profile.codec)
    }
}
fn clock_status(timer: &PtpMaster, hosts: &[IpAddr], phase: &str) -> Json {
    let members: Vec<_> = timer
        .snapshots()
        .into_iter()
        .filter(|peer| hosts.contains(&peer.host))
        .collect();
    json!({
        "event":"clock_status", "phase":phase, "members":members,
        "network":timer.network_snapshot(), "mmcss":*timer.mmcss.lock().unwrap(),
        "engine_path":std::env::current_exe().ok(),
        "event_port":319, "general_port":320,
    })
}
fn dictionary(items: Vec<(&str, Value)>) -> Value {
    Value::Dictionary(
        items
            .into_iter()
            .map(|(k, v)| (k.to_owned(), v))
            .collect::<Dictionary>(),
    )
}
fn string(s: impl Into<String>) -> Value {
    Value::String(s.into())
}
fn number(n: u64) -> Value {
    Value::Integer(n.into())
}
fn get_port(value: &Value, key: &str) -> Result<u16> {
    let p = value
        .as_dictionary()
        .and_then(|d| d.get(key))
        .and_then(Value::as_unsigned_integer)
        .context(format!("missing {key}"))?;
    ensure!(p > 0 && p <= 65535, "invalid {key}");
    Ok(p as u16)
}

struct NtpServer {
    port: u16,
    cancel: Cancellation,
    worker: Option<JoinHandle<()>>,
}
impl NtpServer {
    fn start(peers: Vec<IpAddr>, clock: Clock) -> Result<Self> {
        let sock = UdpSocket::bind("0.0.0.0:0")?;
        let port = sock.local_addr()?.port();
        sock.set_read_timeout(Some(Duration::from_millis(100)))?;
        let cancel = Cancellation::default();
        let stop = cancel.clone();
        let worker = thread::spawn(move || {
            let mut buf = [0; 128];
            while !stop.is_cancelled() {
                if let Ok((n, addr)) = sock.recv_from(&mut buf) {
                    if n != 32 || !peers.contains(&addr.ip()) || buf[1] & 0x7f != 0x52 {
                        continue;
                    }
                    let received = clock.now_ns();
                    let mut out = vec![0x80, 0xd3, 0, 7, 0, 0, 0, 0];
                    out.extend_from_slice(&buf[24..32]);
                    out.extend(rtp::ntp(received));
                    out.extend(rtp::ntp(clock.now_ns()));
                    let _ = sock.send_to(&out, addr);
                }
            }
        });
        Ok(Self {
            port,
            cancel,
            worker: Some(worker),
        })
    }
}
impl Drop for NtpServer {
    fn drop(&mut self) {
        self.cancel.cancel();
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
    }
}
fn authenticate_with<C, T>(
    host: IpAddr,
    connection: &mut C,
    saved: Result<Option<auth::Credentials>>,
    verify: impl FnOnce(&mut C, &auth::Credentials) -> Result<T>,
    transient: impl FnOnce(&mut C) -> Result<T>,
) -> Result<T> {
    // Storage errors precede the authentication wrapper and retain setup classification.
    let saved = saved?;
    (if let Some(credentials) = saved {
        verify(connection, &credentials)
    } else {
        transient(connection)
    })
    .map_err(|error| {
        let mut fault = Fault::from_error(host, "authentication", &error);
        if fault.code == "protocol_error" {
            fault.code = "authentication_failed".into();
        }
        fault.into()
    })
}

struct Member {
    compat: crate::profile::CompatProfile,
    peer: Peer,
    conn: std::sync::Arc<std::sync::Mutex<Connection>>,
    events: Option<EventWorker>,
    feedback: Option<FeedbackWorker>,
    health: Health,
    media: MediaTransport,
    uri: String,
    remote_control: u16,
    ready: bool,
    closed: bool,
    session_started: bool,
    /// True while the timeline is running on a frozen PTP offset.
    clock_frozen: bool,
    /// Where and with which key the event channel is opened, so it can be rebuilt
    /// without touching the media stream (the reference implementation keeps the
    /// shared RTP alive in multi-device sessions).
    event_addr: Option<SocketAddr>,
    event_key: Vec<u8>,
    event_rebuilds: u8,
    next_event_rebuild: Instant,
    /// Control-plane evidence plus the quiet re-probe queue. The receiver's live
    /// `statusFlags` probe — not a signal and not a media symptom — decides.
    preemption: preemption::Tracker,
    /// The receiver took this session over. Never send a TEARDOWN back to it.
    preempted: bool,
    /// Loss of the established feedback/clock association is not proof of takeover.
    ownership_uncertain: bool,
    ownership_check_started: Option<Instant>,
    probe_task: Option<ProbeTask>,
    event_task: Option<EventTask>,
}
struct ProbeTask {
    stop: Cancellation,
    worker: Option<JoinHandle<PreemptionRound>>,
}
impl Drop for ProbeTask {
    fn drop(&mut self) {
        self.stop.cancel();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
struct EventTask {
    stop: Cancellation,
    worker: Option<JoinHandle<Result<EventWorker>>>,
}
impl Drop for EventTask {
    fn drop(&mut self) {
        if let Some(worker) = self.worker.take() {
            self.stop.cancel();
            let _ = worker.join();
        }
    }
}
/// Outcome of one preemption poll. Only `Yield` is a terminal fault; a
/// confirmed takeover returns a reclaim signal so the live loop can open a
/// fresh connection instead of reporting a generic transport failure.
#[derive(Debug)]
enum PreemptionPoll {
    /// No evidence, or the next probe is not due yet.
    None,
    /// A probe (or its confirmation) is in flight, or the receiver is playing
    /// another sender's session. This is a queue, not a failure: the next probe
    /// is scheduled and the session keeps its media loop.
    Waiting,
    /// A takeover was confirmed (or the probe failed open, or the busy queue
    /// was exhausted): end this attempt and reclaim the receiver on a fresh
    /// connection.
    /// The consecutive limit was hit: stop reclaiming, non-retryable.
    Yield(Fault),
}

/// One member's preemption evidence for a loop round, gathered **before** any
/// decision so Q42's group-wide rule can see every member at once: deciding
/// member by member would reclaim the first failing receiver before the second
/// one's identical failure was even probed.
struct PreemptionRound {
    media_healthy: bool,
    /// Signals drained from the feedback workers this round (evidence line).
    signals: Vec<preemption::Signal>,
    /// The control-port probe taken for the evidence line, when a signal
    /// arrived but no verdict was due.
    evidence: Option<preemption::ReceiverLiveness>,
    /// The verdict half: present only when a probe was due this round.
    probe: Option<RoundProbe>,
}

/// The status-probe half of a due round, already combined with the raw
/// liveness. It is re-derived from the same `statusFlags` when the escalation
/// or the group-wide rule settles on a different verdict.
struct RoundProbe {
    /// The raw control-port probe (the log keeps `refused` distinct from
    /// `dead`, Q42).
    liveness: preemption::ReceiverLiveness,
    result: Result<preemption::ProbeReport>,
    we_hold_session: bool,
}

/// Q46's all-failed episode: the gateway reference discovered for the current
/// run of all-failed full rounds, plus the suppression state.
///
/// Keeping the bookkeeping here (instead of four locals in the audio loop) lets
/// the rule be exercised with fake time, a fake gateway and no stream: what the
/// loop does is hand in each round's probed liveness and emit the one warning
/// the episode returns. Discovery runs once per episode; `probe` runs on every
/// full all-failed round, because a gateway that comes back has to end the
/// suppression and, with no gateway at all, the fallback clock only advances
/// here.
#[cfg(test)]
struct AllFailedEpisode {
    /// Start of the current all-failed episode; `None` between episodes.
    since: Option<Instant>,
    /// The default gateway discovered for this episode. `None` **inside** a
    /// started episode means "no default route", which is what triggers
    /// [`preemption::GATEWAY_FALLBACK_WINDOW`].
    gateway: Option<IpAddr>,
    /// Whether `gateway` was discovered for this episode (it may legitimately be
    /// `None`).
    discovered: bool,
    /// Whether the previous full round was suppressed, so the warning is a state
    /// change and not a line per outage round.
    suppressing: bool,
}

#[cfg(test)]
impl AllFailedEpisode {
    fn new() -> Self {
        Self {
            since: None,
            gateway: None,
            discovered: false,
            suppressing: false,
        }
    }

    /// Feed one polling round. `discover` is called at most once per episode,
    /// `probe` once per full all-failed round. Returns whether the group-wide
    /// rule suppresses this round, and the one warning to log when the
    /// suppression state changed.
    fn observe(
        &mut self,
        probes: &[preemption::ReceiverLiveness],
        receivers: usize,
        now: Instant,
        discover: &dyn Fn() -> Option<IpAddr>,
        probe: &dyn Fn(IpAddr) -> preemption::GatewayReachability,
    ) -> (bool, Option<Json>) {
        // A round that did not probe every member proves nothing about the
        // group, and it must not reset the episode clock either.
        if probes.len() != receivers {
            return (false, None);
        }
        if !preemption::all_receivers_failed(probes, receivers) {
            *self = Self::new();
            return (false, None);
        }
        let since = *self.since.get_or_insert(now);
        if !self.discovered {
            self.gateway = discover();
            self.discovered = true;
        }
        let reference = self.gateway.map(probe);
        let held = now.saturating_duration_since(since);
        let judgement = preemption::judge_all_failed(probes, receivers, reference, Some(held));
        let suppress = judgement.suppress();
        let warning = if suppress && !self.suppressing {
            Some(preemption_local_network(
                receivers, probes, judgement, reference, held,
            ))
        } else {
            None
        };
        // A round that stops suppressing (the fallback expired, or a receiver
        // answered again) leaves the episode armed: the loop reclaims right
        // away on the former, and the latter already reset `self` above.
        self.suppressing = suppress;
        (suppress, warning)
    }
}

/// One receiver's `/info` evidence, fetched before the group timing decision.
///
/// The RTSP connection stays open between probing and SETUP; `fingerprint` is
/// what the group plan reads, so every member is characterized before any member
/// is configured.
struct MemberProbe {
    peer: Peer,
    conn: Connection,
    info: Value,
    fingerprint: crate::profile::CompatFingerprint,
}

impl MemberProbe {
    fn connect(peer: &Peer, cancel: Cancellation, emit: &dyn Fn(Json)) -> Result<Self> {
        let mut conn = Connection::connect(SocketAddr::new(peer.host, peer.port), cancel)?;
        emit(json!({"event":"phase","host":peer.host,"phase":"info"}));
        let info = conn.request("GET", "/info", &[], &[])?.plist()?;
        let fingerprint =
            crate::profile::CompatFingerprint::from_info(&info, &peer.profile, &peer.codecs);
        Ok(Self {
            peer: peer.clone(),
            conn,
            info,
            fingerprint,
        })
    }
}

impl Member {
    #[allow(clippy::too_many_arguments)]
    fn connect(
        probe: MemberProbe,
        plan: crate::profile::TimingPlan,
        options: &ProbeOptions,
        ptp: &mut Option<PtpMaster>,
        clock: &Clock,
        ntp_port: u16,
        initial_rtp: u32,
        emit: &dyn Fn(Json),
    ) -> Result<Self> {
        let MemberProbe {
            peer,
            mut conn,
            info,
            fingerprint,
        } = probe;
        let local = conn.local_addr()?.ip();
        let ssrc = rand::random::<u32>();
        let uri = format!("rtsp://{local}/{ssrc}");
        let capabilities = info.as_dictionary().context("info is not a dictionary")?;
        let compat = fingerprint.negotiate_planned(
            plan,
            &peer.profile,
            options.sample_rate,
            options.timing == "ptp",
        )?;
        if compat.uses_ptp() && ptp.is_none() {
            let peers = options.peers.iter().map(|p| p.host).collect();
            let followers = options
                .peers
                .iter()
                .filter(|p| p.profile.follow_receiver_clock)
                .map(|p| p.host)
                .collect();
            let timer = PtpMaster::start_with_followers(peers, followers, clock.clone())?;
            // Timing is activated after SETPEERS below. Automatic role negotiation
            // before activation cannot send probes; keep the configured clock role.
            *ptp = Some(timer);
        }
        let clock_id = compat.uses_ptp().then(|| ptp.as_ref().unwrap().clock_id);
        emit(
            json!({"event":"peer_info","host":peer.host,"model":capabilities.get("model").and_then(Value::as_string),"source_version":capabilities.get("sourceVersion").and_then(Value::as_string)}),
        );
        emit(json!({"event":"phase","host":peer.host,"phase":"authenticate"}));
        let device_id = capabilities
            .get("deviceID")
            .and_then(Value::as_string)
            .unwrap_or("");
        let saved = if device_id.is_empty() {
            Ok(None)
        } else {
            crate::credentials::load(&crate::credentials::directory(), device_id)
        };
        // Credential lookup errors must be returned before selecting any authentication method.
        let password = if !device_id.is_empty() && matches!(&saved, Ok(None)) {
            crate::credentials::load_password(&crate::credentials::directory(), device_id)?
        } else {
            None
        };
        let secret = authenticate_with(peer.host, &mut conn, saved, auth::verify, |connection| {
            if let Some(password) = password {
                auth::transient_password(connection, &password)
            } else {
                auth::transient(connection)
            }
        })?;
        emit(json!({"event":"phase","host":peer.host,"phase":"authenticated"}));
        let control = UdpSocket::bind(SocketAddr::new(local, 0))?;
        control.set_nonblocking(true)?;
        let audio = UdpSocket::bind(SocketAddr::new(local, 0))?;
        let audio_key = derive(&secret, "Events-Salt", "Events-Write-Encryption-Key");
        let rate = options.sample_rate;
        let mut packetizer =
            Packetizer::with_rate(audio_key, rand::random(), initial_rtp, ssrc, rate);
        let use_alac = compat.prefers_alac();
        if use_alac {
            packetizer.enable_alac();
        }
        let mut member = Self {
            compat,
            peer: peer.clone(),
            conn: std::sync::Arc::new(std::sync::Mutex::new(conn)),
            events: None,
            feedback: None,
            health: Health::default(),
            media: MediaTransport::new(packetizer, audio, control, SocketAddr::new(peer.host, 0))?,
            uri,
            remote_control: 0,
            ready: false,
            closed: false,
            session_started: false,
            clock_frozen: false,
            event_addr: None,
            event_key: secret.to_vec(),
            event_rebuilds: 0,
            next_event_rebuild: Instant::now(),
            preemption: preemption::Tracker::default(),
            preempted: false,
            ownership_uncertain: false,
            ownership_check_started: None,
            probe_task: None,
            event_task: None,
        };
        // RAII owns the connection before SETUP so partial handshakes are torn down too.
        let sender_id = "02:57:32:41:50:01";
        let mut setup = vec![
            ("deviceID", string(sender_id)),
            ("macAddress", string(sender_id)),
            ("name", string("AirFlash")),
            ("sessionUUID", string(Uuid::new_v4().to_string())),
            ("timingProtocol", string(member.compat.timing_protocol())),
            ("isMultiSelectAirPlay", Value::Boolean(true)),
            ("groupContainsGroupLeader", Value::Boolean(false)),
            ("senderSupportsRelay", Value::Boolean(false)),
        ];
        if let Some(group) = &options.group_id {
            setup.push(("groupUUID", string(group)));
        }
        if let Some(id) = clock_id {
            let peer_info = dictionary(vec![
                ("ID", string(Uuid::new_v4().to_string())),
                ("DeviceType", number(0)),
                ("ClockID", number(id)),
                ("SupportsClockPortMatchingOverride", Value::Boolean(false)),
                ("Addresses", Value::Array(vec![string(local.to_string())])),
            ]);
            setup.push(("timingPeerInfo", peer_info.clone()));
            setup.push(("timingPeerList", Value::Array(vec![peer_info])));
        } else {
            setup.push(("timingPort", number(ntp_port as u64)));
        }
        emit(
            json!({"event":"phase","host":peer.host,"phase":"setup_session","timing":options.timing}),
        );
        let response = member
            .conn
            .lock()
            .unwrap()
            .plist_request("SETUP", &member.uri, &dictionary(setup))?
            .plist()?;
        member.session_started = true;
        let event_port = get_port(&response, "eventPort")?;
        member.event_addr = Some(SocketAddr::new(peer.host, event_port));
        member.events = Some(EventWorker::connect(
            SocketAddr::new(peer.host, event_port),
            &secret,
            member.health.clone(),
        )?);
        member.record()?;
        if clock_id.is_some() {
            let mut peers: Vec<Value> = options
                .peers
                .iter()
                .map(|p| string(p.host.to_string()))
                .collect();
            peers.push(string(local.to_string()));
            member.conn.lock().unwrap().plist_request(
                "SETPEERS",
                &member.uri,
                &Value::Array(peers),
            )?;
            ptp.as_ref().unwrap().activate(peer.host, local);
        }
        let requested_latency = peer.profile.latency_ms.unwrap_or(options.latency_ms);
        let latency = (requested_latency as u64 * rate as u64) / 1000;
        let mut stream_fields = vec![
            (
                "audioFormat",
                number(crate::rtp::audio_format(use_alac, rate)),
            ),
            ("audioMode", string("default")),
            ("controlPort", number(member.media.control_port()? as u64)),
            ("ct", number(if use_alac { 2 } else { 1 })),
            ("isMedia", Value::Boolean(true)),
            ("shk", Value::Data(audio_key.to_vec())),
            ("spf", number(FRAMES as u64)),
            ("sr", number(rate as u64)),
            (
                "type",
                number(if member.compat.uses_buffered_stream() {
                    103
                } else {
                    96
                }),
            ),
            ("supportsDynamicStreamID", Value::Boolean(false)),
            ("streamConnectionID", number(ssrc as u64)),
        ];
        if !member.compat.uses_buffered_stream() {
            stream_fields.push(("latencyMin", number(latency)));
            stream_fields.push(("latencyMax", number(latency)));
        }
        let stream = dictionary(stream_fields);
        emit(
            json!({"event":"phase","host":peer.host,"phase":"setup_audio","requested_latency_ms":options.latency_ms}),
        );
        let response = member
            .conn
            .lock()
            .unwrap()
            .plist_request(
                "SETUP",
                &member.uri,
                &dictionary(vec![("streams", Value::Array(vec![stream]))]),
            )?
            .plist()?;
        let stream = response
            .as_dictionary()
            .and_then(|d| d.get("streams"))
            .and_then(Value::as_array)
            .and_then(|a| a.first())
            .context("missing stream response")?;
        member.remote_control = if member.compat.uses_buffered_stream() {
            0
        } else {
            get_port(stream, "controlPort")?
        };
        let data_port = get_port(stream, "dataPort")?;
        member.media.set_control_port(member.remote_control);
        if member.compat.uses_buffered_stream() {
            member
                .media
                .connect_buffered(SocketAddr::new(peer.host, data_port))?;
        } else {
            member
                .media
                .connect_audio(SocketAddr::new(peer.host, data_port))?;
        }
        let receiver_latency = stream
            .as_dictionary()
            .and_then(|d| d.get("latencyMin"))
            .and_then(Value::as_unsigned_integer);
        let receiver_min = receiver_latency
            .filter(|v| *v > 0 && *v <= rate as u64 * 10)
            .map(|samples| Duration::from_nanos(samples * 1_000_000_000 / rate as u64));
        let effective_latency =
            selected_latency(requested_latency, receiver_min, options.force_latency);
        // A recovery rebuild must actually retain its raised target even when
        // the receiver reports a lower minimum. Fixed modes keep negotiation.
        let effective_latency = if options.adaptive_latency {
            effective_latency.max(Duration::from_millis(u64::from(requested_latency)))
        } else {
            effective_latency
        };
        member.media.configure_latency(
            effective_latency,
            receiver_min.is_none() || options.force_latency,
        );
        emit(
            json!({"event":"negotiated","receiver_id":peer.receiver_id,"host":peer.host,"port":peer.port,"clock_role":member.compat.clock_role(),"data_port":data_port,"codec":member.compat.codec_name(),"transport":member.compat.transport(),"timing":member.compat.timing_name(),"channel":peer.profile.channel,"sync_offset_ms":peer.profile.sync_offset_ms,"qualification":"experimental","sample_rate":rate,"control_port":member.remote_control,"requested_latency_ms":requested_latency,"effective_latency_ms":effective_latency.as_secs_f64()*1000.0,"force_latency":options.force_latency,"receiver_latency_min_ms":receiver_min.map(|v|v.as_secs_f64()*1000.0),"receiver_latency_min_samples":receiver_latency,"measured_latency_ms":null}),
        );
        if options.force_latency && receiver_min.is_some_and(|min| effective_latency < min) {
            emit(
                json!({"event":"warning","channel":"latency","host":peer.host,
                "code":"forced_latency_below_minimum","recovered":false,
                "requested_latency_ms":requested_latency,"receiver_latency_min_ms":receiver_min.map(|v|v.as_secs_f64()*1000.0),
                "message":"Forced target is below the receiver's reported minimum; continuing with the requested value. Playback may be rejected or silent."}),
            );
        }
        // A receiver that already ended the session during setup is a plain setup
        // failure, never preemption: there is no media or PTP history to pair the
        // request with yet.
        ensure!(
            member.conn.lock().unwrap().take_unsolicited().is_empty(),
            "receiver ended the session during setup"
        );
        Ok(member)
    }
    fn record(&mut self) -> Result<()> {
        let headers = [
            ("Range", "npt=0-".into()),
            (
                "RTP-Info",
                format!(
                    "seq={};rtptime={}",
                    self.media.packetizer.seq, self.media.packetizer.timestamp
                ),
            ),
        ];
        self.conn
            .lock()
            .unwrap()
            .request("RECORD", &self.uri, &headers, &[])?;
        self.ready = true;
        Ok(())
    }
    fn start_feedback(&mut self, now: Instant) -> Result<()> {
        self.media.reset_watchdog(now);
        self.feedback = Some(FeedbackWorker::start(
            self.conn.clone(),
            self.peer.host.to_string(),
            self.health.clone(),
            FeedbackTiming::default(),
        )?);
        Ok(())
    }
    /// Synchronous fixture wrapper around the asynchronous production probe.
    /// Confirmed busy ownership yields; free or unreachable peers are ordinary
    /// retryable transport faults, and healthy media survives auxiliary failures.
    #[cfg(test)]
    fn poll_preemption(
        &mut self,
        cancel: &Cancellation,
        takeovers: &mut preemption::TakeoverCounters,
        now: Instant,
        emit: &impl Fn(Json),
    ) -> (PreemptionPoll, bool) {
        let mut round = self.prepare_preemption(cancel, now);
        while round.probe.is_none() && self.probe_task.is_some() {
            thread::sleep(Duration::from_millis(1));
            round = self.prepare_preemption(cancel, now);
        }
        self.apply_preemption(round, takeovers, now, emit, false)
    }

    /// Drain completed probe results or start one bounded background round.
    /// The sender never waits for a TCP connect or a receiver response here.
    fn prepare_preemption(&mut self, cancel: &Cancellation, now: Instant) -> PreemptionRound {
        let healthy = self.media.media_healthy(now, MEDIA_HEALTH_WINDOW);
        let empty = || PreemptionRound {
            media_healthy: healthy,
            signals: Vec::new(),
            evidence: None,
            probe: None,
        };
        if cancel.is_cancelled() {
            return empty();
        }
        if let Some(task) = &self.probe_task {
            if !task
                .worker
                .as_ref()
                .is_some_and(|worker| worker.is_finished())
            {
                return empty();
            }
            let mut task = self.probe_task.take().unwrap();
            return task
                .worker
                .take()
                .unwrap()
                .join()
                .unwrap_or_else(|_| empty());
        }
        let signals = self.health.take_preemption_signals();
        for signal in &signals {
            if signal.ends_session()
                || matches!(
                    signal,
                    preemption::Signal::FeedbackLost | preemption::Signal::ClockLost
                )
            {
                self.ownership_check_started.get_or_insert(now);
            }
            if matches!(
                signal,
                preemption::Signal::FeedbackLost | preemption::Signal::ClockLost
            ) {
                self.ownership_uncertain = true;
            }
            self.preemption.observe(*signal, now);
        }
        if !self.preemption.due(now) {
            return empty();
        }
        // A successful UDP send is local socket acceptance, not receiver playback.
        // In particular it cannot contradict a receiver-initiated TEARDOWN.
        let media_healthy = self.media.media_healthy(now, MEDIA_HEALTH_WINDOW)
            && !self.preemption.session_ended()
            && !self.ownership_uncertain;
        let we_hold_session = !self.preemption.session_ended() && !self.ownership_uncertain;
        let host = self.peer.host;
        let port = self.peer.port;
        let stop = Cancellation::default();
        let done = stop.clone();
        let worker = thread::Builder::new()
            .name("airplay-status-probe".into())
            .spawn(move || {
                let liveness =
                    preemption::receiver_liveness(host, port, preemption::LIVENESS_TIMEOUT);
                let result = if done.is_cancelled() {
                    Err(crate::rtsp::WireError::Cancelled.into())
                } else if matches!(
                    liveness,
                    preemption::ReceiverLiveness::Dead | preemption::ReceiverLiveness::Refused
                ) {
                    Ok(preemption::ProbeReport {
                        outcome: preemption::ProbeOutcome::Gone,
                        status_flags: None,
                    })
                } else {
                    match preemption::probe_report(
                        SocketAddr::new(host, port),
                        &done,
                        preemption::PROBE_TIMEOUT,
                        we_hold_session,
                    ) {
                        Ok(report) => Ok(preemption::ProbeReport {
                            outcome: preemption::combine(
                                media_healthy,
                                liveness,
                                report.status_flags,
                                we_hold_session,
                            ),
                            status_flags: report.status_flags,
                        }),
                        Err(_) if media_healthy => Ok(preemption::ProbeReport {
                            outcome: preemption::ProbeOutcome::OursActive,
                            status_flags: None,
                        }),
                        Err(error) => Err(error),
                    }
                };
                PreemptionRound {
                    media_healthy,
                    signals,
                    evidence: None,
                    probe: Some(RoundProbe {
                        liveness,
                        result,
                        we_hold_session,
                    }),
                }
            });
        match worker {
            Ok(worker) => {
                self.probe_task = Some(ProbeTask {
                    stop,
                    worker: Some(worker),
                })
            }
            Err(_) => self.preemption.await_liveness(now),
        }
        empty()
    }

    /// Phase 2: feed the round through Q42's escalation rule and act on the
    /// verdict. `local_suspected` is the group-wide result: every receiver
    /// failed at once, so the failure belongs to our own side, not to this
    /// device.
    fn apply_preemption(
        &mut self,
        round: PreemptionRound,
        takeovers: &mut preemption::TakeoverCounters,
        now: Instant,
        emit: &impl Fn(Json),
        local_suspected: bool,
    ) -> (PreemptionPoll, bool) {
        let media_healthy = round.media_healthy;
        if self
            .ownership_check_started
            .is_some_and(|at| now.saturating_duration_since(at) >= Duration::from_secs(15))
        {
            self.preempted = true;
            self.preemption.suspend();
            return (
                PreemptionPoll::Yield(ownership_unconfirmed(self.peer.host)),
                media_healthy,
            );
        }
        let Some(probe) = round.probe else {
            // Evidence only: no probe was due this round.
            if let Some(probed) = round.evidence {
                emit(preemption_evidence(
                    self,
                    &round.signals,
                    media_healthy,
                    probed,
                    None,
                    false,
                ));
            }
            let investigating = (self.ownership_uncertain || self.preemption.session_ended())
                && (self.probe_task.is_some() || self.preemption.scheduled().is_some());
            return (
                if investigating {
                    PreemptionPoll::Waiting
                } else {
                    PreemptionPoll::None
                },
                media_healthy,
            );
        };
        let sample = if local_suspected {
            preemption::LivenessSample::LocalSuspect
        } else {
            preemption::LivenessSample::Probe(probe.liveness)
        };
        let liveness = self.preemption.observe_liveness(sample, now);
        let result = settle_liveness(
            media_healthy,
            probe.liveness,
            liveness.verdict,
            probe.result,
            probe.we_hold_session,
        );
        if !round.signals.is_empty() {
            emit(preemption_evidence(
                self,
                &round.signals,
                media_healthy,
                probe.liveness,
                Some(liveness.verdict),
                local_suspected,
            ));
        }
        let note = self.preemption.note_result(&result, now);
        if note.log_changed || liveness.log_changed {
            emit(preemption_probe(
                self,
                &result,
                probe.we_hold_session,
                media_healthy,
                ProbeLiveness {
                    probed: probe.liveness,
                    verdict: liveness.verdict,
                    failures: self.preemption.liveness_failures(),
                    local_suspected,
                },
            ));
        }
        if let Some((first, second)) = note.disagreement {
            emit(preemption_probe_disagreement(self, first, second));
            self.preempted = true;
            self.preemption.suspend();
            return (
                PreemptionPoll::Yield(ownership_unconfirmed(self.peer.host)),
                media_healthy,
            );
        }
        match note.action {
            preemption::ProbeAction::Confirm => (PreemptionPoll::Waiting, media_healthy),
            preemption::ProbeAction::Requeue => {
                let _ = takeovers.takeover(self.peer.host, now);
                self.preempted = true;
                if let Ok(mut connection) = self.conn.try_lock() {
                    connection.poison();
                }
                self.preemption.suspend();
                (
                    PreemptionPoll::Yield(
                        if self.preemption.session_ended()
                            && result
                                .as_ref()
                                .is_ok_and(|r| r.outcome == preemption::ProbeOutcome::BusyElsewhere)
                        {
                            preemption_fault(self, preemption::ReclaimReason::Takeover, 1)
                        } else {
                            ownership_unconfirmed(self.peer.host)
                        },
                    ),
                    media_healthy,
                )
            }
            preemption::ProbeAction::FallThrough => {
                if liveness.verdict == preemption::Verdict::Unknown {
                    self.preemption.await_liveness(now);
                }
                (PreemptionPoll::None, media_healthy)
            }
            preemption::ProbeAction::Reclaim => {
                self.preempted = true;
                if let Ok(mut connection) = self.conn.try_lock() {
                    connection.poison();
                }
                self.preemption.suspend();
                // Only confirmed Free permits a new association. A timeout, an
                // unreachable peer or missing flags cannot authorize a takeover.
                (
                    PreemptionPoll::Yield(
                        if result
                            .as_ref()
                            .is_ok_and(|r| r.outcome == preemption::ProbeOutcome::Free)
                        {
                            Fault::new(
                                self.peer.host,
                                "feedback",
                                "peer_closed",
                                true,
                                "Receiver ended the current session; reconnect explicitly or use the configured transport retry",
                            )
                        } else {
                            ownership_unconfirmed(self.peer.host)
                        },
                    ),
                    media_healthy,
                )
            }
        }
    }
    fn poll_event_rebuild(&mut self, emit: &impl Fn(Json)) {
        if let Some(task) = &self.event_task {
            if !task
                .worker
                .as_ref()
                .is_some_and(|worker| worker.is_finished())
            {
                return;
            }
            let mut task = self.event_task.take().unwrap();
            match task.worker.take().unwrap().join() {
                Ok(Ok(worker)) => {
                    self.events = Some(worker);
                    emit(
                        json!({"event":"warning","host":self.peer.host,"channel":"events","code":"event_channel_rebuilt","recovered":true,"message":"Event channel reopened"}),
                    );
                }
                _ => emit(
                    json!({"event":"warning","host":self.peer.host,"channel":"events","code":"event_channel_rebuild_failed","recovered":false,"message":"Event channel could not be reopened"}),
                ),
            }
        }
        if self.health.degraded_channel("events").is_none()
            || self.event_rebuilds >= MAX_EVENT_REBUILDS
            || Instant::now() < self.next_event_rebuild
        {
            return;
        }
        let Some(addr) = self.event_addr else {
            return;
        };
        self.event_rebuilds += 1;
        self.next_event_rebuild = Instant::now() + EVENT_REBUILD_INTERVAL;
        let key = zeroize::Zeroizing::new(self.event_key.clone());
        let health = self.health.clone();
        let old = self.events.take();
        let stop = Cancellation::default();
        let done = stop.clone();
        if let Ok(worker) = thread::Builder::new()
            .name("airplay-event-rebuild".into())
            .spawn(move || {
                drop(old);
                done.check()?;
                EventWorker::connect_with_stop(addr, &key, health, done)
            })
        {
            self.event_task = Some(EventTask {
                stop,
                worker: Some(worker),
            });
        }
    }

    fn metrics(&self, ptp: Option<&PtpMaster>) -> Json {
        let clock = ptp.and_then(|timer| {
            timer
                .snapshots()
                .into_iter()
                .find(|snapshot| snapshot.host == self.peer.host)
        });
        json!({"id":if self.peer.receiver_id.is_empty() {self.peer.host.to_string()} else {self.peer.receiver_id.clone()}, "host":self.peer.host, "ip":self.peer.host, "active":!self.closed, "codec":self.compat.codec_name(),"transport":self.compat.transport(),"timing":self.compat.timing_name(),"clock_role":self.compat.clock_role(),"channel":self.peer.profile.channel,"sync_offset_ms":self.peer.profile.sync_offset_ms,"qualification":"experimental","media":self.media.metrics(), "health":self.health.snapshot(), "ptp_offset_ns":clock.as_ref().and_then(|s| s.offset_ns), "ptp_offset_count":clock.as_ref().map(|s| s.exchanges), "ptp_required":self.compat.uses_ptp(), "ptp_ready":clock.as_ref().is_some_and(|s| s.ready), "ptp_phase":clock.as_ref().map(|s| s.phase)})
    }
    fn close(&mut self) {
        if self.closed {
            return;
        }
        self.closed = true;
        self.probe_task.take();
        self.event_task.take();
        self.feedback.take();
        // A receiver that took the session over must not receive a TEARDOWN from
        // the session it just displaced; the takeover already ended it.
        if self.session_started && !self.preempted && !self.health.ownership_uncertain() {
            let _ = self.conn.lock().unwrap().finish_session(&self.uri);
        }
        self.events.take();
        self.ready = false;
    }
}
impl Drop for Member {
    fn drop(&mut self) {
        self.close();
    }
}

/// Probe one member's `/info`, retrying a transient connection failure exactly
/// like the old single-phase connect did.
fn probe_member(peer: &Peer, cancel: &Cancellation, emit: &dyn Fn(Json)) -> Result<MemberProbe> {
    let mut attempt = 0;
    loop {
        match MemberProbe::connect(peer, cancel.clone(), emit) {
            Ok(probe) => return Ok(probe),
            Err(error) if attempt < MEMBER_SETUP_RETRIES => {
                attempt += 1;
                emit(json!({
                    "event": "warning",
                    "host": peer.host.to_string(),
                    "channel": "setup",
                    "code": "member_setup_retry",
                    "recovered": false,
                    "message": error.to_string(),
                }));
                std::thread::sleep(MEMBER_SETUP_RETRY_DELAY);
            }
            Err(error) => return Err(Fault::from_setup_error(peer.host, &error).into()),
        }
    }
}

/// The member a group-level fault should name.
///
/// A refused group has no single failing receiver. Pointing at the first member
/// whose AirPlay 2 evidence differs from the group's first member gives
/// diagnostics the concrete device that disagrees, deterministically.
fn mixed_group_host(peers: &[Peer], fingerprints: &[crate::profile::CompatFingerprint]) -> String {
    let Some(reference) = fingerprints
        .first()
        .map(crate::profile::CompatFingerprint::airplay2)
    else {
        return String::new();
    };
    let index = fingerprints
        .iter()
        .position(|fingerprint| fingerprint.airplay2() != reference)
        .unwrap_or(0);
    peers
        .get(index)
        .map(|peer| peer.host.to_string())
        .unwrap_or_default()
}

/// The internal diagnostic for one group setup, never a user-facing warning.
///
/// Q33: the reference implementation has one timing plan per group and asserts
/// `snapshot.Timing == plan.Timing` at runtime, so a group that runs two clock
/// domains is a planning bug, not a runtime risk the user can act on. The old
/// `group_timing_divergence` warning therefore becomes a stderr log line: the
/// desktop's `warning` channel never sees it, but the evidence summary (every
/// member, its AirPlay 2 evidence, its solo answer and its explicit request)
/// survives in the engine log and in diagnostic exports.
///
/// It reports when a member's solo answer differs from the group plan or when
/// an explicit per-member request is overridden. Both are expected inputs to
/// the one-plan design. AirPlay 2 evidence is homogeneous by construction --
/// `TimingPlan::select` refuses a mixed group before this point (Q39) -- so the
/// line never explains a whole-group downgrade.
fn group_timing_diagnostic(
    peers: &[Peer],
    fingerprints: &[crate::profile::CompatFingerprint],
    plan: crate::profile::TimingPlan,
) -> Option<String> {
    use crate::profile::Timing;
    if !plan.is_group() || peers.len() != fingerprints.len() {
        return None;
    }
    let evidence: Vec<_> = fingerprints
        .iter()
        .map(crate::profile::CompatFingerprint::timing_evidence)
        .collect();
    debug_assert!(
        evidence
            .windows(2)
            .all(|pair| pair[0].airplay2 == pair[1].airplay2),
        "mixed groups are refused before the plan exists"
    );
    let solos: Vec<_> = evidence
        .iter()
        .map(|evidence| evidence.solo_timing())
        .collect();
    let solo_answers_disagree = solos.windows(2).any(|pair| pair[0] != pair[1]);
    let settings_forced = peers.iter().any(|peer| match peer.profile.timing {
        Timing::Ntp => plan.timing() != Timing::Ntp,
        Timing::Ptp => plan.timing() != Timing::Ptp,
        _ => false,
    });
    if !solo_answers_disagree && !settings_forced {
        return None;
    }
    let label = |peer: &Peer| {
        if peer.receiver_id.is_empty() {
            peer.host.to_string()
        } else {
            peer.receiver_id.clone()
        }
    };
    let described: Vec<_> = peers
        .iter()
        .zip(&evidence)
        .zip(&solos)
        .map(|((peer, evidence), solo)| {
            let requested = match peer.profile.timing {
                Timing::Ntp => ", requested ntp",
                Timing::Ptp => ", requested ptp",
                _ => "",
            };
            format!(
                "{}{} ({}, {}{})",
                label(peer),
                if peer.profile.follow_receiver_clock {
                    " [follow]"
                } else {
                    ""
                },
                if evidence.airplay2 == Some(true) {
                    "airplay2"
                } else {
                    "no airplay2"
                },
                if *solo == Timing::Ptp { "ptp" } else { "ntp" },
                requested,
            )
        })
        .collect();
    Some(format!(
        "group_timing_plan: {}; one {} clock domain for the group",
        described.join(", "),
        if plan.timing() == Timing::Ptp {
            "PTP"
        } else {
            "NTP"
        },
    ))
}

fn load_quiet_wav(options: &ProbeOptions) -> Result<Vec<i16>> {
    let rate = options.sample_rate;
    let mut reader = hound::WavReader::open(&options.wav_path).context("open probe WAV")?;
    let spec = reader.spec();
    ensure!(
        spec.channels == 2
            && spec.sample_rate == rate
            && spec.bits_per_sample == 16
            && spec.sample_format == hound::SampleFormat::Int,
        "probe WAV must be S16 stereo {rate} Hz"
    );
    ensure!(
        u64::from(reader.len()) <= rate as u64 * 2 * 5,
        "probe WAV exceeds five seconds"
    );
    let samples = reader
        .samples::<i16>()
        .collect::<std::result::Result<Vec<_>, _>>()?;
    ensure!(
        !samples.is_empty() && samples.iter().all(|s| (*s as i32).abs() <= 1639),
        "probe WAV must have peak amplitude <= 0.05"
    );
    Ok(samples
        .into_iter()
        .map(|s| (s as f32 * options.gain).round() as i16)
        .collect())
}

pub fn probe(
    options: ProbeOptions,
    cancel: Cancellation,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    emit: impl Fn(Json),
) -> Result<()> {
    probe_with_volume(
        options,
        cancel,
        gain,
        crate::volume::Control::default(),
        emit,
    )
}

pub fn probe_with_volume(
    options: ProbeOptions,
    cancel: Cancellation,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    volume: crate::volume::Control,
    emit: impl Fn(Json),
) -> Result<()> {
    let equalizer = crate::equalizer::Control::new(options.equalizer, options.sample_rate)?;
    probe_with_controls(options, cancel, gain, volume, equalizer, emit)
}

pub fn probe_with_controls(
    options: ProbeOptions,
    cancel: Cancellation,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    volume: crate::volume::Control,
    equalizer: crate::equalizer::Control,
    emit: impl Fn(Json),
) -> Result<()> {
    let standby = crate::standby::Control::new(options.standby);
    probe_with_all_controls(options, cancel, gain, volume, equalizer, standby, emit)
}

pub fn probe_with_all_controls(
    options: ProbeOptions,
    cancel: Cancellation,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    volume: crate::volume::Control,
    equalizer: crate::equalizer::Control,
    standby: crate::standby::Control,
    emit: impl Fn(Json),
) -> Result<()> {
    if options.duration_ms == 0 {
        options.validate_start()?;
        run_live(options, cancel, gain, volume, equalizer, standby, emit)
    } else {
        run_session(
            options,
            cancel,
            gain,
            volume,
            equalizer,
            emit,
            (Clock::new(), None),
        )
    }
}

// Finite qualification can inject a local clock in the transport simulation.
#[allow(clippy::too_many_arguments)]
fn run_session(
    options: ProbeOptions,
    cancel: Cancellation,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    volume: crate::volume::Control,
    equalizer: crate::equalizer::Control,
    emit: impl Fn(Json),
    clocks: (Clock, Option<PtpMaster>),
) -> Result<()> {
    options.validate()?;
    let standby = crate::standby::Control::new(options.standby);
    let mut takeovers = preemption::TakeoverCounters::default();
    if let Err(error) = run_stream(
        &options,
        &cancel,
        &gain,
        volume,
        equalizer,
        None,
        &standby,
        &mut takeovers,
        // A finite qualification is a single fresh timeline: the baseline of its
        // own row, not a reset.
        None,
        None,
        &emit,
        clocks,
    ) {
        // A finite qualification cannot reopen a session; report the confirmed
        // takeover as the preempted fault instead of the internal reclaim signal.
        if let Some(reclaim) = error.downcast_ref::<preemption::Reclaim>() {
            return Err(preemption_takeover_fault(reclaim.host, reclaim.consecutive).into());
        }
        return Err(error);
    }
    emit(json!({"event":"stopped","cancelled":cancel.is_cancelled()}));
    Ok(())
}

#[derive(Debug)]
enum StreamEnd {
    Finished,
    Silence(u64),
    SourceEnded,
}

/// A capture source that ended is reopened with a brand-new timeline. This is
/// deliberately not `start`: the reference implementation's `start` means
/// "session start", which AirFlash never counts because the first timeline of a
/// row is its baseline (Q43 §四). `source_restart` keeps the two meanings apart.
fn source_restart_reason() -> TimelineResetReason {
    TimelineResetReason::SourceRestart
}

fn run_live(
    mut options: ProbeOptions,
    cancel: Cancellation,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    volume: crate::volume::Control,
    equalizer: crate::equalizer::Control,
    standby: crate::standby::Control,
    emit: impl Fn(Json),
) -> Result<()> {
    if options.source == "application" {
        ensure!(
            crate::applications::supported(),
            "Application capture requires Windows build 20348 or later. Use a virtual audio endpoint on this Windows version."
        );
    }
    let mut source = if options.source == "loopback" {
        Some(crate::live::Loopback::start(
            options.capture_endpoint.clone(),
            options.sample_rate,
            equalizer.clone(),
        )?)
    } else {
        None
    };
    let mut paused = None;
    let mut reason = "";
    let mut hint = options
        .capture_process_id
        .zip(options.capture_process_started_at);
    let mut next_metrics = Instant::now();
    // Persist across reclaims: "taken over N times in a row" is a property of
    // this live playback run, not of one Member/connection.
    let mut takeovers = preemption::TakeoverCounters::default();
    // Q40: why the *next* attempt builds a fresh timeline. `None` for the first
    // attempt — that schedule is the baseline of the row, so a normal stop stays
    // all-zero. Every resumed attempt must name its cause here.
    let mut reset_before: Option<TimelineResetReason> = None;
    // The member the previous iteration reclaimed. Its fresh session is the
    // "successful reclaim" the takeover counter needs: only a takeover that
    // follows one inside `TAKEOVER_WINDOW` continues the same cycle (Q34 §四).
    let mut reclaimed: Option<IpAddr> = None;
    while !cancel.is_cancelled() {
        if let Some(live) = &source {
            emit_capture_warnings(live, &emit);
        }
        if source.as_ref().is_some_and(crate::live::Loopback::ended) {
            source = None;
            paused = None;
            // The capture source ended outside a transport end; the next loop
            // reopens it and starts playback again. That is a source restart, not
            // a session start and not a reconnect after a fault (Q43 §四).
            reset_before = Some(source_restart_reason());
        }
        if source.is_none() && options.source == "loopback" {
            // The capture thread repairs an endpoint in place. Reaching this
            // branch means its rebind budget was exhausted, so reopen the
            // requested (or current default) endpoint and try again.
            source = Some(crate::live::Loopback::start(
                options.capture_endpoint.clone(),
                options.sample_rate,
                equalizer.clone(),
            )?);
            paused = None;
            reason = "";
        }
        if source.is_none() {
            let path = options
                .capture_application_path
                .as_deref()
                .unwrap_or_default();
            let candidates = crate::applications::matching(path)?;
            let plan = crate::applications::bind_plan(
                path,
                &candidates,
                hint,
                crate::applications::MAX_CAPTURE_INSTANCES,
            );
            if let Some(application) = plan.first().cloned() {
                let pinned = hint.is_some_and(|(pid, started_at)| {
                    application.process_id == pid && application.started_at == started_at
                });
                match crate::live::Loopback::start_application(
                    application.clone(),
                    pinned,
                    options.sample_rate,
                    equalizer.clone(),
                ) {
                    Ok(capture) => source = Some(capture),
                    Err(_) if crate::applications::Process::verify(&application).is_err() => {
                        hint = None;
                        continue;
                    }
                    Err(error) => return Err(error),
                }
                hint = None;
                paused = None;
                reason = "";
            } else {
                let current = if candidates.is_empty() {
                    "application_unavailable"
                } else {
                    "application_ambiguous"
                };
                if current != reason {
                    emit(json!({"event":"standby","reason":current}));
                    options.intent = StartIntent::Automatic;
                    reason = current;
                }
                if Instant::now() >= next_metrics {
                    emit(
                        json!({"event":"capture_metrics","metrics":crate::live::Metrics::default()}),
                    );
                    next_metrics = Instant::now() + Duration::from_secs(1);
                }
                for _ in 0..20 {
                    if cancel.is_cancelled() {
                        break;
                    }
                    thread::sleep(Duration::from_millis(50));
                }
                continue;
            }
        }
        let live = source.as_ref().unwrap();
        live.ready()?; // Endpoint failures remain errors even while transport is released.
        if let Some(at) = paused {
            if !standby.should_resume(live.last_audio(), at) {
                live.discard_stale();
                if Instant::now() >= next_metrics {
                    emit(json!({"event":"capture_metrics","metrics":live.metrics()}));
                    next_metrics = Instant::now() + Duration::from_secs(1);
                }
                thread::sleep(Duration::from_millis(20));
                continue;
            }
        }
        emit(json!({"event":"resuming"}));
        live.discard_stale();
        let ended = match run_stream(
            &options,
            &cancel,
            &gain,
            volume.clone(),
            equalizer.clone(),
            Some(live),
            &standby,
            &mut takeovers,
            reset_before,
            reclaimed.take(),
            &emit,
            (Clock::new(), None),
        ) {
            Ok(ended) => ended,
            Err(error) => {
                // The live status probe said free (or the device is gone / the
                // probe failed open). This is a reclaim, not a transport
                // failure: close the poisoned control connection by dropping the
                // Member and open a fresh one. The counter above limits how
                // often a *takeover* can happen in a row; a busy receiver never
                // gets here at all because it only queues.
                let Some(reclaim) = error.downcast_ref::<preemption::Reclaim>() else {
                    return Err(error);
                };
                reclaimed = Some(reclaim.host);
                live.discard_stale();
                // The next attempt is a reconnect after the receiver was taken
                // over: the engine-side timeline restarts with the new session.
                reset_before = Some(TimelineResetReason::SessionRestart);
                options.intent = StartIntent::Automatic;
                continue;
            }
        };
        // Every subsequent attempt is automatic, including a capture-source
        // restart and waking after we released the receiver for silence.
        options.intent = StartIntent::Automatic;
        match ended {
            StreamEnd::Finished => break,
            StreamEnd::SourceEnded => {
                reset_adaptive_target(&mut options);
                source = None;
                paused = None;
                // A new capture source starts a new playback timeline, but the
                // session itself did not start: its own bucket, never `start`.
                reset_before = Some(source_restart_reason());
            }
            StreamEnd::Silence(at) => {
                reset_adaptive_target(&mut options);
                paused = Some(at);
                emit(json!({"event":"standby","reason":"silence"}));
                // Standby released the receiver, so resuming rebuilds the whole
                // timeline: this is exactly the `silence_resume` bucket and must
                // stay distinguishable from a transport failure.
                reset_before = Some(TimelineResetReason::SilenceResume);
            }
        }
    }
    emit(json!({"event":"stopped","cancelled":cancel.is_cancelled()}));
    Ok(())
}

fn reset_adaptive_target(options: &mut ProbeOptions) {
    if options.adaptive_latency
        && let Some(initial) = options.adaptive_initial_latency_ms
    {
        options.latency_ms = initial;
        for peer in &mut options.peers {
            peer.profile.latency_ms = Some(initial);
        }
    }
}

fn ownership_unconfirmed(host: impl ToString) -> Fault {
    Fault::new(
        host,
        "preemption",
        "ownership_unconfirmed",
        false,
        "Receiver session ownership could not be confirmed; automatic recovery paused until manual reconnect",
    )
}

/// Read-only preflight shared by transport retries and native standby resumes.
/// No authentication, SETUP or RECORD may precede both complete free rounds.
fn require_free_receivers(
    peers: &[Peer],
    cancel: &Cancellation,
    emit: &impl Fn(Json),
) -> Result<()> {
    for round in 0..2 {
        cancel.check()?;
        let reports = thread::scope(|scope| {
            let jobs: Vec<_> = peers
                .iter()
                .map(|peer| {
                    scope.spawn(move || {
                        preemption::probe_report(
                            SocketAddr::new(peer.host, peer.port),
                            cancel,
                            preemption::PROBE_TIMEOUT,
                            false,
                        )
                    })
                })
                .collect();
            jobs.into_iter()
                .map(|job| {
                    job.join()
                        .unwrap_or_else(|_| Err(anyhow::anyhow!("ownership probe panicked")))
                })
                .collect::<Vec<_>>()
        });
        cancel.check()?;
        let mut blocked = None;
        for (peer, report) in peers.iter().zip(reports) {
            let free = report
                .as_ref()
                .is_ok_and(|r| r.outcome == preemption::ProbeOutcome::Free);
            emit(
                json!({"event":"warning","channel":"preemption","code":"automatic_start_probe",
                "host":peer.host,"round":round + 1,"status_flags":report.as_ref().ok().and_then(|r| r.status_flags),
                "automatic_recovery_allowed":free && round == 1,"recovered":free,
                "message":match &report { Ok(r) => format!("Automatic start ownership check: {}", r.outcome.name()), Err(e) => format!("Automatic start ownership check failed: {e:#}") }}),
            );
            if !free {
                blocked.get_or_insert(peer.host);
            }
        }
        if let Some(host) = blocked {
            return Err(ownership_unconfirmed(host).into());
        }
        if round == 0 {
            let until = Instant::now() + preemption::CONFIRM_DELAY;
            while Instant::now() < until {
                cancel.check()?;
                thread::sleep(Duration::from_millis(10));
            }
        }
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn run_stream(
    options: &ProbeOptions,
    cancel: &Cancellation,
    gain: &std::sync::Arc<std::sync::atomic::AtomicU32>,
    volume: crate::volume::Control,
    equalizer: crate::equalizer::Control,
    provided: Option<&crate::live::Loopback>,
    standby: &crate::standby::Control,
    takeovers: &mut preemption::TakeoverCounters,
    reset_before: Option<TimelineResetReason>,
    // The receiver the previous attempt reclaimed, if any. Once this fresh
    // session is established it counts as a *successful* reclaim for the
    // takeover cycle window (Q34 §四).
    reclaimed: Option<IpAddr>,
    emit: &impl Fn(Json),
    clocks: (Clock, Option<PtpMaster>),
) -> Result<StreamEnd> {
    if options.duration_ms == 0 {
        options.validate_start()?;
    } else {
        options.validate()?;
    }
    let samples = if options.source == "wav" {
        load_quiet_wav(options)?
    } else {
        Vec::new()
    };
    #[cfg(windows)]
    let microphone = options
        .record_mic_path
        .clone()
        .map(crate::wasapi::Microphone::start)
        .transpose()?;
    #[cfg(windows)]
    let _priority = crate::wasapi::Mmcss::new();
    #[cfg(windows)]
    emit(
        json!({"event":"scheduler_status","registered":_priority.registered(),"priority_set":_priority.priority_set()}),
    );
    let (clock, mut ptp) = clocks;
    let peers: Vec<_> = options.peers.iter().map(|p| p.host).collect();
    let ntp = NtpServer::start(peers.clone(), clock.clone())?;
    let initial_rtp = rand::random::<u32>();
    let mut members = Vec::new();
    // Phase one: characterize every member before any SETUP. The group timing
    // decision needs every fingerprint, and no receiver may be configured before
    // that one decision exists.
    let mut probes = Vec::new();
    if options.intent == StartIntent::Automatic {
        require_free_receivers(&options.peers, cancel, emit)?;
    }
    for peer in &options.peers {
        cancel.check()?;
        probes.push(probe_member(peer, cancel, &emit)?);
    }
    let fingerprints: Vec<_> = probes
        .iter()
        .map(|probe| probe.fingerprint.clone())
        .collect();
    let codec = options.profile_codec();
    let evidence: Vec<_> = fingerprints
        .iter()
        .map(crate::profile::CompatFingerprint::timing_evidence)
        .collect();
    // One topology decision for the whole group. `select_timing` already
    // implements every branch of WAC's `CreateTopologyPlan`; per-member
    // settings are folded in by `negotiate_planned`, never by changing the
    // plan here.
    //
    // Q39: a group whose members disagree on AirPlay 2 evidence is refused
    // here, after the `/info` probes and before any SETUP. WAC never creates
    // such a group (UI selection, member table and backend each refuse it), so
    // silently downgrading the AirPlay 2 members to NTP would ship the exact
    // "half an AirPlay 2 session" failure it refuses to ship.
    let plan = match crate::profile::TimingPlan::select(&evidence, codec) {
        Ok(plan) => plan,
        Err(mixed) => {
            return Err(Fault::new(
                mixed_group_host(&options.peers, &fingerprints),
                "setup",
                crate::profile::MixedTimingGroup::CODE,
                false,
                mixed,
            )
            .into());
        }
    };
    // Q33: a group has exactly one plan, so differences between a member's solo
    // answer and the group plan are expected inputs, not a runtime risk. Keep
    // the evidence summary as an internal debug log; it must never reach the
    // desktop's user-facing `warning` channel.
    if let Some(diagnostic) = group_timing_diagnostic(&options.peers, &fingerprints, plan) {
        eprintln!("{diagnostic}");
    }
    // Phase two: every member negotiates under the same timing decision.
    for (index, probe) in probes.into_iter().enumerate() {
        let peer = &options.peers[index];
        // A group joins as one unit: if a member cannot be brought up after one
        // retry, the members already built are dropped (their Drop sends TEARDOWN),
        // so the receiver never sees a half-configured stereo pair.
        let mut probe = Some(probe);
        let mut attempt = 0;
        let member = loop {
            let current = match probe.take() {
                Some(current) => current,
                None => probe_member(peer, cancel, &emit)?,
            };
            match Member::connect(
                current,
                plan,
                options,
                &mut ptp,
                &clock,
                ntp.port,
                initial_rtp,
                &emit,
            ) {
                Ok(member) => break member,
                Err(error) if attempt < MEMBER_SETUP_RETRIES => {
                    attempt += 1;
                    emit(json!({
                        "event": "warning",
                        "host": peer.host.to_string(),
                        "channel": "setup",
                        "code": "member_setup_retry",
                        "recovered": false,
                        "message": error.to_string(),
                    }));
                    std::thread::sleep(MEMBER_SETUP_RETRY_DELAY);
                }
                Err(error) => return Err(Fault::from_setup_error(peer.host, &error).into()),
            }
        };
        members.push(member);
    }
    if options.handshake_only {
        emit(
            json!({"event":"handshake_complete","members":members.len(),"audio_sent":false,"qualified":false}),
        );
        return Ok(StreamEnd::Finished);
    }
    let timing_peers: Vec<_> = members
        .iter()
        .filter(|member| member.compat.uses_ptp())
        .map(|member| member.peer.host)
        .collect();
    if let Some(timer) = &ptp {
        let started = Instant::now();
        let mut next_status = started;
        while members
            .iter()
            .any(|m| m.compat.uses_ptp() && !timer.ready(m.peer.host))
        {
            cancel.check()?;
            let now = Instant::now();
            let timeout = timing_peers
                .iter()
                .map(|host| timer.startup_timeout(*host))
                .max()
                .unwrap_or(Duration::from_secs(8));
            if now >= next_status || now.duration_since(started) >= timeout {
                emit(clock_status(timer, &timing_peers, "starting"));
                next_status = now + Duration::from_millis(500);
            }
            if now.duration_since(started) >= timeout {
                let failed: Vec<_> = timer
                    .snapshots()
                    .into_iter()
                    .filter(|peer| timing_peers.contains(&peer.host) && !peer.ready)
                    .collect();
                if failed.is_empty() {
                    break; // The worker completed the exchange at the deadline.
                }
                let details = failed
                    .iter()
                    .map(|peer| format!("{} ({})", peer.host, peer.phase))
                    .collect::<Vec<_>>()
                    .join(", ");
                let hint = if failed.iter().any(|peer| peer.phase == "no_rx") {
                    " Check UDP 319/320 access for airflash-engine.exe on this network."
                } else {
                    ""
                };
                return Err(Fault::new(
                    failed
                        .first()
                        .map(|peer| peer.host.to_string())
                        .unwrap_or_default(),
                    "clock",
                    "clock_not_ready",
                    true,
                    format!("PTP exchange did not become ready: {details}.{hint}"),
                )
                .into());
            }
            thread::sleep(Duration::from_millis(10));
        }
        emit(clock_status(timer, &timing_peers, "ready"));
    }
    let clock_id = ptp.as_ref().map(|p| p.clock_id);
    let rate = options.sample_rate;
    let owned_live = if provided.is_none() && options.source == "loopback" {
        Some(crate::live::Loopback::start(
            options.capture_endpoint.clone(),
            rate,
            equalizer,
        )?)
    } else {
        None
    };
    let live = provided.or(owned_live.as_ref());
    if let Some(source) = live {
        let until = Instant::now() + Duration::from_millis(200);
        while !source.ready()? && Instant::now() < until {
            cancel.check()?;
            thread::sleep(Duration::from_millis(2));
        }
    }
    // The group is fully configured and RECORDed: a reclaim that produced this
    // session is "successful" from here on. Only then can a following takeover
    // be another turn of the same reclaim → takeover cycle (Q34 §四). A failed
    // reclaim never reaches this point, so it can never arm the counter.
    if let Some(host) = reclaimed {
        takeovers.reclaimed(host, Instant::now());
    }
    let outcome = send_packets(
        options,
        cancel,
        gain,
        volume,
        live,
        standby,
        &clock,
        clock_id,
        &mut members,
        ptp.as_ref(),
        takeovers,
        &samples,
        reset_before,
        emit,
    );
    #[cfg(windows)]
    if let Some(microphone) = microphone {
        let recording = microphone.finish()?;
        emit(
            json!({"event":"microphone_recorded","path":recording.path,"sample_rate":recording.sample_rate,"frames":recording.frames}),
        );
    }
    outcome
}

/// Cumulative RTP totals across the group: retransmit requests and packets sent.
///
/// The retransmit fraction (Q47 §二) needs both sides of the ratio, and the group
/// is one logical stream, so the members are summed just like the IPC metrics
/// already do.
fn rtp_totals(members: &[Member]) -> (u64, u64) {
    members.iter().fold((0, 0), |(rtx, sent), member| {
        (
            rtx.saturating_add(member.media.metrics.retransmit_requested_packets),
            sent.saturating_add(member.media.metrics.packets_sent),
        )
    })
}

fn rtp_queue_state(members: &[Member]) -> (usize, u64) {
    members
        .iter()
        .fold((0usize, 0u64), |(pending, drops), member| {
            (
                pending.saturating_add(member.media.retransmit_pending()),
                drops.saturating_add(member.media.metrics.retransmit_queue_drops),
            )
        })
}

#[allow(clippy::too_many_arguments)]
fn send_packets(
    options: &ProbeOptions,
    cancel: &Cancellation,
    gain: &std::sync::Arc<std::sync::atomic::AtomicU32>,
    volume: crate::volume::Control,
    live: Option<&crate::live::Loopback>,
    standby: &crate::standby::Control,
    clock: &Clock,
    clock_id: Option<u64>,
    members: &mut [Member],
    ptp: Option<&PtpMaster>,
    takeovers: &mut preemption::TakeoverCounters,
    samples: &[i16],
    reset_before: Option<TimelineResetReason>,
    emit: &impl Fn(Json),
) -> Result<StreamEnd> {
    let rate = options.sample_rate;
    let start = Instant::now();
    let base_ns = clock.now_ns();
    let negotiated_baseline = members.iter().map(|m| m.media.latency()).max().unwrap();
    let common_latency = negotiated_baseline
        + Duration::from_millis(
            members
                .iter()
                .map(|m| (-m.peer.profile.sync_offset_ms).max(0) as u64)
                .max()
                .unwrap_or(0),
        );
    for member in members.iter() {
        if member.compat.uses_buffered_stream() {
            let render = offset_time(
                base_ns,
                common_latency,
                Duration::ZERO,
                member.peer.profile.sync_offset_ms,
            )?;
            let (id, network) = ptp.as_ref().unwrap().timeline(member.peer.host, render)?;
            member.conn.lock().unwrap().plist_request(
                "SETRATEANCHORTIME",
                &member.uri,
                &dictionary(vec![
                    ("rate", number(1)),
                    ("rtpTime", number(member.media.packetizer.timestamp as u64)),
                    ("networkTimeSecs", number(network / 1_000_000_000)),
                    (
                        "networkTimeFrac",
                        number(
                            (((network % 1_000_000_000) as u128) << 64)
                                .checked_div(1_000_000_000)
                                .unwrap() as u64,
                        ),
                    ),
                    ("networkTimeTimelineID", number(id)),
                ]),
            )?;
        }
    }
    let duration = Duration::from_millis(options.duration_ms as u64);
    // Q40: creating the schedule is the row's baseline, not a reset, so a normal
    // stop stays all-zero. A resumed attempt is created *because* something reset
    // the previous timeline; the caller passes that cause explicitly.
    let mut schedule = MediaSchedule::new(start, rate);
    let mut packets = 0u64;
    let mut telemetry = crate::telemetry::SessionTelemetry::new();
    if let Some(reason) = reset_before {
        telemetry.record_timeline_reset(reason);
    }
    let mut capture_baseline: Option<crate::live::Metrics> = None;
    for member in members.iter_mut() {
        member.start_feedback(start)?;
    }
    let volume_peers = members
        .iter()
        .map(|m| (m.peer.host.to_string(), m.uri.clone(), m.conn.clone()))
        .collect::<Vec<_>>();
    let volume_workers = if options.group_mode == "software_stereo" {
        volume_peers
            .iter()
            .cloned()
            .map(|p| crate::volume::Worker::start_member(p, volume.clone()))
            .collect::<Result<Vec<_>>>()?
    } else {
        vec![crate::volume::Worker::start(volume_peers.clone(), volume)?]
    };
    let metadata_timeline =
        crate::metadata::Timeline::new(members[0].media.packetizer.timestamp, rate);
    let metadata_worker = crate::metadata::Worker::start(
        volume_peers,
        crate::metadata::global().clone(),
        metadata_timeline.clone(),
    )
    .ok();
    let mut next_metrics = start + Duration::from_secs(1);
    // Applied lead in microseconds: the IPC request is whole milliseconds today,
    // but keeping the accumulator exact means a future finer-grained request (and
    // the microsecond reset comparison below) does not lose a remainder. This is
    // an internal invariant, not a promise of sub-millisecond lead control
    // (Q44 §二).
    begin_lead(telemetry.session_epoch);
    let mutable_anchor =
        !options.force_latency && members.iter().all(|m| m.media.supports_timeline_shift());
    let mut lead = LiveLead {
        ceiling_ms: lead_ceiling(common_latency),
        supported: mutable_anchor,
        ..LiveLead::default()
    };
    let mut lead_revision = 0;
    // RTP-side evidence for distinguishing a transport failure from a local
    // sender stall when a successful re-anchor absorbs a missed deadline (Q43,
    // Q44). The pre-stall window is compared against the sender's own steady
    // state, so a link with constant background loss is not a fault.
    let mut rtp_evidence = crate::telemetry::RtpEvidence::default();
    // The busy time of the most recent stall: a fixed 250 ms window that
    // accumulates the retransmit requests the stall left queued (Q44 §一,
    // Q47 §三). A second re-anchor closes it early as capped instead of merging
    // two stalls into one number.
    let mut stall_drain: Option<crate::telemetry::StallDrainWindow> = None;
    let mut last_marker = 0u64;
    let mut next_sync = start;
    let mut silence_packets = 0u64;
    let mut recovery_left = 0u64;
    emit(with_lead(
        json!({"event":"streaming","members":members.len(),"clock_id":clock_id,"first_send_unix_ns":clock.now_ns(),"first_send_qpc_ns":crate::wasapi::qpc_ns(),"requested_latency_ms":options.latency_ms,"sample_rate":rate,"measured_latency_ms":null,"qualified":false}),
        &lead,
        &schedule,
        negotiated_baseline,
        telemetry.session_epoch,
    ));
    let mut pcm = vec![0; PCM_BYTES];
    let started_ns = crate::wasapi::qpc_ns();
    let mut pending_clock_fault: Option<String> = None;
    // Q46's all-failed rule state. The gateway address is discovered once per
    // all-failed episode (not per round: a routing-table lookup every 3 s of an
    // outage would buy nothing) while its reachability is probed again on every
    // full round, so a recovered reference ends the suppression.

    let outcome = (|| -> Result<StreamEnd> {
        while (options.duration_ms == 0 || start.elapsed() < duration) && !cancel.is_cancelled() {
            if let Some(source) = live {
                emit_capture_warnings(source, emit);
                if source.ended() {
                    return Ok(StreamEnd::SourceEnded);
                }
                source.ready()?;
                let now = crate::wasapi::qpc_ns();
                if standby.should_pause(now, source.last_audio(), started_ns) {
                    return Ok(StreamEnd::Silence(now));
                }
            }
            metadata_timeline.set(members[0].media.packetizer.timestamp);
            for worker in &volume_workers {
                for event in worker.events.try_iter() {
                    emit(event);
                }
            }
            if let Some(worker) = &metadata_worker {
                for event in worker.events.try_iter() {
                    emit(event);
                }
            }
            // Gather the whole round before deciding anything: Q42's group-wide
            // rule needs every member's liveness at once, and a member-by-member
            // decision would reclaim the first failing receiver before the
            // second one's identical failure was even probed.
            let mut rounds = Vec::with_capacity(members.len());
            for member in members.iter_mut() {
                rounds.push(member.prepare_preemption(cancel, Instant::now()));
            }
            for (member, round) in members.iter_mut().zip(rounds) {
                for notice in member.health.notices() {
                    emit(notice);
                }
                let (preemption, media_healthy) =
                    member.apply_preemption(round, takeovers, Instant::now(), emit, false);
                let waiting = match preemption {
                    PreemptionPoll::None => false,
                    PreemptionPoll::Waiting => true,
                    PreemptionPoll::Yield(mut fault) => {
                        if fault.code == "peer_closed"
                            && let Some(host) = &pending_clock_fault
                        {
                            fault = Fault::new(
                                host,
                                "clock",
                                "clock_lost",
                                true,
                                "PTP exchanges stopped; receiver confirmed free",
                            );
                        }
                        emit(
                            json!({"event":"warning","channel":"preemption","code":"recovery_decision","host":fault.host,"recovered":false,"fault_code":fault.code,"automatic_recovery_allowed":fault.retryable,"message":fault.message}),
                        );
                        return Err(fault.into());
                    }
                };
                member.health.check()?;
                // While another sender owns the receiver, a dead control channel
                // is expected; the status-probe queue decides when to move, not the
                // generic control check.
                if !waiting {
                    // Control-plane trouble only ends media that has already stopped
                    // arriving; a dead RTSP feedback loop on a healthy RTP stream keeps
                    // playing (and is reported as a warning instead).
                    member.health.check_control(media_healthy)?;
                }
                // The event channel can be reopened on the same key while audio keeps
                // flowing, so a dead event channel costs metadata and buttons only
                // until the next attempt. Bounded, because a receiver that refuses
                // the channel will refuse it forever.
                member.poll_event_rebuild(emit);
            }
            if let Some(timer) = &ptp {
                // PTP silence is not an immediate fault: the last offset keeps
                // driving the timeline for a grace window (the reference
                // implementation freezes drift correction here rather than ending
                // the session). Only a peer that never answered, or one that stayed
                // silent past the grace, ends the session.
                let now = Instant::now();
                if let Some(host) = members.iter().find_map(|m| {
                    if !m.compat.uses_ptp() {
                        return None;
                    }
                    match timer.stale_for(m.peer.host, now) {
                        Some(stale) if stale <= CLOCK_FREEZE_GRACE => None,
                        Some(_) => Some(m.peer.host),
                        None if timer.ready(m.peer.host) => None,
                        None => Some(m.peer.host),
                    }
                }) {
                    let hosts: Vec<_> = members
                        .iter()
                        .filter(|m| m.compat.uses_ptp())
                        .map(|m| m.peer.host)
                        .collect();
                    if pending_clock_fault.is_none() {
                        emit(clock_status(timer, &hosts, "lost"));
                        emit(
                            json!({"event":"warning","channel":"clock","code":"recovery_original_fault","host":host,"fault_code":"clock_lost","recovered":false,"message":"PTP exchanges stopped; verifying receiver ownership before recovery"}),
                        );
                        pending_clock_fault = Some(host.to_string());
                        for member in members.iter() {
                            member
                                .health
                                .preemption_signal(preemption::Signal::ClockLost);
                        }
                    }
                }
            }
            if pending_clock_fault.is_some() {
                // Timing is unusable. Stop producing media while the bounded
                // background probes settle; cancellation remains responsive.
                thread::sleep(Duration::from_millis(2));
                continue;
            }
            for member in members.iter_mut() {
                member
                    .media
                    .service_buffered(Instant::now())
                    .map_err(|e| Fault::from_error(member.peer.host, "buffered", &e))?;
            }
            // Adaptive latency: the desktop asks for more or less media held at the
            // receiver. `base_ns` stays immutable — the published mapping is
            // `base_ns + anchor_shift + frames`, so a lead change moves the sync
            // anchor without rewriting the session's origin (a rewritten origin is
            // exactly what makes the receiver jump a segment).
            let requested = lead_request();
            if requested.revision != lead_revision
                && requested.session_epoch == telemetry.session_epoch
            {
                lead_revision = requested.revision;
                emit(lead.apply(
                    requested,
                    &mut schedule,
                    &mut telemetry,
                    negotiated_baseline,
                    members,
                ));
                next_sync = Instant::now();
            }
            let deadline = schedule.deadline();
            let now = Instant::now();
            // Sample the cumulative RTP counters for the re-anchor evidence
            // window. `sample` throttles itself; this runs once per packet at
            // most, and never during a stall (the loop is not running). Packets
            // sent is sampled with the counters because the discriminator is a
            // retransmit *fraction*, not a rate (Q47 §二).
            let (rtx_total, sent_total) = rtp_totals(members);
            rtp_evidence.sample(now, rtx_total, telemetry.underruns_active, sent_total);
            if now < deadline {
                thread::sleep((deadline - now).min(Duration::from_millis(2)));
                continue;
            }
            let late = now.saturating_duration_since(deadline);
            schedule.max_lateness_us = schedule.max_lateness_us.max(late.as_micros());
            telemetry.observe_lateness(late.as_secs_f64() * 1000.0);
            if let Some(source) = &live {
                telemetry.capture_age.observe(source.sample_age_ms());
                // Slow outer loop: sustained lateness trims the capture resampler.
                source.report_lateness(late.as_secs_f64() * 1000.0);
            }
            if options.duration_ms > 0 {
                // Finite qualification: `recover` only skips slots so seq/rtptime
                // stay linear. Skipped slots are not a reset (the timeline keeps
                // its origin), so nothing is recorded here.
                schedule.recover(now, false)?;
            } else if recovery_left == 0 {
                let earliest = members
                    .iter()
                    .map(|m| {
                        offset_deadline(
                            schedule.playout_deadline(),
                            common_latency,
                            m.peer.profile.sync_offset_ms,
                        )
                    })
                    .collect::<Result<Vec<_>>>()?
                    .into_iter()
                    .min()
                    .unwrap();
                match schedule.late_action(now, earliest) {
                    crate::transport::LateAction::OnTime => {}
                    crate::transport::LateAction::Silence(fill) => {
                        // Filling the scheduled slots with silence keeps the
                        // timeline intact: seq and rtptime are still linear, so
                        // this is *not* a reset and must not be counted as
                        // `silence_resume` (that bucket is the standby pause →
                        // resume path, which builds a brand-new timeline).
                        recovery_left = fill;
                        schedule.recoveries += 1;
                        telemetry.gap_filled += fill;
                        if let Some(source) = &live {
                            source.discard_stale();
                        }
                        emit(
                            json!({"event":"warning","code":"sender_late_recovered","channel":"scheduler","host":null,"recovered":true,"silence_packets":fill,"message":"Sender filled valid timeline slots with encoded silence"}),
                        );
                    }
                    crate::transport::LateAction::Reanchor => {
                        if !mutable_anchor {
                            telemetry.expired += 1;
                            return Err(Fault::new("", "scheduler", "timeline_expired", true,
                                if options.force_latency { "Forced latency requires a fresh session after a sender stall" } else { "Buffered playback requires a fresh session anchor after a sender stall" }).into());
                        }
                        // Over budget: move the whole timeline forward instead of
                        // tearing the session down. Sequence numbers and RTP
                        // timestamps stay untouched; only the rtptime→networkTime
                        // anchor moves, one frame of phase margin ahead of now.
                        let Some(shift) = schedule.reanchor(now) else {
                            telemetry.expired += 1;
                            // Delivery was late beyond both the silence window and
                            // the re-anchor budget; a fresh session is the only
                            // answer, which is a confirmed transport failure rather
                            // than a local adjustment.
                            telemetry.record_timeline_reset(
                                TimelineResetReason::ConfirmedTransportFailure,
                            );
                            emit(
                                json!({"event":"warning","code":"timeline_reanchor_exhausted","channel":"scheduler","host":null,"recovered":false,"message":"Sender stalled past the re-anchor budget; a fresh group timeline is required"}),
                            );
                            return Err(Fault::new(
                                "",
                                "scheduler",
                                "timeline_expired",
                                true,
                                "expired audio requires a fresh group timeline",
                            )
                            .into());
                        };
                        telemetry.reanchored = schedule.reanchored;
                        telemetry.reanchor_shift_us = schedule.reanchor_shift_us;
                        if !shift.is_zero() {
                            // Q43 §三, Q44 §一: a successful re-anchor is not
                            // automatically a transport failure. A missed deadline
                            // can be the local scheduler (GC, CPU contention,
                            // disk), which must not pollute the only drift signal.
                            // Only an RTP spike above this link's own steady rate
                            // in the window leading into the stall is transport
                            // evidence; clean or steady counters mean a sender
                            // stall. The heuristic is documented on `RtpEvidence`;
                            // the fallback is deliberately the conservative
                            // `sender_stall`.
                            let (rtp_req, packets_sent) = rtp_totals(members);
                            let reason = rtp_evidence.reset_reason(
                                now,
                                rtp_req,
                                telemetry.underruns_active,
                                packets_sent,
                            );
                            // Start a new pre-stall window: the RTCP requests that
                            // arrived during this stall are drained after it, and
                            // must not be counted against the next re-anchor. The
                            // long steady baseline is kept.
                            rtp_evidence.reset(now);
                            // A window still open here belongs to an *earlier*
                            // stall: close it capped (a lower bound) instead of
                            // letting this re-anchor merge two stalls (Q47 §三).
                            if let Some(window) = stall_drain.take() {
                                telemetry.record_stall_drain(window.close_early(rtp_req));
                            }
                            stall_drain = Some(crate::telemetry::StallDrainWindow::open(
                                now,
                                rtp_req,
                                rtp_queue_state(members).1,
                            ));
                            telemetry.record_timeline_reset(reason);
                            recovery_left = 0;
                            if let Some(source) = &live {
                                // Everything queued is older than the new deadline.
                                source.discard_stale();
                            }
                            emit(
                                json!({"event":"warning","code":"timeline_reanchored","channel":"scheduler","host":null,"recovered":true,"late_ms":round3(late.as_secs_f64() * 1000.0),"shift_ms":round3(shift.as_secs_f64() * 1000.0),"lead_ms":round3(schedule.shift.as_secs_f64() * 1000.0),"reanchored":schedule.reanchored,"reset_reason":reason.name(),"message":"Sender thread fell behind its catch-up budget; re-anchored the deadline one frame ahead"}),
                            );
                        }
                    }
                }
            }
            // A deliberate lead change (adaptive latency ladder) keeps its value;
            // only an automatic re-anchor surplus is slewed back. Relaxing is the
            // unwind of an already-counted re-anchor, not a new timeline reset:
            // counting it would turn one stall into a stream of `latency_change`
            // events that nobody caused.
            schedule.relax(now, late);
            if now >= next_sync {
                for m in members.iter_mut() {
                    let local = offset_time(
                        base_ns
                            + schedule.anchor_shift_ns()
                            + schedule.frames * 1_000_000_000 / u64::from(rate),
                        common_latency,
                        m.media.latency(),
                        m.peer.profile.sync_offset_ms,
                    )?;
                    let (id, network) = if m.compat.uses_ptp() {
                        let (id, t, frozen) = ptp.as_ref().unwrap().timeline_frozen(
                            m.peer.host,
                            local,
                            CLOCK_FREEZE_GRACE,
                        )?;
                        if frozen != m.clock_frozen {
                            m.clock_frozen = frozen;
                            emit(json!({
                                "event": "warning",
                                "host": m.peer.host.to_string(),
                                "channel": "clock",
                                "code": "clock_frozen",
                                "recovered": !frozen,
                                "message": if frozen {
                                    "PTP timing is stale; holding the last offset"
                                } else {
                                    "PTP timing is fresh again"
                                },
                            }));
                        }
                        (Some(id), t)
                    } else {
                        (None, local)
                    };
                    let latency =
                        (m.media.latency().as_nanos() * u128::from(rate) / 1_000_000_000) as u32;
                    m.media.sync(network, id, latency, packets == 0);
                }
                next_sync = now + Duration::from_millis(100);
            }
            if let Some(source) = &live {
                let current_gain = f32::from_bits(gain.load(Ordering::Relaxed));
                let (packet, marker) = if recovery_left > 0 {
                    source.silence_packet(current_gain)?
                } else {
                    source.packet(current_gain)?
                };
                pcm = packet;
                if let Some(qpc) = marker {
                    if qpc.saturating_sub(last_marker) > 200_000_000 {
                        emit(
                            json!({"event":"audio_onset","capture_qpc_ns":qpc,"send_qpc_ns":crate::wasapi::qpc_ns()}),
                        );
                    }
                    last_marker = qpc;
                }
            } else {
                for (j, bytes) in pcm.chunks_exact_mut(2).enumerate() {
                    let index = schedule.frames as usize * 2 + j;
                    let sample = samples.get(index).copied().unwrap_or(0);
                    bytes.copy_from_slice(&sample.to_be_bytes());
                }
            }
            if now >= next_metrics {
                if let Some(timer) = &ptp {
                    let hosts: Vec<_> = members
                        .iter()
                        .filter(|m| m.compat.uses_ptp())
                        .map(|m| m.peer.host)
                        .collect();
                    emit(clock_status(timer, &hosts, "streaming"));
                }
                if let Some(source) = &live {
                    sample_capture(source, &mut capture_baseline, &mut telemetry);
                    emit(json!({"event":"capture_metrics","metrics":source.metrics()}));
                }
                let snapshot = transport_metrics(
                    members,
                    &schedule,
                    &telemetry,
                    common_latency,
                    live.map(crate::live::Loopback::depth),
                    ptp,
                    &lead,
                );
                crate::telemetry::publish_diagnostics(snapshot.clone());
                telemetry.lateness_interval = crate::telemetry::Histogram::default();
                emit(snapshot);
                next_metrics = now + Duration::from_secs(1);
            }
            // Receiver-side buttons and sliders arrive on the event channel; relay
            // them so the desktop side can drive the system media session.
            for member in members.iter_mut() {
                for command in member.health.commands() {
                    emit(json!({
                        "event": "remote_command",
                        "host": member.peer.host,
                        "receiver_id": member.peer.receiver_id,
                        "commands": command.commands.iter().map(|c| c.name()).collect::<Vec<_>>(),
                        "source": command.source,
                        "volume": command.volume,
                    }));
                }
            }
            // Every member gets new media before any member services retransmissions.
            for member in members.iter_mut() {
                member.media.send(
                    &member.peer.profile.channel.map(&pcm),
                    packets == 0,
                    Instant::now(),
                    offset_deadline(
                        schedule.playout_deadline(),
                        common_latency - member.media.latency(),
                        member.peer.profile.sync_offset_ms,
                    )?,
                    &member.health,
                )?;
            }
            for member in members.iter_mut() {
                member.media.service_retransmits();
            }
            // Q44 §一, Q47 §三: accumulate the backlog a stall left queued over a
            // fixed 250 ms window. One `service_retransmits` call accepts at most
            // 16 requests, so measuring only the first drain after the re-anchor
            // saturates at 16 and stops saying anything about severity.
            if let Some(window) = stall_drain.as_mut() {
                let (pending, drops) = rtp_queue_state(members);
                let drained = window.observe(Instant::now(), rtp_totals(members).0, pending, drops);
                if let Some(drain) = drained {
                    telemetry.record_stall_drain(drain);
                    stall_drain = None;
                }
            }
            schedule.sent();
            packets += 1;
            if recovery_left > 0 {
                recovery_left -= 1;
                silence_packets += 1;
            }
        }
        Ok(StreamEnd::Finished)
    })();
    // A single uncertain member makes group-wide cleanup unsafe. Never send an
    // old association's TEARDOWN to the other half of a taken-over stereo pair.
    if members.iter().any(|m| {
        m.preempted
            || m.ownership_uncertain
            || m.preemption.session_ended()
            || m.health.ownership_uncertain()
    }) {
        for member in members.iter_mut() {
            member.preempted = true;
        }
    }
    // A stall in the last 250 ms of the session never reaches its own deadline.
    // Report what the window saw rather than dropping the stall from the summary.
    if let Some(window) = stall_drain.take() {
        let (pending, drops) = rtp_queue_state(members);
        telemetry.record_stall_drain(window.close(rtp_totals(members).0, pending, drops));
    }
    // Stop/reconfiguration shares one faded final packet and drains its render
    // deadline on every member before tearing down the common session.
    //
    // The drain is fallible (the final packet still goes through the packetizer),
    // but its error must not skip the end-of-session record below. Keep it and
    // return it after the summary has been emitted.
    let drained = if outcome.is_ok() {
        (|| -> Result<()> {
            if let Some(source) = &live {
                let render = offset_deadline(schedule.playout_deadline(), common_latency, 0)?;
                let (fade, _) = source.silence_packet(0.0)?;
                let mut sent = true;
                for member in members.iter_mut() {
                    sent &= member
                        .media
                        .send(
                            &member.peer.profile.channel.map(&fade),
                            false,
                            Instant::now(),
                            offset_deadline(
                                schedule.playout_deadline(),
                                common_latency - member.media.latency(),
                                member.peer.profile.sync_offset_ms,
                            )?,
                            &Health::default(),
                        )
                        .is_ok();
                }
                if sent {
                    let until = render
                        + Duration::from_nanos(FRAMES as u64 * 1_000_000_000 / u64::from(rate))
                        + Duration::from_millis(
                            members
                                .iter()
                                .map(|m| m.peer.profile.sync_offset_ms.max(0) as u64)
                                .max()
                                .unwrap_or(0),
                        );
                    while Instant::now() < until {
                        if members
                            .iter_mut()
                            .any(|m| m.media.service_buffered(Instant::now()).is_err())
                        {
                            break;
                        }
                        thread::sleep(Duration::from_millis(2));
                    }
                }
                // A normal stop sends one faded final packet and drains it. That
                // is a deliberate end of the timeline, not a `flush` reset: the
                // receiver is never asked to discard queued audio, so a normal
                // stop keeps every reset bucket at zero.
                emit(json!({"event":"timeline_drained","faded":sent}));
            }
            Ok(())
        })()
    } else {
        Ok(())
    };
    if let Some(source) = &live {
        sample_capture(source, &mut capture_baseline, &mut telemetry);
        emit(json!({"event":"capture_metrics","metrics":source.metrics()}));
    }
    for member in members.iter() {
        for notice in member.health.notices() {
            emit(notice);
        }
    }
    let snapshot = transport_metrics(
        members,
        &schedule,
        &telemetry,
        common_latency,
        live.map(crate::live::Loopback::depth),
        ptp,
        &lead,
    );
    crate::telemetry::publish_diagnostics(snapshot.clone());
    emit(snapshot);
    let mut summary = telemetry.snapshot();
    if let Json::Object(ref mut fields) = summary {
        fields.insert("event".into(), json!("metrics"));
        fields.insert("packets_per_member".into(), json!(packets));
        fields.insert("frames".into(), json!(schedule.frames));
        fields.insert("silence_packets".into(), json!(silence_packets));
        fields.insert(
            "max_send_lateness_us".into(),
            json!(schedule.max_lateness_us),
        );
        fields.insert(
            "retransmits".into(),
            json!(
                members
                    .iter()
                    .map(|m| m.media.metrics.retransmits_sent)
                    .sum::<u64>()
            ),
        );
        fields.insert(
            "ptp_packets_received".into(),
            json!(ptp.as_ref().map(|p| p.received.load(Ordering::Relaxed))),
        );
        fields.insert("measured_latency_ms".into(), Json::Null);
        fields.insert("qualified".into(), json!(false));
    }
    emit(summary);
    // Roadmap §12.3: exactly one summary per stream attempt. A normal stop, an
    // in-loop fault and the teardown above all reach this single call site with
    // `outcome` already decided, so no path can emit a second record.
    let summary = session_summary(
        options,
        members,
        &telemetry,
        schedule.playout_lead.as_millis() as i64,
        start.elapsed(),
    );
    let path = (!options.report_session_id.is_empty()).then(crate::telemetry::summary_path);
    emit_session_summary(&summary, path.as_deref(), emit);
    drop(volume_workers);
    for m in members.iter_mut() {
        m.close();
    }
    drained?;
    outcome
}

/// Build the §12.3 summary from counters that already exist. Nothing is sampled
/// here, so calling it after the audio loop cannot disturb the audio path.
fn session_summary(
    options: &ProbeOptions,
    members: &[Member],
    telemetry: &crate::telemetry::SessionTelemetry,
    applied_lead_ms: i64,
    duration: Duration,
) -> crate::telemetry::SessionSummary {
    let first = members.first();
    // The desktop regenerates its session id per reconnect; the epoch suffix
    // keeps resumed attempts inside one IPC `start` distinguishable as well.
    let session_id = if options.report_session_id.is_empty() {
        format!("local-{}", telemetry.session_epoch)
    } else {
        format!("{}-{}", options.report_session_id, telemetry.session_epoch)
    };
    // Existing negotiated topology labels. An unset group mode (a standalone
    // qualification call) falls back to the member count.
    let topology = match options.group_mode.as_str() {
        "software_stereo" => "software_stereo",
        "native_stereo" => "native_stereo",
        "single" => "single",
        _ if members.len() > 1 => "group",
        _ => "single",
    };
    crate::telemetry::SessionSummary {
        session_id,
        topology,
        // The group plan negotiates one codec/timing for the whole session, so
        // the first member is representative.
        codec: first.map_or("pcm", |member| member.compat.codec_name()),
        timing: first.map_or("ntp", |member| member.compat.timing_name()),
        applied_lead_ms,
        // Q44 §二: the adaptive ladder's twitching lives here, not in the reset
        // buckets. Every applied shift counts; only shifts at least as long as
        // the splice ramp (5 ms) are resets.
        lead_shift_count: telemetry.lead_shift_count,
        lead_shift_total_us: telemetry.lead_shift_total_us,
        // One scheduler tick per media packet; `packets` is the existing
        // per-member counter that `transport_metrics` already publishes.
        packets: telemetry.packets,
        rtx_req: members
            .iter()
            .map(|member| member.media.metrics.retransmit_requests)
            .sum(),
        rtx_resent: members
            .iter()
            .map(|member| member.media.metrics.retransmits_sent)
            .sum(),
        // Only concealed underruns count: an idle keepalive gap is not a dropout.
        underruns: telemetry.underruns_active,
        gap_filled: telemetry.gap_filled,
        reanchored: telemetry.reanchored,
        // Q44 §一, Q47 §三: severity, not just count. `reanchored` says how often
        // the sender stalled; these say how loud the stalls were. A high stall
        // count with a near-zero drain is healthy (the buffer absorbed it).
        stall_rtx_drained: telemetry.stall_rtx_drained,
        stall_rtx_drained_max: telemetry.stall_rtx_drained_max,
        // Stalls whose 250 ms window closed capped: the drain is a lower bound,
        // so it stays out of `_max` and is counted here instead.
        stall_rtx_drained_capped: telemetry.stall_rtx_drained_capped,
        servo_engagements: telemetry.servo_engagements,
        // Servo "steps" is the existing full-loop reset after a discontinuity
        // (filter, correction and engagement cleared). It is the closest counter
        // to the roadmap's `baseline_resets`; there is no separate counter.
        baseline_resets: telemetry.servo_step_resets,
        mean_lateness_ms: round3(telemetry.lateness.mean_ms()),
        p99_lateness_ms: round3(telemetry.lateness.percentile_ms(0.99)),
        // `percentile_ms(1.0)` returns the true maximum, including out-of-range
        // samples, without exposing histogram internals.
        max_lateness_ms: round3(telemetry.lateness.percentile_ms(1.0)),
        duration_s: round3(duration.as_secs_f64()),
        // Q40: the record says *why* the timeline was reset, never how many times
        // in total. `confirmed_transport_failure` is the only drift signal.
        timeline_resets: telemetry.timeline_resets,
        last_timeline_reset_reason: telemetry
            .last_timeline_reset_reason
            .map_or(crate::telemetry::NO_TIMELINE_RESET, |reason| reason.name()),
    }
}

/// Best-effort end-of-session record. The IPC event is always emitted; the CSV
/// append is attempted only when `path` is given, and every I/O error is logged
/// to stderr (the desktop drains engine stderr into its log) and swallowed.
pub(crate) fn emit_session_summary(
    summary: &crate::telemetry::SessionSummary,
    path: Option<&std::path::Path>,
    emit: &impl Fn(Json),
) {
    emit(crate::telemetry::redact(&summary.event()));
    let Some(path) = path else {
        return;
    };
    if let Err(error) = crate::telemetry::append_summary_row(path, summary) {
        eprintln!(
            "session_summary: could not append {}: {error}",
            path.display()
        );
    }
}

/// A confirmed takeover yields until the user explicitly starts playback again.
fn preemption_fault(
    member: &Member,
    reason: preemption::ReclaimReason,
    _consecutive: u32,
) -> Fault {
    Fault::new(
        member.peer.host,
        "control",
        "preempted",
        false,
        "Another controller took over this receiver; AirFlash stopped and will not reconnect automatically. Start playback again to claim it back.",
    )
    .with_reason(reason.name())
}
/// Finite qualification cannot reopen a session, so a confirmed takeover is
/// reported as the terminal `PREEMPTED(takeover)` fault there.
fn preemption_takeover_fault(host: IpAddr, consecutive: u32) -> Fault {
    Fault::new(
        host,
        "control",
        "preempted",
        false,
        format!(
            "Another controller took over this receiver (takeover {consecutive}); AirFlash stopped and will not reconnect automatically. Start playback again to claim it back."
        ),
    )
    .with_reason(preemption::ReclaimReason::Takeover.name())
}
/// Re-derive Q34's combine table for the verdict Q42's escalation rule or the
/// group-wide cross-check settled on.
///
/// The raw `/info` half is reused — when the verdict differs from the raw probe
/// (an `Unknown` chain escalated to `Dead`, or an all-failed round downgraded to
/// `Unknown`) the already fetched `statusFlags` are combined again instead of
/// probing the receiver a second time. A failed `/info` probe stays a failure:
/// there is nothing better to combine, and the caller's fail-open path applies.
fn settle_liveness(
    media_healthy: bool,
    probed: preemption::ReceiverLiveness,
    verdict: preemption::Verdict,
    result: Result<preemption::ProbeReport>,
    we_hold_session: bool,
) -> Result<preemption::ProbeReport> {
    let effective = verdict.liveness();
    if effective == probed {
        return result;
    }
    match result {
        Ok(report) => Ok(preemption::ProbeReport {
            outcome: preemption::combine(
                media_healthy,
                effective,
                report.status_flags,
                we_hold_session,
            ),
            status_flags: report.status_flags,
        }),
        Err(error) => Err(error),
    }
}
/// Control-plane symptoms are logged as evidence, never as a verdict. The three
/// context fields are the ones Q30 recovers from the reference log; `policy` is
/// our proxy for WAC's session policy (timing/codec family). Q34 adds the
/// receiver-side liveness dimension as the first-class `tcp_reachable` field;
/// `tcp_liveness` keeps the four-state distinction a bool cannot (`alive` /
/// `refused` / `dead` / `unknown`, Q42) and `liveness_verdict` is what the
/// escalation rule made of it. `local_network_suspected` marks the rounds where
/// every receiver failed at once, i.e. the failure is ours, not the device's.
fn preemption_evidence(
    member: &Member,
    signals: &[preemption::Signal],
    rtp_healthy: bool,
    liveness: preemption::ReceiverLiveness,
    verdict: Option<preemption::Verdict>,
    local_suspected: bool,
) -> Json {
    json!({
        "event": "warning",
        "host": member.peer.host.to_string(),
        "channel": "preemption",
        "code": "preemption_evidence",
        "recovered": true,
        "signals": signals.iter().map(|signal| signal.name()).collect::<Vec<_>>(),
        "policy": format!("{}/{}", member.compat.timing_label(), member.compat.codec_name()),
        "rtp_healthy": rtp_healthy,
        "packets_prepared": member.media.metrics.packets_sent,
        "tcp_reachable": liveness.reachable(),
        "tcp_liveness": liveness.name(),
        "liveness_verdict": verdict.map(preemption::Verdict::name),
        "local_network_suspected": local_suspected,
        "message": "Control-plane takeover evidence; asking the receiver for its live statusFlags and TCP reachability",
    })
}
/// The liveness half of one diagnostic line (Q34/Q42): what the control-port
/// probe said, what the escalation rule made of it, how many consecutive
/// non-definitive samples there are, and whether the whole group failed at
/// once.
#[derive(Clone, Copy, Debug)]
struct ProbeLiveness {
    probed: preemption::ReceiverLiveness,
    verdict: preemption::Verdict,
    failures: u32,
    local_suspected: bool,
}
/// One diagnostic line per probe change. It carries the same context fields as
/// the evidence line (the reference implementation logs them next to the
/// symptom) plus `we_hold_session`, the locally derived dimension the verdict
/// uses, the raw 64-bit `statusFlags` value so the bits can be checked against a
/// real receiver, and Q34/Q42's liveness fields.
fn preemption_probe(
    member: &Member,
    result: &Result<preemption::ProbeReport>,
    we_hold_session: bool,
    rtp_healthy: bool,
    liveness: ProbeLiveness,
) -> Json {
    let ProbeLiveness {
        probed,
        verdict,
        failures,
        local_suspected,
    } = liveness;
    let mut event = json!({
        "event": "warning",
        "host": member.peer.host.to_string(),
        "channel": "preemption",
        "code": "preemption_probe",
        "recovered": true,
        "policy": format!("{}/{}", member.compat.timing_label(), member.compat.codec_name()),
        "rtp_healthy": rtp_healthy,
        "packets_prepared": member.media.metrics.packets_sent,
        "we_hold_session": we_hold_session,
        "tcp_reachable": probed.reachable(),
        "tcp_liveness": probed.name(),
        "liveness_verdict": verdict.name(),
        "liveness_failures": failures,
        "local_network_suspected": local_suspected,
    });
    match result {
        Ok(report) => {
            event["outcome"] = json!(report.outcome.name());
            event["status_flags"] = json!(report.status_flags);
            // Telemetry only: bit 14 says the stereo partner is unreachable. It
            // has no session-ownership meaning and never changes the verdict.
            event["stereo_partner_unreachable"] = json!(
                report
                    .status_flags
                    .is_some_and(|flags| flags & preemption::STEREO_PARTNER_UNREACHABLE != 0)
            );
            event["message"] = json!(match (report.outcome, report.status_flags) {
                // A group-wide failure is never reported as a dead device: the
                // evidence belongs to our own side (Q42).
                _ if local_suspected => {
                    "Every receiver failed its liveness probe in the same round; treating it as a local network problem, not a dead device".to_string()
                }
                (preemption::ProbeOutcome::Gone, _)
                    if probed == preemption::ReceiverLiveness::Refused =>
                {
                    "Receiver refused the control connection (the host is alive, it is just not serving AirPlay); ownership is unconfirmed"
                        .to_string()
                }
                (preemption::ProbeOutcome::Gone, _) => {
                    "Receiver control port is not reachable; ownership is unconfirmed".to_string()
                }
                (_, Some(flags)) => {
                    format!("Receiver /info statusFlags=0x{flags:016x} ({flags})")
                }
                (preemption::ProbeOutcome::OursActive, None) => {
                    "Receiver /info has no usable statusFlags; keeping our session (conservative)"
                        .to_string()
                }
                (_, None) =>
                    "Receiver /info has no usable statusFlags; ownership is unconfirmed".to_string(),
            });
        }
        Err(error) => {
            event["outcome"] = json!("error");
            event["message"] = json!(format!(
                "Receiver status probe failed ({error:#}); ownership is unconfirmed"
            ));
        }
    }
    event
}
/// Q46's all-failed rule fired: every receiver failed its liveness probe in the
/// same round and the gateway reference did not clear them. The common cause is
/// our own side (routing table, interface, Wi-Fi), not several devices dying at
/// once, so no device is marked dead and the media-health backstop keeps the
/// decision. The event is emitted once per episode — the state change — not on
/// every re-evaluation round; `verdict` separates "the gateway answered nothing"
/// (`local_network`) from "there was no default route to ask"
/// (`local_network_unproven`), and `all_failed_for_ms` is how long the episode
/// has run (it matters only for the time-boxed fallback).
#[cfg(test)]
fn preemption_local_network(
    receivers: usize,
    probes: &[preemption::ReceiverLiveness],
    judgement: preemption::AllFailed,
    gateway: Option<preemption::GatewayReachability>,
    held: Duration,
) -> Json {
    let message = match judgement {
        preemption::AllFailed::LocalNetwork => format!(
            "All {receivers} receiver liveness probes failed this round and the gateway did not answer; suspecting the local network, keeping the session"
        ),
        _ => format!(
            "All {receivers} receiver liveness probes failed this round and no default gateway could be probed; suppressing for at most {} s, then reclaiming",
            preemption::GATEWAY_FALLBACK_WINDOW.as_secs()
        ),
    };
    json!({
        "event": "warning",
        "host": null,
        "channel": "preemption",
        "code": "preemption_local_network",
        "recovered": false,
        "receivers": receivers,
        "probes": probes.iter().map(|probed| probed.name()).collect::<Vec<_>>(),
        "verdict": judgement.name(),
        "gateway": gateway.map(preemption::GatewayReachability::name),
        "gateway_probed": gateway.is_some(),
        "all_failed_for_ms": held.as_millis() as u64,
        "message": message,
    })
}
/// The confirmation probe disagreed with the first verdict. Both raw outcomes
/// are logged; the caller keeps the non-destructive verdict (never a reclaim),
/// so a receiver flapping between `Free` and `BusyElsewhere` cannot make us
/// fight for it.
fn preemption_probe_disagreement(
    member: &Member,
    first: preemption::ProbeOutcome,
    second: preemption::ProbeOutcome,
) -> Json {
    json!({
        "event": "warning",
        "host": member.peer.host.to_string(),
        "channel": "preemption",
        "code": "preemption_probe_disagreement",
        "recovered": true,
        "first": first.name(),
        "second": second.name(),
        "message": "The two statusFlags probes disagreed; keeping the non-destructive verdict",
    })
}
fn emit_capture_warnings(source: &crate::live::Loopback, emit: &impl Fn(Json)) {
    for warning in source.take_warnings() {
        emit(json!({
            "event": "warning",
            "code": warning.code,
            "channel": "capture",
            "host": null,
            "recovered": warning.recovered,
            "message": warning.message,
        }));
    }
}

fn sample_capture(
    source: &crate::live::Loopback,
    baseline: &mut Option<crate::live::Metrics>,
    telemetry: &mut crate::telemetry::SessionTelemetry,
) {
    let metrics = source.metrics();
    if let Some(previous) = baseline {
        telemetry.input_drops += metrics
            .dropped_frames
            .saturating_sub(previous.dropped_frames);
        telemetry.capture_glitches += metrics
            .discontinuities
            .saturating_sub(previous.discontinuities);
        let servo = source.servo_metrics();
        telemetry.servo_engagements = servo.engagements;
        telemetry.servo_releases = servo.releases;
        telemetry.servo_step_resets = servo.steps;
        telemetry.servo_correction_ms = servo.correction_ppm / 1000.0;
        telemetry.servo_filtered_ms = servo.filtered_ms;
        telemetry.servo_drift_ppb = source.drift_ppb();
        let underruns = metrics
            .underrun_packets
            .saturating_sub(previous.underrun_packets);
        if underruns > 0 {
            // An underrun while the source has been quiet for a second is a
            // keepalive gap, not a concealed music dropout.
            let idle = metrics.last_audio_qpc_ns == 0
                || crate::wasapi::qpc_ns().saturating_sub(metrics.last_audio_qpc_ns)
                    > 1_000_000_000;
            for _ in 0..underruns {
                telemetry.underflow(if idle {
                    crate::telemetry::UnderflowCause::IdleKeepalive
                } else {
                    crate::telemetry::UnderflowCause::ActiveMedia
                });
            }
        }
    }
    *baseline = Some(metrics);
}

fn transport_metrics(
    members: &[Member],
    schedule: &MediaSchedule,
    telemetry: &crate::telemetry::SessionTelemetry,
    playout: Duration,
    queue: Option<(usize, usize)>,
    ptp: Option<&PtpMaster>,
    lead: &LiveLead,
) -> Json {
    let mut snapshot = telemetry.snapshot();
    if let Json::Object(ref mut fields) = snapshot {
        fields.insert("event".into(), json!("transport_metrics"));
        fields.insert(
            "session_uptime_ms".into(),
            json!(schedule.start.elapsed().as_millis()),
        );
        fields.insert("sender_late_recoveries".into(), json!(schedule.recoveries));
        fields.insert("skipped_packets".into(), json!(schedule.skipped_packets));
        fields.insert(
            "max_send_lateness_us".into(),
            json!(schedule.max_lateness_us),
        );
        fields.insert(
            "playout_ms".into(),
            json!(round3(
                (playout + schedule.playout_lead).as_secs_f64() * 1000.0
            )),
        );
        fields.insert("queue_depth".into(), json!(queue.map(|q| q.0)));
        fields.insert("queue_capacity".into(), json!(queue.map(|q| q.1)));
        fields.insert(
            "members".into(),
            json!(members.iter().map(|m| m.metrics(ptp)).collect::<Vec<_>>()),
        );
    }
    let baseline = members
        .iter()
        .map(|m| m.media.latency())
        .max()
        .unwrap_or(playout);
    with_lead(snapshot, lead, schedule, baseline, telemetry.session_epoch)
}
#[derive(Default)]
struct LiveLead {
    sequence: u64,
    requested_ms: i64,
    ceiling_ms: i64,
    supported: bool,
}
impl LiveLead {
    fn apply(
        &mut self,
        request: LeadRequest,
        schedule: &mut MediaSchedule,
        telemetry: &mut crate::telemetry::SessionTelemetry,
        baseline: Duration,
        members: &mut [Member],
    ) -> Json {
        if !self.supported {
            return json!({"event":"lead_error","sequence":request.sequence,"session_epoch":telemetry.session_epoch,"message":"Runtime lead changes require unforced realtime UDP on every member"});
        }
        let wanted_us = request.ms.min(self.ceiling_ms) * 1_000;
        let applied_us =
            schedule.add_shift_us(wanted_us - schedule.playout_lead.as_micros() as i64);
        self.sequence = request.sequence;
        self.requested_ms = request.ms;
        telemetry.lead_extra_ms = schedule.playout_lead.as_secs_f64() * 1000.0;
        if applied_us != 0 {
            telemetry.record_lead_shift(applied_us);
            for member in members {
                member
                    .media
                    .packetizer
                    .shift_playout(applied_us, schedule.playout_lead);
            }
        }
        // No-ops and clamps must acknowledge the real result, too.
        with_lead(
            json!({"event":"lead_changed","channel":"scheduler","host":null,"sequence":request.sequence,"requested_ms":request.ms,"applied_ms":schedule.playout_lead.as_millis(),"shift_us":applied_us,"ceiling_ms":self.ceiling_ms,"shift_ms":round3(schedule.shift.as_secs_f64() * 1000.0)}),
            self,
            schedule,
            baseline,
            telemetry.session_epoch,
        )
    }
}
fn with_lead(
    mut event: Json,
    lead: &LiveLead,
    schedule: &MediaSchedule,
    baseline: Duration,
    epoch: u64,
) -> Json {
    event["session_epoch"] = json!(epoch);
    event["lead_sequence"] = json!(lead.sequence);
    event["lead_requested_ms"] = json!(lead.requested_ms);
    event["baseline_ms"] = json!(round3(baseline.as_secs_f64() * 1000.0));
    event["lead_extra_ms"] = json!(round3(schedule.playout_lead.as_secs_f64() * 1000.0));
    event["effective_latency_ms"] = json!(round3(
        (baseline + schedule.playout_lead).as_secs_f64() * 1000.0
    ));
    event["lead_ceiling_ms"] = json!(lead.ceiling_ms);
    event["live_lead_supported"] = json!(lead.supported);
    event
}
fn round3(value: f64) -> f64 {
    (value * 1000.0).round() / 1000.0
}
/// Extra media lead requested by the adaptive latency ladder, in milliseconds.
///
/// The engine runs one session at a time (the IPC front end serialises `start`),
/// so a process-wide cell keeps `set_lead` from having to thread another parameter
/// through six call sites. Zero means "whatever the timeline needs".
#[derive(Clone, Copy, Default)]
struct LeadRequest {
    ms: i64,
    sequence: u64,
    session_epoch: u64,
    revision: u64,
}
static LEAD_REQUEST: std::sync::Mutex<LeadRequest> = std::sync::Mutex::new(LeadRequest {
    ms: 0,
    sequence: 0,
    session_epoch: 0,
    revision: 0,
});
/// Absolute ceiling for an operator-requested extra lead. The lower session cap
/// is a local policy derived from requested latency, not a measured device buffer.
pub const MAX_LEAD_MS: i64 = 2000;
/// Local headroom above requested latency; receiver capacity is not measured here.
pub const LEAD_HEADROOM_MS: i64 = 100;
fn selected_latency(requested_ms: u32, receiver_min: Option<Duration>, forced: bool) -> Duration {
    let requested = Duration::from_millis(u64::from(requested_ms));
    if forced {
        requested
    } else {
        receiver_min.unwrap_or(requested)
    }
}
/// Bound requested extra lead conservatively using the session's latency settings.
fn lead_ceiling(baseline: Duration) -> i64 {
    (MAX_LEAD_MS - baseline.as_millis().min(i64::MAX as u128) as i64).clamp(0, LEAD_HEADROOM_MS)
}
pub fn request_lead(ms: i64) {
    let mut request = LEAD_REQUEST.lock().unwrap();
    request.ms = ms.clamp(0, MAX_LEAD_MS);
    request.sequence += 1;
    request.revision += 1;
}
pub fn request_lead_for(ms: i64, sequence: u64, epoch: u64) -> Result<()> {
    let mut request = LEAD_REQUEST.lock().unwrap();
    ensure!(
        epoch > 0 && request.session_epoch == epoch,
        "lead stream epoch is no longer active"
    );
    ensure!(
        sequence > 0 && sequence >= request.sequence,
        "stale lead sequence"
    );
    ensure!(
        sequence != request.sequence || ms == request.ms,
        "lead sequence already used"
    );
    request.ms = ms.clamp(0, MAX_LEAD_MS);
    request.sequence = sequence;
    request.revision += 1;
    Ok(())
}
fn lead_request() -> LeadRequest {
    *LEAD_REQUEST.lock().unwrap()
}
pub fn requested_lead() -> i64 {
    lead_request().ms
}
pub fn reset_lead() {
    *LEAD_REQUEST.lock().unwrap() = LeadRequest::default();
}
fn begin_lead(epoch: u64) {
    *LEAD_REQUEST.lock().unwrap() = LeadRequest {
        session_epoch: epoch,
        ..LeadRequest::default()
    };
}
/// Recent socket-write health. This does not prove receiver ownership and cannot
/// override an explicit receiver-initiated TEARDOWN.
const MEDIA_HEALTH_WINDOW: Duration = Duration::from_secs(2);
/// How long a stale PTP offset keeps driving the timeline. The reference
/// implementation freezes drift correction on PTP silence instead of ending the
/// session; ten seconds is our own patience limit before we do end it.
const CLOCK_FREEZE_GRACE: Duration = Duration::from_secs(10);
/// Event-channel rebuild budget. Each attempt is a fresh TCP connection with the
/// same key; audio is never touched.
const MAX_EVENT_REBUILDS: u8 = 3;
const EVENT_REBUILD_INTERVAL: Duration = Duration::from_secs(2);
/// One retry per member: enough to ride out a transient refusal, short enough that
/// a stereo pair does not wait seconds before giving up.
const MEMBER_SETUP_RETRIES: u8 = 1;
const MEMBER_SETUP_RETRY_DELAY: Duration = Duration::from_millis(400);
fn offset_time(base: u64, common: Duration, own: Duration, offset_ms: i32) -> Result<u64> {
    Ok(u64::try_from(
        base as i128 + common.as_nanos() as i128 - own.as_nanos() as i128
            + i128::from(offset_ms) * 1_000_000,
    )?)
}
fn offset_deadline(base: Instant, lead: Duration, offset_ms: i32) -> Result<Instant> {
    let ns = lead.as_nanos() as i128 + i128::from(offset_ms) * 1_000_000;
    if ns >= 0 {
        base.checked_add(Duration::from_nanos(u64::try_from(ns)?))
    } else {
        base.checked_sub(Duration::from_nanos(u64::try_from(-ns)?))
    }
    .context("invalid media deadline")
}

#[cfg(test)]
#[path = "session_simulation.rs"]
mod simulation;

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn forced_latency_keeps_the_requested_value_and_legacy_keeps_negotiation() {
        for requested in [0, 120, 200, 500, 2000] {
            for minimum in [
                None,
                Some(Duration::from_millis(50)),
                Some(Duration::from_millis(200)),
                Some(Duration::from_millis(500)),
            ] {
                assert_eq!(
                    selected_latency(requested, minimum, true),
                    Duration::from_millis(u64::from(requested))
                );
                assert_eq!(
                    selected_latency(requested, minimum, false),
                    minimum.unwrap_or(Duration::from_millis(u64::from(requested)))
                );
            }
        }
    }
    #[test]
    fn forced_group_targets_must_match_but_legacy_values_remain_valid() {
        let mut options: ProbeOptions = serde_json::from_value(json!({
            "peers":[{"host":"127.0.0.1","profile":{"latency_ms":120}},{"host":"127.0.0.2","profile":{"latency_ms":200}}],
            "source":"loopback","duration_ms":0,"latency_ms":120,"gain":0.1
        })).unwrap();
        assert!(!options.force_latency);
        assert!(options.validate_start().is_ok());
        options.force_latency = true;
        assert!(options.validate_start().is_err());
        options.peers[1].profile.latency_ms = Some(120);
        assert!(options.validate_start().is_ok());
    }
    use crate::profile::{
        AIRPLAY2_BIT, Codec, CompatFingerprint, Formats, Profile, Timing, TimingPlan, Transport,
    };
    #[test]
    fn lead_ceiling_bounds_extra_and_total_latency_separately() {
        for baseline in [0, 50, 120, 250, 300] {
            assert_eq!(lead_ceiling(Duration::from_millis(baseline)), 100);
        }
        assert_eq!(lead_ceiling(Duration::from_millis(1950)), 50);
        assert_eq!(lead_ceiling(Duration::from_millis(2000)), 0);
        assert_eq!(lead_ceiling(Duration::from_millis(5000)), 0);
    }
    #[test]
    fn lead_acknowledges_no_ops_clamps_and_rejections_with_actual_runtime() {
        let mut schedule = MediaSchedule::new(Instant::now(), 44_100);
        let mut telemetry = crate::telemetry::SessionTelemetry::new();
        let mut lead = LiveLead {
            supported: true,
            ceiling_ms: 100,
            ..LiveLead::default()
        };
        let baseline = Duration::from_millis(120);
        for (sequence, requested, applied) in [(1, 0, 0), (2, 20, 20), (3, 20, 20), (4, 500, 100)] {
            let reply = lead.apply(
                LeadRequest {
                    ms: requested,
                    sequence,
                    ..LeadRequest::default()
                },
                &mut schedule,
                &mut telemetry,
                baseline,
                &mut [],
            );
            assert_eq!(reply["event"], "lead_changed");
            assert_eq!(reply["sequence"], sequence);
            assert_eq!(reply["session_epoch"], telemetry.session_epoch);
            assert_eq!(reply["requested_ms"], requested);
            assert_eq!(reply["applied_ms"], applied);
            assert_eq!(reply["effective_latency_ms"], 120.0 + applied as f64);
            let periodic = with_lead(
                json!({}),
                &lead,
                &schedule,
                baseline,
                telemetry.session_epoch,
            );
            assert_eq!(periodic["lead_sequence"], reply["sequence"]);
            assert_eq!(
                periodic["effective_latency_ms"],
                reply["effective_latency_ms"]
            );
        }
        assert_eq!(
            telemetry.lead_shift_count, 2,
            "no-op acknowledgements are not timeline changes"
        );
        lead.supported = false;
        let rejected = lead.apply(
            LeadRequest {
                sequence: 5,
                ..LeadRequest::default()
            },
            &mut schedule,
            &mut telemetry,
            baseline,
            &mut [],
        );
        assert_eq!(rejected["event"], "lead_error");
        assert_eq!(rejected["sequence"], 5);
        assert_eq!(lead.sequence, 4);
        assert_eq!(schedule.playout_lead, Duration::from_millis(100));
    }
    #[test]
    fn adaptive_udp_packets_and_sync_keep_sample_identity_and_gain_real_headroom() {
        for rate in [44_100, 48_000] {
            for ptp in [false, true] {
                for offset in [0, -80, 25] {
                    let audio_rx = UdpSocket::bind("127.0.0.1:0").unwrap();
                    let control_rx = UdpSocket::bind("127.0.0.1:0").unwrap();
                    for socket in [&audio_rx, &control_rx] {
                        socket
                            .set_read_timeout(Some(Duration::from_secs(1)))
                            .unwrap();
                    }
                    let mut media = MediaTransport::new(
                        Packetizer::with_rate([7; 32], 100, 0, 1, rate),
                        UdpSocket::bind("127.0.0.1:0").unwrap(),
                        UdpSocket::bind("127.0.0.1:0").unwrap(),
                        control_rx.local_addr().unwrap(),
                    )
                    .unwrap();
                    media.connect_audio(audio_rx.local_addr().unwrap()).unwrap();
                    media.configure_latency(Duration::from_millis(120), true);
                    let start = Instant::now();
                    let base_ns = 1_000_000_000u64;
                    let mut schedule = MediaSchedule::new(start, rate);
                    let common = Duration::from_millis(120 + (-offset).max(0) as u64);
                    let mut buf = [0; 4096];
                    for (index, target) in [120, 140, 160, 200].into_iter().enumerate() {
                        schedule.add_shift_us(
                            (target - 120) * 1_000 - schedule.playout_lead.as_micros() as i64,
                        );
                        let expected_send = start
                            + Duration::from_nanos(
                                index as u64 * FRAMES as u64 * 1_000_000_000 / rate as u64,
                            );
                        assert_eq!(schedule.deadline(), expected_send);
                        let media_time = offset_deadline(
                            schedule.playout_deadline(),
                            common - media.latency(),
                            offset,
                        )
                        .unwrap();
                        let margin = media_time + media.latency() - schedule.deadline();
                        assert_eq!(
                            margin,
                            common
                                + schedule.playout_lead
                                + Duration::from_millis(offset.max(0) as u64)
                                - Duration::from_millis((-offset).max(0) as u64)
                        );
                        let network = offset_time(
                            base_ns
                                + schedule.anchor_shift_ns()
                                + schedule.frames * 1_000_000_000 / rate as u64,
                            common,
                            media.latency(),
                            offset,
                        )
                        .unwrap();
                        media.sync(network, ptp.then_some(7), rate * 120 / 1000, index == 0);
                        let length = control_rx.recv(&mut buf).unwrap();
                        assert_eq!(
                            u32::from_be_bytes(buf[4..8].try_into().unwrap()),
                            index as u32 * FRAMES as u32
                        );
                        if ptp {
                            assert_eq!(length, 28);
                            assert_eq!(
                                u64::from_be_bytes(buf[8..16].try_into().unwrap())
                                    - base_ns
                                    - schedule.frames * 1_000_000_000 / rate as u64,
                                (margin - media.latency()).as_nanos() as u64
                            );
                        } else {
                            assert_eq!(length, 20);
                            assert_eq!(&buf[8..16], &rtp::ntp(network));
                        }
                        media
                            .send(
                                &vec![0; PCM_BYTES],
                                index == 0,
                                expected_send,
                                media_time,
                                &Health::default(),
                            )
                            .unwrap();
                        audio_rx.recv(&mut buf).unwrap();
                        assert_eq!(
                            u16::from_be_bytes(buf[2..4].try_into().unwrap()),
                            100 + index as u16
                        );
                        assert_eq!(
                            u32::from_be_bytes(buf[4..8].try_into().unwrap()),
                            index as u32 * FRAMES as u32
                        );
                        schedule.sent();
                    }
                }
            }
        }
    }
    #[test]
    fn adaptive_standby_restores_initial_target_without_changing_fixed_modes() {
        let mut options = summary_options("", "native_stereo", 0);
        options.latency_ms = 200;
        options.peers[0].profile.latency_ms = Some(200);
        options.adaptive_initial_latency_ms = Some(120);
        reset_adaptive_target(&mut options);
        assert_eq!(options.latency_ms, 200);
        options.adaptive_latency = true;
        reset_adaptive_target(&mut options);
        assert_eq!(options.latency_ms, 120);
        assert_eq!(options.peers[0].profile.latency_ms, Some(120));
    }
    #[test]
    fn alignment_guard_is_not_mistaken_for_an_adaptive_ladder_step() {
        let (mut member, _receiver, server) = local_member();
        member
            .media
            .configure_latency(Duration::from_millis(120), true);
        member.peer.profile.sync_offset_ms = -80;
        let mut schedule = MediaSchedule::new(Instant::now(), 44_100);
        schedule.add_shift(20);
        let telemetry = crate::telemetry::SessionTelemetry::new();
        let lead = LiveLead {
            sequence: 1,
            requested_ms: 20,
            ceiling_ms: 100,
            supported: true,
        };
        let members = vec![member];
        let snapshot = transport_metrics(
            &members,
            &schedule,
            &telemetry,
            Duration::from_millis(200),
            None,
            None,
            &lead,
        );
        assert_eq!(snapshot["baseline_ms"], 120.0);
        assert_eq!(snapshot["effective_latency_ms"], 140.0);
        assert_eq!(snapshot["playout_ms"], 220.0);
        drop(members);
        server.join().unwrap();
    }
    #[test]
    fn group_timing_diagnostic_is_log_only_and_names_every_member() {
        let formats = Formats {
            realtime: Some(1 << 18),
            features: Some(AIRPLAY2_BIT),
            ..Default::default()
        };
        let advertised = Profile::default();
        let homepod = CompatFingerprint::from_evidence(
            formats,
            &advertised,
            &[],
            Some("AudioAccessory5,1"),
            None,
            Some("366.0"),
        );
        // Both members are AirPlay 2 (a legal group); only their solo answers
        // differ, which the group plan resolves to PTP.
        let appletv = CompatFingerprint::from_evidence(
            formats,
            &advertised,
            &[],
            Some("AppleTV6,2"),
            None,
            Some("366.0"),
        );
        let peers: Vec<Peer> = serde_json::from_value(json!([
            {"receiver_id": "left", "host": "127.0.0.2", "profile": {"timing": "auto"}},
            {"receiver_id": "right", "host": "127.0.0.3", "profile": {"timing": "auto"}},
        ]))
        .unwrap();
        let plan = TimingPlan::select(
            &[homepod.timing_evidence(), appletv.timing_evidence()],
            Codec::Auto,
        )
        .unwrap();
        let line = group_timing_diagnostic(&peers, &[homepod.clone(), appletv], plan).unwrap();
        // The content that used to ride on the warning is still available, but
        // as a plain string written to the engine log. It explains the group
        // plan, never a downgrade.
        assert!(line.starts_with("group_timing_plan:"));
        assert!(line.contains("left") && line.contains("right"), "{line}");
        assert!(
            line.contains("one PTP clock domain for the group"),
            "{line}"
        );
        assert!(!line.contains("forced"), "{line}");
        // A group whose members agree on everything has no diagnostic at all.
        let homepod_plan = TimingPlan::select(
            &[homepod.timing_evidence(), homepod.timing_evidence()],
            Codec::Auto,
        )
        .unwrap();
        assert_eq!(
            group_timing_diagnostic(&peers, &[homepod.clone(), homepod], homepod_plan),
            None
        );
    }
    #[test]
    fn a_group_plan_governs_explicit_member_timing() {
        let formats = Formats {
            realtime: Some(1 << 18),
            features: Some(crate::profile::AIRPLAY2_BIT),
            ..Default::default()
        };
        let advertised = Profile::default();
        let appletv = CompatFingerprint::from_evidence(
            formats,
            &advertised,
            &[],
            Some("AppleTV6,2"),
            None,
            Some("366.0"),
        );
        let plan = TimingPlan::new(Timing::Ptp, 2);
        let profile = Profile {
            codec: Codec::Auto,
            transport: Transport::Realtime,
            timing: Timing::Ntp,
            ..Default::default()
        };
        // The group plan wins over the member's explicit request: no second
        // clock domain, and the Apple TV is a PTP participant.
        let compat = appletv
            .negotiate_planned(plan, &profile, 44100, false)
            .unwrap();
        assert!(compat.uses_ptp());
        assert_eq!(compat.timing_protocol(), "PTP");
    }
    // A minimal live-status probe target: one fresh TCP connection, one
    // `GET /info`, one binary plist. `status_flags = None` omits the field.
    fn status_probe_server(status_flags: Option<u64>) -> (SocketAddr, JoinHandle<()>) {
        status_probe_server_seq(vec![status_flags])
    }
    /// Same, but answers one `/info` per entry, each on a fresh connection. A
    /// state-changing verdict (Free / BusyElsewhere) is always confirmed by a
    /// second probe, so tests expecting a verdict need two (or more) entries.
    ///
    /// Q34's TCP liveness probe also connects to this port before each `/info`
    /// probe and closes immediately without a request. Such connections are
    /// skipped: they are not `/info` probes and must not consume a response.
    fn status_probe_server_seq(responses: Vec<Option<u64>>) -> (SocketAddr, JoinHandle<()>) {
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let worker = thread::spawn(move || {
            for status_flags in responses {
                loop {
                    let (socket, _) = listener.accept().unwrap();
                    let mut connection =
                        Connection::from_stream(socket, Cancellation::default()).unwrap();
                    let Ok(request) = connection.read() else {
                        // The liveness probe (or any client that hung up): not
                        // an `/info` request.
                        continue;
                    };
                    assert!(request.first.starts_with("GET /info"));
                    let mut body = Dictionary::new();
                    if let Some(flags) = status_flags {
                        body.insert("statusFlags".into(), Value::Integer(flags.into()));
                    }
                    let mut bytes = Vec::new();
                    Value::Dictionary(body)
                        .to_writer_binary(&mut bytes)
                        .unwrap();
                    let response = format!(
                        "RTSP/1.0 200 OK\r\nCSeq: {}\r\nContent-Length: {}\r\n\r\n",
                        request.headers["cseq"],
                        bytes.len()
                    )
                    .into_bytes();
                    let mut response = response;
                    response.extend(bytes);
                    connection.write(&response).unwrap();
                    break;
                }
            }
        });
        (address, worker)
    }
    // Exercises the production media loop and TEARDOWN against two localhost
    // RTSP/UDP peers, with a synthetic capture queue and no HAP/audio devices.
    fn local_member() -> (Member, UdpSocket, JoinHandle<()>) {
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        receiver
            .set_read_timeout(Some(Duration::from_millis(30)))
            .unwrap();
        let (member, worker, _) = local_member_with(receiver.local_addr().unwrap(), None);
        (member, receiver, worker)
    }
    /// The fake receiver answers every request. With `teardown` it also sends an
    /// unsolicited TEARDOWN after the first volume probe and drops the media
    /// socket, which is the offline shape of "another controller took over".
    fn local_member_with(
        receiver_addr: SocketAddr,
        teardown: Option<std::sync::Arc<std::sync::Mutex<Option<UdpSocket>>>>,
    ) -> (
        Member,
        JoinHandle<()>,
        std::sync::Arc<std::sync::Mutex<Vec<String>>>,
    ) {
        local_member_with_health(receiver_addr, teardown, Health::default())
    }
    fn local_member_with_health(
        receiver_addr: SocketAddr,
        teardown: Option<std::sync::Arc<std::sync::Mutex<Option<UdpSocket>>>>,
        health: Health,
    ) -> (
        Member,
        JoinHandle<()>,
        std::sync::Arc<std::sync::Mutex<Vec<String>>>,
    ) {
        local_member_with_feedback_drop(receiver_addr, teardown, health, false)
    }
    fn local_member_with_feedback_drop(
        receiver_addr: SocketAddr,
        teardown: Option<std::sync::Arc<std::sync::Mutex<Option<UdpSocket>>>>,
        health: Health,
        drop_feedback: bool,
    ) -> (
        Member,
        JoinHandle<()>,
        std::sync::Arc<std::sync::Mutex<Vec<String>>>,
    ) {
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let conn =
            Connection::connect(listener.local_addr().unwrap(), Cancellation::default()).unwrap();
        let (socket, _) = listener.accept().unwrap();
        let methods = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let recorded = methods.clone();
        let worker = thread::spawn(move || {
            let mut connection = Connection::from_stream(socket, Cancellation::default()).unwrap();
            loop {
                let Ok(request) = connection.read() else {
                    break;
                };
                recorded.lock().unwrap().push(
                    request
                        .first
                        .split_whitespace()
                        .next()
                        .unwrap_or("")
                        .to_owned(),
                );
                if drop_feedback && request.first.starts_with("POST /feedback") {
                    break;
                }
                let body = if request.first.starts_with("GET /info") {
                    let value = dictionary(vec![("initialVolume", Value::Real(-15.0))]);
                    let mut bytes = Vec::new();
                    value.to_writer_binary(&mut bytes).unwrap();
                    bytes
                } else {
                    Vec::new()
                };
                let response = format!(
                    "RTSP/1.0 200 OK\r\nCSeq: {}\r\nContent-Length: {}\r\n\r\n",
                    request.headers["cseq"],
                    body.len()
                );
                let mut bytes = response.into_bytes();
                bytes.extend(body);
                if connection.write(&bytes).is_err() {
                    break;
                }
                if request.first.starts_with("TEARDOWN ") {
                    break;
                }
                if let Some(slot) = &teardown
                    && request.first.starts_with("POST /feedback")
                {
                    if connection
                        .write(b"TEARDOWN rtsp://127.0.0.1/test RTSP/1.0\r\nCSeq: 4242\r\n\r\n")
                        .is_err()
                    {
                        break;
                    }
                    drop(slot.lock().unwrap().take());
                    // The status probe, not a media-stopped symptom, is the
                    // verdict path exercised by the sender test.
                    break;
                }
            }
        });
        let audio = UdpSocket::bind("127.0.0.1:0").unwrap();
        let control = UdpSocket::bind("127.0.0.1:0").unwrap();
        let media = MediaTransport::new(
            Packetizer::new([7; 32], 1, 0, 1),
            audio,
            control,
            receiver_addr,
        )
        .unwrap();
        let member = Member {
            compat: crate::profile::CompatProfile::unnegotiated(),
            peer: Peer {
                receiver_id: String::new(),
                host: "127.0.0.1".parse().unwrap(),
                port,
                codecs: vec![],
                profile: crate::profile::Profile::default(),
            },
            conn: std::sync::Arc::new(std::sync::Mutex::new(conn)),
            events: None,
            feedback: None,
            health,
            media,
            uri: "rtsp://127.0.0.1/test".into(),
            remote_control: 0,
            ready: true,
            closed: false,
            session_started: true,
            clock_frozen: false,
            event_addr: None,
            event_key: Vec::new(),
            event_rebuilds: 0,
            next_event_rebuild: Instant::now(),
            preemption: preemption::Tracker::default(),
            preempted: false,
            ownership_uncertain: false,
            ownership_check_started: None,
            probe_task: None,
            event_task: None,
        };
        (member, worker, methods)
    }
    #[test]
    fn feedback_eof_without_teardown_stops_the_whole_group_without_reclaiming() {
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut left, left_worker, left_methods) = local_member_with_feedback_drop(
            receiver.local_addr().unwrap(),
            None,
            Health::default(),
            true,
        );
        let (mut right, right_worker, right_methods) =
            local_member_with(receiver.local_addr().unwrap(), None);
        let (probe_addr, probe_worker) = status_probe_server_seq(vec![
            Some(preemption::RECEIVER_SESSION_ACTIVE),
            Some(preemption::RECEIVER_SESSION_ACTIVE),
        ]);
        left.peer.port = probe_addr.port();
        left.media
            .connect_audio(receiver.local_addr().unwrap())
            .unwrap();
        right
            .media
            .connect_audio(receiver.local_addr().unwrap())
            .unwrap();
        left.start_feedback(Instant::now()).unwrap();
        let mut members = vec![left, right];
        let events = std::sync::Mutex::new(Vec::new());
        let result = send_packets(
            &summary_options("", "native_stereo", 4500),
            &Cancellation::default(),
            &std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0.0f32.to_bits())),
            crate::volume::Control::default(),
            None,
            &crate::standby::Control::default(),
            &Clock::new(),
            None,
            &mut members,
            None,
            &mut preemption::TakeoverCounters::default(),
            &vec![0; FRAMES * 2],
            None,
            &|event| events.lock().unwrap().push(event),
        );
        let fault = result.unwrap_err().downcast::<Fault>().unwrap();
        assert_eq!(fault.code, "ownership_unconfirmed");
        assert!(!fault.retryable);
        assert!(members.iter().all(|m| m.media.metrics.packets_sent > 0));
        assert!(members.iter().all(|m| m.preempted));
        assert!(
            events
                .lock()
                .unwrap()
                .iter()
                .any(|e| e["code"] == "feedback_lost")
        );
        drop(members);
        left_worker.join().unwrap();
        right_worker.join().unwrap();
        probe_worker.join().unwrap();
        for methods in [left_methods, right_methods] {
            assert!(
                !methods
                    .lock()
                    .unwrap()
                    .iter()
                    .any(|m| matches!(m.as_str(), "TEARDOWN" | "SETUP" | "RECORD"))
            );
        }
    }
    #[test]
    fn clock_loss_alone_is_unknown_when_the_active_session_cannot_be_identified() {
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, methods) = local_member_with(receiver.local_addr().unwrap(), None);
        let (addr, probe_worker) =
            status_probe_server_seq(vec![Some(preemption::RECEIVER_SESSION_ACTIVE); 2]);
        member.peer.port = addr.port();
        member
            .health
            .preemption_signal(preemption::Signal::ClockLost);
        let now = Instant::now();
        let mut takeovers = preemption::TakeoverCounters::default();
        assert!(matches!(
            member
                .poll_preemption(&Cancellation::default(), &mut takeovers, now, &|_| {})
                .0,
            PreemptionPoll::Waiting
        ));
        assert!(
            matches!(member.poll_preemption(&Cancellation::default(), &mut takeovers, now + preemption::CONFIRM_DELAY, &|_| {}).0,
            PreemptionPoll::Yield(ref f) if f.code == "ownership_unconfirmed" && !f.retryable)
        );
        drop(member);
        worker.join().unwrap();
        probe_worker.join().unwrap();
        assert!(!methods.lock().unwrap().iter().any(|m| m == "TEARDOWN"));
    }
    #[test]
    fn clock_failure_is_adjudicated_before_cleanup_and_keeps_the_original_fault() {
        let (mut member, receiver, server) = local_member();
        member.compat = crate::profile::CompatProfile::derive(
            &dictionary(vec![]),
            &crate::profile::Profile {
                timing: crate::profile::Timing::Ptp,
                ..Default::default()
            },
            &[],
            44100,
            true,
        )
        .unwrap();
        member
            .media
            .connect_audio(receiver.local_addr().unwrap())
            .unwrap();
        let (addr, probe_server) =
            status_probe_server_seq(vec![Some(preemption::RECEIVER_SESSION_ACTIVE); 2]);
        member.peer.port = addr.port();
        let clock = Clock::new();
        let timer = PtpMaster::from_sockets(
            vec![],
            vec![],
            clock.clone(),
            UdpSocket::bind("127.0.0.1:0").unwrap(),
            UdpSocket::bind("127.0.0.1:0").unwrap(),
        )
        .unwrap();
        let mut members = vec![member];
        let events = std::sync::Mutex::new(Vec::new());
        let result = send_packets(
            &summary_options("", "single", 4000),
            &Cancellation::default(),
            &std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0.0f32.to_bits())),
            crate::volume::Control::default(),
            None,
            &crate::standby::Control::default(),
            &clock,
            None,
            &mut members,
            Some(&timer),
            &mut preemption::TakeoverCounters::default(),
            &vec![0; FRAMES * 2],
            None,
            &|e| events.lock().unwrap().push(e),
        );
        assert_eq!(
            result.unwrap_err().downcast::<Fault>().unwrap().code,
            "ownership_unconfirmed"
        );
        let events = events.lock().unwrap();
        assert!(
            events
                .iter()
                .any(|e| e["code"] == "recovery_original_fault" && e["fault_code"] == "clock_lost")
        );
        assert!(
            events
                .iter()
                .any(|e| e["code"] == "recovery_decision"
                    && e["automatic_recovery_allowed"] == false)
        );
        drop(events);
        drop(members);
        server.join().unwrap();
        probe_server.join().unwrap();
    }
    #[test]
    fn ownership_probe_timeout_and_deadline_never_authorize_recovery() {
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, server, _) = local_member_with(receiver.local_addr().unwrap(), None);
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        member.peer.port = listener.local_addr().unwrap().port();
        let probe_server = thread::spawn(move || {
            let (liveness, _) = listener.accept().unwrap();
            drop(liveness);
            let (socket, _) = listener.accept().unwrap();
            let mut conn = Connection::from_stream(socket, Cancellation::default()).unwrap();
            assert!(conn.read().unwrap().first.starts_with("GET /info"));
            assert!(conn.read().is_err()); // requester closes after its bounded timeout
        });
        member
            .health
            .preemption_signal(preemption::Signal::FeedbackLost);
        let now = Instant::now();
        let mut takeovers = preemption::TakeoverCounters::default();
        assert!(
            matches!(member.poll_preemption(&Cancellation::default(), &mut takeovers, now, &|_| {}).0,
            PreemptionPoll::Yield(ref f) if f.code == "ownership_unconfirmed" && !f.retryable)
        );
        drop(member);
        server.join().unwrap();
        probe_server.join().unwrap();

        let (mut member, _, server) = local_member();
        member.ownership_check_started = Some(now);
        let round = PreemptionRound {
            media_healthy: true,
            signals: vec![],
            evidence: None,
            probe: None,
        };
        assert!(
            matches!(member.apply_preemption(round, &mut takeovers, now + Duration::from_secs(15), &|_| {}, false).0,
            PreemptionPoll::Yield(ref f) if f.code == "ownership_unconfirmed" && !f.retryable)
        );
        drop(member);
        server.join().unwrap();
    }
    #[test]
    fn automatic_start_requires_two_free_rounds_for_every_receiver() {
        for blocked in [None, Some(0), Some(preemption::RECEIVER_SESSION_ACTIVE)] {
            let (left_addr, left_server) = status_probe_server_seq(vec![Some(0), Some(0)]);
            let right_responses = if blocked == Some(0) {
                vec![Some(0), Some(0)]
            } else {
                vec![Some(0), blocked]
            };
            let (right_addr, right_server) = status_probe_server_seq(right_responses);
            let peers: Vec<Peer> = [left_addr, right_addr]
                .into_iter()
                .map(|addr| Peer {
                    receiver_id: String::new(),
                    host: addr.ip(),
                    port: addr.port(),
                    codecs: vec![],
                    profile: Default::default(),
                })
                .collect();
            let result = require_free_receivers(&peers, &Cancellation::default(), &|_| {});
            if blocked == Some(0) {
                assert!(result.is_ok());
            } else {
                assert_eq!(
                    result.unwrap_err().downcast::<Fault>().unwrap().code,
                    "ownership_unconfirmed"
                );
            }
            left_server.join().unwrap();
            right_server.join().unwrap();
        }
    }
    #[test]
    fn a_preempted_member_closes_without_sending_a_teardown() {
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, methods) = local_member_with(receiver.local_addr().unwrap(), None);
        member.preempted = true;
        member.close();
        drop(member);
        worker.join().unwrap();
        assert!(
            !methods.lock().unwrap().iter().any(|m| m == "TEARDOWN"),
            "the receiver that took the session over must not get a TEARDOWN back"
        );
    }
    #[test]
    fn stopping_before_ownership_signals_are_polled_skips_stale_teardown() {
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, methods) = local_member_with(receiver.local_addr().unwrap(), None);
        member
            .health
            .preemption_signal(preemption::Signal::FeedbackLost);
        member.health.take_preemption_signals();
        assert!(member.health.ownership_uncertain());
        member.close();
        drop(member);
        worker.join().unwrap();
        assert!(!methods.lock().unwrap().iter().any(|m| m == "TEARDOWN"));
    }
    #[test]
    fn a_normal_member_close_still_sends_teardown() {
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, methods) = local_member_with(receiver.local_addr().unwrap(), None);
        member.close();
        drop(member);
        worker.join().unwrap();
        assert_eq!(
            methods
                .lock()
                .unwrap()
                .iter()
                .filter(|m| *m == "TEARDOWN")
                .count(),
            1
        );
    }
    #[test]
    fn a_stalled_status_probe_keeps_sending_audio_and_is_cancelled_on_close() {
        use std::io::Read;
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let probe_port = listener.local_addr().unwrap().port();
        let (ready, waiting) = std::sync::mpsc::channel();
        let probe_server = thread::spawn(move || {
            let (mut liveness, _) = listener.accept().unwrap();
            liveness
                .set_read_timeout(Some(Duration::from_secs(3)))
                .unwrap();
            let mut bytes = [0; 1024];
            assert_eq!(liveness.read(&mut bytes).unwrap(), 0);
            let (mut status, _) = listener.accept().unwrap();
            status
                .set_read_timeout(Some(Duration::from_secs(3)))
                .unwrap();
            assert!(status.read(&mut bytes).unwrap() > 0);
            ready.send(()).unwrap();
            // Keep /info unanswered until Member::close cancels the probe.
            assert_eq!(status.read(&mut bytes).unwrap(), 0);
        });
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        receiver
            .set_read_timeout(Some(Duration::from_secs(3)))
            .unwrap();
        let (mut member, worker, _) =
            local_member_with_health(receiver.local_addr().unwrap(), None, Health::default());
        member
            .media
            .connect_audio(receiver.local_addr().unwrap())
            .unwrap();
        member.peer.port = probe_port;
        member
            .health
            .preemption_signal(preemption::Signal::ConnectionClose);
        let cancel = Cancellation::default();
        let started = Instant::now();
        member.prepare_preemption(&cancel, started);
        assert!(started.elapsed() < Duration::from_millis(250));
        waiting.recv_timeout(Duration::from_secs(3)).unwrap();
        for index in 0..20 {
            member.prepare_preemption(&cancel, Instant::now());
            let now = Instant::now();
            member
                .media
                .send(
                    &[0; PCM_BYTES],
                    index == 0,
                    now,
                    now + Duration::from_millis(200),
                    &member.health,
                )
                .unwrap();
        }
        let mut packet = [0; 4096];
        for _ in 0..20 {
            assert!(receiver.recv(&mut packet).unwrap() > 0);
        }
        let stopped = Instant::now();
        member.close();
        assert!(stopped.elapsed() < Duration::from_secs(2));
        drop(member);
        worker.join().unwrap();
        probe_server.join().unwrap();
    }
    #[test]
    fn an_unexpected_teardown_with_a_free_receiver_is_a_transport_fault() {
        // The control symptom only triggers a probe. The fake receiver's fresh
        // `/info` connections say statusFlags=0, so the confirmed verdict is a
        // reclaim on a different connection rather than a fault. Q34 only
        // consults statusFlags while the RTP side is unhealthy, so the poll
        // happens after the media-health window has expired.
        let (probe_addr, probe_server) = status_probe_server_seq(vec![Some(0), Some(0)]);
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, methods) =
            local_member_with_health(receiver.local_addr().unwrap(), None, Health::default());
        member.peer.port = probe_addr.port();
        member
            .health
            .preemption_signal(preemption::Signal::UnexpectedTeardown);
        let emitted = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let seen = emitted.clone();
        let mut counters = preemption::TakeoverCounters::default();
        let now = Instant::now() + Duration::from_secs(3);
        let (poll, healthy) =
            member.poll_preemption(&Cancellation::default(), &mut counters, now, &|json| {
                seen.lock().unwrap().push(json)
            });
        assert!(!healthy, "the media health window has expired");
        assert!(
            matches!(poll, PreemptionPoll::Waiting),
            "the first Free verdict must be confirmed before it is acted on"
        );
        assert!(!member.preempted);
        assert_eq!(
            member.preemption.scheduled(),
            Some(now + preemption::CONFIRM_DELAY)
        );
        {
            let events = emitted.lock().unwrap();
            assert!(
                !events.iter().any(|event| event["code"] == "preempted"),
                "one probe is not enough to reclaim"
            );
        }
        let (poll, _) = member.poll_preemption(
            &Cancellation::default(),
            &mut counters,
            now + preemption::CONFIRM_DELAY,
            &|json| seen.lock().unwrap().push(json),
        );
        assert!(matches!(
            poll,
            PreemptionPoll::Yield(ref fault) if fault.code == "peer_closed"
        ));
        assert!(member.preempted);
        assert!(member.conn.lock().unwrap().is_poisoned());
        assert!(
            member
                .conn
                .lock()
                .unwrap()
                .finish_session("rtsp://127.0.0.1/test")
                .is_err(),
            "a poisoned connection must never carry the reclaim's TEARDOWN"
        );
        {
            let events = emitted.lock().unwrap();
            let evidence = events
                .iter()
                .find(|event| event["code"] == "preemption_evidence")
                .expect("the control symptom must be logged as evidence");
            assert_eq!(evidence["signals"][0], "unexpected_teardown");
            assert!(evidence["policy"].is_string());
            assert!(evidence["rtp_healthy"].is_boolean());
            assert!(evidence["packets_prepared"].is_number());
            assert_eq!(
                evidence["tcp_reachable"], true,
                "Q34's liveness field rides on the evidence line"
            );
            assert_eq!(evidence["tcp_liveness"], "alive");
            let probe = events
                .iter()
                .find(|event| event["code"] == "preemption_probe")
                .expect("the raw statusFlags must be logged");
            assert_eq!(probe["outcome"], "free");
            assert_eq!(probe["status_flags"], 0);
            assert_eq!(probe["tcp_reachable"], true);
            assert_eq!(probe["tcp_liveness"], "alive");
            // Q34 only consults statusFlags while RTP is unhealthy. The
            // TEARDOWN is fresh but the media has stopped, so the session is
            // not ours: this is the displacement path.
            assert_eq!(probe["we_hold_session"], false);
            assert!(probe["rtp_healthy"].is_boolean());
            assert!(probe["policy"].is_string());
            assert!(probe["packets_prepared"].is_number());
            assert_eq!(probe["stereo_partner_unreachable"], false);
            assert!(
                events.iter().all(|event| event["code"] != "preempted"),
                "an idle receiver does not prove another controller took over"
            );
        }
        drop(member);
        worker.join().unwrap();
        assert!(
            !methods.lock().unwrap().iter().any(|m| m == "TEARDOWN"),
            "the displaced session must not send TEARDOWN down the old connection"
        );
        probe_server.join().unwrap();
    }
    #[test]
    fn our_own_active_session_is_never_classified_as_a_takeover() {
        // Q31's hazard, end to end: the receiver reports an active session and
        // that session is ours (healthy media, no TEARDOWN/403). A spurious
        // `Connection: close` on the control channel must not reclaim it.
        let (probe_addr, probe_server) =
            status_probe_server(Some(preemption::RECEIVER_SESSION_ACTIVE));
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, methods) =
            local_member_with_health(receiver.local_addr().unwrap(), None, Health::default());
        member.peer.port = probe_addr.port();
        member
            .health
            .preemption_signal(preemption::Signal::ConnectionClose);
        let emitted = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let seen = emitted.clone();
        let mut counters = preemption::TakeoverCounters::default();
        let (poll, healthy) = member.poll_preemption(
            &Cancellation::default(),
            &mut counters,
            Instant::now(),
            &|json| seen.lock().unwrap().push(json),
        );
        assert!(healthy, "our own media is still flowing");
        assert!(
            matches!(poll, PreemptionPoll::None),
            "our own session must fall through to the non-preemption path, not reclaim"
        );
        assert!(!member.preempted, "no takeover was detected");
        assert!(
            !member.conn.lock().unwrap().is_poisoned(),
            "a healthy session's control connection must stay usable"
        );
        assert_eq!(counters.consecutive(member.peer.host), 0);
        {
            let events = emitted.lock().unwrap();
            assert!(
                !events.iter().any(|event| event["code"] == "preempted"),
                "no preemption warning may be reported for our own session"
            );
            let probe = events
                .iter()
                .find(|event| event["code"] == "preemption_probe")
                .expect("the probe must still be logged");
            assert_eq!(probe["outcome"], "ours_active");
            assert_eq!(probe["we_hold_session"], true);
            assert_eq!(probe["status_flags"], preemption::RECEIVER_SESSION_ACTIVE);
            assert_eq!(probe["rtp_healthy"], true);
            assert_eq!(probe["stereo_partner_unreachable"], false);
        }
        drop(member);
        worker.join().unwrap();
        // The session was never displaced, so it still ends with a normal
        // TEARDOWN instead of being abandoned.
        assert_eq!(
            methods
                .lock()
                .unwrap()
                .iter()
                .filter(|m| *m == "TEARDOWN")
                .count(),
            1
        );
        probe_server.join().unwrap();
    }
    #[test]
    fn udp_writes_do_not_override_a_confirmed_receiver_takeover() {
        let active = preemption::RECEIVER_SESSION_ACTIVE;
        let (probe_addr, probe_server) = status_probe_server_seq(vec![Some(active), Some(active)]);
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, methods) =
            local_member_with_health(receiver.local_addr().unwrap(), None, Health::default());
        member.peer.port = probe_addr.port();
        member
            .health
            .preemption_signal(preemption::Signal::UnexpectedTeardown);
        let mut counters = preemption::TakeoverCounters::default();
        let now = Instant::now();
        let (first, healthy) =
            member.poll_preemption(&Cancellation::default(), &mut counters, now, &|_| {});
        assert!(
            !healthy,
            "UDP acceptance cannot contradict the explicit TEARDOWN"
        );
        assert!(matches!(first, PreemptionPoll::Waiting));
        let (second, _) = member.poll_preemption(
            &Cancellation::default(),
            &mut counters,
            now + preemption::CONFIRM_DELAY,
            &|_| {},
        );
        assert!(
            matches!(second, PreemptionPoll::Yield(ref fault) if fault.code == "preempted" && !fault.retryable)
        );
        assert!(member.preemption.suspended());
        assert!(member.preempted);
        drop(member);
        worker.join().unwrap();
        assert_eq!(
            methods
                .lock()
                .unwrap()
                .iter()
                .filter(|m| *m == "TEARDOWN")
                .count(),
            0
        );
        probe_server.join().unwrap();
    }
    #[test]
    fn ended_session_with_unhealthy_rtp_is_busy_elsewhere() {
        // Only when the RTP side is unhealthy too does the receiver's TEARDOWN
        // turn a session-active probe into BusyElsewhere. The verdict is
        // confirmed once before it is queued.
        let active = preemption::RECEIVER_SESSION_ACTIVE | preemption::STEREO_PARTNER_UNREACHABLE;
        let (probe_addr, probe_server) = status_probe_server_seq(vec![Some(active), Some(active)]);
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, _methods) =
            local_member_with_health(receiver.local_addr().unwrap(), None, Health::default());
        member.peer.port = probe_addr.port();
        // Push the health window past the last successful send so RTP is
        // unhealthy, then let the receiver end the session right now.
        let now = Instant::now() + Duration::from_secs(3);
        member
            .health
            .preemption_signal(preemption::Signal::UnexpectedTeardown);
        let emitted = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let seen = emitted.clone();
        let mut counters = preemption::TakeoverCounters::default();
        let (first, healthy) =
            member.poll_preemption(&Cancellation::default(), &mut counters, now, &|json| {
                seen.lock().unwrap().push(json)
            });
        assert!(!healthy, "the media health window has expired");
        assert!(
            matches!(first, PreemptionPoll::Waiting),
            "the first busy verdict is confirmed before queueing"
        );
        assert!(!member.preempted);
        assert!(!member.conn.lock().unwrap().is_poisoned());
        let (poll, _) = member.poll_preemption(
            &Cancellation::default(),
            &mut counters,
            now + preemption::CONFIRM_DELAY,
            &|json| seen.lock().unwrap().push(json),
        );
        assert!(
            matches!(poll, PreemptionPoll::Yield(ref fault) if fault.code == "preempted" && !fault.retryable)
        );
        assert_eq!(member.preemption.scheduled(), None);
        {
            let events = emitted.lock().unwrap();
            let probe = events
                .iter()
                .find(|event| event["code"] == "preemption_probe")
                .expect("the probe must be logged");
            assert_eq!(probe["outcome"], "busy_elsewhere");
            assert_eq!(probe["we_hold_session"], false);
            assert_eq!(probe["tcp_reachable"], true);
            // Bit 14 is telemetry only and must not change the verdict.
            assert_eq!(probe["stereo_partner_unreachable"], true);
            assert!(events.iter().all(|event| event["code"] != "preempted"));
            assert!(
                events.iter().all(|event| event["code"] != "busy_exhausted"),
                "queueing is unbounded: there is no forced-reclaim escape any more"
            );
        }
        drop(member);
        worker.join().unwrap();
        probe_server.join().unwrap();
    }
    #[test]
    fn missing_status_flags_require_manual_reconnect() {
        let (probe_addr, probe_server) = status_probe_server(None);
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, _methods) =
            local_member_with_health(receiver.local_addr().unwrap(), None, Health::default());
        member.peer.port = probe_addr.port();
        member
            .health
            .preemption_signal(preemption::Signal::ConnectionClose);
        let emitted = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let seen = emitted.clone();
        let mut counters = preemption::TakeoverCounters::default();
        let (poll, _) = member.poll_preemption(
            &Cancellation::default(),
            &mut counters,
            // Q34 only consults statusFlags while RTP is unhealthy; fail-open
            // applies on that branch.
            Instant::now() + Duration::from_secs(3),
            &|json| seen.lock().unwrap().push(json),
        );
        assert!(matches!(
            poll,
            PreemptionPoll::Yield(ref fault) if fault.code == "ownership_unconfirmed" && !fault.retryable
        ));
        let events = emitted.lock().unwrap();
        let probe = events
            .iter()
            .find(|event| event["code"] == "preemption_probe")
            .expect("probe outcome must be logged");
        assert_eq!(probe["outcome"], "unknown");
        assert!(probe["status_flags"].is_null());
        assert_eq!(probe["tcp_reachable"], true);
        drop(events);
        drop(member);
        worker.join().unwrap();
        probe_server.join().unwrap();
    }
    #[test]
    fn healthy_media_with_a_reachable_receiver_ignores_a_free_status_flags() {
        // Q34's combine table, end to end: `(media healthy, Alive) => OursActive`
        // even when statusFlags says no session is active. The TCP probe is the
        // receiver-side liveness dimension; a clean `statusFlags=0` alone does
        // not displace a session whose media is still flowing.
        let (probe_addr, probe_server) = status_probe_server(Some(0));
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, methods) =
            local_member_with_health(receiver.local_addr().unwrap(), None, Health::default());
        member.peer.port = probe_addr.port();
        member
            .health
            .preemption_signal(preemption::Signal::ConnectionClose);
        let emitted = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let seen = emitted.clone();
        let mut counters = preemption::TakeoverCounters::default();
        let (poll, healthy) = member.poll_preemption(
            &Cancellation::default(),
            &mut counters,
            Instant::now(),
            &|json| seen.lock().unwrap().push(json),
        );
        assert!(healthy);
        assert!(
            matches!(poll, PreemptionPoll::None),
            "healthy media + a reachable receiver is OursActive, not a reclaim"
        );
        assert!(!member.preempted);
        assert!(!member.conn.lock().unwrap().is_poisoned());
        {
            let events = emitted.lock().unwrap();
            let probe = events
                .iter()
                .find(|event| event["code"] == "preemption_probe")
                .expect("the probe must still be logged");
            assert_eq!(probe["outcome"], "ours_active");
            assert_eq!(probe["status_flags"], 0, "the raw flags are still logged");
            assert_eq!(probe["tcp_reachable"], true);
            assert!(events.iter().all(|event| event["code"] != "preempted"));
        }
        drop(member);
        worker.join().unwrap();
        assert_eq!(
            methods
                .lock()
                .unwrap()
                .iter()
                .filter(|m| *m == "TEARDOWN")
                .count(),
            1,
            "the session was never displaced and closes normally"
        );
        probe_server.join().unwrap();
    }
    #[test]
    fn an_unreachable_control_port_requires_manual_reconnect_without_claiming_takeover() {
        // Bind and drop a listener to get a port with no receiver listening.
        // The TCP liveness probe proves the receiver cannot serve us, so the
        // verdict is gone: a reclaim, never busy and never a generic transport
        // fault. Which raw state it is depends on the local stack (a returned
        // RST is `refused`, a dropped SYN is `dead`); both are the same action
        // and both are Q42's "not serving us".
        let probe = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let dead = probe.local_addr().unwrap();
        drop(probe);
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, _methods) =
            local_member_with_health(receiver.local_addr().unwrap(), None, Health::default());
        member.peer.port = dead.port();
        member
            .health
            .preemption_signal(preemption::Signal::ConnectionClose);
        let emitted = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let seen = emitted.clone();
        let mut counters = preemption::TakeoverCounters::default();
        let (poll, _) = member.poll_preemption(
            &Cancellation::default(),
            &mut counters,
            Instant::now(),
            &|json| seen.lock().unwrap().push(json),
        );
        assert!(
            matches!(poll, PreemptionPoll::Yield(ref fault) if fault.code == "ownership_unconfirmed" && !fault.retryable),
            "an unreachable receiver must not authorize automatic recovery"
        );
        assert_eq!(
            counters.consecutive(member.peer.host),
            0,
            "a receiver that cannot serve us is not a takeover cycle"
        );
        let events = emitted.lock().unwrap();
        let probe = events
            .iter()
            .find(|event| event["code"] == "preemption_probe")
            .expect("the unreachable receiver must be logged");
        assert_eq!(probe["outcome"], "gone");
        assert_eq!(probe["tcp_reachable"], false);
        assert_eq!(probe["liveness_verdict"], "dead");
        assert!(
            matches!(probe["tcp_liveness"].as_str(), Some("dead" | "refused")),
            "the raw probe state is logged, got {}",
            probe["tcp_liveness"]
        );
        assert!(probe["message"].as_str().unwrap().contains("not reachable"));
        assert!(
            events.iter().all(|event| event["code"] != "preempted"),
            "an unreachable device is not reported as another controller's takeover"
        );
        drop(events);
        drop(member);
        worker.join().unwrap();
    }
    /// One synthetic verdict round, so the escalation and group-wide rules can
    /// be tested without depending on which errno the local stack returns.
    fn synthetic_round(
        media_healthy: bool,
        liveness: preemption::ReceiverLiveness,
        result: Result<preemption::ProbeReport>,
        signals: Vec<preemption::Signal>,
    ) -> PreemptionRound {
        PreemptionRound {
            media_healthy,
            signals,
            evidence: None,
            probe: Some(RoundProbe {
                liveness,
                result,
                we_hold_session: true,
            }),
        }
    }
    #[test]
    fn a_refused_control_port_is_logged_as_refused_and_never_as_dead() {
        // A returned RST is not a dead device (Q42): the host is alive, it just
        // is not serving the control port. The action is the same fail-open
        // reclaim; the reported state must not be.
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, _methods) =
            local_member_with_health(receiver.local_addr().unwrap(), None, Health::default());
        let emitted = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let seen = emitted.clone();
        let mut counters = preemption::TakeoverCounters::default();
        let round = synthetic_round(
            true,
            preemption::ReceiverLiveness::Refused,
            Ok(preemption::ProbeReport {
                outcome: preemption::ProbeOutcome::Gone,
                status_flags: None,
            }),
            vec![preemption::Signal::ConnectionClose],
        );
        let (poll, _) = member.apply_preemption(
            round,
            &mut counters,
            Instant::now(),
            &|json| seen.lock().unwrap().push(json),
            false,
        );
        assert!(
            matches!(poll, PreemptionPoll::Yield(ref fault) if fault.code == "ownership_unconfirmed" && !fault.retryable)
        );
        assert_eq!(
            counters.consecutive(member.peer.host),
            0,
            "refused is not a takeover cycle"
        );
        let events = emitted.lock().unwrap();
        let probe = events
            .iter()
            .find(|event| event["code"] == "preemption_probe")
            .expect("the refused receiver must be logged");
        assert_eq!(probe["tcp_liveness"], "refused");
        assert_eq!(probe["liveness_verdict"], "dead");
        assert_eq!(probe["tcp_reachable"], false);
        assert_eq!(probe["outcome"], "gone");
        let message = probe["message"].as_str().unwrap();
        assert!(message.contains("refused"), "{message}");
        assert!(message.contains("alive"), "{message}");
        assert!(
            !message.contains("device is gone"),
            "a refusal must never be labelled dead: {message}"
        );
        let evidence = events
            .iter()
            .find(|event| event["code"] == "preemption_evidence")
            .expect("the evidence line keeps the same state");
        assert_eq!(evidence["tcp_liveness"], "refused");
        assert_eq!(evidence["liveness_verdict"], "dead");
        assert_eq!(evidence["local_network_suspected"], false);
        assert!(
            events.iter().all(|event| event["code"] != "preempted"),
            "refused is not reported as another controller's takeover"
        );
        drop(events);
        drop(member);
        worker.join().unwrap();
    }
    #[test]
    fn an_escalating_unknown_liveness_pauses_automatic_recovery_after_patience() {
        // Q42: `Unknown` is "temporarily unknown", not "mine forever". Media is
        // healthy — the UDP sends keep succeeding even when the device is
        // unreachable, which is exactly why MEDIA_HEALTH_WINDOW cannot decide
        // this on its own and the escalation timer has to.
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, _methods) =
            local_member_with_health(receiver.local_addr().unwrap(), None, Health::default());
        let emitted = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let seen = emitted.clone();
        let mut counters = preemption::TakeoverCounters::default();
        let now = Instant::now();
        let unknown_round = || {
            synthetic_round(
                true,
                preemption::ReceiverLiveness::Unknown,
                Ok(preemption::ProbeReport {
                    outcome: preemption::ProbeOutcome::OursActive,
                    status_flags: None,
                }),
                Vec::new(),
            )
        };
        for step in 0..preemption::LIVENESS_PATIENCE - 1 {
            let at = now + Duration::from_secs(u64::from(step) * 3);
            let (poll, _) = member.apply_preemption(
                unknown_round(),
                &mut counters,
                at,
                &|json| seen.lock().unwrap().push(json),
                false,
            );
            assert!(
                matches!(poll, PreemptionPoll::None),
                "unknown is not a death yet"
            );
            assert_eq!(member.preemption.liveness_failures(), step + 1);
            assert_eq!(
                member.preemption.scheduled(),
                Some(at + preemption::LIVENESS_RETRY),
                "another liveness sample is scheduled while the verdict is undecided"
            );
            assert!(!member.preempted);
        }
        let at = now + Duration::from_secs(u64::from(preemption::LIVENESS_PATIENCE) * 3);
        let (poll, _) = member.apply_preemption(
            unknown_round(),
            &mut counters,
            at,
            &|json| seen.lock().unwrap().push(json),
            false,
        );
        assert!(
            matches!(poll, PreemptionPoll::Yield(ref fault) if fault.code == "ownership_unconfirmed" && !fault.retryable),
            "the fifth consecutive unknown escalates to a reclaim"
        );
        assert_eq!(
            counters.consecutive(member.peer.host),
            0,
            "an escalated unknown is not a takeover cycle"
        );
        assert!(member.preempted);
        let events = emitted.lock().unwrap();
        let last = events
            .iter()
            .rfind(|event| event["code"] == "preemption_probe")
            .expect("the escalated probe is logged");
        assert_eq!(last["tcp_liveness"], "unknown");
        assert_eq!(last["liveness_verdict"], "dead");
        assert_eq!(last["liveness_failures"], preemption::LIVENESS_PATIENCE);
        assert_eq!(last["outcome"], "gone");
        drop(events);
        drop(member);
        worker.join().unwrap();
    }
    #[test]
    fn a_group_wide_liveness_failure_does_not_mark_the_device_dead() {
        // Every receiver failed at once and the gateway reference did not clear
        // them, so the failure belongs to our own side (Q42 + Q46). The dead
        // verdict is downgraded to unknown and a healthy session keeps playing
        // instead of being reclaimed.
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, _methods) =
            local_member_with_health(receiver.local_addr().unwrap(), None, Health::default());
        let emitted = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let seen = emitted.clone();
        let mut counters = preemption::TakeoverCounters::default();
        let round = synthetic_round(
            true,
            preemption::ReceiverLiveness::Dead,
            Ok(preemption::ProbeReport {
                outcome: preemption::ProbeOutcome::Gone,
                status_flags: None,
            }),
            Vec::new(),
        );
        let now = Instant::now();
        let (poll, media_healthy) = member.apply_preemption(
            round,
            &mut counters,
            now,
            &|json| seen.lock().unwrap().push(json),
            true,
        );
        assert!(
            matches!(poll, PreemptionPoll::None),
            "a group-wide failure must not reclaim any single device"
        );
        assert!(media_healthy);
        assert!(!member.preempted);
        assert!(!member.conn.lock().unwrap().is_poisoned());
        assert_eq!(
            member.preemption.liveness_failures(),
            0,
            "a local round is not evidence about this device"
        );
        assert_eq!(
            member.preemption.scheduled(),
            Some(now + preemption::LIVENESS_RETRY),
            "the reference is re-evaluated: a recovered gateway has to end the suppression"
        );
        let events = emitted.lock().unwrap();
        let probe = events
            .iter()
            .find(|event| event["code"] == "preemption_probe")
            .expect("the local-suspect round is still logged");
        assert_eq!(probe["tcp_liveness"], "dead", "the raw probe is kept");
        assert_eq!(probe["liveness_verdict"], "unknown", "the verdict is local");
        assert_eq!(probe["local_network_suspected"], true);
        assert_eq!(probe["outcome"], "ours_active");
        assert!(
            probe["message"].as_str().unwrap().contains("local network"),
            "the line must say it is a local problem, got {}",
            probe["message"]
        );
        drop(events);
        drop(member);
        worker.join().unwrap();
    }
    #[test]
    fn the_local_network_warning_names_every_failed_probe_and_the_reference() {
        let event = preemption_local_network(
            2,
            &[
                preemption::ReceiverLiveness::Dead,
                preemption::ReceiverLiveness::Unknown,
            ],
            preemption::AllFailed::LocalNetwork,
            Some(preemption::GatewayReachability::Silent),
            Duration::from_secs(4),
        );
        assert_eq!(event["code"], "preemption_local_network");
        assert_eq!(event["channel"], "preemption");
        assert_eq!(event["receivers"], 2);
        assert_eq!(event["probes"], json!(["dead", "unknown"]));
        assert_eq!(event["verdict"], "local_network");
        assert_eq!(event["gateway"], "unreachable");
        assert_eq!(event["gateway_probed"], true);
        assert_eq!(event["all_failed_for_ms"], 4_000);
        assert!(event["message"].as_str().unwrap().contains("local network"));
        // No default route at all: the fallback is time-boxed and the message
        // says so instead of pretending a reference answered.
        let unproven = preemption_local_network(
            2,
            &[
                preemption::ReceiverLiveness::Dead,
                preemption::ReceiverLiveness::Dead,
            ],
            preemption::AllFailed::LocalNetworkUnproven,
            None,
            Duration::from_secs(3),
        );
        assert_eq!(unproven["verdict"], "local_network_unproven");
        assert_eq!(unproven["gateway"], json!(null));
        assert_eq!(unproven["gateway_probed"], false);
        assert!(
            unproven["message"]
                .as_str()
                .unwrap()
                .contains("no default gateway"),
            "got {}",
            unproven["message"]
        );
    }
    #[test]
    fn the_all_failed_episode_warns_once_and_re_arms_after_recovery() {
        let base = Instant::now();
        let mut episode = AllFailedEpisode::new();
        let discover = || Some("10.0.0.1".parse().unwrap());
        let silent = |_gateway| preemption::GatewayReachability::Silent;
        let dead = [
            preemption::ReceiverLiveness::Dead,
            preemption::ReceiverLiveness::Dead,
        ];
        let alive = [
            preemption::ReceiverLiveness::Alive,
            preemption::ReceiverLiveness::Dead,
        ];
        let (suppress, warning) = episode.observe(&dead, 2, base, &discover, &silent);
        assert!(suppress);
        let event = warning.expect("the first suppressed round warns");
        assert_eq!(event["verdict"], "local_network");
        assert_eq!(event["gateway"], "unreachable");
        assert_eq!(event["all_failed_for_ms"], 0);
        // The same episode on a later round: still suppressed, no second line.
        let (suppress, warning) =
            episode.observe(&dead, 2, base + Duration::from_secs(3), &discover, &silent);
        assert!(suppress);
        assert!(
            warning.is_none(),
            "the warning is a state change, not a heartbeat"
        );
        // One receiver answers again: the episode ends.
        let (suppress, warning) =
            episode.observe(&alive, 2, base + Duration::from_secs(6), &discover, &silent);
        assert!(!suppress);
        assert!(warning.is_none());
        // A later all-failed run is a new episode and warns again.
        let (suppress, warning) =
            episode.observe(&dead, 2, base + Duration::from_secs(9), &discover, &silent);
        assert!(suppress);
        assert_eq!(
            warning.expect("a new episode warns again")["all_failed_for_ms"],
            0
        );
    }
    #[test]
    fn a_partial_round_never_resets_the_all_failed_episode() {
        let base = Instant::now();
        let mut episode = AllFailedEpisode::new();
        let discover = || Some("10.0.0.1".parse().unwrap());
        let silent = |_gateway| preemption::GatewayReachability::Silent;
        let dead = [
            preemption::ReceiverLiveness::Dead,
            preemption::ReceiverLiveness::Dead,
        ];
        assert!(
            episode
                .observe(&dead, 2, base, &discover, &silent)
                .1
                .is_some()
        );
        // Only one member was due this round: no group judgement, and the
        // episode clock has to survive it.
        let (suppress, warning) = episode.observe(
            &dead[..1],
            2,
            base + Duration::from_secs(3),
            &discover,
            &silent,
        );
        assert!(!suppress);
        assert!(warning.is_none());
        assert_eq!(episode.since, Some(base), "the episode clock was not reset");
        let (suppress, warning) =
            episode.observe(&dead, 2, base + Duration::from_secs(9), &discover, &silent);
        assert!(suppress);
        assert!(warning.is_none());
    }
    #[test]
    fn the_all_failed_fallback_expires_after_the_window_without_a_gateway() {
        let base = Instant::now();
        let mut episode = AllFailedEpisode::new();
        // No default route at all: Q46's last resort applies.
        let discover = || None;
        let unanswered = |_gateway| preemption::GatewayReachability::Silent;
        let dead = [
            preemption::ReceiverLiveness::Dead,
            preemption::ReceiverLiveness::Dead,
        ];
        let (suppress, warning) = episode.observe(&dead, 2, base, &discover, &unanswered);
        assert!(suppress);
        assert_eq!(
            warning.expect("the unproven suppression warns")["verdict"],
            "local_network_unproven"
        );
        let (suppress, _) = episode.observe(
            &dead,
            2,
            base + preemption::GATEWAY_FALLBACK_WINDOW - Duration::from_millis(1),
            &discover,
            &unanswered,
        );
        assert!(
            suppress,
            "inside the window the failure is still suppressed"
        );
        let (suppress, warning) = episode.observe(
            &dead,
            2,
            base + preemption::GATEWAY_FALLBACK_WINDOW,
            &discover,
            &unanswered,
        );
        assert!(
            !suppress,
            "the fallback window is a limit, not another guess"
        );
        assert!(warning.is_none());
    }
    #[test]
    fn a_reachable_gateway_makes_the_episode_reclaim_instead_of_warning() {
        let base = Instant::now();
        let mut episode = AllFailedEpisode::new();
        let discover = || Some("10.0.0.1".parse().unwrap());
        let answered = |_gateway| preemption::GatewayReachability::Answered;
        let dead = [
            preemption::ReceiverLiveness::Dead,
            preemption::ReceiverLiveness::Dead,
        ];
        let (suppress, warning) = episode.observe(&dead, 2, base, &discover, &answered);
        assert!(!suppress, "the devices really are gone");
        assert!(warning.is_none(), "no local-network warning");
        // The per-device verdicts now stand, so the loop reclaims them.
        for probe in dead {
            let mut tracker = preemption::Tracker::default();
            let note = tracker.observe_liveness(preemption::LivenessSample::Probe(probe), base);
            assert_eq!(note.verdict, preemption::Verdict::Dead);
        }
    }
    #[test]
    fn setup_rejections_are_terminal_while_running_feedback_rejections_degrade() {
        // Q16 boundary: 401/403/454 before streaming starts cannot be fixed on
        // this connection, so they stay terminal authentication failures. The
        // classifier is never run on the preemption path.
        for status in [401, 403, 454] {
            let error = anyhow::anyhow!("SETUP rtsp://127.0.0.1/test: RTSP/1.0 {status} Rejected");
            let fault = Fault::from_setup_error("127.0.0.1", &error);
            assert_eq!(fault.channel, "setup");
            assert_eq!(fault.code, "authentication_failed");
            assert!(!fault.retryable);
            assert_ne!(fault.code, "preempted");
        }
        // Q34: the same status on a *running* `/feedback` probe is a different
        // path — a retryable control-plane degradation, not an auth fault.
        let feedback = Fault::feedback_rejected("127.0.0.1", 403, "RTSP/1.0 403 Forbidden");
        assert_eq!(feedback.channel, "feedback");
        assert_eq!(feedback.code, "feedback_rejected");
        assert!(feedback.retryable);
        assert!(feedback.message.contains("keeping healthy media session"));
        // Non-status setup failures keep their existing generic classification.
        let generic =
            Fault::from_setup_error("127.0.0.1", &anyhow::anyhow!("missing stream response"));
        assert_eq!(generic.code, "protocol_error");
        assert!(!generic.retryable);
    }
    #[test]
    fn stalled_sender_reanchors_instead_of_rebuilding_and_keeps_seq_linear() {
        // Protocol-level version of the roadmap acceptance check: after a 300 ms
        // stall the sender must move its timeline, not tear the session down.
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        receiver
            .set_read_timeout(Some(Duration::from_millis(200)))
            .unwrap();
        let audio = UdpSocket::bind("127.0.0.1:0").unwrap();
        let control = UdpSocket::bind("127.0.0.1:0").unwrap();
        let mut media = MediaTransport::new(
            Packetizer::new([7; 32], 100, 0, 1),
            audio,
            control,
            receiver.local_addr().unwrap(),
        )
        .unwrap();
        media.connect_audio(receiver.local_addr().unwrap()).unwrap();
        media.configure_latency(Duration::from_millis(200), false);
        let rate = 44_100u32;
        let start = Instant::now();
        let mut schedule = MediaSchedule::new(start, rate);
        let pcm = vec![0u8; rtp::PCM_BYTES];
        let mut sends = 0usize;
        let mut pending_silence = 0u64;
        let mut stalls = 0usize;
        for step in 0..40 {
            if step == 10 || step == 25 {
                // Simulate the sender thread being descheduled.
                thread::sleep(Duration::from_millis(300));
                stalls += 1;
            }
            let now = Instant::now();
            let earliest_render = schedule.deadline() + Duration::from_millis(200);
            match schedule.late_action(now, earliest_render) {
                crate::transport::LateAction::OnTime => {}
                crate::transport::LateAction::Silence(fill) => {
                    // Covering the gap in place is still the cheapest recovery.
                    assert_eq!(
                        schedule.recovery_silence(now, earliest_render).unwrap(),
                        fill
                    );
                    schedule.recoveries += 1;
                    pending_silence += fill;
                }
                crate::transport::LateAction::Reanchor => {
                    // The pre-change code returned `timeline_expired` here.
                    assert!(
                        schedule.recovery_silence(now, earliest_render).is_err(),
                        "a session-wide expiry is what this test avoids"
                    );
                    let shift = schedule.reanchor(now).expect("re-anchor budget");
                    assert!(!shift.is_zero());
                }
            }
            for _ in 0..pending_silence {
                while Instant::now() < schedule.deadline() {
                    thread::sleep(Duration::from_micros(200));
                }
                media
                    .send(
                        &pcm,
                        false,
                        Instant::now(),
                        schedule.deadline(),
                        &Health::default(),
                    )
                    .expect("concealment must not fail the session");
                schedule.sent();
                sends += 1;
            }
            pending_silence = 0;
            while Instant::now() < schedule.deadline() {
                thread::sleep(
                    schedule
                        .deadline()
                        .saturating_duration_since(Instant::now())
                        .min(Duration::from_millis(1)),
                );
            }
            media
                .send(
                    &pcm,
                    step == 0,
                    Instant::now(),
                    schedule.deadline(),
                    &Health::default(),
                )
                .expect("a stalled sender must recover without a fresh session");
            schedule.sent();
            sends += 1;
        }
        assert_eq!(stalls, 2);
        assert!(
            schedule.reanchored >= 1,
            "the stall must have been absorbed by a re-anchor"
        );
        assert!(schedule.shift > Duration::ZERO);
        // Every received packet is contiguous and steps exactly one frame block:
        // re-anchoring moves the anchor, never the sequence.
        let mut packets = Vec::new();
        let mut buffer = [0u8; 1600];
        while let Ok(n) = receiver.recv(&mut buffer) {
            packets.push(buffer[..n].to_vec());
        }
        assert_eq!(packets.len(), sends);
        for pair in packets.windows(2) {
            let seq = u16::from_be_bytes([pair[0][2], pair[0][3]]);
            let next = u16::from_be_bytes([pair[1][2], pair[1][3]]);
            assert_eq!(next, seq.wrapping_add(1), "sequence must stay linear");
            let rtp = u32::from_be_bytes(pair[0][4..8].try_into().unwrap());
            let next_rtp = u32::from_be_bytes(pair[1][4..8].try_into().unwrap());
            assert_eq!(next_rtp.wrapping_sub(rtp), rtp::FRAMES as u32);
        }
        assert_eq!(schedule.skipped_packets, 0, "no slots were dropped");
    }

    #[test]
    fn silence_releases_both_members_and_activity_resumes_current_audio() {
        silence_release(false);
    }
    #[test]
    fn software_stereo_releases_both_members_and_activity_resumes_current_audio() {
        silence_release(true);
    }
    fn silence_release(software: bool) {
        let options: ProbeOptions = serde_json::from_value(json!({"peers":[{"host":"127.0.0.1"}],"source":"loopback","duration_ms":0,"latency_ms":120,"gain":0.0})).unwrap();
        let options = ProbeOptions {
            group_mode: if software {
                "software_stereo"
            } else {
                "native_stereo"
            }
            .into(),
            ..options
        };
        // Accelerated policy used only with synthetic audio and localhost peers.
        let standby = crate::standby::Control::new(crate::standby::Settings {
            enabled: true,
            silence_seconds: 0.08,
        });
        let source = crate::live::tests::source();
        let gain = std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0.0f32.to_bits()));
        for _ in 0..2 {
            let (a, rx_a, server_a) = local_member();
            let (b, rx_b, server_b) = local_member();
            let mut members = vec![a, b];
            if software {
                members[0].peer.profile.channel = crate::profile::Channel::Left;
                members[1].peer.profile.channel = crate::profile::Channel::Right;
            }
            // Connect each member to its own UDP peer.
            members[0]
                .media
                .connect_audio(rx_a.local_addr().unwrap())
                .unwrap();
            members[1]
                .media
                .connect_audio(rx_b.local_addr().unwrap())
                .unwrap();
            let began = Instant::now();
            let mut takeovers = preemption::TakeoverCounters::default();
            let outcome = thread::scope(|scope| {
                scope.spawn(|| {
                    for _ in 0..12 {
                        crate::live::tests::feed(&source, 0.02);
                        thread::sleep(Duration::from_millis(10));
                    }
                });
                send_packets(
                    &options,
                    &Cancellation::default(),
                    &gain,
                    crate::volume::Control::default(),
                    Some(&source),
                    &standby,
                    &Clock::new(),
                    None,
                    &mut members,
                    None,
                    &mut takeovers,
                    &[],
                    None,
                    &|_| {},
                )
            })
            .unwrap();
            let StreamEnd::Silence(at) = outcome else {
                panic!("transport did not enter standby")
            };
            assert!(
                began.elapsed() >= Duration::from_millis(150),
                "gain zero must not conceal source activity"
            );
            assert!(members.iter().all(|m| m.closed && m.feedback.is_none()));
            server_a.join().unwrap();
            server_b.join().unwrap();
            for receiver in [&rx_a, &rx_b] {
                let mut data = [0u8; 4096];
                let mut packets = 0;
                while receiver.recv(&mut data).is_ok() {
                    packets += 1;
                }
                assert!(packets > 0, "both receivers must get media before standby");
                assert!(receiver.recv(&mut data).is_err(), "standby must stop media");
            }
            assert!(!standby.should_resume(source.last_audio(), at));
            crate::live::tests::feed(&source, 0.02);
            assert!(standby.should_resume(source.last_audio(), at));
            source.discard_stale();
        }
    }
    /// A session shaped for the summary tests: one loopback peer, no audio
    /// device, and an optional IPC identity (empty means "do not touch the file").
    fn summary_options(report_id: &str, group_mode: &str, duration_ms: u32) -> ProbeOptions {
        let mut options: ProbeOptions = serde_json::from_value(json!({
            "peers": [{"host": "127.0.0.1"}],
            "source": "loopback",
            "duration_ms": duration_ms,
            "latency_ms": 120,
            "gain": 0.0
        }))
        .unwrap();
        options.group_mode = group_mode.into();
        options.report_session_id = report_id.into();
        options
    }

    fn summary_events(events: &std::sync::Arc<std::sync::Mutex<Vec<Json>>>) -> Vec<Json> {
        events
            .lock()
            .unwrap()
            .iter()
            .filter(|event| event["event"] == "session_summary")
            .cloned()
            .collect()
    }

    #[test]
    fn a_buffered_link_expires_instead_of_shifting_an_unpublished_anchor() {
        buffered_expiry(false);
    }
    #[test]
    fn forced_buffered_playback_also_rebuilds_instead_of_shifting_the_anchor() {
        buffered_expiry(true);
    }
    fn buffered_expiry(forced: bool) {
        use std::io::Read;
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let tcp = thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            let mut bytes = [0; 4096];
            while socket.read(&mut bytes).unwrap_or(0) > 0 {}
        });
        let (mut member, _receiver, server) = local_member();
        member.media.connect_buffered(address).unwrap();
        let mut members = vec![member];
        let mut options = summary_options("", "single", 0);
        options.force_latency = forced;
        let gain = std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0.0f32.to_bits()));
        let events = std::sync::Mutex::new(Vec::new());
        let outcome = send_packets(
            &options,
            &Cancellation::default(),
            &gain,
            crate::volume::Control::default(),
            None,
            &crate::standby::Control::default(),
            &Clock::new(),
            None,
            &mut members,
            None,
            &mut preemption::TakeoverCounters::default(),
            &[],
            None,
            &|event| {
                if event["event"] == "streaming" {
                    thread::sleep(Duration::from_millis(200));
                }
                events.lock().unwrap().push(event);
            },
        );
        let error = outcome.unwrap_err();
        assert_eq!(
            error.downcast_ref::<Fault>().unwrap().code,
            "timeline_expired"
        );
        assert!(
            events
                .lock()
                .unwrap()
                .iter()
                .all(|event| event["code"] != "timeline_reanchored")
        );
        drop(members);
        server.join().unwrap();
        tcp.join().unwrap();
    }
    #[test]
    fn forced_udp_expires_without_moving_the_timeline_after_a_large_stall() {
        let (mut member, receiver, server) = local_member();
        member
            .media
            .connect_audio(receiver.local_addr().unwrap())
            .unwrap();
        let mut members = vec![member];
        let mut options = summary_options("", "single", 0);
        options.force_latency = true;
        let gain = std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0.0f32.to_bits()));
        let events = std::sync::Mutex::new(Vec::new());
        let result = send_packets(
            &options,
            &Cancellation::default(),
            &gain,
            crate::volume::Control::default(),
            None,
            &crate::standby::Control::default(),
            &Clock::new(),
            None,
            &mut members,
            None,
            &mut preemption::TakeoverCounters::default(),
            &[],
            None,
            &|event| {
                if event["event"] == "streaming" {
                    thread::sleep(Duration::from_millis(200));
                }
                events.lock().unwrap().push(event);
            },
        );
        assert_eq!(
            result.unwrap_err().downcast_ref::<Fault>().unwrap().code,
            "timeline_expired"
        );
        let events = events.lock().unwrap();
        assert!(
            events
                .iter()
                .all(|e| e["code"] != "timeline_reanchored" && e["event"] != "lead_changed")
        );
        let summary = events
            .iter()
            .find(|e| e["event"] == "transport_metrics")
            .unwrap();
        assert_eq!(summary["timeline_reanchored"], 0);
        drop(members);
        server.join().unwrap();
    }
    #[test]
    fn a_zero_forced_target_expires_promptly_instead_of_spinning_or_adding_delay() {
        let (mut member, receiver, server) = local_member();
        member
            .media
            .connect_audio(receiver.local_addr().unwrap())
            .unwrap();
        member.media.configure_latency(Duration::ZERO, true);
        let mut members = vec![member];
        let mut options = summary_options("", "single", 0);
        options.force_latency = true;
        options.latency_ms = 0;
        let gain = std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0.0f32.to_bits()));
        let began = Instant::now();
        let result = send_packets(
            &options,
            &Cancellation::default(),
            &gain,
            crate::volume::Control::default(),
            None,
            &crate::standby::Control::default(),
            &Clock::new(),
            None,
            &mut members,
            None,
            &mut preemption::TakeoverCounters::default(),
            &[],
            None,
            &|_| {},
        );
        assert_eq!(
            result.unwrap_err().downcast_ref::<Fault>().unwrap().code,
            "timeline_expired"
        );
        assert!(began.elapsed() < Duration::from_secs(3));
        assert_eq!(members[0].media.metrics.packets_sent, 0);
        drop(members);
        server.join().unwrap();
    }
    #[test]
    fn a_normal_stop_emits_exactly_one_session_summary() {
        let options = summary_options("", "single", 40);
        let (mut member, receiver, server) = local_member();
        member
            .media
            .connect_audio(receiver.local_addr().unwrap())
            .unwrap();
        let gain = std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0.0f32.to_bits()));
        let events = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let sink = events.clone();
        let mut members = vec![member];
        let mut takeovers = preemption::TakeoverCounters::default();
        let outcome = send_packets(
            &options,
            &Cancellation::default(),
            &gain,
            crate::volume::Control::default(),
            None,
            &crate::standby::Control::default(),
            &Clock::new(),
            None,
            &mut members,
            None,
            &mut takeovers,
            &[],
            None,
            &|event| sink.lock().unwrap().push(event),
        );
        assert!(matches!(&outcome, Ok(StreamEnd::Finished)), "{outcome:?}");
        let summaries = summary_events(&events);
        assert_eq!(
            summaries.len(),
            1,
            "a normal stop must emit exactly one summary: {summaries:?}"
        );
        for column in crate::telemetry::SUMMARY_COLUMNS {
            if column.starts_with("timeline_resets_") {
                continue;
            }
            assert!(summaries[0].get(column).is_some(), "missing {column}");
        }
        // A normal stop has no reset: every bucket is zero and the last reason is
        // the sentinel, not an empty string and not a fabricated `start`.
        let resets = summaries[0]["timeline_resets"].as_object().unwrap();
        for reason in crate::telemetry::TimelineResetReason::ALL {
            assert_eq!(resets[reason.name()], json!(0), "{}", reason.name());
        }
        assert_eq!(
            summaries[0]["last_timeline_reset_reason"],
            crate::telemetry::NO_TIMELINE_RESET
        );
        // No stall happened, so the severity counters stay at zero (Q44 §一).
        assert_eq!(summaries[0]["stall_rtx_drained"], json!(0));
        assert_eq!(summaries[0]["stall_rtx_drained_max"], json!(0));
        assert_eq!(summaries[0]["stall_rtx_drained_capped"], json!(0));
        assert!(summaries[0].get("rebuild_count").is_none());
        drop(members);
        server.join().unwrap();
    }

    #[test]
    fn one_summary_on_the_fault_path_and_teardown_adds_none() {
        let options = summary_options("", "single", 0);
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let health = Health::default();
        let (mut member, server, methods) =
            local_member_with_health(receiver.local_addr().unwrap(), None, health.clone());
        member
            .media
            .connect_audio(receiver.local_addr().unwrap())
            .unwrap();
        // The media loop checks this on its first iteration and fails exactly like
        // a worker-detected fault would.
        health.fail_for_test(Fault::new(
            "127.0.0.1",
            "control",
            "synthetic_fault",
            true,
            "synthetic fault",
        ));
        let gain = std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0.0f32.to_bits()));
        let events = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let sink = events.clone();
        let mut members = vec![member];
        let mut takeovers = preemption::TakeoverCounters::default();
        let outcome = send_packets(
            &options,
            &Cancellation::default(),
            &gain,
            crate::volume::Control::default(),
            None,
            &crate::standby::Control::default(),
            &Clock::new(),
            None,
            &mut members,
            None,
            &mut takeovers,
            &[],
            None,
            &|event| sink.lock().unwrap().push(event),
        );
        assert!(outcome.is_err(), "the injected fault must end the session");
        let summaries = summary_events(&events);
        assert_eq!(
            summaries.len(),
            1,
            "the fault path must emit exactly one summary: {summaries:?}"
        );
        drop(members);
        server.join().unwrap();
        // The normal teardown still ran after the fault; it must not add a row.
        assert!(
            methods
                .lock()
                .unwrap()
                .iter()
                .any(|method| method == "TEARDOWN"),
            "normal teardown did not run"
        );
    }

    #[test]
    fn session_summary_copies_every_number_from_its_existing_counter() {
        let options = summary_options("abc", "software_stereo", 0);
        let (mut member, _receiver, _server) = local_member();
        member.media.metrics.retransmit_requests = 7;
        member.media.metrics.retransmits_sent = 5;
        let mut telemetry = crate::telemetry::SessionTelemetry::new();
        for lateness_ms in [1.0, 2.0, 3.0] {
            telemetry.observe_lateness(lateness_ms);
        }
        telemetry.gap_filled = 11;
        telemetry.reanchored = 2;
        // Q44 §一: stall severity is copied verbatim, separately from the count.
        telemetry.stall_rtx_drained = 12;
        telemetry.stall_rtx_drained_max = 7;
        // Q47 §三: drains the 250 ms window could not prove exact are counted
        // separately so a distribution over `_max` never mixes in lower bounds.
        telemetry.stall_rtx_drained_capped = 2;
        telemetry.underruns_active = 4;
        telemetry.underruns_idle = 9;
        telemetry.servo_engagements = 3;
        telemetry.servo_step_resets = 6;
        // `expired` stays a telemetry counter (the `timeline_expired` symptom),
        // but it is no longer a summary column: only the reason buckets are.
        telemetry.expired = 1;
        telemetry.lead_extra_ms = 45.0;
        telemetry.lead_shift_count = 2;
        telemetry.lead_shift_total_us = 3_000;
        telemetry.record_timeline_reset(TimelineResetReason::ConfirmedTransportFailure);
        telemetry.record_timeline_reset(TimelineResetReason::LatencyChange);
        telemetry.record_timeline_reset(TimelineResetReason::LatencyChange);
        let summary = session_summary(
            &options,
            std::slice::from_ref(&member),
            &telemetry,
            45,
            Duration::from_millis(12_500),
        );
        assert_eq!(
            summary.session_id,
            format!("abc-{}", telemetry.session_epoch)
        );
        assert_eq!(summary.topology, "software_stereo");
        // `unnegotiated()` is the NTP/PCM profile used by these local peers.
        assert_eq!(summary.codec, "pcm");
        assert_eq!(summary.timing, "ntp");
        assert_eq!(summary.applied_lead_ms, 45);
        // Lead-shift counters are copied verbatim and stay outside the buckets.
        assert_eq!(summary.lead_shift_count, 2);
        assert_eq!(summary.lead_shift_total_us, 3_000);
        assert_eq!(summary.packets, 3);
        assert_eq!(summary.rtx_req, 7);
        assert_eq!(summary.rtx_resent, 5);
        // Idle keepalive underruns are not dropouts and must not be counted.
        assert_eq!(summary.underruns, 4);
        assert_eq!(summary.gap_filled, 11);
        assert_eq!(summary.reanchored, 2);
        assert_eq!(summary.stall_rtx_drained, 12);
        assert_eq!(summary.stall_rtx_drained_max, 7);
        assert_eq!(summary.stall_rtx_drained_capped, 2);
        assert_eq!(summary.servo_engagements, 3);
        assert_eq!(summary.baseline_resets, 6);
        assert_eq!(summary.mean_lateness_ms, 2.0);
        assert_eq!(
            summary.p99_lateness_ms,
            round3(telemetry.lateness.percentile_ms(0.99))
        );
        assert_eq!(summary.max_lateness_ms, 3.0);
        assert_eq!(summary.duration_s, 12.5);
        // Q40: the buckets come from the reason sites, never from `expired`.
        assert_eq!(summary.timeline_resets.confirmed_transport_failure, 1);
        assert_eq!(summary.timeline_resets.latency_change, 2);
        assert_eq!(summary.timeline_resets.silence_resume, 0);
        assert_eq!(summary.timeline_resets.total(), 3);
        assert_eq!(summary.last_timeline_reset_reason, "latency_change");
        let event = summary.event();
        assert!(event.get("rebuild_count").is_none());
        assert_eq!(event["stall_rtx_drained"], json!(12));
        assert_eq!(event["stall_rtx_drained_max"], json!(7));
        assert_eq!(event["stall_rtx_drained_capped"], json!(2));
        assert_eq!(
            event["timeline_resets"]["confirmed_transport_failure"],
            json!(1)
        );
        assert_eq!(event["last_timeline_reset_reason"], json!("latency_change"));
    }

    #[test]
    fn a_resumed_attempt_records_the_cause_it_was_started_with() {
        // The standby pause ends the previous attempt; the resume builds a fresh
        // timeline and must report `silence_resume`, not a transport failure.
        let options = summary_options("", "single", 40);
        let (mut member, receiver, server) = local_member();
        member
            .media
            .connect_audio(receiver.local_addr().unwrap())
            .unwrap();
        let gain = std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0.0f32.to_bits()));
        let events = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let sink = events.clone();
        let mut members = vec![member];
        let mut takeovers = preemption::TakeoverCounters::default();
        let outcome = send_packets(
            &options,
            &Cancellation::default(),
            &gain,
            crate::volume::Control::default(),
            None,
            &crate::standby::Control::default(),
            &Clock::new(),
            None,
            &mut members,
            None,
            &mut takeovers,
            &[],
            Some(TimelineResetReason::SilenceResume),
            &|event| sink.lock().unwrap().push(event),
        );
        assert!(matches!(&outcome, Ok(StreamEnd::Finished)), "{outcome:?}");
        let summaries = summary_events(&events);
        assert_eq!(summaries.len(), 1);
        assert_eq!(summaries[0]["timeline_resets"]["silence_resume"], json!(1));
        assert_eq!(
            summaries[0]["timeline_resets"]["confirmed_transport_failure"],
            json!(0)
        );
        assert_eq!(
            summaries[0]["last_timeline_reset_reason"],
            json!("silence_resume")
        );
        drop(members);
        server.join().unwrap();
    }

    #[test]
    fn a_capture_source_reopen_records_source_restart_not_start() {
        // Q43 §四: the first timeline of a row is the baseline (never `start`),
        // and a reopened capture source gets its own bucket.
        assert_eq!(source_restart_reason(), TimelineResetReason::SourceRestart);
        let options = summary_options("", "single", 40);
        let (mut member, receiver, server) = local_member();
        member
            .media
            .connect_audio(receiver.local_addr().unwrap())
            .unwrap();
        let gain = std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0.0f32.to_bits()));
        let events = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let sink = events.clone();
        let mut members = vec![member];
        let mut takeovers = preemption::TakeoverCounters::default();
        let outcome = send_packets(
            &options,
            &Cancellation::default(),
            &gain,
            crate::volume::Control::default(),
            None,
            &crate::standby::Control::default(),
            &Clock::new(),
            None,
            &mut members,
            None,
            &mut takeovers,
            &[],
            Some(source_restart_reason()),
            &|event| sink.lock().unwrap().push(event),
        );
        assert!(matches!(&outcome, Ok(StreamEnd::Finished)), "{outcome:?}");
        let summaries = summary_events(&events);
        assert_eq!(summaries.len(), 1);
        assert_eq!(summaries[0]["timeline_resets"]["source_restart"], json!(1));
        assert_eq!(
            summaries[0]["timeline_resets"]["start"],
            json!(0),
            "a capture-source reopen must never be recorded as a session start"
        );
        assert_eq!(
            summaries[0]["last_timeline_reset_reason"],
            json!("source_restart")
        );
        drop(members);
        server.join().unwrap();
    }

    #[test]
    fn a_lead_shift_below_the_splice_ramp_is_counted_without_a_latency_change_reset() {
        let mut schedule = MediaSchedule::new(Instant::now(), 44_100);
        let mut telemetry = crate::telemetry::SessionTelemetry::new();
        // 3 ms is shorter than the 5 ms continuity splice ramp: audible lead
        // activity, not a timeline event.
        let applied = schedule.add_shift_us(3_000);
        assert_eq!(applied, 3_000, "a microsecond shift must survive exactly");
        telemetry.record_lead_shift(applied);
        assert_eq!(telemetry.lead_shift_count, 1);
        assert_eq!(telemetry.lead_shift_total_us, 3_000);
        assert_eq!(
            telemetry.timeline_resets.latency_change, 0,
            "a shift inside the splice ramp must not fill the reset bucket"
        );
        // 5 ms is exactly the ramp length and 7 ms is past it; both are resets.
        let applied = schedule.add_shift_us(5_000);
        telemetry.record_lead_shift(applied);
        assert_eq!(telemetry.timeline_resets.latency_change, 1);
        let applied = schedule.add_shift_us(7_000);
        telemetry.record_lead_shift(applied);
        assert_eq!(telemetry.timeline_resets.latency_change, 2);
        assert_eq!(telemetry.lead_shift_count, 3);
        assert_eq!(telemetry.lead_shift_total_us, 15_000);
    }

    #[test]
    fn a_summary_file_failure_never_fails_the_session() {
        let options = summary_options("", "single", 0);
        let (member, _receiver, _server) = local_member();
        let telemetry = crate::telemetry::SessionTelemetry::new();
        let summary = session_summary(
            &options,
            std::slice::from_ref(&member),
            &telemetry,
            0,
            Duration::from_secs(1),
        );
        let root =
            std::env::temp_dir().join(format!("airflash-blocked-summary-{}", Uuid::new_v4()));
        std::fs::create_dir_all(&root).unwrap();
        // A regular file where the logs directory should be makes create_dir_all
        // and the append fail deterministically.
        let blocker = root.join("logs");
        std::fs::write(&blocker, b"not a directory").unwrap();
        let path = blocker.join("session-summary.csv");
        assert!(
            crate::telemetry::append_summary_row(&path, &summary).is_err(),
            "the fixture must make the append fail"
        );
        let events = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let sink = events.clone();
        // The wrapper returns (), so no I/O failure can propagate into the audio
        // or control path; the event is still emitted.
        emit_session_summary(&summary, Some(&path), &|event| {
            sink.lock().unwrap().push(event)
        });
        assert_eq!(summary_events(&events).len(), 1);
        let _ = std::fs::remove_dir_all(root);
    }

    struct TemporaryRoot(PathBuf);
    impl TemporaryRoot {
        fn new() -> Self {
            let root = std::env::temp_dir()
                .join(format!("airflash-authentication-tests-{}", Uuid::new_v4()));
            std::fs::create_dir(&root).unwrap();
            Self(root)
        }
    }
    impl Drop for TemporaryRoot {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    #[derive(Default)]
    struct AuthenticationCalls {
        verify: usize,
        transient: usize,
    }
    fn synthetic_credentials() -> auth::Credentials {
        auth::Credentials {
            accessory_id: b"synthetic-accessory".to_vec(),
            accessory_public: [11; 32],
            controller_id: b"synthetic-controller".to_vec(),
            controller_secret: [22; 32],
        }
    }
    fn select_authentication(
        calls: &mut AuthenticationCalls,
        saved: Result<Option<auth::Credentials>>,
        fail_verify: bool,
    ) -> Result<()> {
        authenticate_with(
            "127.0.0.1".parse().unwrap(),
            calls,
            saved,
            |calls, credentials| {
                calls.verify += 1;
                assert_eq!(credentials.accessory_id, b"synthetic-accessory");
                if fail_verify {
                    anyhow::bail!("synthetic verification failure");
                }
                Ok(())
            },
            |calls| {
                calls.transient += 1;
                Ok(())
            },
        )
    }
    #[test]
    fn absent_and_saved_credentials_select_exactly_one_authentication_method() {
        let root = TemporaryRoot::new();
        let id = "synthetic-accessory";
        let mut calls = AuthenticationCalls::default();
        let saved = crate::credentials::load(&root.0, id);
        select_authentication(&mut calls, saved, false).unwrap();
        assert_eq!((calls.verify, calls.transient), (0, 1));
        crate::credentials::save(&root.0, id, &synthetic_credentials()).unwrap();
        let saved = crate::credentials::load(&root.0, id);
        let mut calls = AuthenticationCalls::default();
        select_authentication(&mut calls, saved, false).unwrap();
        assert_eq!((calls.verify, calls.transient), (1, 0));
    }
    #[test]
    fn credential_io_errors_precede_both_authentication_methods_and_keep_setup_classification() {
        for kind in [
            std::io::ErrorKind::NotFound,
            std::io::ErrorKind::PermissionDenied,
            std::io::ErrorKind::Other,
        ] {
            let mut calls = AuthenticationCalls::default();
            let error = select_authentication(
                &mut calls,
                Err(std::io::Error::new(kind, "synthetic credential read failure").into()),
                false,
            )
            .unwrap_err();
            assert_eq!((calls.verify, calls.transient), (0, 0));
            assert_eq!(error.downcast_ref::<std::io::Error>().unwrap().kind(), kind);
            let fault = Fault::from_error("127.0.0.1", "setup", &error);
            assert_eq!(fault.channel, "setup");
            assert_eq!(fault.code, "socket_error");
            assert!(fault.retryable);
        }
    }
    #[test]
    fn invalid_loaded_credentials_precede_both_authentication_methods() {
        let root = TemporaryRoot::new();
        let id = "synthetic-accessory";
        for invalid in [
            "unsupported",
            "tampered",
            "malformed-json",
            "empty-accessory",
            "empty-controller",
        ] {
            let mut credentials = synthetic_credentials();
            match invalid {
                "empty-accessory" => credentials.accessory_id.clear(),
                "empty-controller" => credentials.controller_id.clear(),
                _ => {}
            }
            crate::credentials::save(&root.0, id, &credentials).unwrap();
            let target = crate::credentials::path(&root.0, id);
            if invalid == "unsupported" {
                std::fs::write(&target, b"W2AP\x02").unwrap();
            } else if invalid == "tampered" {
                let mut data = std::fs::read(&target).unwrap();
                let last = data.len() - 1;
                data[last] ^= 1;
                std::fs::write(&target, data).unwrap();
            } else if invalid == "malformed-json" {
                crate::credentials::write_test_payload(&root.0, id, b"not-json").unwrap();
            }
            let saved = crate::credentials::load(&root.0, id);
            assert!(saved.is_err(), "fixture must fail credential loading");
            if invalid == "malformed-json" {
                assert!(saved.as_ref().err().unwrap().is::<serde_json::Error>());
            }
            let mut calls = AuthenticationCalls::default();
            let error = select_authentication(&mut calls, saved, false).unwrap_err();
            assert_eq!((calls.verify, calls.transient), (0, 0));
            let fault = Fault::from_error("127.0.0.1", "setup", &error);
            assert_eq!(fault.channel, "setup");
            assert_eq!(fault.code, "protocol_error");
            assert!(!fault.retryable);
        }
    }
    #[test]
    fn failed_verification_never_falls_back_to_transient() {
        let mut calls = AuthenticationCalls::default();
        let error =
            select_authentication(&mut calls, Ok(Some(synthetic_credentials())), true).unwrap_err();
        assert_eq!((calls.verify, calls.transient), (1, 0));
        let fault = error.downcast_ref::<Fault>().unwrap();
        assert_eq!(fault.channel, "authentication");
        assert_eq!(fault.code, "authentication_failed");
        assert_eq!(fault.message, "synthetic verification failure");
        assert!(!fault.retryable);
    }
    #[test]
    fn unsafe_probe_rejected() {
        let mut parameters =
            json!({"peers":[{"host":"127.0.0.1"}],"duration_ms":0,"latency_ms":120,"gain":0.1});
        assert_eq!(
            serde_json::from_value::<ProbeOptions>(parameters.clone())
                .unwrap()
                .intent,
            StartIntent::Manual
        );
        parameters["intent"] = json!("automatic");
        assert_eq!(
            serde_json::from_value::<ProbeOptions>(parameters.clone())
                .unwrap()
                .intent,
            StartIntent::Automatic
        );
        parameters["intent"] = json!("unknown");
        assert!(serde_json::from_value::<ProbeOptions>(parameters).is_err());
        let mut o = ProbeOptions {
            intent: StartIntent::Manual,
            peers: vec![Peer {
                receiver_id: String::new(),
                host: "127.0.0.1".parse().unwrap(),
                port: 7000,
                codecs: Vec::new(),
                profile: crate::profile::Profile::default(),
            }],
            wav_path: PathBuf::new(),
            source: "wav".into(),
            capture_endpoint: None,
            capture_application_path: None,
            capture_process_id: None,
            capture_process_started_at: None,
            standby: crate::standby::Settings::default(),
            duration_ms: 5001,
            latency_ms: 200,
            force_latency: false,
            adaptive_latency: false,
            adaptive_initial_latency_ms: None,
            gain: 0.1,
            equalizer: crate::equalizer::Settings::default(),
            sample_rate: 44100,
            timing: "ptp".into(),
            group_id: None,
            group_mode: String::new(),
            handshake_only: false,
            record_mic_path: None,
            report_session_id: String::new(),
        };
        assert!(o.validate().is_err());
        o.duration_ms = 5000;
        o.gain = 1.0;
        assert!(o.validate().is_err());
        o.gain = f32::NAN;
        assert!(o.validate().is_err());
        o.gain = 0.1;
        assert!(o.validate().is_ok());
        o.equalizer.enabled = true;
        assert!(o.validate().is_err());
        o.source = "loopback".into();
        o.duration_ms = 0;
        assert!(o.validate_start().is_ok());
        o.equalizer.preamp_db = f64::NAN;
        assert!(o.validate_start().is_err());
        o.equalizer = crate::equalizer::Settings::default();
        o.source = "wav".into();
        o.duration_ms = 5000;
        o.latency_ms = 0;
        assert!(o.validate().is_ok());
        o.sample_rate = 48000;
        assert!(o.validate().is_ok());
        o.sample_rate = 96000;
        assert!(o.validate().is_err());
    }
}
