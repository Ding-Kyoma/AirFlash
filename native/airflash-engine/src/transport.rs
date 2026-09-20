//! Session health and bounded media recovery, shared by production and localhost tests.
use crate::{
    crypto::derive,
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
        }
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
    notices: VecDeque<Value>,
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
    fn fail(&self, fault: Fault) {
        let mut state = self.0.lock().unwrap();
        if state.failure.is_none() {
            state.failure = Some(fault);
        }
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
                        health.fail(Fault::from_error(host, "events", &error));
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
        health.0.lock().unwrap().metrics.event_messages += 1;
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
                        health.fail(fault);
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
                break response;
            }
        };
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
        } else {
            return Err(Fault::new(
                host,
                "feedback",
                if matches!(status, 401 | 403) {
                    "authentication_failed"
                } else {
                    "session_rejected"
                },
                status == 454,
                format!("Receiver feedback rejected: {}", response.first),
            )
            .into());
        }
        drop(connection);
        next = Instant::now() + timing.interval;
    }
    Ok(())
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
}
pub struct MediaTransport {
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
        Ok(Self {
            packetizer,
            metrics: MediaMetrics::default(),
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
        v
    }
    pub fn sync(&mut self, clock_ns: u64, id: Option<u64>, latency: u32, first: bool) {
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
        let packet = self
            .packetizer
            .packet_at(pcm, first, now, media_time + self.latency)?;
        self.observe_send(self.audio.send(&packet).map(|_| ()), now, health)
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

/// The timeline advances for intentionally dropped slots and sent packets alike.
pub struct MediaSchedule {
    pub start: Instant,
    pub frames: u64,
    pub recoveries: u64,
    pub skipped_packets: u64,
    pub max_lateness_us: u128,
    rate: u32,
}
impl MediaSchedule {
    pub fn new(start: Instant, rate: u32) -> Self {
        Self {
            start,
            frames: 0,
            recoveries: 0,
            skipped_packets: 0,
            max_lateness_us: 0,
            rate,
        }
    }
    pub fn deadline(&self) -> Instant {
        self.start + Duration::from_nanos(self.frames * 1_000_000_000 / self.rate as u64)
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
}

#[cfg(test)]
mod schedule_tests {
    use super::*;
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
}
