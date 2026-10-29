//! Session health and bounded media recovery, shared by production and localhost tests.
use crate::{
    crypto::derive,
    preemption::{self, Signal},
    rtp::{self, FRAMES, Packetizer, Retransmit},
    rtsp::{Cancellation, Connection, WireError},
};
use anyhow::{Context, Result};
use serde::Serialize;
use serde_json::{Value, json};
use std::{
    collections::VecDeque,
    net::{SocketAddr, UdpSocket},
    sync::{Arc, Mutex},
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

pub const FEEDBACK_INTERVAL: Duration = Duration::from_secs(2);
pub const FEEDBACK_SOFT_TIMEOUT: Duration = Duration::from_secs(4);
pub const FAILURE_TIMEOUT: Duration = Duration::from_secs(12);
pub const FEEDBACK_FAILURE_LIMIT: u32 = 3;
pub const LATE_LIMIT: Duration = Duration::from_millis(60);
const POLL: Duration = Duration::from_millis(20);
const RETRANSMIT_BUDGET: Duration = Duration::from_millis(1);
const MAX_REQUESTS: usize = 16;
const MAX_RESENDS: usize = 32;
const MAX_PENDING: usize = 512;

#[derive(Clone, Debug, Serialize)]
pub struct Fault {
    pub code: String,
    pub host: String,
    pub channel: String,
    pub retryable: bool,
    pub message: String,
    /// Structured sub-reason for faults that share one code. `PREEMPTED` uses
    /// `takeover` and `limit`; the desktop distinguishes the two messages.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}
impl std::fmt::Display for Fault {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "{} [{} {}: {}]",
            self.message, self.host, self.channel, self.code
        )
    }
}
impl std::error::Error for Fault {}
impl Fault {
    pub fn new(
        host: impl ToString,
        channel: &str,
        code: &str,
        retryable: bool,
        message: impl ToString,
    ) -> Self {
        Self {
            code: code.into(),
            host: host.to_string(),
            channel: channel.into(),
            retryable,
            message: message.to_string(),
            reason: None,
        }
    }
    pub fn with_reason(mut self, reason: &str) -> Self {
        self.reason = Some(reason.into());
        self
    }
    pub fn from_error(host: impl ToString, channel: &str, error: &anyhow::Error) -> Self {
        if let Some(fault) = error.downcast_ref::<Self>() {
            return fault.clone();
        }
        let (code, retryable) = if let Some(wire) = error.downcast_ref::<WireError>() {
            match wire {
                WireError::Cancelled => ("cancelled", false),
                WireError::PeerClosed => ("peer_closed", true),
                WireError::ReadTimeout => ("read_timeout", true),
                WireError::WriteTimeout | WireError::Poisoned => ("write_failed", true),
                WireError::Authentication => ("authentication_failed", false),
            }
        } else if let Some(io) = error.downcast_ref::<std::io::Error>() {
            match io.kind() {
                std::io::ErrorKind::ConnectionReset => ("connection_reset", true),
                _ => ("socket_error", true),
            }
        } else {
            ("protocol_error", false)
        };
        Self::new(host, channel, code, retryable, format!("{error:#}"))
    }
    pub fn event(&self) -> Value {
        let mut value = serde_json::to_value(self).unwrap();
        value["event"] = json!("error");
        value["qualified"] = json!(false);
        value
    }
}
pub fn error_event(error: &anyhow::Error) -> Value {
    if let Some(fault) = error.downcast_ref::<Fault>() {
        return fault.event();
    }
    // Older authentication/capture code carries anyhow context rather than Fault.
    let message = format!("{error:#}");
    let lower = message.to_lowercase();
    let authentication = [
        "pairing",
        "srp",
        "verification",
        "credentials",
        "signature",
        "identity",
        "authentication",
    ]
    .iter()
    .any(|s| lower.contains(s));
    json!({"event":"error", "message":message, "code":if authentication {"authentication_failed"} else {"engine_error"}, "host":null, "channel":null, "retryable":!authentication, "qualified":false})
}
#[derive(Default, Clone, Serialize)]
pub struct HealthSnapshot {
    pub event_messages: u64,
    pub feedback_successes: u64,
    pub feedback_failures: u64,
    pub feedback_rtt_ms: Option<f64>,
    pub feedback_delayed: bool,
}
#[derive(Default)]
struct HealthState {
    metrics: HealthSnapshot,
    failure: Option<Fault>,
    /// Control-plane trouble that must not stop healthy media. RTSP/event feedback
    /// and the UDP RTP stream are independent; an AP roam that kills the control
    /// socket should not end playback that is still arriving.
    degraded: Option<Fault>,
    notices: VecDeque<Value>,
    commands: crate::events::Inbox,
    /// Control-plane symptoms of a takeover. They are evidence only: the
    /// session thread probes the receiver's live `statusFlags` before deciding.
    preemption_signals: Vec<Signal>,
}
#[derive(Clone, Default)]
pub struct Health(Arc<Mutex<HealthState>>);
impl Health {
    pub fn snapshot(&self) -> HealthSnapshot {
        self.0.lock().unwrap().metrics.clone()
    }
    pub fn check(&self) -> Result<()> {
        if let Some(error) = &self.0.lock().unwrap().failure {
            return Err(error.clone().into());
        }
        Ok(())
    }
    pub fn notices(&self) -> Vec<Value> {
        self.0.lock().unwrap().notices.drain(..).collect()
    }
    /// Commands the receiver sent on the event channel, in arrival order.
    pub fn commands(&self) -> Vec<crate::events::ParsedEvent> {
        self.0.lock().unwrap().commands.drain()
    }
    pub fn commands_dropped(&self) -> u64 {
        self.0.lock().unwrap().commands.dropped()
    }
    fn fail(&self, fault: Fault) {
        let mut state = self.0.lock().unwrap();
        if state.failure.is_none() {
            state.failure = Some(fault);
        }
    }
    /// Record a control-plane fault without stopping the media stream.
    fn degrade(&self, fault: Fault) {
        let mut state = self.0.lock().unwrap();
        if state.degraded.is_none() {
            state.degraded = Some(fault);
        }
    }
    /// Control-plane verdict: a degraded control channel only ends the session when
    /// the media side is unhealthy too, or when the receiver rejected our session
    /// outright (reconnecting is the only fix for that).
    pub fn check_control(&self, media_healthy: bool) -> Result<()> {
        let degraded = self.0.lock().unwrap().degraded.clone();
        match degraded {
            Some(fault) if !media_healthy || matches!(fault.code.as_str(), "authentication_failed" | "session_rejected") => {
                Err(fault.into())
            }
            _ => Ok(()),
        }
    }
    pub fn control_degraded(&self) -> Option<Fault> {
        self.0.lock().unwrap().degraded.clone()
    }
    /// Forget a degraded control channel after it has been rebuilt. The media side
    /// keeps running; only the verdict is cleared.
    pub fn clear_degraded(&self) {
        self.0.lock().unwrap().degraded = None;
    }
    /// Record one control-plane symptom. Symptoms alone never end the session:
    /// the session thread drains them and probes the receiver's live status.
    pub fn preemption_signal(&self, signal: Signal) {
        let mut state = self.0.lock().unwrap();
        if state.preemption_signals.len() == 8 {
            state.preemption_signals.remove(0);
        }
        state.preemption_signals.push(signal);
    }
    /// Take the symptoms collected since the previous poll.
    pub fn take_preemption_signals(&self) -> Vec<Signal> {
        std::mem::take(&mut self.0.lock().unwrap().preemption_signals)
    }
    fn notice(&self, host: &str, channel: &str, code: &str, message: &str, recovered: bool) {
        let mut state = self.0.lock().unwrap();
        if state.notices.len() == 16 {
            state.notices.pop_front();
        }
        state.notices.push_back(json!({"event":"warning","host":host,"channel":channel,"code":code,"message":message,"recovered":recovered}));
    }
}
pub struct EventWorker {
    stop: Cancellation,
    worker: Option<JoinHandle<()>>,
}
impl EventWorker {
    pub fn connect(addr: SocketAddr, key: &[u8], health: Health) -> Result<Self> {
        let stop = Cancellation::default();
        let mut connection = Connection::connect(addr, stop.clone())?;
        connection.encrypt(
            derive(key, "Events-Salt", "Events-Read-Encryption-Key"),
            derive(key, "Events-Salt", "Events-Write-Encryption-Key"),
        );
        Self::start(connection, addr.ip().to_string(), health, stop)
    }
    pub fn start(
        mut connection: Connection,
        host: String,
        health: Health,
        stop: Cancellation,
    ) -> Result<Self> {
        let done = stop.clone();
        let worker = thread::Builder::new()
            .name("airplay-events".into())
            .spawn(move || {
                if let Err(error) = event_loop(&mut connection, &health, &done) {
                    if !done.is_cancelled() {
                        let fault = Fault::from_error(host, "events", &error);
                        if matches!(fault.code.as_str(), "authentication_failed" | "session_rejected") {
                            health.fail(fault);
                        } else {
                            // Losing the event channel silences buttons and metadata;
                            // it does not stop audio that is still flowing.
                            health.degrade(fault);
                        }
                    }
                }
            })?;
        Ok(Self {
            stop,
            worker: Some(worker),
        })
    }
}
fn event_loop(connection: &mut Connection, health: &Health, stop: &Cancellation) -> Result<()> {
    while !stop.is_cancelled() {
        let Some(msg) = connection.read_for(POLL, Some(stop))? else {
            continue;
        };
        let protocol = msg
            .first
            .split_whitespace()
            .last()
            .filter(|v| matches!(*v, "HTTP/1.1" | "HTTP/1.0" | "RTSP/1.0"))
            .context("invalid event protocol")?;
        let cseq = msg
            .headers
            .get("cseq")
            .map(|v| format!("CSeq: {v}\r\n"))
            .unwrap_or_default();
        connection.write(
            format!("{protocol} 200 OK\r\n{cseq}Content-Length: 0\r\nAudio-Latency: 0\r\n\r\n")
                .as_bytes(),
        )?;
        {
            let mut state = health.0.lock().unwrap();
            state.metrics.event_messages += 1;
            // The body carries the actual request: a HomePod pause button shows up
            // here and nowhere else. Keep the channel alive even when the body is
            // something we cannot read.
            let parsed = crate::events::parse(&msg.body);
            if !parsed.commands.is_empty() || parsed.volume.is_some() {
                state.commands.push(parsed);
            }
        }
    }
    Ok(())
}
impl Drop for EventWorker {
    fn drop(&mut self) {
        self.stop.cancel();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

#[derive(Clone, Copy)]
pub struct FeedbackTiming {
    pub interval: Duration,
    pub soft: Duration,
    pub hard: Duration,
}
impl Default for FeedbackTiming {
    fn default() -> Self {
        Self {
            interval: FEEDBACK_INTERVAL,
            soft: FEEDBACK_SOFT_TIMEOUT,
            hard: FAILURE_TIMEOUT,
        }
    }
}
pub struct FeedbackWorker {
    stop: Cancellation,
    worker: Option<JoinHandle<()>>,
}
impl FeedbackWorker {
    pub fn start(
        connection: Arc<Mutex<Connection>>,
        host: String,
        health: Health,
        timing: FeedbackTiming,
    ) -> Result<Self> {
        let stop = Cancellation::default();
        let done = stop.clone();
        let worker = thread::Builder::new()
            .name("airplay-feedback".into())
            .spawn(move || {
                if let Err(error) = feedback_loop(&connection, &host, &health, &done, timing) {
                    if !done.is_cancelled() {
                        let fault = Fault::from_error(host, "feedback", &error);
                        if fault.code != "feedback_rejected" {
                            health.0.lock().unwrap().metrics.feedback_failures += 1;
                        }
                        // 401 and 403 are access/authentication failures in
                        // the reference classification (Q32): they take the
                        // existing hard auth path, not the preemption probe.
                        // 454 and other rejections stay on `session_rejected`.
                        if matches!(fault.code.as_str(), "authentication_failed" | "session_rejected") {
                            health.fail(fault);
                        } else {
                            // Feedback is a health probe, not the audio path: keep
                            // sending and let `check_control` decide when to give up.
                            health.notice(
                                &fault.host,
                                "feedback",
                                "feedback_lost",
                                "Receiver feedback stopped; audio continues",
                                false,
                            );
                            health.degrade(fault);
                        }
                    }
                }
            })?;
        Ok(Self {
            stop,
            worker: Some(worker),
        })
    }
}
fn feedback_loop(
    connection: &Mutex<Connection>,
    host: &str,
    health: &Health,
    stop: &Cancellation,
    timing: FeedbackTiming,
) -> Result<()> {
    let mut failures = 0;
    let mut next = Instant::now() + timing.interval;
    while !stop.is_cancelled() {
        if let Some(fault) = drain_incoming(connection, host, health) {
            return Err(fault.into());
        }
        if Instant::now() < next {
            thread::sleep(POLL.min(next.saturating_duration_since(Instant::now())));
            continue;
        }
        let mut connection = connection.lock().unwrap();
        let started = Instant::now();
        connection.begin_request("POST", "/feedback", &[], &[])?;
        let response = loop {
            stop.check()?;
            let elapsed = started.elapsed();
            if elapsed >= timing.hard {
                return Err(Fault::new(
                    host,
                    "feedback",
                    "feedback_timeout",
                    true,
                    "Receiver feedback exceeded the recovery deadline",
                )
                .into());
            }
            if elapsed >= timing.soft {
                let notify = {
                    let mut s = health.0.lock().unwrap();
                    let notify = !s.metrics.feedback_delayed;
                    s.metrics.feedback_delayed = true;
                    notify
                };
                if notify {
                    health.notice(
                        host,
                        "feedback",
                        "feedback_delayed",
                        "Receiver feedback is delayed; audio continues",
                        false,
                    );
                }
            }
            if let Some(response) =
                connection.read_for(POLL.min(timing.hard - elapsed), Some(stop))?
            {
                if let Some(method) = response.method() {
                    if preemption::incoming_teardown(Some(method)).is_some() {
                        health.preemption_signal(Signal::UnexpectedTeardown);
                        return Err(Fault::new(
                            host,
                            "feedback",
                            "peer_teardown",
                            true,
                            "Receiver ended the session with an unexpected TEARDOWN",
                        )
                        .into());
                    }
                    // Another receiver-initiated request; keep waiting for the
                    // answer to the feedback probe.
                    continue;
                }
                if !connection.stale_response(&response) {
                    break response;
                }
            }
        };
        let close_requested = connection.take_close_requested();
        if close_requested {
            health.preemption_signal(Signal::ConnectionClose);
        }
        connection.validate_cseq(&response)?;
        let status = response.status()?;
        if status == 200 {
            failures = 0;
            let recovered = {
                let mut s = health.0.lock().unwrap();
                s.metrics.feedback_successes += 1;
                s.metrics.feedback_rtt_ms = Some(started.elapsed().as_secs_f64() * 1000.0);
                let recovered = s.metrics.feedback_delayed;
                s.metrics.feedback_delayed = false;
                recovered
            };
            if recovered {
                health.notice(
                    host,
                    "feedback",
                    "feedback_recovered",
                    "Receiver feedback recovered",
                    true,
                );
            }
        } else if matches!(status, 500 | 502 | 503 | 504) {
            failures += 1;
            {
                let mut s = health.0.lock().unwrap();
                s.metrics.feedback_failures += 1;
                s.metrics.feedback_delayed = true;
            }
            if failures >= FEEDBACK_FAILURE_LIMIT {
                return Err(Fault::new(
                    host,
                    "feedback",
                    "feedback_rejected",
                    true,
                    format!("Receiver feedback failed {failures} times (status {status})"),
                )
                .into());
            }
            health.notice(
                host,
                "feedback",
                "feedback_retry",
                &format!("Receiver feedback returned {status}; retrying while audio continues"),
                false,
            );
        } else if status == 401 || status == 403 {
            // 403 is an access/authentication error in the reference
            // classification (password/PIN/470 family), never "another
            // controller took the session" (Q32). It goes down the same hard
            // auth path as 401; it is not preemption evidence and can never
            // latch `we_hold_session` to false.
            return Err(Fault::new(
                host,
                "feedback",
                "authentication_failed",
                false,
                format!(
                    "Receiver denied access to the feedback channel (HTTP {status}): {}",
                    response.first
                ),
            )
            .into());
        } else {
            return Err(Fault::new(
                host,
                "feedback",
                "session_rejected",
                status == 454,
                format!("Receiver feedback rejected: {}", response.first),
            )
            .into());
        }
        if close_requested {
            return Err(Fault::new(
                host,
                "feedback",
                "peer_closed",
                true,
                "Receiver asked to close the control connection",
            )
            .into());
        }
        drop(connection);
        next = Instant::now() + timing.interval;
    }
    Ok(())
}

/// Receiver-initiated traffic can land on the shared control connection while a
/// volume or metadata worker owns it. The request is stashed by `Connection`; it
/// is inspected here rather than inside every request path.
fn drain_incoming(connection: &Mutex<Connection>, host: &str, health: &Health) -> Option<Fault> {
    let mut connection = connection.lock().unwrap();
    for message in connection.take_unsolicited() {
        if preemption::incoming_teardown(message.method()).is_some() {
            health.preemption_signal(Signal::UnexpectedTeardown);
            return Some(Fault::new(
                host,
                "feedback",
                "peer_teardown",
                true,
                "Receiver ended the session with an unexpected TEARDOWN",
            ));
        }
    }
    if connection.take_close_requested() {
        health.preemption_signal(Signal::ConnectionClose);
        return Some(Fault::new(
            host,
            "feedback",
            "peer_closed",
            true,
            "Receiver asked to close the control connection",
        ));
    }
    None
}
impl Drop for FeedbackWorker {
    fn drop(&mut self) {
        self.stop.cancel();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

#[derive(Default, Clone, Serialize)]
pub struct MediaMetrics {
    pub packets_sent: u64,
    pub media_send_errors: u64,
    pub sync_send_errors: u64,
    pub control_receive_errors: u64,
    pub retransmit_requests: u64,
    pub retransmit_requested_packets: u64,
    pub retransmits_sent: u64,
    pub retransmit_missing: u64,
    pub retransmit_expired: u64,
    pub retransmit_send_errors: u64,
    pub retransmit_queue_drops: u64,
    pub invalid_requests: u64,
    /// Whether the socket accepted the DSCP EF mark (diagnostic only).
    pub qos_marked: bool,
}
pub struct MediaTransport {
    buffered: Option<crate::buffered::Sender>,
    pub packetizer: Packetizer,
    pub metrics: MediaMetrics,
    audio: UdpSocket,
    control: UdpSocket,
    remote: SocketAddr,
    pending: VecDeque<(u16, SocketAddr)>,
    latency: Duration,
    estimated: bool,
    last_success: Instant,
    send_warning: bool,
}
impl MediaTransport {
    pub fn new(
        packetizer: Packetizer,
        audio: UdpSocket,
        control: UdpSocket,
        remote: SocketAddr,
    ) -> Result<Self> {
        audio.set_nonblocking(true)?;
        control.set_nonblocking(true)?;
        let qos_marked = crate::qos::mark_audio_socket(&audio);
        Ok(Self {
            buffered: None,
            packetizer,
            metrics: MediaMetrics { qos_marked, ..Default::default() },
            audio,
            control,
            remote,
            pending: VecDeque::new(),
            latency: Duration::from_millis(150),
            estimated: true,
            last_success: Instant::now(),
            send_warning: false,
        })
    }
    pub fn configure_latency(&mut self, latency: Duration, estimated: bool) {
        self.latency = latency;
        self.estimated = estimated;
        self.packetizer.configure_latency(latency);
    }
    pub fn control_port(&self) -> Result<u16> {
        Ok(self.control.local_addr()?.port())
    }
    pub fn connect_audio(&self, address: SocketAddr) -> Result<()> {
        Ok(self.audio.connect(address)?)
    }
    pub fn connect_buffered(&mut self, address: SocketAddr) -> Result<()> {
        self.buffered = Some(crate::buffered::Sender::connect(address)?);
        self.packetizer.enable_buffered();
        Ok(())
    }
    pub fn latency(&self) -> Duration {
        self.latency
    }
    /// Media health independent of the control channel: have we moved audio to the
    /// receiver recently?
    pub fn media_healthy(&self, now: Instant, window: Duration) -> bool {
        now.saturating_duration_since(self.last_success) < window
    }
    pub fn service_buffered(&mut self, now: Instant) -> Result<()> {
        if let Some(sender) = &mut self.buffered {
            sender.service(now).map_err(|e| {
                Fault::new(self.remote.ip(), "buffered", "timeline_expired", true, e)
            })?;
        }
        Ok(())
    }
    pub fn set_control_port(&mut self, port: u16) {
        self.remote.set_port(port);
    }
    pub fn reset_watchdog(&mut self, now: Instant) {
        self.last_success = now;
    }
    pub fn metrics(&self) -> Value {
        let mut v = serde_json::to_value(&self.metrics).unwrap();
        v["receiver_latency_ms"] = json!(self.latency.as_secs_f64() * 1000.0);
        v["receiver_latency_estimated"] = json!(self.estimated);
        v["retransmit_pending"] = json!(self.pending.len());
        v["buffered_pending"] = json!(self.buffered.as_ref().map_or(0, |s| s.queued()));
        v
    }
    pub fn sync(&mut self, clock_ns: u64, id: Option<u64>, latency: u32, first: bool) {
        if self.buffered.is_some() {
            return;
        }
        let packet = rtp::sync_packet(self.packetizer.timestamp, latency, clock_ns, id, first);
        if self.control.send_to(&packet, self.remote).is_err() {
            self.metrics.sync_send_errors += 1;
        }
    }
    pub fn send(
        &mut self,
        pcm: &[u8],
        first: bool,
        now: Instant,
        media_time: Instant,
        health: &Health,
    ) -> Result<()> {
        health.check()?;
        if now >= media_time + self.latency {
            return Err(Fault::new(
                self.remote.ip(),
                "media",
                "timeline_expired",
                true,
                "frame missed its presentation deadline",
            )
            .into());
        }
        let packet = self
            .packetizer
            .packet_at(pcm, first, now, media_time + self.latency)?;
        if let Some(sender) = &mut self.buffered {
            sender
                .enqueue(packet, media_time + self.latency)
                .map_err(|e| {
                    Fault::new(self.remote.ip(), "buffered", "timeline_expired", true, e)
                })?;
            sender.service(now).map_err(|e| {
                Fault::new(self.remote.ip(), "buffered", "timeline_expired", true, e)
            })?;
            self.metrics.packets_sent += 1;
            Ok(())
        } else {
            self.observe_send(self.audio.send(&packet).map(|_| ()), now, health)
        }
    }
    // One failed datagram is media loss, not evidence of a dead RTSP session.
    pub fn observe_send(
        &mut self,
        result: std::io::Result<()>,
        now: Instant,
        health: &Health,
    ) -> Result<()> {
        if let Err(error) = result {
            self.metrics.media_send_errors += 1;
            if !self.send_warning {
                self.send_warning = true;
                health.notice(
                    &self.remote.ip().to_string(),
                    "media",
                    "media_send_delayed",
                    &format!("Audio send failed temporarily: {error}"),
                    false,
                );
            }
            if now.saturating_duration_since(self.last_success) >= FAILURE_TIMEOUT {
                return Err(Fault::new(
                    self.remote.ip(),
                    "media",
                    "media_send_timeout",
                    true,
                    error,
                )
                .into());
            }
        } else {
            self.metrics.packets_sent += 1;
            self.last_success = now;
            if self.send_warning {
                self.send_warning = false;
                health.notice(
                    &self.remote.ip().to_string(),
                    "media",
                    "media_send_recovered",
                    "Audio sending recovered",
                    true,
                );
            }
        }
        Ok(())
    }
    pub fn service_retransmits(&mut self) {
        if self.buffered.is_some() {
            return;
        }
        let until = Instant::now() + RETRANSMIT_BUDGET;
        let mut buffer = [0u8; 128];
        for _ in 0..MAX_REQUESTS {
            if Instant::now() >= until {
                break;
            }
            let (n, addr) = match self.control.recv_from(&mut buffer) {
                Ok(value) => value,
                Err(e)
                    if matches!(
                        e.kind(),
                        std::io::ErrorKind::WouldBlock | std::io::ErrorKind::Interrupted
                    ) =>
                {
                    break;
                }
                Err(_) => {
                    self.metrics.control_receive_errors += 1;
                    break;
                }
            };
            if addr.ip() != self.remote.ip() {
                continue;
            }
            let Some((start, count)) = Packetizer::retransmit_request(&buffer[..n]) else {
                self.metrics.invalid_requests += 1;
                continue;
            };
            self.metrics.retransmit_requests += 1;
            self.metrics.retransmit_requested_packets += u64::from(count);
            let accepted = usize::from(count).min(MAX_PENDING - self.pending.len());
            self.metrics.retransmit_queue_drops +=
                usize::from(count).saturating_sub(accepted) as u64;
            for i in 0..accepted {
                self.pending.push_back((start.wrapping_add(i as u16), addr));
            }
        }
        for _ in 0..MAX_RESENDS {
            let now = Instant::now();
            if now >= until {
                break;
            }
            let Some((seq, addr)) = self.pending.pop_front() else {
                break;
            };
            match self.packetizer.retransmit_packet(seq, now) {
                Retransmit::Packet(packet) => {
                    if self.control.send_to(&packet, addr).is_ok() {
                        self.metrics.retransmits_sent += 1;
                    } else {
                        self.metrics.retransmit_send_errors += 1;
                    }
                }
                Retransmit::Missing => self.metrics.retransmit_missing += 1,
                Retransmit::Expired => self.metrics.retransmit_expired += 1,
            }
        }
    }
}

/// What the send loop should do about a packet that woke up late.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LateAction {
    /// Not late enough to matter; send normally.
    OnTime,
    /// The gap still fits a playable buffer: fill this many slots with faded
    /// silence so seq/rtptime stay linear.
    Silence(u64),
    /// The gap is beyond silence coverage: move the timeline forward instead of
    /// tearing the session down.
    Reanchor,
}

/// The timeline advances for intentionally dropped slots and sent packets alike.
pub struct MediaSchedule {
    pub start: Instant,
    pub frames: u64,
    pub recoveries: u64,
    pub skipped_packets: u64,
    pub max_lateness_us: u128,
    /// Extra playout lead accumulated by re-anchoring. The sync anchor must be
    /// shifted by the same amount, so `seq → rtptime → networkTime` stays linear.
    pub shift: Duration,
    /// Re-anchors are bounded so a permanently stalled sender still expires.
    pub reanchored: u64,
    pub reanchor_shift_us: i64,
    reanchors: VecDeque<Instant>,
    next_relax: Option<Instant>,
    rate: u32,
}
/// Beyond eight packets of silence (≈64 ms at 44.1 kHz) the receiver's buffer is
/// already exhausted, so covering the gap in place no longer helps.
const MAX_SILENCE_PACKETS: u64 = 8;
const REANCHOR_LIMIT: usize = 3;
const REANCHOR_WINDOW: Duration = Duration::from_secs(10);
/// A re-anchor buys lead; past this the receiver's declared buffer is exceeded and
/// a fresh session is the honest answer.
const MAX_SHIFT: Duration = Duration::from_millis(2000);
const RELAX_INTERVAL: Duration = Duration::from_secs(1);
impl MediaSchedule {
    pub fn new(start: Instant, rate: u32) -> Self {
        Self {
            start,
            frames: 0,
            recoveries: 0,
            skipped_packets: 0,
            max_lateness_us: 0,
            shift: Duration::ZERO,
            reanchored: 0,
            reanchor_shift_us: 0,
            reanchors: VecDeque::new(),
            next_relax: None,
            rate,
        }
    }
    fn frame_duration(&self) -> Duration {
        Duration::from_nanos(FRAMES as u64 * 1_000_000_000 / self.rate as u64)
    }
    pub fn deadline(&self) -> Instant {
        self.start + self.shift + Duration::from_nanos(self.frames * 1_000_000_000 / self.rate as u64)
    }
    /// The sync anchor moves with the timeline: callers add this to the session's
    /// base network time before publishing `networkTime`.
    pub fn anchor_shift_ns(&self) -> u64 {
        self.shift.as_nanos() as u64
    }
    pub fn recover(&mut self, now: Instant, live: bool) -> Result<u64> {
        let late = now.saturating_duration_since(self.deadline());
        self.max_lateness_us = self.max_lateness_us.max(late.as_micros());
        if late < LATE_LIMIT {
            return Ok(0);
        }
        if !live {
            return Err(Fault::new(
                "",
                "scheduler",
                "qualification_late",
                false,
                "sender fell >60ms behind; qualification aborted",
            )
            .into());
        }
        let due = (now.saturating_duration_since(self.start).as_nanos() * u128::from(self.rate)
            / 1_000_000_000) as u64;
        let skipped = due.saturating_sub(self.frames) / FRAMES as u64;
        self.frames += skipped * FRAMES as u64;
        self.skipped_packets += skipped;
        self.recoveries += 1;
        Ok(skipped)
    }
    pub fn sent(&mut self) {
        self.frames += FRAMES as u64;
    }
    /// Classify a late wake-up. `earliest_render` is the receiver's playout
    /// deadline; padding silence past it would be heard as a stall.
    pub fn late_action(&self, now: Instant, earliest_render: Instant) -> LateAction {
        let due = self.due_packets(now);
        if due < 2 {
            return LateAction::OnTime;
        }
        if due > MAX_SILENCE_PACKETS || now + Duration::from_millis(2) >= earliest_render {
            return LateAction::Reanchor;
        }
        LateAction::Silence(due)
    }
    /// How many whole packet slots we are behind the plan.
    pub fn due_packets(&self, now: Instant) -> u64 {
        (now.saturating_duration_since(self.deadline()).as_nanos() * u128::from(self.rate)
            / 1_000_000_000
            / FRAMES as u128) as u64
    }
    /// Keep the original sample timeline: fill only a bounded, still playable gap.
    pub fn recovery_silence(&self, now: Instant, earliest_render: Instant) -> Result<u64> {
        match self.late_action(now, earliest_render) {
            LateAction::OnTime => Ok(0),
            LateAction::Silence(due) => Ok(due),
            LateAction::Reanchor => Err(Fault::new(
                "",
                "scheduler",
                "timeline_expired",
                true,
                "expired audio requires a fresh group timeline",
            )
            .into()),
        }
    }
    /// Move the whole timeline forward so the next packet is due one frame after
    /// `now`, and report the applied shift.
    ///
    /// Crucially this does **not** advance `frames`: seq and rtptime stay exactly
    /// as the receiver expects, and only the `rtptime → networkTime` anchor moves.
    /// Returns `None` when the re-anchor budget or the lead ceiling is reached,
    /// which is the only remaining path to a session rebuild.
    pub fn reanchor(&mut self, now: Instant) -> Option<Duration> {
        while self
            .reanchors
            .front()
            .is_some_and(|at| now.saturating_duration_since(*at) >= REANCHOR_WINDOW)
        {
            self.reanchors.pop_front();
        }
        if self.reanchors.len() >= REANCHOR_LIMIT {
            return None;
        }
        let frame = self.frame_duration();
        let deadline = self.deadline();
        let target = now + frame;
        if target <= deadline {
            // Sending already caught the timeline up; nothing to re-anchor.
            return Some(Duration::ZERO);
        }
        let apply = target - deadline;
        if self.shift.saturating_add(apply) > MAX_SHIFT {
            return None;
        }
        self.shift += apply;
        self.reanchored += 1;
        self.reanchor_shift_us = apply.as_micros() as i64;
        self.reanchors.push_back(now);
        Some(apply)
    }
    /// Slew the extra lead back to zero one frame per second whenever the sender
    /// is comfortably on time, so a re-anchor does not raise latency forever.
    pub fn relax(&mut self, now: Instant, late: Duration) {
        if self.shift.is_zero() || late >= self.frame_duration() {
            return;
        }
        if self.next_relax.is_some_and(|at| now < at) {
            return;
        }
        self.shift = self.shift.saturating_sub(self.frame_duration());
        self.next_relax = Some(now + RELAX_INTERVAL);
    }
    /// Apply an operator-requested lead change (adaptive latency ladder). Positive
    /// values delay playout, which is what the receiver sees; the sync anchor moves
    /// with the deadline so the timeline stays consistent. Returns the signed change
    /// actually applied, in milliseconds, clamped to the same ceiling as a re-anchor.
    pub fn add_shift(&mut self, delta_ms: i64) -> i64 {
        let before = self.shift;
        let target = if delta_ms >= 0 {
            self.shift.saturating_add(Duration::from_millis(delta_ms as u64))
        } else {
            self.shift
                .saturating_sub(Duration::from_millis(delta_ms.unsigned_abs()))
        }
        .min(MAX_SHIFT);
        self.shift = target;
        // A deliberate lead change resets the settle timer so the new value is not
        // immediately relaxed back.
        self.next_relax = None;
        if target >= before {
            (target - before).as_millis() as i64
        } else {
            -((before - target).as_millis() as i64)
        }
    }
}

#[cfg(test)]
mod schedule_tests {
    use super::*;
    #[test]
    fn control_plane_faults_keep_playing_while_media_is_healthy() {
        let health = Health::default();
        health.degrade(Fault::new(
            "127.0.0.1",
            "feedback",
            "feedback_timeout",
            true,
            "Receiver feedback exceeded the recovery deadline",
        ));
        // Healthy media: the session continues.
        assert!(health.check_control(true).is_ok());
        assert!(health.check().is_ok(), "nothing failed the media path");
        assert!(health.control_degraded().is_some());
        // Media stopped too: now the degradation is fatal.
        assert!(health.check_control(false).is_err());
    }
    #[test]
    fn authentication_faults_are_fatal_even_with_healthy_media() {
        let health = Health::default();
        health.degrade(Fault::new(
            "127.0.0.1",
            "feedback",
            "authentication_failed",
            false,
            "Receiver rejected the session",
        ));
        assert!(health.check_control(true).is_err());
    }
    #[test]
    fn a_healthy_control_channel_has_nothing_to_report() {
        let health = Health::default();
        assert!(health.check_control(false).is_ok());
        assert!(health.control_degraded().is_none());
    }
    #[test]
    fn expired_udp_media_is_rejected_before_packetization_or_network_write() {
        let peer = UdpSocket::bind("127.0.0.1:0").unwrap();
        peer.set_nonblocking(true).unwrap();
        let audio = UdpSocket::bind("127.0.0.1:0").unwrap();
        audio.connect(peer.local_addr().unwrap()).unwrap();
        let control = UdpSocket::bind("127.0.0.1:0").unwrap();
        let mut media = MediaTransport::new(
            Packetizer::new([1; 32], 7, 1000, 42),
            audio,
            control,
            peer.local_addr().unwrap(),
        )
        .unwrap();
        let now = Instant::now();
        let error = media
            .send(
                &vec![0; rtp::PCM_BYTES],
                false,
                now,
                now - Duration::from_secs(1),
                &Health::default(),
            )
            .unwrap_err();
        let fault = error.downcast_ref::<Fault>().unwrap();
        assert!(fault.retryable);
        assert_eq!(fault.code, "timeline_expired");
        assert_eq!(media.packetizer.seq, 7);
        assert_eq!(media.packetizer.timestamp, 1000);
        assert_eq!(media.metrics.packets_sent, 0);
        let mut bytes = [0; 2048];
        assert_eq!(
            peer.recv(&mut bytes).unwrap_err().kind(),
            std::io::ErrorKind::WouldBlock
        );
    }
    #[test]
    fn live_recovery_fills_valid_slots_without_advancing_or_reanchoring() {
        let start = Instant::now();
        for rate in [44_100, 48_000] {
            let mut schedule = MediaSchedule::new(start, rate);
            let now = start + Duration::from_millis(30);
            let due = schedule
                .recovery_silence(now, start + Duration::from_millis(150))
                .unwrap();
            assert!((2..=8).contains(&due));
            assert_eq!(schedule.frames, 0);
            assert_eq!(schedule.deadline(), start);
            for _ in 0..due {
                schedule.sent();
            }
            assert_eq!(schedule.frames, due * FRAMES as u64);
            assert_eq!(schedule.skipped_packets, 0);
            assert!(
                schedule
                    .recovery_silence(
                        start + Duration::from_millis(250),
                        start + Duration::from_secs(1)
                    )
                    .is_err()
            );
            let schedule = MediaSchedule::new(start, rate);
            let error = schedule
                .recovery_silence(now, now + Duration::from_millis(1))
                .unwrap_err();
            let fault = error.downcast_ref::<Fault>().unwrap();
            assert!(fault.retryable);
            assert_eq!(fault.code, "timeline_expired");
        }
    }
    #[test]
    fn schedule_advances_by_selected_rate() {
        let start = Instant::now();
        let mut s = MediaSchedule::new(start, 48_000);
        for _ in 0..10 {
            s.sent();
        }
        assert_eq!(
            s.deadline().saturating_duration_since(start),
            Duration::from_nanos(10u64 * FRAMES as u64 * 1_000_000_000 / 48_000)
        );
        let mut slow = MediaSchedule::new(start, 44_100);
        for _ in 0..10 {
            slow.sent();
        }
        assert!(s.deadline() < slow.deadline());
    }
    #[test]
    fn forty_eight_k_schedule_skips_expected_slots_after_stall() {
        let start = Instant::now();
        let mut s = MediaSchedule::new(start, 48_000);
        let now = start + Duration::from_millis(500);
        let skipped = s.recover(now, true).unwrap();
        let due = (500_000_000u64 * 48_000) / 1_000_000_000;
        assert_eq!(skipped, due / FRAMES as u64);
        assert_eq!(s.frames, skipped * FRAMES as u64);
        assert_eq!(s.skipped_packets, skipped);
    }
    #[test]
    fn late_wake_up_escalates_from_silence_to_reanchor() {
        let start = Instant::now();
        let s = MediaSchedule::new(start, 44_100);
        // 2..=8 packets of lateness still fits a playable buffer.
        let small = start + Duration::from_millis(30);
        assert_eq!(
            s.late_action(small, start + Duration::from_secs(1)),
            LateAction::Silence(s.due_packets(small))
        );
        // Ten packets late is past silence coverage.
        let large = start + Duration::from_millis(100);
        assert_eq!(
            s.late_action(large, start + Duration::from_secs(1)),
            LateAction::Reanchor
        );
        // Reaching the receiver's playout deadline also escalates, even when the
        // packet count alone would still be coverable.
        assert_eq!(
            s.late_action(small, small + Duration::from_millis(1)),
            LateAction::Reanchor
        );
    }
    #[test]
    fn reanchor_moves_the_whole_timeline_without_touching_the_sequence() {
        let start = Instant::now();
        for rate in [44_100, 48_000] {
            let mut s = MediaSchedule::new(start, rate);
            for _ in 0..10 {
                s.sent();
            }
            let frames = s.frames;
            let now = start + Duration::from_millis(300);
            let shift = s.reanchor(now).unwrap();
            assert!(shift > Duration::ZERO);
            assert_eq!(s.frames, frames, "seq/rtptime must not skip");
            assert_eq!(s.reanchored, 1);
            assert_eq!(s.anchor_shift_ns(), shift.as_nanos() as u64);
            let frame = Duration::from_nanos(FRAMES as u64 * 1_000_000_000 / u64::from(rate));
            assert_eq!(s.deadline(), now + frame, "one frame of phase margin");
            assert_eq!(s.due_packets(now), 0, "the schedule is on time again");
        }
    }
    #[test]
    fn reanchor_budget_expires_a_stalled_sender() {
        let start = Instant::now();
        let mut s = MediaSchedule::new(start, 44_100);
        let mut now;
        for i in 0..REANCHOR_LIMIT {
            now = s.deadline() + Duration::from_millis(120);
            assert!(s.reanchor(now).is_some(), "re-anchor {i} within budget");
        }
        // The fourth stall in the same window must expire the session instead of
        // re-anchoring forever.
        now = s.deadline() + Duration::from_millis(120);
        assert!(s.reanchor(now).is_none());
        assert_eq!(s.reanchored as usize, REANCHOR_LIMIT);
        // Once the window has rolled over the budget is available again; advance
        // the plan so the sender is genuinely behind by the same amount.
        let later = now + REANCHOR_WINDOW;
        while s.deadline() < later {
            s.sent();
        }
        assert!(s.reanchor(later + Duration::from_millis(120)).is_some());
    }
    #[test]
    fn reanchor_lead_is_bounded_so_buffer_overflow_still_expires() {
        let start = Instant::now();
        let mut s = MediaSchedule::new(start, 44_100);
        // Walk the window forward so the count budget never trips.
        let mut now = start;
        let mut applied = false;
        for _ in 0..40 {
            now += REANCHOR_WINDOW;
            let late_by = 400; // fewer than MAX_SHIFT ms per step
            match s.reanchor(now + Duration::from_millis(late_by)) {
                Some(_) => applied = true,
                None => {
                    assert!(s.shift <= MAX_SHIFT);
                    return;
                }
            }
        }
        assert!(applied);
        panic!("lead must never grow without a ceiling");
    }
    #[test]
    fn operator_lead_changes_move_the_anchor_and_stay_bounded() {
        let start = Instant::now();
        let mut s = MediaSchedule::new(start, 44_100);
        let deadline = s.deadline();
        assert_eq!(s.add_shift(40), 40);
        assert_eq!(s.deadline(), deadline + Duration::from_millis(40));
        assert_eq!(s.anchor_shift_ns(), 40_000_000);
        // Negative deltas come back off the same ledger.
        assert_eq!(s.add_shift(-15), -15);
        assert_eq!(s.shift, Duration::from_millis(25));
        // Never below zero.
        assert_eq!(s.add_shift(-1000), -25);
        assert_eq!(s.shift, Duration::ZERO);
        // And never above the re-anchor ceiling.
        assert_eq!(s.add_shift(10_000), MAX_SHIFT.as_millis() as i64);
        assert_eq!(s.shift, MAX_SHIFT);
    }
    #[test]
    fn reanchor_does_not_count_a_no_op() {
        let start = Instant::now();
        let mut s = MediaSchedule::new(start, 44_100);
        for _ in 0..8 {
            s.sent();
        }
        // Sending is already ahead of the clock; re-anchoring has nothing to add.
        assert_eq!(s.reanchor(start), Some(Duration::ZERO));
        assert_eq!(s.reanchored, 0);
        assert_eq!(s.shift, Duration::ZERO);
        assert_eq!(s.frames, 8 * FRAMES as u64);
    }
    #[test]
    fn extra_lead_slews_back_to_zero_when_the_sender_is_on_time() {
        let start = Instant::now();
        let mut s = MediaSchedule::new(start, 44_100);
        let frame = Duration::from_nanos(FRAMES as u64 * 1_000_000_000 / 44_100);
        s.reanchor(start + Duration::from_millis(300)).unwrap();
        let raised = s.shift;
        assert!(raised > frame);
        // Late by a frame or more: do not relax, the sender has no slack.
        s.relax(start + Duration::from_secs(5), frame);
        assert_eq!(s.shift, raised);
        // Comfortably on time: one frame comes off, then at most once a second.
        s.relax(start + Duration::from_secs(10), Duration::ZERO);
        assert_eq!(s.shift, raised - frame);
        s.relax(start + Duration::from_secs(10), Duration::ZERO);
        assert_eq!(s.shift, raised - frame, "relaxation is rate limited");
        s.relax(start + Duration::from_secs(12), Duration::ZERO);
        assert!(s.shift < raised - frame);
        for second in 14..120 {
            s.relax(start + Duration::from_secs(second), Duration::ZERO);
        }
        assert_eq!(s.shift, Duration::ZERO);
    }
}
