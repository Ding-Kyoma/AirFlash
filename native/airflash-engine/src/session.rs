//! AirPlay member orchestration, live loopback and finite quiet qualification.
use crate::{
    auth,
    clock::{Clock, PtpMaster},
    crypto::derive,
    preemption,
    rtp::{self, FRAMES, PCM_BYTES, Packetizer},
    rtsp::{Cancellation, Connection},
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
}
fn default_source() -> String {
    "wav".into()
}
fn default_timing() -> String {
    "ptp".into()
}
impl ProbeOptions {
    pub fn validate_start(&self) -> Result<()> {
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
    Reclaimed { consecutive: u32 },
    /// The consecutive limit was hit: stop reclaiming, non-retryable.
    Yield(Fault),
}

impl Member {
    #[allow(clippy::too_many_arguments)]
    fn connect(
        peer: &Peer,
        options: &ProbeOptions,
        ptp: &mut Option<PtpMaster>,
        clock: &Clock,
        ntp_port: u16,
        initial_rtp: u32,
        cancel: Cancellation,
        emit: &dyn Fn(Json),
    ) -> Result<Self> {
        let mut conn = Connection::connect(SocketAddr::new(peer.host, peer.port), cancel)?;
        let local = conn.local_addr()?.ip();
        let ssrc = rand::random::<u32>();
        let uri = format!("rtsp://{local}/{ssrc}");
        emit(json!({"event":"phase","host":peer.host,"phase":"info"}));
        let info = conn.request("GET", "/info", &[], &[])?.plist()?;
        let capabilities = info.as_dictionary().context("info is not a dictionary")?;
        let compat = crate::profile::CompatProfile::derive(
            &info,
            &peer.profile,
            &peer.codecs,
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
            // BMCA runs here because it must: before RECORD there is no timeline and
            // no audio, so yielding cannot be heard. A receiver that announces a
            // lower priority1 becomes the master; silence or a worse dataset leaves
            // us as master. This never re-runs mid-session.
            let verdict = timer.negotiate(peer.host, BRMCA_NEGOTIATION_TIMEOUT);
            if verdict.yield_master {
                timer.yield_to(peer.host);
            }
            emit(json!({"event":"bmca_verdict","host":peer.host,"yielded":verdict.yield_master,"our_p1":verdict.our_p1,"remote_p1":verdict.remote_p1,"remote_is_master":verdict.remote_is_master,"probes":verdict.probes}));
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
        let effective_latency = receiver_latency
            .filter(|v| *v > 0 && *v <= rate as u64 * 10)
            .map(|samples| Duration::from_nanos(samples * 1_000_000_000 / rate as u64));
        member.media.configure_latency(
            effective_latency.unwrap_or(Duration::from_millis(u64::from(requested_latency))),
            effective_latency.is_none(),
        );
        emit(
            json!({"event":"negotiated","receiver_id":peer.receiver_id,"host":peer.host,"port":peer.port,"clock_role":member.compat.clock_role(),"data_port":data_port,"codec":member.compat.codec_name(),"transport":member.compat.transport(),"timing":member.compat.timing_name(),"channel":peer.profile.channel,"sync_offset_ms":peer.profile.sync_offset_ms,"qualification":"experimental","sample_rate":rate,"control_port":member.remote_control,"requested_latency_ms":options.latency_ms,"receiver_latency_min_samples":stream.as_dictionary().and_then(|d|d.get("latencyMin")).and_then(Value::as_unsigned_integer),"measured_latency_ms":null}),
        );
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
    /// Drain control-plane evidence, then ask the receiver's live `statusFlags`
    /// when a probe is due. Control symptoms never decide on their own:
    /// - A `Free` or `BusyElsewhere` verdict is confirmed by one more probe
    ///   after [`preemption::CONFIRM_DELAY`]; a disagreement never reclaims.
    /// - `BusyElsewhere` queues another probe and keeps the media loop alive,
    ///   but only up to [`preemption::BUSY_LIMIT`] confirmed verdicts in a row:
    ///   then it forces a reclaim and logs `busy_exhausted`.
    /// - `Unknown` / probe error / timeout reclaim immediately (fail-open).
    /// - The per-receiver consecutive counter decides whether a reclaim is
    ///   allowed or the sender must yield.
    fn poll_preemption(
        &mut self,
        cancel: &Cancellation,
        takeovers: &mut preemption::TakeoverCounters,
        now: Instant,
        emit: &impl Fn(Json),
    ) -> (PreemptionPoll, bool) {
        let media_healthy = self.media.media_healthy(now, MEDIA_HEALTH_WINDOW);
        let signals = self.health.take_preemption_signals();
        if !signals.is_empty() {
            for signal in &signals {
                self.preemption.observe(*signal, now);
            }
            emit(preemption_evidence(self, &signals, media_healthy));
        }
        if !self.preemption.due(now) {
            return (PreemptionPoll::None, media_healthy);
        }
        if cancel.is_cancelled() {
            return (PreemptionPoll::None, media_healthy);
        }
        let address = SocketAddr::new(self.peer.host, self.peer.port);
        // The dimension the receiver's session bit cannot give us: our own media
        // is still flowing (or the ended conclusion is stale). Without it our own
        // healthy session would look like "a session is active and we are
        // reclaiming", i.e. a takeover of ourselves (Q31/Q32).
        let we_hold_session = self.preemption.we_hold_session(media_healthy, now);
        let result = preemption::probe_report(
            address,
            cancel,
            preemption::PROBE_TIMEOUT,
            we_hold_session,
        );
        let note = self.preemption.note_result(&result, now);
        if note.log_changed {
            emit(preemption_probe(
                self,
                &result,
                we_hold_session,
                media_healthy,
            ));
        }
        if let Some((first, second)) = note.disagreement {
            emit(preemption_probe_disagreement(self, first, second));
        }
        match note.action {
            // The first verdict would change state; wait for its one
            // confirmation probe. The old control symptom must not decide while
            // its verdict is being checked.
            preemption::ProbeAction::Confirm
            | preemption::ProbeAction::Requeue => (PreemptionPoll::Waiting, media_healthy),
            // The active session on the receiver is ours: the control-plane
            // symptom is something else, so take no preemption action and fall
            // through to the existing health checks below.
            preemption::ProbeAction::FallThrough => (PreemptionPoll::None, media_healthy),
            preemption::ProbeAction::Reclaim | preemption::ProbeAction::ReclaimBusyExhausted => {
                if note.busy_exhausted {
                    emit(preemption_busy_exhausted(self));
                }
                // Confirmed (or fail-open). The old encrypted control connection
                // belongs to the session the receiver already ended; the reclaim
                // must use a fresh connection, never this one (Q16).
                self.preempted = true;
                self.conn.lock().unwrap().poison();
                match takeovers.record(self.peer.host, now) {
                    preemption::TakeoverVerdict::Reclaim { consecutive } => {
                        emit(preemption_warning(
                            self,
                            preemption::ReclaimReason::Takeover,
                            consecutive,
                        ));
                        (PreemptionPoll::Reclaimed { consecutive }, media_healthy)
                    }
                    preemption::TakeoverVerdict::Yield { consecutive } => {
                        emit(preemption_warning(
                            self,
                            preemption::ReclaimReason::Limit,
                            consecutive,
                        ));
                        (
                            PreemptionPoll::Yield(preemption_fault(
                                self,
                                preemption::ReclaimReason::Limit,
                                consecutive,
                            )),
                            media_healthy,
                        )
                    }
                }
            }
        }
    }
    fn metrics(&self, ptp: Option<&PtpMaster>) -> Json {
        let clock = ptp.and_then(|timer| {
            timer
                .snapshots()
                .into_iter()
                .find(|snapshot| snapshot.host == self.peer.host)
        });
        json!({"id":if self.peer.receiver_id.is_empty() {self.peer.host.to_string()} else {self.peer.receiver_id.clone()}, "host":self.peer.host, "ip":self.peer.host, "active":!self.closed, "codec":self.compat.codec_name(),"transport":self.compat.transport(),"timing":self.compat.timing_name(),"clock_role":self.compat.clock_role(),"channel":self.peer.profile.channel,"sync_offset_ms":self.peer.profile.sync_offset_ms,"qualification":"experimental","media":self.media.metrics(), "health":self.health.snapshot(), "ptp_offset_ns":clock.as_ref().and_then(|s| s.offset_ns), "ptp_offset_count":clock.as_ref().map(|s| s.exchanges), "ptp_ready":clock.as_ref().is_some_and(|s| s.ready), "ptp_phase":clock.as_ref().map(|s| s.phase)})
    }
    fn close(&mut self) {
        if self.closed {
            return;
        }
        self.closed = true;
        self.feedback.take();
        // A receiver that took the session over must not receive a TEARDOWN from
        // the session it just displaced; the takeover already ended it.
        if self.session_started && !self.preempted {
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

fn run_live(
    options: ProbeOptions,
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
    while !cancel.is_cancelled() {
        if let Some(live) = &source {
            emit_capture_warnings(live, &emit);
        }
        if source.as_ref().is_some_and(crate::live::Loopback::ended) {
            source = None;
            paused = None;
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
            &emit,
            (Clock::new(), None),
        ) {
            Ok(ended) => ended,
            Err(error) if error.downcast_ref::<preemption::Reclaim>().is_some() => {
                // The live status probe said free (or failed open). This is a
                // reclaim, not a transport failure: close the poisoned control
                // connection by dropping the Member and open a fresh one. The
                // consecutive counter above limits how often this can happen;
                // there is no inner retry loop here.
                live.discard_stale();
                continue;
            }
            Err(error) => return Err(error),
        };
        match ended {
            StreamEnd::Finished => break,
            StreamEnd::SourceEnded => {
                source = None;
                paused = None;
            }
            StreamEnd::Silence(at) => {
                paused = Some(at);
                emit(json!({"event":"standby","reason":"silence"}));
            }
        }
    }
    emit(json!({"event":"stopped","cancelled":cancel.is_cancelled()}));
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
    for peer in &options.peers {
        cancel.check()?;
        // A group joins as one unit: if a member cannot be brought up after one
        // retry, the members already built are dropped (their Drop sends TEARDOWN),
        // so the receiver never sees a half-configured stereo pair.
        let mut attempt = 0;
        let member = loop {
            match Member::connect(
                peer,
                options,
                &mut ptp,
                &clock,
                ntp.port,
                initial_rtp,
                cancel.clone(),
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
                Err(error) => return Err(Fault::from_error(peer.host, "setup", &error).into()),
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
    emit: &impl Fn(Json),
) -> Result<StreamEnd> {
    let rate = options.sample_rate;
    let start = Instant::now();
    let base_ns = clock.now_ns();
    let common_latency = members.iter().map(|m| m.media.latency()).max().unwrap()
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
    let mut schedule = MediaSchedule::new(start, rate);
    let mut packets = 0u64;
    let mut telemetry = crate::telemetry::SessionTelemetry::new();
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
    let metadata_worker = crate::metadata::Worker::start(
        volume_peers,
        crate::metadata::global().clone(),
    )
    .ok();
    let mut next_metrics = start + Duration::from_secs(1);
    let mut applied_lead = 0i64;
    let mut last_marker = 0u64;
    let mut next_sync = start;
    let mut silence_packets = 0u64;
    let mut recovery_left = 0u64;
    emit(
        json!({"event":"streaming","members":members.len(),"clock_id":clock_id,"first_send_unix_ns":clock.now_ns(),"first_send_qpc_ns":crate::wasapi::qpc_ns(),"requested_latency_ms":options.latency_ms,"sample_rate":rate,"measured_latency_ms":null,"qualified":false}),
    );
    let mut pcm = vec![0; PCM_BYTES];
    let started_ns = crate::wasapi::qpc_ns();
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
            for member in members.iter_mut() {
                for notice in member.health.notices() {
                    emit(notice);
                }
                let (preemption, media_healthy) =
                    member.poll_preemption(cancel, takeovers, Instant::now(), emit);
                let waiting = match preemption {
                    PreemptionPoll::None => false,
                    PreemptionPoll::Waiting => true,
                    PreemptionPoll::Reclaimed { consecutive } => {
                        return Err(preemption::Reclaim {
                            host: member.peer.host,
                            consecutive,
                        }
                        .into());
                    }
                    PreemptionPoll::Yield(fault) => return Err(fault.into()),
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
                if let Some(fault) = member.health.control_degraded()
                    && fault.channel == "events"
                    && member.event_rebuilds < MAX_EVENT_REBUILDS
                    && Instant::now() >= member.next_event_rebuild
                    && let Some(addr) = member.event_addr
                {
                    member.event_rebuilds += 1;
                    member.next_event_rebuild = Instant::now() + EVENT_REBUILD_INTERVAL;
                    match EventWorker::connect(addr, &member.event_key, member.health.clone()) {
                        Ok(worker) => {
                            member.events = Some(worker);
                            member.health.clear_degraded();
                            emit(json!({
                                "event": "warning",
                                "host": member.peer.host.to_string(),
                                "channel": "events",
                                "code": "event_channel_rebuilt",
                                "recovered": true,
                                "message": "Event channel reopened",
                            }));
                        }
                        Err(error) => emit(json!({
                            "event": "warning",
                            "host": member.peer.host.to_string(),
                            "channel": "events",
                            "code": "event_channel_rebuild_failed",
                            "recovered": false,
                            "message": error.to_string(),
                        })),
                    }
                }
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
                    emit(clock_status(timer, &hosts, "lost"));
                    return Err(Fault::new(
                        host.to_string(),
                        "clock",
                        "clock_lost",
                        true,
                        "PTP exchanges stopped",
                    )
                    .into());
                }
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
            let ceiling = lead_ceiling(members);
            let wanted = requested_lead().min(ceiling);
            if wanted != applied_lead {
                let applied = schedule.add_shift(wanted - applied_lead);
                if applied != 0 {
                    applied_lead += applied;
                    telemetry.lead_extra_ms = applied_lead as f64;
                    emit(json!({"event":"lead_changed","channel":"scheduler","host":null,"requested_ms":wanted,"applied_ms":applied_lead,"ceiling_ms":ceiling,"shift_ms":round3(schedule.shift.as_secs_f64() * 1000.0)}));
                }
            }
            let deadline = schedule.deadline();
            let now = Instant::now();
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
                schedule.recover(now, false)?;
            } else if recovery_left == 0 {
                let earliest = members
                    .iter()
                    .map(|m| {
                        offset_deadline(deadline, common_latency, m.peer.profile.sync_offset_ms)
                    })
                    .collect::<Result<Vec<_>>>()?
                    .into_iter()
                    .min()
                    .unwrap();
                match schedule.late_action(now, earliest) {
                    crate::transport::LateAction::OnTime => {}
                    crate::transport::LateAction::Silence(fill) => {
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
                        // Over budget: move the whole timeline forward instead of
                        // tearing the session down. Sequence numbers and RTP
                        // timestamps stay untouched; only the rtptime→networkTime
                        // anchor moves, one frame of phase margin ahead of now.
                        let Some(shift) = schedule.reanchor(now) else {
                            telemetry.expired += 1;
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
                            recovery_left = 0;
                            if let Some(source) = &live {
                                // Everything queued is older than the new deadline.
                                source.discard_stale();
                            }
                            emit(
                                json!({"event":"warning","code":"timeline_reanchored","channel":"scheduler","host":null,"recovered":true,"late_ms":round3(late.as_secs_f64() * 1000.0),"shift_ms":round3(shift.as_secs_f64() * 1000.0),"lead_ms":round3(schedule.shift.as_secs_f64() * 1000.0),"reanchored":schedule.reanchored,"message":"Sender thread fell behind its catch-up budget; re-anchored the deadline one frame ahead"}),
                            );
                        }
                    }
                }
            }
            // A deliberate lead change (adaptive latency ladder) keeps its value;
            // only an automatic re-anchor surplus is slewed back.
            if applied_lead == 0 {
                schedule.relax(now, late);
            }
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
                );
                crate::telemetry::publish_diagnostics(snapshot.clone());
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
                        schedule.deadline(),
                        common_latency - member.media.latency(),
                        member.peer.profile.sync_offset_ms,
                    )?,
                    &member.health,
                )?;
            }
            for member in members.iter_mut() {
                member.media.service_retransmits();
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
    // Stop/reconfiguration shares one faded final packet and drains its render
    // deadline on every member before tearing down the common session.
    if outcome.is_ok() {
        if let Some(source) = &live {
            let render = offset_deadline(schedule.deadline(), common_latency, 0)?;
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
                            schedule.deadline(),
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
            emit(json!({"event":"timeline_drained","faded":sent}));
        }
    }
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
    );
    crate::telemetry::publish_diagnostics(snapshot.clone());
    emit(snapshot);
    let mut summary = telemetry.snapshot();
    if let Json::Object(ref mut fields) = summary {
        fields.insert("event".into(), json!("metrics"));
        fields.insert("packets_per_member".into(), json!(packets));
        fields.insert("frames".into(), json!(schedule.frames));
        fields.insert("silence_packets".into(), json!(silence_packets));
        fields.insert("max_send_lateness_us".into(), json!(schedule.max_lateness_us));
        fields.insert("retransmits".into(), json!(members.iter().map(|m|m.media.metrics.retransmits_sent).sum::<u64>()));
        fields.insert("ptp_packets_received".into(), json!(ptp.as_ref().map(|p|p.received.load(Ordering::Relaxed))));
        fields.insert("measured_latency_ms".into(), Json::Null);
        fields.insert("qualified".into(), json!(false));
    }
    emit(summary);
    drop(volume_workers);
    for m in members.iter_mut() {
        m.close();
    }
    outcome
}

/// One warning in the existing shape. `reason` is `takeover` (the live loop
/// reclaims on a fresh connection) or `limit` (the consecutive counter yields,
/// non-retryable). The two must stay distinguishable in the payload.
fn preemption_warning(
    member: &Member,
    reason: preemption::ReclaimReason,
    consecutive: u32,
) -> Json {
    json!({
        "event": "warning",
        "host": member.peer.host.to_string(),
        "channel": "control",
        "code": "preempted",
        "recovered": false,
        "reason": reason.name(),
        "consecutive": consecutive,
        "message": match reason {
            preemption::ReclaimReason::Takeover => "Another controller took over this receiver; AirFlash is reclaiming it",
            preemption::ReclaimReason::Limit => "Another controller keeps taking over this receiver; AirFlash is yielding",
        },
    })
}
fn preemption_fault(
    member: &Member,
    reason: preemption::ReclaimReason,
    consecutive: u32,
) -> Fault {
    Fault::new(
        member.peer.host,
        "control",
        "preempted",
        false,
        format!(
            "Another controller keeps taking over this receiver ({consecutive} in a row within {} s); AirFlash yielded and will not reconnect automatically.",
            preemption::TAKEOVER_WINDOW.as_secs()
        ),
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
/// Control-plane symptoms are logged as evidence, never as a verdict. The three
/// context fields are the ones Q30 recovers from the reference log; `policy` is
/// our proxy for WAC's session policy (timing/codec family).
fn preemption_evidence(
    member: &Member,
    signals: &[preemption::Signal],
    rtp_healthy: bool,
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
        "message": "Control-plane takeover evidence; asking the receiver for its live statusFlags",
    })
}
/// One diagnostic line per probe change. It carries the same three context
/// fields as the evidence line (the reference implementation logs them next to
/// the symptom) plus `we_hold_session`, the locally derived dimension the
/// verdict uses, and the raw 64-bit `statusFlags` value so the bits can be
/// checked against a real receiver.
fn preemption_probe(
    member: &Member,
    result: &Result<preemption::ProbeReport>,
    we_hold_session: bool,
    rtp_healthy: bool,
) -> Json {
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
    });
    match result {
        Ok(report) => {
            event["outcome"] = json!(report.outcome.name());
            event["status_flags"] = json!(report.status_flags);
            // Telemetry only: bit 14 says the stereo partner is unreachable. It
            // has no session-ownership meaning and never changes the verdict.
            event["stereo_partner_unreachable"] = json!(report
                .status_flags
                .is_some_and(|flags| flags & preemption::STEREO_PARTNER_UNREACHABLE != 0));
            event["message"] = json!(match report.status_flags {
                Some(flags) => format!("Receiver /info statusFlags=0x{flags:016x} ({flags})"),
                None => "Receiver /info has no usable statusFlags; reclaiming".to_string(),
            });
        }
        Err(error) => {
            event["outcome"] = json!("error");
            event["message"] = json!(format!(
                "Receiver status probe failed ({error:#}); reclaiming anyway"
            ));
        }
    }
    event
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
/// The busy queue hit its bound. This is a distinct, visible outcome: the
/// sender stops queueing and reclaims even though the receiver still reports an
/// active session, so the `BusyElsewhere` loop can never run forever.
fn preemption_busy_exhausted(member: &Member) -> Json {
    json!({
        "event": "warning",
        "host": member.peer.host.to_string(),
        "channel": "preemption",
        "code": "busy_exhausted",
        "recovered": false,
        "attempts": preemption::BUSY_LIMIT,
        "message": format!(
            "Receiver kept reporting another sender's session for {} probes in a row; reclaiming anyway",
            preemption::BUSY_LIMIT
        ),
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
        telemetry.input_drops += metrics.dropped_frames.saturating_sub(previous.dropped_frames);
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
) -> Json {
    let mut snapshot = telemetry.snapshot();
    if let Json::Object(ref mut fields) = snapshot {
        fields.insert("event".into(), json!("transport_metrics"));
        fields.insert("session_uptime_ms".into(), json!(schedule.start.elapsed().as_millis()));
        fields.insert("sender_late_recoveries".into(), json!(schedule.recoveries));
        fields.insert("skipped_packets".into(), json!(schedule.skipped_packets));
        fields.insert("max_send_lateness_us".into(), json!(schedule.max_lateness_us));
        fields.insert(
            "playout_ms".into(),
            json!((playout.as_secs_f64() * 1000.0 * 1000.0).round() / 1000.0),
        );
        fields.insert("queue_depth".into(), json!(queue.map(|q| q.0)));
        fields.insert("queue_capacity".into(), json!(queue.map(|q| q.1)));
        fields.insert("members".into(), json!(members.iter().map(|m| m.metrics(ptp)).collect::<Vec<_>>()));
    }
    snapshot
}
fn round3(value: f64) -> f64 {
    (value * 1000.0).round() / 1000.0
}
/// Extra media lead requested by the adaptive latency ladder, in milliseconds.
///
/// The engine runs one session at a time (the IPC front end serialises `start`),
/// so a process-wide cell keeps `set_lead` from having to thread another parameter
/// through six call sites. Zero means "whatever the timeline needs".
static LEAD_MS: std::sync::atomic::AtomicI64 = std::sync::atomic::AtomicI64::new(0);
/// Absolute ceiling for an operator-requested lead. The effective ceiling is the
/// receiver's declared buffer (`lead_ceiling`); this only bounds a request when the
/// device never declared one.
pub const MAX_LEAD_MS: i64 = 2000;
/// The adaptive range WAC allows above the chosen latency is +100 ms. Staying inside
/// the device's own buffer is what keeps a larger lead from simply overflowing.
pub const LEAD_HEADROOM_MS: i64 = 100;
/// Extra lead a request may add, from what the receivers actually promised to buffer.
fn lead_ceiling(members: &[Member]) -> i64 {
    lead_ceiling_from(members.iter().filter_map(|m| m.peer.profile.latency_ms))
}
fn lead_ceiling_from(declared: impl Iterator<Item = u32>) -> i64 {
    declared
        .max()
        .map_or(MAX_LEAD_MS, |ms| i64::from(ms) + LEAD_HEADROOM_MS)
        .clamp(LEAD_HEADROOM_MS, MAX_LEAD_MS)
}
pub fn request_lead(ms: i64) {
    LEAD_MS.store(ms.clamp(0, MAX_LEAD_MS), std::sync::atomic::Ordering::Release);
}
pub fn requested_lead() -> i64 {
    LEAD_MS.load(std::sync::atomic::Ordering::Acquire)
}
pub fn reset_lead() {
    LEAD_MS.store(0, std::sync::atomic::Ordering::Release);
}
/// Time allowed for the receiver's Announce to arrive before SETUP. Three probes
/// are sent 300 ms apart, so this only rides out one late reply.
const BRMCA_NEGOTIATION_TIMEOUT: Duration = Duration::from_millis(1_200);
/// A receiver that has accepted RTP this recently is still listening, even if the
/// control channel is unhappy.
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
    fn lead_ceiling_follows_what_the_receiver_promised_to_buffer() {
        // No declared buffer: only the absolute cap applies.
        assert_eq!(lead_ceiling_from(std::iter::empty()), MAX_LEAD_MS);
        // A solo HomePod buffer of 250 ms allows 250 + 100 ms of lead.
        assert_eq!(lead_ceiling_from([250].into_iter()), 350);
        // A stereo pair uses the largest member, not the smallest.
        assert_eq!(lead_ceiling_from([120, 300].into_iter()), 400);
        // Tiny values still leave the +100 ms headroom available.
        assert_eq!(lead_ceiling_from([10].into_iter()), 110);
        // And nothing exceeds the absolute cap.
        assert_eq!(lead_ceiling_from([5000].into_iter()), MAX_LEAD_MS);
        // A request above the ceiling is clamped before it reaches the scheduler.
        assert_eq!(5000.min(lead_ceiling_from([250].into_iter())), 350);
    }
    // A minimal live-status probe target: one fresh TCP connection, one
    // `GET /info`, one binary plist. `status_flags = None` omits the field.
    fn status_probe_server(status_flags: Option<u64>) -> (SocketAddr, JoinHandle<()>) {
        status_probe_server_seq(vec![status_flags])
    }
    /// Same, but answers one `/info` per entry, each on a fresh connection. A
    /// state-changing verdict (Free / BusyElsewhere) is always confirmed by a
    /// second probe, so tests expecting a verdict need two (or more) entries.
    fn status_probe_server_seq(responses: Vec<Option<u64>>) -> (SocketAddr, JoinHandle<()>) {
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let worker = thread::spawn(move || {
            for status_flags in responses {
                let (socket, _) = listener.accept().unwrap();
                let mut connection =
                    Connection::from_stream(socket, Cancellation::default()).unwrap();
                let request = connection.read().unwrap();
                assert!(request.first.starts_with("GET /info"));
                let mut body = Dictionary::new();
                if let Some(flags) = status_flags {
                    body.insert("statusFlags".into(), Value::Integer(flags.into()));
                }
                let mut bytes = Vec::new();
                Value::Dictionary(body).to_writer_binary(&mut bytes).unwrap();
                let response = format!(
                    "RTSP/1.0 200 OK\r\nCSeq: {}\r\nContent-Length: {}\r\n\r\n",
                    request.headers["cseq"],
                    bytes.len()
                )
                .into_bytes();
                let mut response = response;
                response.extend(bytes);
                connection.write(&response).unwrap();
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
                        .write(
                            b"TEARDOWN rtsp://127.0.0.1/test RTSP/1.0\r\nCSeq: 4242\r\n\r\n",
                        )
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
        };
        (member, worker, methods)
    }
    #[test]
    fn a_preempted_member_closes_without_sending_a_teardown() {
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, methods) =
            local_member_with(receiver.local_addr().unwrap(), None);
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
    fn a_normal_member_close_still_sends_teardown() {
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, methods) =
            local_member_with(receiver.local_addr().unwrap(), None);
        member.close();
        drop(member);
        worker.join().unwrap();
        assert_eq!(
            methods.lock().unwrap().iter().filter(|m| *m == "TEARDOWN").count(),
            1
        );
    }
    #[test]
    fn an_unexpected_teardown_probes_the_receiver_and_reclaims() {
        // The control symptom only triggers a probe. The fake receiver's fresh
        // `/info` connections say statusFlags=0, so the confirmed verdict is a
        // reclaim on a different connection rather than a fault.
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
        let now = Instant::now();
        let (poll, healthy) = member.poll_preemption(
            &Cancellation::default(),
            &mut counters,
            now,
            &|json| seen.lock().unwrap().push(json),
        );
        assert!(healthy);
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
            PreemptionPoll::Reclaimed { consecutive: 1 }
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
            let probe = events
                .iter()
                .find(|event| event["code"] == "preemption_probe")
                .expect("the raw statusFlags must be logged");
            assert_eq!(probe["outcome"], "free");
            assert_eq!(probe["status_flags"], 0);
            // Q32: healthy RTP wins over the receiver's TEARDOWN; the Free
            // verdict itself does not depend on this dimension.
            assert_eq!(probe["we_hold_session"], true);
            assert!(probe["rtp_healthy"].is_boolean());
            assert!(probe["policy"].is_string());
            assert!(probe["packets_prepared"].is_number());
            assert_eq!(probe["stereo_partner_unreachable"], false);
            let warning = events
                .iter()
                .find(|event| event["code"] == "preempted")
                .expect("the takeover must be reported");
            assert_eq!(warning["reason"], "takeover");
            assert_eq!(warning["consecutive"], 1);
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
            methods.lock().unwrap().iter().filter(|m| *m == "TEARDOWN").count(),
            1
        );
        probe_server.join().unwrap();
    }
    #[test]
    fn healthy_rtp_beats_a_receiver_teardown_signal() {
        // Q32: RTP health is stronger than any control-plane signal. The
        // receiver's own TEARDOWN with our media still flowing does not make the
        // active session bit somebody else's: it is our session, so fall
        // through instead of reclaiming or queueing.
        let (probe_addr, probe_server) =
            status_probe_server(Some(preemption::RECEIVER_SESSION_ACTIVE));
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
        let (poll, healthy) = member.poll_preemption(
            &Cancellation::default(),
            &mut counters,
            Instant::now(),
            &|json| seen.lock().unwrap().push(json),
        );
        assert!(healthy);
        assert!(
            matches!(poll, PreemptionPoll::None),
            "our own active session must never reclaim or queue"
        );
        assert!(!member.preempted);
        assert!(!member.conn.lock().unwrap().is_poisoned());
        assert_eq!(
            member.preemption.scheduled(),
            None,
            "OursActive does not schedule a probe"
        );
        {
            let events = emitted.lock().unwrap();
            let probe = events
                .iter()
                .find(|event| event["code"] == "preemption_probe")
                .expect("the probe must be logged");
            assert_eq!(probe["outcome"], "ours_active");
            assert_eq!(probe["we_hold_session"], true);
            assert_eq!(probe["status_flags"], preemption::RECEIVER_SESSION_ACTIVE);
            assert!(events.iter().all(|event| event["code"] != "preempted"));
        }
        drop(member);
        worker.join().unwrap();
        // Not displaced: the session still ends with its normal TEARDOWN.
        assert_eq!(
            methods.lock().unwrap().iter().filter(|m| *m == "TEARDOWN").count(),
            1
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
        let (first, healthy) = member.poll_preemption(
            &Cancellation::default(),
            &mut counters,
            now,
            &|json| seen.lock().unwrap().push(json),
        );
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
            matches!(poll, PreemptionPoll::Waiting),
            "another sender's session is a queue, never a reclaim"
        );
        assert_eq!(
            member.preemption.scheduled(),
            Some(now + preemption::CONFIRM_DELAY + preemption::REPROBE_DELAY)
        );
        {
            let events = emitted.lock().unwrap();
            let probe = events
                .iter()
                .find(|event| event["code"] == "preemption_probe")
                .expect("the probe must be logged");
            assert_eq!(probe["outcome"], "busy_elsewhere");
            assert_eq!(probe["we_hold_session"], false);
            // Bit 14 is telemetry only and must not change the verdict.
            assert_eq!(probe["stereo_partner_unreachable"], true);
            assert!(events.iter().all(|event| event["code"] != "preempted"));
        }
        drop(member);
        worker.join().unwrap();
        probe_server.join().unwrap();
    }
    #[test]
    fn a_status_flags_field_that_is_missing_is_unknown_and_still_reclaims() {
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
            Instant::now(),
            &|json| seen.lock().unwrap().push(json),
        );
        assert!(matches!(
            poll,
            PreemptionPoll::Reclaimed { consecutive: 1 }
        ));
        let events = emitted.lock().unwrap();
        let probe = events
            .iter()
            .find(|event| event["code"] == "preemption_probe")
            .expect("probe outcome must be logged");
        assert_eq!(probe["outcome"], "unknown");
        assert!(probe["status_flags"].is_null());
        drop(events);
        drop(member);
        worker.join().unwrap();
        probe_server.join().unwrap();
    }
    #[test]
    fn a_failed_status_probe_reclaims_instead_of_a_generic_fault() {
        // Bind and drop a listener to get a port with no receiver listening.
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
            matches!(poll, PreemptionPoll::Reclaimed { consecutive: 1 }),
            "a probe error must take the reclaim path, never the generic fault path"
        );
        let events = emitted.lock().unwrap();
        let probe = events
            .iter()
            .find(|event| event["code"] == "preemption_probe")
            .expect("probe failure must be logged");
        assert_eq!(probe["outcome"], "error");
        assert!(
            probe["message"]
                .as_str()
                .unwrap()
                .contains("reclaiming anyway")
        );
        drop(events);
        drop(member);
        worker.join().unwrap();
    }
    #[test]
    fn hitting_the_consecutive_limit_yields_with_reason_limit() {
        // Two agreeing Free probes are needed: the first Free schedules the one
        // confirmation probe and the second one carries the reclaim into the
        // takeover counter, which is already at the limit here.
        let (probe_addr, probe_server) = status_probe_server_seq(vec![Some(0), Some(0)]);
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (mut member, worker, _methods) =
            local_member_with_health(receiver.local_addr().unwrap(), None, Health::default());
        member.peer.port = probe_addr.port();
        let now = Instant::now();
        let mut counters = preemption::TakeoverCounters::default();
        counters.record(member.peer.host, now - Duration::from_secs(2));
        counters.record(member.peer.host, now - Duration::from_secs(1));
        member
            .health
            .preemption_signal(preemption::Signal::UnexpectedTeardown);
        let emitted = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let seen = emitted.clone();
        let (first, _) = member.poll_preemption(
            &Cancellation::default(),
            &mut counters,
            now,
            &|json| seen.lock().unwrap().push(json),
        );
        assert!(
            matches!(first, PreemptionPoll::Waiting),
            "the first Free still needs its confirmation"
        );
        let (poll, _) = member.poll_preemption(
            &Cancellation::default(),
            &mut counters,
            now + preemption::CONFIRM_DELAY,
            &|json| seen.lock().unwrap().push(json),
        );
        let fault = match poll {
            PreemptionPoll::Yield(fault) => fault,
            other => panic!("expected a yield, got {other:?}"),
        };
        assert_eq!(fault.code, "preempted");
        assert_eq!(fault.reason.as_deref(), Some("limit"));
        assert!(!fault.retryable);
        assert!(fault.message.contains("yielded"));
        assert_eq!(counters.consecutive(member.peer.host), 3);
        let events = emitted.lock().unwrap();
        let warning = events
            .iter()
            .find(|event| event["code"] == "preempted")
            .expect("the yield must be reported");
        assert_eq!(warning["reason"], "limit");
        assert_eq!(warning["consecutive"], 3);
        drop(events);
        drop(member);
        worker.join().unwrap();
        probe_server.join().unwrap();
    }
    #[test]
    fn busy_exhausted_is_a_distinct_logged_outcome() {
        let receiver = UdpSocket::bind("127.0.0.1:0").unwrap();
        let (member, worker, _methods) =
            local_member_with(receiver.local_addr().unwrap(), None);
        let event = preemption_busy_exhausted(&member);
        assert_eq!(event["code"], "busy_exhausted");
        assert_eq!(event["channel"], "preemption");
        assert_eq!(event["attempts"], preemption::BUSY_LIMIT);
        assert!(event["message"]
            .as_str()
            .unwrap()
            .contains(&preemption::BUSY_LIMIT.to_string()));
        drop(member);
        worker.join().unwrap();
    }
    #[test]
    fn a_setup_403_stays_a_generic_non_retryable_fault() {
        // A 403 while opening the session is an authentication/setup failure, not
        // evidence that another controller took over; the classifier is never run
        // on this path. Keep its existing (non-retryable) classification.
        let error = anyhow::anyhow!("SETUP rtsp://127.0.0.1/test: RTSP/1.0 403 Forbidden");
        let fault = Fault::from_error("127.0.0.1", "setup", &error);
        assert_ne!(fault.code, "preempted");
        assert_eq!(fault.code, "protocol_error");
        assert!(!fault.retryable);
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
        media
            .connect_audio(receiver.local_addr().unwrap())
            .unwrap();
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
        let mut o = ProbeOptions {
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
            gain: 0.1,
            equalizer: crate::equalizer::Settings::default(),
            sample_rate: 44100,
            timing: "ptp".into(),
            group_id: None,
            group_mode: String::new(),
            handshake_only: false,
            record_mic_path: None,
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
