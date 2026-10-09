//! Type-103 media: bounded nonblocking TCP frames on an anchored PTP timeline.
use crate::{
    rtp::{FRAMES, Packetizer},
    rtsp::Cancellation,
    transport::{Fault, Health, MediaTransport},
};
use anyhow::{Result, ensure};
use serde_json::{Value, json};
use std::{
    collections::VecDeque,
    io::Write,
    net::{SocketAddr, TcpStream},
    time::{Duration, Instant},
};

pub const STALL_TIMEOUT: Duration = Duration::from_secs(10);
struct Frame {
    bytes: Vec<u8>,
    offset: usize,
}
pub struct BufferedTransport {
    pub packetizer: Packetizer,
    socket: Option<TcpStream>,
    pending: VecDeque<Frame>,
    capacity: usize,
    last_progress: Instant,
    latency: Duration,
    packets_sent: u64,
    bytes_sent: u64,
}
impl BufferedTransport {
    pub fn new(mut packetizer: Packetizer) -> Self {
        packetizer.buffered();
        let capacity = (packetizer.rate as usize * 10).div_ceil(FRAMES);
        Self {
            packetizer,
            socket: None,
            pending: VecDeque::new(),
            capacity,
            last_progress: Instant::now(),
            latency: Duration::from_secs(3),
            packets_sent: 0,
            bytes_sent: 0,
        }
    }
    pub fn connect(&mut self, address: SocketAddr) -> Result<()> {
        let socket = TcpStream::connect_timeout(&address, Duration::from_secs(3))?;
        socket.set_nodelay(true)?;
        socket.set_nonblocking(true)?;
        self.socket = Some(socket);
        Ok(())
    }
    fn enqueue(&mut self, pcm: &[u8], first: bool, now: Instant, deadline: Instant) -> Result<()> {
        if self.pending.len() >= self.capacity {
            return Err(Fault::new(
                "",
                "media",
                "buffer_exhausted",
                true,
                "Buffered audio queue reached its ten-second limit",
            )
            .into());
        }
        let packet = self.packetizer.packet_at(pcm, first, now, deadline)?;
        let length = u16::try_from(packet.len() + 2)?;
        let mut bytes = Vec::with_capacity(usize::from(length));
        bytes.extend(length.to_be_bytes());
        bytes.extend(packet);
        if self.pending.is_empty() {
            self.last_progress = now;
        }
        self.pending.push_back(Frame { bytes, offset: 0 });
        Ok(())
    }
    fn pump(
        &mut self,
        mut write: impl FnMut(&[u8]) -> std::io::Result<usize>,
        now: Instant,
    ) -> Result<()> {
        let budget = Instant::now() + Duration::from_millis(1);
        while let Some(frame) = self.pending.front_mut() {
            match write(&frame.bytes[frame.offset..]) {
                Ok(0) => {
                    return Err(Fault::new(
                        "",
                        "media",
                        "peer_closed",
                        true,
                        "Buffered media connection closed",
                    )
                    .into());
                }
                Ok(n) => {
                    ensure!(
                        n <= frame.bytes.len() - frame.offset,
                        "invalid TCP write length"
                    );
                    frame.offset += n;
                    self.bytes_sent += n as u64;
                    self.last_progress = now;
                    if frame.offset == frame.bytes.len() {
                        self.pending.pop_front();
                        self.packets_sent += 1;
                    }
                }
                Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => break,
                Err(e) if e.kind() == std::io::ErrorKind::Interrupted => continue,
                Err(e) => return Err(e.into()),
            }
            if Instant::now() >= budget {
                break;
            }
        }
        if !self.pending.is_empty()
            && now.saturating_duration_since(self.last_progress) >= STALL_TIMEOUT
        {
            return Err(Fault::new(
                "",
                "media",
                "buffered_stalled",
                true,
                "Buffered audio made no progress for ten seconds",
            )
            .into());
        }
        Ok(())
    }
    pub fn flush(&mut self, now: Instant) -> Result<()> {
        let mut socket = self
            .socket
            .take()
            .ok_or_else(|| anyhow::anyhow!("buffered socket not connected"))?;
        let result = self.pump(|bytes| socket.write(bytes), now);
        self.socket = Some(socket);
        result
    }
    pub fn send(
        &mut self,
        pcm: &[u8],
        first: bool,
        now: Instant,
        deadline: Instant,
        health: &Health,
    ) -> Result<()> {
        health.check()?;
        self.flush(now)?;
        self.enqueue(pcm, first, now, deadline)?;
        self.flush(now)
    }
    pub fn metrics(&self) -> Value {
        json!({"packets_sent":self.packets_sent,"bytes_sent":self.bytes_sent,"buffered_pending_packets":self.pending.len(),
            "buffered_queue_ms":self.pending.len() as f64 * FRAMES as f64 * 1000.0 / self.packetizer.rate as f64,
            "receiver_latency_ms":self.latency.as_secs_f64()*1000.0,"receiver_latency_estimated":true,"transport":"buffered"})
    }
    fn quiesce(&mut self, cancel: &Cancellation) -> Result<()> {
        // Unstarted frames can be dropped; a partial frame must finish to keep TCP framing valid.
        let partial = self.pending.front().is_some_and(|f| f.offset > 0);
        self.pending.truncate(usize::from(partial));
        while !self.pending.is_empty() {
            cancel.check()?;
            self.flush(Instant::now())?;
            std::thread::sleep(Duration::from_millis(2));
        }
        Ok(())
    }
}

pub enum Media {
    Realtime(Box<MediaTransport>),
    Buffered(Box<BufferedTransport>),
}
impl Media {
    pub fn packetizer(&self) -> &Packetizer {
        match self {
            Self::Realtime(m) => &m.packetizer,
            Self::Buffered(m) => &m.packetizer,
        }
    }
    pub fn packetizer_mut(&mut self) -> &mut Packetizer {
        match self {
            Self::Realtime(m) => &mut m.packetizer,
            Self::Buffered(m) => &mut m.packetizer,
        }
    }
    pub fn control_port(&self) -> Result<u16> {
        match self {
            Self::Realtime(m) => m.control_port(),
            Self::Buffered(_) => Ok(0),
        }
    }
    pub fn set_control_port(&mut self, port: u16) {
        if let Self::Realtime(m) = self {
            m.set_control_port(port);
        }
    }
    pub fn connect_audio(&mut self, address: SocketAddr) -> Result<()> {
        match self {
            Self::Realtime(m) => m.connect_audio(address),
            Self::Buffered(m) => m.connect(address),
        }
    }
    pub fn configure_latency(&mut self, latency: Duration, estimated: bool) {
        match self {
            Self::Realtime(m) => m.configure_latency(latency, estimated),
            Self::Buffered(m) => m.latency = latency,
        }
    }
    pub fn reset_watchdog(&mut self, now: Instant) {
        match self {
            Self::Realtime(m) => m.reset_watchdog(now),
            Self::Buffered(m) => m.last_progress = now,
        }
    }
    pub fn sync(&mut self, ns: u64, id: Option<u64>, latency: u32, first: bool) {
        if let Self::Realtime(m) = self {
            m.sync(ns, id, latency, first);
        }
    }
    pub fn send(
        &mut self,
        pcm: &[u8],
        first: bool,
        now: Instant,
        deadline: Instant,
        health: &Health,
    ) -> Result<()> {
        match self {
            Self::Realtime(m) => m.send(pcm, first, now, deadline, health),
            Self::Buffered(m) => m.send(pcm, first, now, deadline, health),
        }
    }
    pub fn service(&mut self) -> Result<()> {
        match self {
            Self::Realtime(m) => {
                m.service_retransmits();
                Ok(())
            }
            Self::Buffered(m) => m.flush(Instant::now()),
        }
    }
    pub fn metrics(&self) -> Value {
        match self {
            Self::Realtime(m) => m.metrics(),
            Self::Buffered(m) => m.metrics(),
        }
    }
    pub fn retransmits(&self) -> u64 {
        match self {
            Self::Realtime(m) => m.metrics.retransmits_sent,
            Self::Buffered(_) => 0,
        }
    }
    pub fn stop_buffered(&mut self) {
        if let Self::Buffered(m) = self {
            if let Some(socket) = m.socket.take() {
                let _ = socket.shutdown(std::net::Shutdown::Both);
            }
            m.pending.clear();
        }
    }
    pub fn quiesce(&mut self, cancel: &Cancellation) -> Result<()> {
        if let Self::Buffered(m) = self {
            m.quiesce(cancel)?;
        }
        Ok(())
    }
    pub fn drain(&mut self, cancellation: &Cancellation, latency: Duration) -> Result<()> {
        if let Self::Buffered(m) = self {
            let until = Instant::now() + latency;
            let deadline = Instant::now() + STALL_TIMEOUT;
            while !m.pending.is_empty() || Instant::now() < until {
                cancellation.check()?;
                ensure!(Instant::now() < deadline, "buffered drain timed out");
                m.flush(Instant::now())?;
                std::thread::sleep(Duration::from_millis(2));
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{crypto::Cipher, rtp::PCM_BYTES};
    #[test]
    fn partial_writes_preserve_framing_and_crypto_after_wrap() {
        let key = [19; 32];
        let mut m = BufferedTransport::new(Packetizer::new(key, 65535, u32::MAX - 20, 3));
        let now = Instant::now();
        for _ in 0..3 {
            m.enqueue(&vec![0; PCM_BYTES], false, now, now).unwrap();
        }
        let mut wire = Vec::new();
        while !m.pending.is_empty() {
            m.pump(
                |bytes| {
                    let n = bytes.len().min(7);
                    wire.extend_from_slice(&bytes[..n]);
                    Ok(n)
                },
                now,
            )
            .unwrap();
        }
        let mut pos = 0;
        let mut cipher = Cipher::new(key);
        for seq in [65535u16, 0, 1] {
            let len = u16::from_be_bytes(wire[pos..pos + 2].try_into().unwrap()) as usize;
            let packet = &wire[pos + 2..pos + len];
            assert_eq!(packet[1] & 0x7f, 103);
            assert_eq!(u16::from_be_bytes(packet[2..4].try_into().unwrap()), seq);
            let decoded = cipher
                .decrypt(&packet[12..packet.len() - 8], &packet[4..12])
                .unwrap();
            assert_eq!(decoded, vec![0; PCM_BYTES]);
            pos += len;
        }
        assert_eq!(pos, wire.len());
        assert_eq!(m.packets_sent, 3);
    }
    #[test]
    fn backpressure_is_bounded_and_cancellable() {
        let mut m = BufferedTransport::new(Packetizer::new([1; 32], 0, 0, 0));
        let now = Instant::now();
        m.enqueue(&vec![0; crate::rtp::PCM_BYTES], false, now, now)
            .unwrap();
        let blocked = |_: &[u8]| Err(std::io::ErrorKind::WouldBlock.into());
        assert!(m.pump(blocked, now + Duration::from_secs(9)).is_ok());
        assert!(m.pump(blocked, now + STALL_TIMEOUT).is_err());
        m.capacity = 1;
        assert!(
            m.enqueue(&vec![0; crate::rtp::PCM_BYTES], false, now, now)
                .is_err()
        );
        let cancellation = Cancellation::default();
        cancellation.cancel();
        assert!(
            Media::Buffered(Box::new(m))
                .drain(&cancellation, Duration::ZERO)
                .is_err()
        );
    }
}
