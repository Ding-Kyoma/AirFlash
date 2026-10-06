//! AirPlay member orchestration, live loopback and finite quiet qualification.
use crate::{
    auth,
    buffered::{BufferedTransport, Media},
    capabilities::{Capabilities, Profile, Service, Transport},
    clock::{Clock, PtpMaster},
    crypto::derive,
    rtp::{self, FRAMES, PCM_BYTES, Packetizer},
    rtsp::{Cancellation, Connection, Rejected},
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
    pub services: Vec<Service>,
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
    #[serde(default = "default_transport")]
    pub transport: String,
    #[serde(default)]
    pub compatibility_buffer_ms: Option<u32>,
    #[serde(default)]
    pub group_id: Option<String>,
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
fn default_transport() -> String {
    "legacy".into()
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
            self.peers.iter().all(|p| p.host.is_ipv4()
                && p.port > 0
                && p.services.len() <= 16
                && p.services
                    .iter()
                    .all(|s| s.host.is_ipv4() && s.port > 0 && s.txt.len() <= 128)),
            "IPv4 endpoint required"
        );
        if self.peers.len() == 2 {
            ensure!(self.peers[0].host != self.peers[1].host, "duplicate peer");
        }
        ensure!(
            (1..=5000).contains(&self.duration_ms),
            "probe duration must be 1..5000 ms"
        );
        ensure!(
            (0..=10000).contains(&self.latency_ms),
            "latency must be 0..10000 ms"
        );
        ensure!(
            self.gain.is_finite() && (0.0..=0.1).contains(&self.gain),
            "probe gain must be 0..0.1"
        );
        ensure!(
            matches!(self.timing.as_str(), "auto" | "ptp" | "ntp"),
            "timing must be auto, ptp or ntp"
        );
        ensure!(
            matches!(
                self.transport.as_str(),
                "legacy" | "auto" | "realtime" | "buffered"
            ),
            "unknown media transport"
        );
        ensure!(
            self.compatibility_buffer_ms.is_none_or(|v| v <= 10000),
            "compatibility buffer must be 0..10000 ms"
        );
        ensure!(
            self.peers.len() == 1 || self.transport != "buffered",
            "buffered transport supports a single receiver"
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
struct PreparedPeer {
    peer: Peer,
    conn: Connection,
    capabilities: Capabilities,
    profile: Profile,
}
impl PreparedPeer {
    fn inspect(
        peer: &Peer,
        options: &ProbeOptions,
        cancel: Cancellation,
        emit: &dyn Fn(Json),
    ) -> Result<Self> {
        let mut endpoints = vec![(peer.host, peer.port)];
        let mut services: Vec<_> = peer.services.iter().collect();
        services.sort_by_key(|s| !s.service_type.starts_with("_airplay"));
        for service in services {
            if !endpoints.contains(&(service.host, service.port)) {
                endpoints.push((service.host, service.port));
            }
        }
        let mut last = None;
        for (host, port) in endpoints {
            cancel.check()?;
            emit(json!({"event":"phase","host":host,"phase":"info"}));
            let attempt = (|| -> Result<_> {
                let mut conn = Connection::connect(SocketAddr::new(host, port), cancel.clone())?;
                let info = conn.request("GET", "/info", &[], &[])?.plist()?;
                let capabilities = Capabilities::read(&peer.services, &peer.codecs, &info)?;
                let buffer = if options.transport != "legacy"
                    && options.peers.len() == 1
                    && !capabilities.homepod()
                {
                    options.compatibility_buffer_ms.unwrap_or(3000)
                } else {
                    options.latency_ms
                };
                let profile = Profile::resolve(
                    &capabilities,
                    &options.timing,
                    &options.transport,
                    options.sample_rate,
                    buffer,
                    options.peers.len() == 2,
                )?;
                let mut resolved = peer.clone();
                resolved.host = host;
                resolved.port = port;
                emit(
                    json!({"event":"peer_info","host":host,"requested_host":peer.host,"model":capabilities.model,"source_version":info.as_dictionary().and_then(|d|d.get("sourceVersion")).and_then(Value::as_string)}),
                );
                Ok(Self {
                    peer: resolved,
                    conn,
                    capabilities,
                    profile,
                })
            })();
            match attempt {
                Ok(p) => return Ok(p),
                Err(e) => {
                    let terminal = e.downcast_ref::<Fault>().is_some()
                        || e.downcast_ref::<Rejected>()
                            .is_some_and(|r| matches!(r.status, 401 | 403 | 470));
                    if terminal {
                        return Err(classify_auth(host, e));
                    }
                    last = Some(e);
                }
            }
        }
        Err(last.unwrap_or_else(|| anyhow::anyhow!("no receiver endpoint")))
    }
}
fn classify_auth(host: IpAddr, error: anyhow::Error) -> anyhow::Error {
    if let Some(rejected) = error.downcast_ref::<Rejected>() {
        if matches!(rejected.status, 401 | 403 | 470) {
            let code = if rejected.status == 470 {
                "pairing_required"
            } else {
                "authentication_failed"
            };
            return Fault::new(host, "authentication", code, false, &error).into();
        }
    }
    error
}
struct Member {
    peer: Peer,
    profile: Profile,
    conn: std::sync::Arc<std::sync::Mutex<Connection>>,
    events: Option<EventWorker>,
    feedback: Option<FeedbackWorker>,
    health: Health,
    media: Media,
    uri: String,
    ready: bool,
    closed: bool,
    session_started: bool,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum SetupStage {
    Session,
    Audio,
}
impl std::fmt::Display for SetupStage {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            Self::Session => "session SETUP",
            Self::Audio => "audio stream SETUP",
        })
    }
}
impl Member {
    fn connect(
        prepared: PreparedPeer,
        options: &ProbeOptions,
        clock_id: Option<u64>,
        ntp_port: u16,
        initial_rtp: u32,
        emit: &dyn Fn(Json),
    ) -> Result<Self> {
        let PreparedPeer {
            peer,
            mut conn,
            capabilities,
            profile,
        } = prepared;
        let local = conn.local_addr()?.ip();
        let ssrc = rand::random::<u32>();
        let uri = format!("rtsp://{local}/{ssrc}");
        if capabilities.restricted {
            return Err(Fault::new(
                peer.host,
                "authentication",
                "access_restricted",
                false,
                "Receiver restricts access to its configured home/current user",
            )
            .into());
        }
        let saved = if capabilities.device_id.is_empty() {
            None
        } else {
            crate::credentials::load(&crate::credentials::directory(), &capabilities.device_id)?
        };
        if saved.is_none() && capabilities.needs_password() {
            return Err(Fault::new(peer.host, "authentication", "password_required", false, "AirPlay access passwords are not supported; use a receiver without an access password").into());
        }
        if saved.is_none() && capabilities.needs_pin() {
            return Err(Fault::new(
                peer.host,
                "authentication",
                "pairing_required",
                false,
                "Pair this receiver in Settings > Receivers, then reconnect",
            )
            .into());
        }
        emit(json!({"event":"phase","host":peer.host,"phase":"authenticate"}));
        let secret = (if let Some(credentials) = saved {
            auth::verify(&mut conn, &credentials)
        } else {
            auth::transient(&mut conn)
        })
        .map_err(|e| {
            let e = classify_auth(peer.host, e);
            if e.downcast_ref::<Fault>().is_some() {
                return e;
            }
            let message = format!("{e:#}");
            let code = if message.contains("signature") || message.contains("identity") {
                "signature_invalid"
            } else {
                "authentication_failed"
            };
            Fault::new(peer.host, "authentication", code, false, message).into()
        })?;
        emit(json!({"event":"phase","host":peer.host,"phase":"authenticated"}));
        let audio_key = derive(&secret, "Events-Salt", "Events-Write-Encryption-Key");
        let mut packetizer =
            Packetizer::with_rate(audio_key, rand::random(), initial_rtp, ssrc, profile.rate);
        if profile.alac {
            packetizer.enable_alac();
        }
        let media = if profile.transport == Transport::Buffered {
            Media::Buffered(Box::new(BufferedTransport::new(packetizer)))
        } else {
            let control = UdpSocket::bind(SocketAddr::new(local, 0))?;
            let audio = UdpSocket::bind(SocketAddr::new(local, 0))?;
            Media::Realtime(Box::new(MediaTransport::new(
                packetizer,
                audio,
                control,
                SocketAddr::new(peer.host, 0),
            )?))
        };
        let mut member = Self {
            peer,
            profile,
            conn: std::sync::Arc::new(std::sync::Mutex::new(conn)),
            events: None,
            feedback: None,
            health: Health::default(),
            media,
            uri,
            ready: false,
            closed: false,
            session_started: false,
        };
        let sender_id = "02:57:32:41:50:01";
        let mut setup = vec![
            ("deviceID", string(sender_id)),
            ("macAddress", string(sender_id)),
            ("name", string("AirFlash")),
            ("sessionUUID", string(Uuid::new_v4().to_string())),
            (
                "timingProtocol",
                string(member.profile.timing.to_uppercase()),
            ),
            (
                "isMultiSelectAirPlay",
                Value::Boolean(member.profile.legacy || options.peers.len() == 2),
            ),
            ("groupContainsGroupLeader", Value::Boolean(false)),
            ("senderSupportsRelay", Value::Boolean(false)),
        ];
        if let Some(group) = &options.group_id {
            setup.push(("groupUUID", string(group)));
        }
        if let Some(id) = clock_id.filter(|_| member.profile.timing == "ptp") {
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
            json!({"event":"phase","host":member.peer.host,"phase":"setup_session","timing":member.profile.timing}),
        );
        let response = member
            .conn
            .lock()
            .unwrap()
            .plist_request("SETUP", &member.uri, &dictionary(setup))
            .context(SetupStage::Session)?
            .plist()?;
        member.session_started = true;
        let event_port = get_port(&response, "eventPort")?;
        member.events = Some(EventWorker::connect(
            SocketAddr::new(member.peer.host, event_port),
            &secret,
            member.health.clone(),
        )?);
        // The validated HomePod stereo/legacy path retains its existing order.
        if !member.profile.legacy {
            member
                .conn
                .lock()
                .unwrap()
                .request("RECORD", &member.uri, &[], &[])?;
        }
        if member.profile.timing == "ptp" {
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
        let rate = member.profile.rate;
        let latency = u64::from(member.profile.latency_ms) * u64::from(rate) / 1000;
        let mut fields = vec![
            (
                "audioFormat",
                number(rtp::audio_format(member.profile.alac, rate)),
            ),
            ("audioMode", string("default")),
            ("ct", number(if member.profile.alac { 2 } else { 1 })),
            ("isMedia", Value::Boolean(true)),
            ("shk", Value::Data(audio_key.to_vec())),
            ("spf", number(FRAMES as u64)),
            ("sr", number(rate as u64)),
            (
                "type",
                number(if member.profile.transport == Transport::Buffered {
                    103
                } else {
                    96
                }),
            ),
            ("supportsDynamicStreamID", Value::Boolean(false)),
            ("streamConnectionID", number(ssrc as u64)),
        ];
        if member.profile.transport == Transport::Realtime {
            fields.push(("controlPort", number(member.media.control_port()? as u64)));
            fields.push(("latencyMin", number(latency)));
            fields.push((
                "latencyMax",
                number(if member.profile.legacy {
                    latency
                } else {
                    rate as u64 * 10
                }),
            ));
        } else {
            fields.push(("audioBufferSize", number(rate as u64 * 4 * 10)));
        }
        emit(
            json!({"event":"phase","host":member.peer.host,"phase":"setup_audio","requested_latency_ms":options.latency_ms}),
        );
        let response = member
            .conn
            .lock()
            .unwrap()
            .plist_request(
                "SETUP",
                &member.uri,
                &dictionary(vec![("streams", Value::Array(vec![dictionary(fields)]))]),
            )
            .context(SetupStage::Audio)?
            .plist()?;
        let stream = response
            .as_dictionary()
            .and_then(|d| d.get("streams"))
            .and_then(Value::as_array)
            .and_then(|a| a.first())
            .context("missing stream response")?;
        let data_port = get_port(stream, "dataPort")?;
        let remote_control = if member.profile.transport == Transport::Realtime {
            get_port(stream, "controlPort")?
        } else {
            0
        };
        member.media.set_control_port(remote_control);
        member
            .media
            .connect_audio(SocketAddr::new(member.peer.host, data_port))?;
        let fields = stream.as_dictionary().context("invalid stream response")?;
        let min = fields
            .get("latencyMin")
            .and_then(Value::as_unsigned_integer);
        let max = fields
            .get("latencyMax")
            .and_then(Value::as_unsigned_integer);
        if member.profile.transport == Transport::Realtime {
            if let Some(min) = min {
                ensure!(
                    min <= rate as u64 * 10,
                    "receiver minimum exceeds ten-second buffering limit"
                );
            }
            if let (Some(min), Some(max)) = (min, max) {
                ensure!(max >= min, "invalid receiver latency range");
            }
            let mut effective = if member.profile.legacy {
                min.unwrap_or(latency)
            } else {
                latency.max(min.unwrap_or(0))
            };
            if let Some(max) = max.filter(|v| *v > 0) {
                effective = effective.min(max);
            }
            member.profile.latency_ms = (effective * 1000).div_ceil(rate as u64) as u32;
        }
        member.media.configure_latency(
            Duration::from_millis(member.profile.latency_ms.into()),
            min.is_none(),
        );
        emit(
            json!({"event":"negotiated","host":member.peer.host,"data_port":data_port,"control_port":remote_control,
            "codec":if member.profile.alac {"alac"} else {"pcm"},"sample_rate":rate,"timing":member.profile.timing,
            "transport":member.profile.transport.name(),"requested_latency_ms":options.latency_ms,
            "effective_latency_ms":member.profile.latency_ms,"receiver_latency_min_samples":min,
            "fallback_reasons":member.profile.reasons,"measured_latency_ms":null}),
        );
        Ok(member)
    }
    fn record(
        &mut self,
        clock: &Clock,
        clock_id: Option<u64>,
        cancel: &Cancellation,
    ) -> Result<()> {
        if self.profile.legacy {
            let headers = [
                ("Range", "npt=0-".into()),
                (
                    "RTP-Info",
                    format!(
                        "seq={};rtptime={}",
                        self.media.packetizer().seq,
                        self.media.packetizer().timestamp
                    ),
                ),
            ];
            self.conn
                .lock()
                .unwrap()
                .request("RECORD", &self.uri, &headers, &[])?;
            self.conn
                .lock()
                .unwrap()
                .request("FLUSH", &self.uri, &headers, &[])?;
        } else if self.profile.transport == Transport::Buffered {
            let deadline = Instant::now() + Duration::from_secs(6);
            loop {
                cancel.check()?;
                let anchor = clock.now_ns() + u64::from(self.profile.latency_ms) * 1_000_000;
                let result = self.conn.lock().unwrap().plist_request(
                    "SETRATEANCHORTIME",
                    &self.uri,
                    &anchor_body(
                        self.media.packetizer().timestamp,
                        clock_id.context("buffered audio requires a PTP clock")?,
                        anchor,
                    ),
                );
                match result {
                    Ok(_) => break,
                    Err(e)
                        if e.downcast_ref::<Rejected>()
                            .is_some_and(|r| r.status == 400)
                            && Instant::now() < deadline =>
                    {
                        let until = Instant::now() + Duration::from_millis(500);
                        while Instant::now() < until {
                            cancel.check()?;
                            thread::sleep(Duration::from_millis(10));
                        }
                    }
                    Err(e) => return Err(classify_auth(self.peer.host, e)),
                }
            }
        }
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
    fn recover_buffered(
        &mut self,
        clock: &Clock,
        id: Option<u64>,
        cancel: &Cancellation,
    ) -> Result<()> {
        if self.profile.transport == Transport::Buffered {
            self.media.quiesce(cancel)?;
            let p = self.media.packetizer();
            self.conn.lock().unwrap().plist_request(
                "FLUSHBUFFERED",
                &self.uri,
                &dictionary(vec![
                    ("flushUntilSeq", number(p.seq.into())),
                    ("flushUntilTS", number(p.timestamp.into())),
                ]),
            )?;
            self.record(clock, id, cancel)?;
        }
        Ok(())
    }
    fn metrics(&self) -> Json {
        json!({"host":self.peer.host,"media":self.media.metrics(),"health":self.health.snapshot()})
    }
    fn close(&mut self) {
        if self.closed {
            return;
        }
        self.closed = true;
        self.feedback.take();
        if self.session_started {
            if self.profile.transport == Transport::Buffered {
                // Stop the media socket before flushing so late frames cannot re-fill the receiver.
                self.media.stop_buffered();
                let packetizer = self.media.packetizer();
                let body = dictionary(vec![
                    ("flushUntilSeq", number(packetizer.seq.into())),
                    ("flushUntilTS", number(packetizer.timestamp.into())),
                ]);
                let _ = self.conn.lock().unwrap().finish_buffered(&self.uri, &body);
            }
            let _ = self.conn.lock().unwrap().finish_session(&self.uri);
        }
        self.events.take();
        self.ready = false;
    }
}
fn anchor_body(rtp: u32, clock_id: u64, ns: u64) -> Value {
    let frac = ((u128::from(ns % 1_000_000_000) << 64) / 1_000_000_000) as u64;
    dictionary(vec![
        ("networkTimeTimelineID", number(clock_id)),
        ("networkTimeSecs", number(ns / 1_000_000_000)),
        ("networkTimeFrac", Value::Integer((frac as i64).into())),
        ("rtpTime", number(rtp.into())),
        ("rate", number(1)),
    ])
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
    probe_inner(options, cancel, gain, volume, equalizer, emit, [319, 320])
}
#[cfg(test)]
pub(crate) fn probe_local(
    options: ProbeOptions,
    cancel: Cancellation,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    emit: impl Fn(Json),
) -> Result<()> {
    let equalizer = crate::equalizer::Control::new(options.equalizer, options.sample_rate)?;
    probe_inner(
        options,
        cancel,
        gain,
        crate::volume::Control::default(),
        equalizer,
        emit,
        [0, 0],
    )
}
fn probe_inner(
    mut options: ProbeOptions,
    cancel: Cancellation,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    volume: crate::volume::Control,
    equalizer: crate::equalizer::Control,
    emit: impl Fn(Json),
    ptp_ports: [u16; 2],
) -> Result<()> {
    if options.duration_ms == 0 {
        options.validate_start()?;
    } else {
        options.validate()?;
    }
    let requested_rate = options.sample_rate;
    let mut samples = if options.source == "wav" {
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
    let clock = Clock::new();
    let mut prepared = Vec::new();
    for peer in &options.peers {
        prepared.push(
            PreparedPeer::inspect(peer, &options, cancel.clone(), &emit)
                .map_err(|e| Fault::from_error(peer.host, "capabilities", &e))?,
        );
    }
    // One capture/resampler is shared by the existing stereo pair.
    if options.transport != "legacy"
        && prepared.len() == 2
        && prepared.iter().any(|p| !p.capabilities.homepod())
    {
        return Err(Fault::new("", "capabilities", "unsupported_group", false, "Experimental third-party playback supports one receiver; multi-target playback requires an existing HomePod stereo pair").into());
    }
    options.sample_rate = prepared.iter().map(|p| p.profile.rate).min().unwrap();
    for p in &mut prepared {
        if p.profile.rate != options.sample_rate {
            ensure!(
                p.capabilities
                    .supports_format(p.profile.alac, options.sample_rate),
                "stereo pair has no shared format"
            );
            p.profile.rate = options.sample_rate;
            p.profile.reasons.push("stereo_shared_sample_rate".into());
        }
    }
    let peers: Vec<_> = prepared.iter().map(|p| p.peer.host).collect();
    let ntp = NtpServer::start(peers.clone(), clock.clone())?;
    let needs_ptp = prepared.iter().any(|p| p.profile.timing == "ptp");
    let ptp = if needs_ptp {
        match PtpMaster::start_with_ports(peers, clock.clone(), ptp_ports) {
            Ok(master) => Some(master),
            Err(e)
                if options.timing == "auto"
                    && options.transport != "buffered"
                    && prepared
                        .iter()
                        .all(|p| p.capabilities.ntp() && p.capabilities.realtime()) =>
            {
                for p in &mut prepared {
                    p.profile.timing = "ntp".into();
                    p.profile.transport = Transport::Realtime;
                    p.profile
                        .reasons
                        .push("ptp_unavailable_ntp_fallback".into());
                }
                emit(
                    json!({"event":"warning","code":"ptp_unavailable","channel":"timing","message":e.to_string(),"recovered":true}),
                );
                None
            }
            Err(e) => return Err(Fault::from_error(options.peers[0].host, "timing", &e).into()),
        }
    } else {
        None
    };
    let clock_id = ptp.as_ref().map(|p| p.clock_id);
    let initial_rtp = rand::random::<u32>();
    let mut members = Vec::new();
    for p in prepared {
        cancel.check()?;
        let peer = p.peer.clone();
        let cap = p.capabilities.clone();
        let original = p.profile.clone();
        let result = Member::connect(p, &options, clock_id, ntp.port, initial_rtp, &emit);
        let member = match result {
            Ok(m) => m,
            Err(e) => {
                let rejection = e.downcast_ref::<Rejected>().is_some_and(|r| {
                    r.method == "SETUP" && matches!(r.status, 400 | 415 | 461 | 501)
                });
                let transport_fallback = options.transport == "auto"
                    && !original.legacy
                    && rejection
                    && e.downcast_ref::<SetupStage>() == Some(&SetupStage::Audio);
                let format_fallback = !original.legacy
                    && original.rate == 48000
                    && cap.supports_format(original.alac, 44100)
                    && e.downcast_ref::<SetupStage>() == Some(&SetupStage::Audio)
                    && e.downcast_ref::<Rejected>()
                        .is_some_and(|r| r.status == 415);
                let timing_fallback = options.timing == "auto"
                    && options.transport != "buffered"
                    && original.timing == "ptp"
                    && cap.ntp()
                    && cap.realtime()
                    && rejection
                    && e.downcast_ref::<SetupStage>() == Some(&SetupStage::Session);
                if !transport_fallback && !timing_fallback && !format_fallback {
                    return Err(Fault::from_error(peer.host, "setup", &e).into());
                }
                let mut retry = PreparedPeer::inspect(&peer, &options, cancel.clone(), &emit)?;
                if format_fallback {
                    retry.profile = original.clone();
                    retry.profile.rate = 44100;
                    retry
                        .profile
                        .reasons
                        .push("sample_rate_rejected_fallback".into());
                } else if timing_fallback {
                    retry.profile.timing = "ntp".into();
                    retry.profile.transport = Transport::Realtime;
                    retry
                        .profile
                        .reasons
                        .push("ptp_rejected_ntp_fallback".into());
                } else if original.transport == Transport::Buffered && cap.realtime() {
                    retry.profile.transport = Transport::Realtime;
                    retry
                        .profile
                        .reasons
                        .push("buffered_rejected_realtime_fallback".into());
                } else if cap.buffered() && original.timing == "ptp" && original.alac {
                    retry.profile.transport = Transport::Buffered;
                    retry
                        .profile
                        .reasons
                        .push("realtime_rejected_buffered_fallback".into());
                } else {
                    return Err(Fault::from_error(peer.host, "setup", &e).into());
                }
                Member::connect(retry, &options, clock_id, ntp.port, initial_rtp, &emit)
                    .map_err(|e| Fault::from_error(peer.host, "setup", &e))?
            }
        };
        members.push(member);
    }
    options.sample_rate = members.iter().map(|m| m.profile.rate).min().unwrap();
    if requested_rate != options.sample_rate && !samples.is_empty() {
        samples = resample_probe(&samples, requested_rate, options.sample_rate);
    }
    equalizer.reconfigure(options.sample_rate)?;
    if options.handshake_only {
        emit(
            json!({"event":"handshake_complete","members":members.len(),"audio_sent":false,"qualified":false}),
        );
        return Ok(());
    }
    for member in &mut members {
        cancel.check()?;
        member
            .record(&clock, clock_id, &cancel)
            .map_err(|e| Fault::from_error(member.peer.host, "control", &e))?;
    }
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
    let duration = Duration::from_millis(options.duration_ms as u64);
    let latency = options.latency_ms * rate / 1000;
    let mut schedule = MediaSchedule::new(start, rate);
    let mut packets = 0u64;
    for member in &mut members {
        member.start_feedback(start)?;
    }
    let volume_worker = crate::volume::Worker::start_with_health(
        members
            .iter()
            .map(|m| (m.peer.host.to_string(), m.uri.clone(), m.conn.clone()))
            .collect(),
        volume,
        members.iter().map(|m| m.health.clone()).collect(),
    )?;
    let mut next_metrics = start + Duration::from_secs(1);
    let mut last_marker = 0u64;
    let mut next_sync = start;
    let mut ptp_unobserved = false;
    emit(
        json!({"event":"streaming","members":members.len(),"clock_id":clock_id,"first_send_unix_ns":clock.now_ns(),"first_send_qpc_ns":crate::wasapi::qpc_ns(),"requested_latency_ms":options.latency_ms,"sample_rate":rate,"measured_latency_ms":null,"qualified":false}),
    );
    let mut pcm = vec![0; PCM_BYTES];
    let outcome = (|| -> Result<()> {
        while (options.duration_ms == 0 || start.elapsed() < duration) && !cancel.is_cancelled() {
            for event in volume_worker.events.try_iter() {
                emit(event);
            }
            for member in &members {
                for notice in member.health.notices() {
                    emit(notice);
                }
                member.health.check()?;
            }
            let deadline = schedule.deadline();
            let now = Instant::now();
            if now < deadline {
                thread::sleep((deadline - now).min(Duration::from_millis(2)));
                continue;
            }
            let skipped = schedule.recover(now, options.duration_ms == 0)?;
            if skipped > 0 {
                for member in &mut members {
                    member.media.packetizer_mut().skip_packets(skipped);
                    member.recover_buffered(&clock, clock_id, &cancel)?;
                }
                if let Some(source) = &live {
                    source.discard_stale();
                }
                next_sync = now;
                emit(
                    json!({"event":"warning","code":"sender_late_recovered","channel":"scheduler","host":null,"recovered":true,"skipped_packets":skipped,"message":"Sender discarded expired audio slots and recovered its timeline"}),
                );
            }
            if now >= next_sync {
                for m in &mut members {
                    m.media.sync(
                        clock.now_ns(),
                        if m.profile.timing == "ptp" {
                            clock_id
                        } else {
                            None
                        },
                        if m.profile.legacy {
                            latency
                        } else {
                            m.profile.latency_ms * rate / 1000
                        },
                        packets == 0,
                    );
                }
                next_sync = now + Duration::from_millis(100);
            }
            if let Some(source) = &live {
                let (packet, marker) =
                    source.packet(f32::from_bits(gain.load(Ordering::Relaxed)))?;
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
                if let Some(master) = &ptp {
                    let unobserved = master.exchanges.load(Ordering::Relaxed) == 0;
                    emit(
                        json!({"event":"timing_metrics","timing":"ptp","packets_received":master.received.load(Ordering::Relaxed),"exchanges":master.exchanges.load(Ordering::Relaxed),"clock_locked":null}),
                    );
                    if start.elapsed() >= Duration::from_secs(5) && unobserved != ptp_unobserved {
                        ptp_unobserved = unobserved;
                        emit(
                            json!({"event":"warning","host":options.peers[0].host,"channel":"timing","code":"ptp_exchange_unobserved","recovered":!unobserved,
                            "message":if unobserved {"No receiver PTP exchange observed; clock lock and audible playback are unverified"} else {"Receiver PTP exchange observed; clock lock is still unverified"}}),
                        );
                    }
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
                    &pcm,
                    packets == 0,
                    Instant::now(),
                    schedule.deadline(),
                    &member.health,
                )?;
            }
            for member in &mut members {
                member.media.service()?;
            }
            schedule.sent();
            packets += 1;
        }
        Ok(())
    })();
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
        json!({"event":"metrics","packets_per_member":packets,"frames":schedule.frames,"max_send_lateness_us":schedule.max_lateness_us,"retransmits":members.iter().map(|m|m.media.retransmits()).sum::<u64>(),"ptp_packets_received":ptp.as_ref().map(|p|p.received.load(Ordering::Relaxed)),"ptp_exchanges":ptp.as_ref().map(|p|p.exchanges.load(Ordering::Relaxed)),"clock_locked":null,"measured_latency_ms":null,"qualified":false}),
    );
    if outcome.is_ok() && options.duration_ms > 0 && !cancel.is_cancelled() {
        for m in &mut members {
            m.media
                .drain(&cancel, Duration::from_millis(m.profile.latency_ms.into()))?;
        }
    }
    drop(volume_worker);
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

fn resample_probe(samples: &[i16], input: u32, output: u32) -> Vec<i16> {
    let frames = samples.len() / 2;
    let count = frames as u64 * u64::from(output) / u64::from(input);
    (0..count)
        .flat_map(|frame| {
            let position = frame as f64 * f64::from(input) / f64::from(output);
            let a = (position as usize).min(frames - 1);
            let b = (a + 1).min(frames - 1);
            let fraction = position - a as f64;
            std::array::from_fn::<_, 2, _>(|channel| {
                (samples[a * 2 + channel] as f64 * (1.0 - fraction)
                    + samples[b * 2 + channel] as f64 * fraction)
                    .round() as i16
            })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn ntp_responder_echoes_origin_and_timestamps_in_sample_clock_timebase() {
        let clock = Clock::new();
        let server = NtpServer::start(vec!["127.0.0.1".parse().unwrap()], clock.clone()).unwrap();
        let peer = UdpSocket::bind("127.0.0.1:0").unwrap();
        peer.set_read_timeout(Some(Duration::from_secs(1))).unwrap();
        let mut request = [0u8; 32];
        request[0] = 0x80;
        request[1] = 0xd2;
        request[24..32].copy_from_slice(&rtp::ntp(clock.now_ns()));
        peer.send_to(&request, ("127.0.0.1", server.port)).unwrap();
        let mut response = [0u8; 32];
        let (length, source) = peer.recv_from(&mut response).unwrap();
        assert_eq!(length, 32);
        assert_eq!(source.port(), server.port);
        assert_eq!(response[1], 0xd3);
        assert_eq!(&response[8..16], &request[24..32]);
        assert!(response[16..24] >= request[24..32]);
        assert!(response[24..32] >= response[16..24]);
    }
    #[test]
    fn buffered_recovery_flushes_before_reanchoring_without_reusing_nonce() {
        use std::{
            io::Read,
            net::TcpListener,
            sync::{Arc, Mutex},
        };
        let control = TcpListener::bind("127.0.0.1:0").unwrap();
        let media_listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let conn =
            Connection::connect(control.local_addr().unwrap(), Cancellation::default()).unwrap();
        let (socket, _) = control.accept().unwrap();
        let server = thread::spawn(move || {
            let mut c = Connection::from_stream(socket, Cancellation::default()).unwrap();
            let flush = c.read().unwrap();
            assert!(flush.first.starts_with("FLUSHBUFFERED"));
            let body = flush.plist().unwrap();
            let d = body.as_dictionary().unwrap();
            assert_eq!(d["flushUntilSeq"].as_unsigned_integer(), Some(5));
            assert_eq!(
                d["flushUntilTS"].as_unsigned_integer(),
                Some(6 * FRAMES as u64)
            );
            crate::auth::protocol_tests::respond(&mut c, &flush, &[]);
            let anchor = c.read().unwrap();
            assert!(anchor.first.starts_with("SETRATEANCHORTIME"));
            let body = anchor.plist().unwrap();
            let d = body.as_dictionary().unwrap();
            assert_eq!(d["networkTimeTimelineID"].as_unsigned_integer(), Some(42));
            assert_eq!(d["rtpTime"].as_unsigned_integer(), Some(6 * FRAMES as u64));
            assert_eq!(d["rate"].as_unsigned_integer(), Some(1));
            assert!(d["networkTimeFrac"].as_signed_integer().is_some());
            crate::auth::protocol_tests::respond(&mut c, &anchor, &[]);
        });
        let key = [13; 32];
        let mut transport = BufferedTransport::new(Packetizer::new(key, 65535, 0, 7));
        transport
            .connect(media_listener.local_addr().unwrap())
            .unwrap();
        let (mut socket, _) = media_listener.accept().unwrap();
        socket
            .set_read_timeout(Some(Duration::from_secs(2)))
            .unwrap();
        let cap = Capabilities {
            features: Some((1 << 40) | (1 << 41)),
            ..Default::default()
        };
        let mut member = Member {
            peer: Peer {
                host: "127.0.0.1".parse().unwrap(),
                port: 7000,
                codecs: vec![],
                services: vec![],
            },
            profile: Profile::resolve(&cap, "auto", "auto", 44100, 3000, false).unwrap(),
            conn: Arc::new(Mutex::new(conn)),
            events: None,
            feedback: None,
            health: Health::default(),
            media: Media::Buffered(Box::new(transport)),
            uri: "rtsp://localhost/test".into(),
            ready: true,
            closed: false,
            session_started: false,
        };
        let pcm = vec![0; PCM_BYTES];
        let now = Instant::now();
        member
            .media
            .send(&pcm, true, now, now, &member.health)
            .unwrap();
        member.media.packetizer_mut().skip_packets(5);
        member
            .recover_buffered(&Clock::new(), Some(42), &Cancellation::default())
            .unwrap();
        member
            .media
            .send(&pcm, false, now, now, &member.health)
            .unwrap();
        let mut cipher = crate::crypto::Cipher::new(key);
        for nonce in [0u64, 1] {
            let mut prefix = [0; 2];
            socket.read_exact(&mut prefix).unwrap();
            let mut packet = vec![0; u16::from_be_bytes(prefix) as usize - 2];
            socket.read_exact(&mut packet).unwrap();
            assert_eq!(
                u64::from_le_bytes(packet[packet.len() - 8..].try_into().unwrap()),
                nonce
            );
            assert_eq!(
                cipher
                    .decrypt(&packet[12..packet.len() - 8], &packet[4..12])
                    .unwrap(),
                pcm
            );
        }
        server.join().unwrap();
    }
    #[test]
    fn unsafe_probe_rejected() {
        let mut o = ProbeOptions {
            peers: vec![Peer {
                host: "127.0.0.1".parse().unwrap(),
                port: 7000,
                codecs: Vec::new(),
                services: Vec::new(),
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
            transport: "legacy".into(),
            compatibility_buffer_ms: None,
            group_id: None,
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
