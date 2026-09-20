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
    #[serde(default = "default_sample_rate")]
    pub sample_rate: u32,
    #[serde(default = "default_timing")]
    pub timing: String,
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
        bounded.validate()
    }
    pub fn validate(&self) -> Result<()> {
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
    fn connect(
        peer: &Peer,
        options: &ProbeOptions,
        clock_id: Option<u64>,
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
        let mut packetizer = Packetizer::with_rate(audio_key, rand::random(), initial_rtp, ssrc, rate);
        let use_alac =
            !peer.codecs.is_empty() && !peer.codecs.contains(&0) && peer.codecs.contains(&1);
        ensure!(
            peer.codecs.is_empty() || peer.codecs.contains(&0) || use_alac,
            "receiver supports neither PCM nor ALAC"
        );
        if use_alac {
            packetizer.enable_alac();
        }
        let mut member = Self {
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
            ("timingProtocol", string(options.timing.to_uppercase())),
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
        let latency = (options.latency_ms as u64 * rate as u64) / 1000;
        let stream = dictionary(vec![
            ("audioFormat", number(crate::rtp::audio_format(use_alac, rate))),
            ("audioMode", string("default")),
            ("controlPort", number(member.media.control_port()? as u64)),
            ("ct", number(if use_alac { 2 } else { 1 })),
            ("isMedia", Value::Boolean(true)),
            ("latencyMin", number(latency)),
            ("latencyMax", number(latency)),
            ("shk", Value::Data(audio_key.to_vec())),
            ("spf", number(FRAMES as u64)),
            ("sr", number(rate as u64)),
            ("type", number(96)),
            ("supportsDynamicStreamID", Value::Boolean(false)),
            ("streamConnectionID", number(ssrc as u64)),
        ]);
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
        member.remote_control = get_port(stream, "controlPort")?;
        let data_port = get_port(stream, "dataPort")?;
        member.media.set_control_port(member.remote_control);
        member
            .media
            .connect_audio(SocketAddr::new(peer.host, data_port))?;
        let receiver_latency = stream
            .as_dictionary()
            .and_then(|d| d.get("latencyMin"))
            .and_then(Value::as_unsigned_integer);
        let effective_latency = receiver_latency
            .filter(|v| *v > 0 && *v <= rate as u64 * 10)
            .map(|samples| Duration::from_nanos(samples * 1_000_000_000 / rate as u64));
        member.media.configure_latency(
            effective_latency.unwrap_or(Duration::from_millis(u64::from(options.latency_ms))),
            effective_latency.is_none(),
        );
        emit(
            json!({"event":"negotiated","host":peer.host,"data_port":data_port,"codec":if use_alac{"alac"}else{"pcm"},"sample_rate":rate,"control_port":member.remote_control,"requested_latency_ms":options.latency_ms,"receiver_latency_min_samples":stream.as_dictionary().and_then(|d|d.get("latencyMin")).and_then(Value::as_unsigned_integer),"measured_latency_ms":null}),
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
        self.conn
            .lock()
            .unwrap()
            .request("FLUSH", &self.uri, &headers, &[])?;
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
        json!({"host":self.peer.host, "media":self.media.metrics(), "health":self.health.snapshot()})
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
    let clock = Clock::new();
    let peers: Vec<_> = options.peers.iter().map(|p| p.host).collect();
    let ntp = NtpServer::start(peers.clone(), clock.clone())?;
    let ptp = if options.timing == "ptp" {
        Some(PtpMaster::start(peers, clock.clone())?)
    } else {
        None
    };
    let clock_id = ptp.as_ref().map(|p| p.clock_id);
    let initial_rtp = rand::random::<u32>();
    let mut members = Vec::new();
    for peer in &options.peers {
        cancel.check()?;
        members.push(
            Member::connect(
                peer,
                &options,
                clock_id,
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
    for member in &mut members {
        cancel.check()?;
        member
            .record()
            .with_context(|| format!("RECORD {}", member.peer.host))
            .map_err(|error| Fault::from_error(member.peer.host, "control", &error))?;
    }
    let rate = options.sample_rate;
    let live = if options.source == "loopback" {
        Some(crate::live::Loopback::start(
            options.capture_endpoint.clone(),
            rate,
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
    let mut next_metrics = start + Duration::from_secs(1);
    let mut last_marker = 0u64;
    let mut next_sync = start;
    emit(
        json!({"event":"streaming","members":members.len(),"clock_id":clock_id,"first_send_unix_ns":clock.now_ns(),"first_send_qpc_ns":crate::wasapi::qpc_ns(),"requested_latency_ms":options.latency_ms,"sample_rate":rate,"measured_latency_ms":null,"qualified":false}),
    );
    let mut pcm = vec![0; PCM_BYTES];
    let outcome = (|| -> Result<()> {
        while (options.duration_ms == 0 || start.elapsed() < duration) && !cancel.is_cancelled() {
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
                    member.media.packetizer.skip_packets(skipped);
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
                    m.media
                        .sync(clock.now_ns(), clock_id, latency, packets == 0);
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
                member.media.service_retransmits();
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
        json!({"event":"metrics","packets_per_member":packets,"frames":schedule.frames,"max_send_lateness_us":schedule.max_lateness_us,"retransmits":members.iter().map(|m|m.media.metrics.retransmits_sent).sum::<u64>(),"ptp_packets_received":ptp.as_ref().map(|p|p.received.load(Ordering::Relaxed)),"measured_latency_ms":null,"qualified":false}),
    );
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
            }],
            wav_path: PathBuf::new(),
            source: "wav".into(),
            capture_endpoint: None,
            duration_ms: 5001,
            latency_ms: 200,
            gain: 0.1,
            sample_rate: 44100,
            timing: "ptp".into(),
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
        o.latency_ms = 0;
        assert!(o.validate().is_ok());
        o.sample_rate = 48000;
        assert!(o.validate().is_ok());
        o.sample_rate = 96000;
        assert!(o.validate().is_err());
    }
}
