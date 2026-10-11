//! AirPlay member orchestration, live loopback and finite quiet qualification.
use crate::{
    auth,
    clock::{Clock, PtpMaster},
    crypto::derive,
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
            self.source == "loopback" && self.duration_ms == 0,
            "start requires unbounded loopback source"
        );
        ensure!(
            self.gain.is_finite() && (0.0..=1.0).contains(&self.gain),
            "gain must be 0..1"
        );
        let mut bounded = self.clone();
        bounded.duration_ms = 5000;
        bounded.gain = 0.1;
        self.equalizer.validate()?;
        bounded.equalizer = crate::equalizer::Settings::default();
        bounded.validate()
    }
    pub fn validate(&self) -> Result<()> {
        self.equalizer.validate()?;
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
struct Member {
    selection: crate::profile::Selection,
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
        let selection = crate::profile::Formats::parse(&info).negotiate(
            &peer.profile,
            &peer.codecs,
            options.sample_rate,
            options.timing == "ptp",
        )?;
        if selection.ptp && ptp.is_none() {
            let peers = options.peers.iter().map(|p| p.host).collect();
            let followers = options
                .peers
                .iter()
                .filter(|p| p.profile.follow_receiver_clock)
                .map(|p| p.host)
                .collect();
            *ptp = Some(PtpMaster::start_with_followers(
                peers,
                followers,
                clock.clone(),
            )?);
        }
        let clock_id = selection.ptp.then(|| ptp.as_ref().unwrap().clock_id);
        emit(
            json!({"event":"peer_info","host":peer.host,"model":capabilities.get("model").and_then(Value::as_string),"source_version":capabilities.get("sourceVersion").and_then(Value::as_string)}),
        );
        emit(json!({"event":"phase","host":peer.host,"phase":"authenticate"}));
        let device_id = capabilities
            .get("deviceID")
            .and_then(Value::as_string)
            .unwrap_or("");
        let saved = if device_id.is_empty() {
            None
        } else {
            crate::credentials::load(&crate::credentials::directory(), device_id)?
        };
        let secret = (if let Some(credentials) = saved {
            auth::verify(&mut conn, &credentials)
        } else if let Some(password) =
            crate::credentials::load_password(&crate::credentials::directory(), device_id)?
        {
            auth::transient_password(&mut conn, &password)
        } else {
            auth::transient(&mut conn)
        })
        .map_err(|error| {
            let mut fault = Fault::from_error(peer.host, "authentication", &error);
            if fault.code == "protocol_error" {
                fault.code = "authentication_failed".into();
            }
            fault
        })?;
        emit(json!({"event":"phase","host":peer.host,"phase":"authenticated"}));
        let control = UdpSocket::bind(SocketAddr::new(local, 0))?;
        control.set_nonblocking(true)?;
        let audio = UdpSocket::bind(SocketAddr::new(local, 0))?;
        let audio_key = derive(&secret, "Events-Salt", "Events-Write-Encryption-Key");
        let rate = options.sample_rate;
        let mut packetizer =
            Packetizer::with_rate(audio_key, rand::random(), initial_rtp, ssrc, rate);
        let use_alac = selection.alac;
        if use_alac {
            packetizer.enable_alac();
        }
        let mut member = Self {
            selection,
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
        };
        // RAII owns the connection before SETUP so partial handshakes are torn down too.
        let sender_id = "02:57:32:41:50:01";
        let mut setup = vec![
            ("deviceID", string(sender_id)),
            ("macAddress", string(sender_id)),
            ("name", string("AirFlash")),
            ("sessionUUID", string(Uuid::new_v4().to_string())),
            (
                "timingProtocol",
                string(if selection.ptp { "PTP" } else { "NTP" }),
            ),
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
                number(
                    if selection.transport == crate::profile::Transport::Buffered {
                        103
                    } else {
                        96
                    },
                ),
            ),
            ("supportsDynamicStreamID", Value::Boolean(false)),
            ("streamConnectionID", number(ssrc as u64)),
        ];
        if selection.transport != crate::profile::Transport::Buffered {
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
        member.remote_control = if selection.transport == crate::profile::Transport::Buffered {
            0
        } else {
            get_port(stream, "controlPort")?
        };
        let data_port = get_port(stream, "dataPort")?;
        member.media.set_control_port(member.remote_control);
        if selection.transport == crate::profile::Transport::Buffered {
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
            json!({"event":"negotiated","host":peer.host,"data_port":data_port,"codec":if use_alac{"alac"}else{"pcm"},"transport":selection.transport,"timing":if selection.ptp{"ptp"}else{"ntp"},"channel":peer.profile.channel,"sync_offset_ms":peer.profile.sync_offset_ms,"qualification":"experimental","sample_rate":rate,"control_port":member.remote_control,"requested_latency_ms":options.latency_ms,"receiver_latency_min_samples":stream.as_dictionary().and_then(|d|d.get("latencyMin")).and_then(Value::as_unsigned_integer),"measured_latency_ms":null}),
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
    fn metrics(&self) -> Json {
        json!({"host":self.peer.host, "codec":if self.selection.alac {"alac"}else{"pcm"},"transport":self.selection.transport,"timing":if self.selection.ptp{"ptp"}else{"ntp"},"channel":self.peer.profile.channel,"sync_offset_ms":self.peer.profile.sync_offset_ms,"qualification":"experimental","media":self.media.metrics(), "health":self.health.snapshot()})
    }
    fn close(&mut self) {
        if self.closed {
            return;
        }
        self.closed = true;
        self.feedback.take();
        if self.session_started {
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
fn run_session(
    options: ProbeOptions,
    cancel: Cancellation,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    volume: crate::volume::Control,
    equalizer: crate::equalizer::Control,
    emit: impl Fn(Json),
    clocks: (Clock, Option<PtpMaster>),
) -> Result<()> {
    if options.duration_ms == 0 {
        options.validate_start()?;
    } else {
        options.validate()?;
    }
    let samples = if options.source == "wav" {
        load_quiet_wav(&options)?
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
        members.push(
            Member::connect(
                peer,
                &options,
                &mut ptp,
                &clock,
                ntp.port,
                initial_rtp,
                cancel.clone(),
                &emit,
            )
            .map_err(|e| Fault::from_error(peer.host, "setup", &e))?,
        );
    }
    if options.handshake_only {
        emit(
            json!({"event":"handshake_complete","members":members.len(),"audio_sent":false,"qualified":false}),
        );
        return Ok(());
    }
    if let Some(timer) = &ptp {
        let until = Instant::now() + Duration::from_secs(8);
        while members
            .iter()
            .any(|m| m.selection.ptp && !timer.ready(m.peer.host))
        {
            cancel.check()?;
            if Instant::now() >= until {
                return Err(Fault::new(
                    "",
                    "clock",
                    "clock_not_ready",
                    true,
                    "PTP exchange did not become ready",
                )
                .into());
            }
            thread::sleep(Duration::from_millis(10));
        }
    }
    let clock_id = ptp.as_ref().map(|p| p.clock_id);
    let rate = options.sample_rate;
    let live = if options.source == "loopback" {
        Some(crate::live::Loopback::start(
            options.capture_endpoint.clone(),
            rate,
            equalizer,
        )?)
    } else {
        None
    };
    if let Some(source) = &live {
        let until = Instant::now() + Duration::from_millis(200);
        while !source.ready()? && Instant::now() < until {
            cancel.check()?;
            thread::sleep(Duration::from_millis(2));
        }
    }
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
    for member in &members {
        if member.selection.transport == crate::profile::Transport::Buffered {
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
                    ("rtpTime", number(initial_rtp as u64)),
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
    for member in &mut members {
        member.start_feedback(start)?;
    }
    let volume_peers = members
        .iter()
        .map(|m| (m.peer.host.to_string(), m.uri.clone(), m.conn.clone()))
        .collect::<Vec<_>>();
    let volume_workers = if options.group_mode == "software_stereo" {
        volume_peers
            .into_iter()
            .map(|p| crate::volume::Worker::start_member(p, volume.clone()))
            .collect::<Result<Vec<_>>>()?
    } else {
        vec![crate::volume::Worker::start(volume_peers, volume)?]
    };
    let mut next_metrics = start + Duration::from_secs(1);
    let mut last_marker = 0u64;
    let mut next_sync = start;
    let mut silence_packets = 0u64;
    let mut recovery_left = 0u64;
    emit(
        json!({"event":"streaming","members":members.len(),"clock_id":clock_id,"first_send_unix_ns":clock.now_ns(),"first_send_qpc_ns":crate::wasapi::qpc_ns(),"requested_latency_ms":options.latency_ms,"sample_rate":rate,"measured_latency_ms":null,"qualified":false}),
    );
    let mut pcm = vec![0; PCM_BYTES];
    let outcome = (|| -> Result<()> {
        while (options.duration_ms == 0 || start.elapsed() < duration) && !cancel.is_cancelled() {
            for worker in &volume_workers {
                for event in worker.events.try_iter() {
                    emit(event);
                }
            }
            for member in &members {
                for notice in member.health.notices() {
                    emit(notice);
                }
                member.health.check()?;
            }
            if let Some(timer) = &ptp {
                if members
                    .iter()
                    .any(|m| m.selection.ptp && !timer.ready(m.peer.host))
                {
                    return Err(Fault::new(
                        "",
                        "clock",
                        "clock_lost",
                        true,
                        "PTP exchanges stopped",
                    )
                    .into());
                }
            }
            for member in &mut members {
                member
                    .media
                    .service_buffered(Instant::now())
                    .map_err(|e| Fault::from_error(member.peer.host, "buffered", &e))?;
            }
            let deadline = schedule.deadline();
            let now = Instant::now();
            if now < deadline {
                thread::sleep((deadline - now).min(Duration::from_millis(2)));
                continue;
            }
            let late = now.saturating_duration_since(deadline);
            schedule.max_lateness_us = schedule.max_lateness_us.max(late.as_micros());
            let due = (late.as_nanos() * u128::from(rate) / 1_000_000_000 / FRAMES as u128) as u64;
            if options.duration_ms > 0 {
                schedule.recover(now, false)?;
            } else if recovery_left == 0 && due >= 2 {
                let earliest = members
                    .iter()
                    .map(|m| {
                        offset_deadline(deadline, common_latency, m.peer.profile.sync_offset_ms)
                    })
                    .collect::<Result<Vec<_>>>()?
                    .into_iter()
                    .min()
                    .unwrap();
                recovery_left = schedule.recovery_silence(now, earliest)?;
                schedule.recoveries += 1;
                if let Some(source) = &live {
                    source.discard_stale();
                }
                emit(
                    json!({"event":"warning","code":"sender_late_recovered","channel":"scheduler","host":null,"recovered":true,"silence_packets":due,"message":"Sender filled valid timeline slots with encoded silence"}),
                );
            }
            if now >= next_sync {
                for m in &mut members {
                    let local = offset_time(
                        base_ns + schedule.frames * 1_000_000_000 / u64::from(rate),
                        common_latency,
                        m.media.latency(),
                        m.peer.profile.sync_offset_ms,
                    )?;
                    let (id, network) = if m.selection.ptp {
                        let (id, t) = ptp.as_ref().unwrap().timeline(m.peer.host, local)?;
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
                    let snapshots: Vec<_> = timer
                        .snapshots()
                        .into_iter()
                        .filter(|s| {
                            members
                                .iter()
                                .any(|m| m.selection.ptp && m.peer.host == s.host)
                        })
                        .collect();
                    emit(
                        json!({"event":"clock_status","members":snapshots,"mmcss":*timer.mmcss.lock().unwrap()}),
                    );
                }
                if let Some(source) = &live {
                    emit(json!({"event":"capture_metrics","metrics":source.metrics()}));
                }
                emit(transport_metrics(&members, &schedule));
                next_metrics = now + Duration::from_secs(1);
            }
            // Every member gets new media before any member services retransmissions.
            for member in &mut members {
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
            for member in &mut members {
                member.media.service_retransmits();
            }
            schedule.sent();
            packets += 1;
            if recovery_left > 0 {
                recovery_left -= 1;
                silence_packets += 1;
            }
        }
        Ok(())
    })();
    // Stop/reconfiguration shares one faded final packet and drains its render
    // deadline on every member before tearing down the common session.
    if outcome.is_ok() {
        if let Some(source) = &live {
            let render = offset_deadline(schedule.deadline(), common_latency, 0)?;
            let (fade, _) = source.silence_packet(0.0)?;
            let mut sent = true;
            for member in &mut members {
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
        emit(json!({"event":"capture_metrics","metrics":source.metrics()}));
    }
    for member in &members {
        for notice in member.health.notices() {
            emit(notice);
        }
    }
    emit(transport_metrics(&members, &schedule));
    emit(
        json!({"event":"metrics","packets_per_member":packets,"frames":schedule.frames,"silence_packets":silence_packets,"max_send_lateness_us":schedule.max_lateness_us,"retransmits":members.iter().map(|m|m.media.metrics.retransmits_sent).sum::<u64>(),"ptp_packets_received":ptp.as_ref().map(|p|p.received.load(Ordering::Relaxed)),"measured_latency_ms":null,"qualified":false}),
    );
    drop(volume_workers);
    for m in &mut members {
        m.close();
    }
    #[cfg(windows)]
    if let Some(microphone) = microphone {
        let recording = microphone.finish()?;
        emit(
            json!({"event":"microphone_recorded","path":recording.path,"sample_rate":recording.sample_rate,"frames":recording.frames}),
        );
    }
    outcome?;
    emit(json!({"event":"stopped","cancelled":cancel.is_cancelled()}));
    Ok(())
}

fn transport_metrics(members: &[Member], schedule: &MediaSchedule) -> Json {
    json!({"event":"transport_metrics","session_uptime_ms":schedule.start.elapsed().as_millis(),"sender_late_recoveries":schedule.recoveries,"skipped_packets":schedule.skipped_packets,"max_send_lateness_us":schedule.max_lateness_us,"members":members.iter().map(Member::metrics).collect::<Vec<_>>()})
}
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
    fn unsafe_probe_rejected() {
        let mut o = ProbeOptions {
            peers: vec![Peer {
                host: "127.0.0.1".parse().unwrap(),
                port: 7000,
                codecs: Vec::new(),
                profile: crate::profile::Profile::default(),
            }],
            wav_path: PathBuf::new(),
            source: "wav".into(),
            capture_endpoint: None,
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
