//! Audio packetization, timing announcements and bounded exact-byte retransmission.
use crate::crypto::Cipher;
use anyhow::{Result, ensure};
use std::{
    collections::VecDeque,
    time::{Duration, Instant},
};
pub const MAX_HISTORY: usize = 512;
struct CachedPacket {
    seq: u16,
    bytes: Vec<u8>,
    sent_at: Instant,
    deadline: Instant,
}
pub enum Retransmit {
    Packet(Vec<u8>),
    Missing,
    Expired,
}
pub const RATE: u32 = 44100;
pub const FRAMES: usize = 352;
pub const PCM_BYTES: usize = FRAMES * 2 * 2;
pub const SUPPORTED_RATES: [u32; 2] = [44100, 48000];
pub fn audio_format(alac: bool, rate: u32) -> u64 {
    match (alac, rate) {
        (true, 48000) => 1 << 20,
        (true, _) => 1 << 18,
        (false, 48000) => 1 << 15,
        (false, _) => 1 << 11,
    }
}
pub struct Packetizer {
    pub seq: u16,
    pub timestamp: u32,
    pub ssrc: u32,
    pub rate: u32,
    cipher: Cipher,
    history: VecDeque<CachedPacket>,
    retention: Duration,
    latency: Duration,
    alac: Option<Box<alac_encoder::AlacEncoder>>,
}
impl Packetizer {
    pub fn new(key: [u8; 32], seq: u16, timestamp: u32, ssrc: u32) -> Self {
        Self::with_rate(key, seq, timestamp, ssrc, RATE)
    }
    pub fn with_rate(key: [u8; 32], seq: u16, timestamp: u32, ssrc: u32, rate: u32) -> Self {
        Self {
            seq,
            timestamp,
            ssrc,
            rate,
            cipher: Cipher::new(key),
            history: VecDeque::with_capacity(MAX_HISTORY),
            retention: Duration::from_secs(1),
            latency: Duration::from_millis(150),
            alac: None,
        }
    }
    pub fn enable_alac(&mut self) {
        self.alac = Some(Box::new(alac_encoder::AlacEncoder::new(
            &alac_encoder::FormatDescription::alac(self.rate as f64, FRAMES as u32, 2),
        )));
    }
    pub fn packet(&mut self, pcm: &[u8], first: bool) -> Result<Vec<u8>> {
        let now = Instant::now();
        self.packet_at(pcm, first, now, now + self.latency)
    }
    pub fn configure_latency(&mut self, latency: Duration) {
        self.latency = latency;
        self.retention = Duration::from_secs(1).max(latency + Duration::from_millis(250));
    }
    /// Deliberately lost media slots advance RTP, but never reuse an encryption nonce.
    pub fn skip_packets(&mut self, count: u64) {
        self.seq = self.seq.wrapping_add(count as u16);
        self.timestamp = self
            .timestamp
            .wrapping_add(count.wrapping_mul(FRAMES as u64) as u32);
    }
    pub fn packet_at(
        &mut self,
        pcm: &[u8],
        first: bool,
        now: Instant,
        deadline: Instant,
    ) -> Result<Vec<u8>> {
        ensure!(pcm.len() == PCM_BYTES, "wrong PCM packet length");
        let mut out = vec![0x80, if first { 0xe0 } else { 0x60 }];
        out.extend(self.seq.to_be_bytes());
        out.extend(self.timestamp.to_be_bytes());
        out.extend(self.ssrc.to_be_bytes());
        let nonce = self.cipher.counter();
        let audio = if let Some(encoder) = &mut self.alac {
            let little: Vec<u8> = pcm
                .chunks_exact(2)
                .flat_map(|bytes| [bytes[1], bytes[0]])
                .collect();
            let format = alac_encoder::FormatDescription::pcm::<i16>(self.rate as f64, 2);
            let mut encoded = vec![0; PCM_BYTES + 64];
            let length = encoder.encode(&format, &little, &mut encoded);
            encoded.truncate(length);
            self.cipher.encrypt(&encoded, &out[4..12])?
        } else {
            self.cipher.encrypt(pcm, &out[4..12])?
        };
        out.extend(audio);
        out.extend(nonce.to_le_bytes());
        while self
            .history
            .front()
            .is_some_and(|p| now.saturating_duration_since(p.sent_at) >= self.retention)
            || self.history.len() >= MAX_HISTORY
        {
            self.history.pop_front();
        }
        self.history.push_back(CachedPacket {
            seq: self.seq,
            bytes: out.clone(),
            sent_at: now,
            deadline,
        });
        self.seq = self.seq.wrapping_add(1);
        self.timestamp = self.timestamp.wrapping_add(FRAMES as u32);
        Ok(out)
    }
    pub fn retransmit_packet(&self, seq: u16, now: Instant) -> Retransmit {
        match self.history.iter().rev().find(|p| p.seq == seq) {
            Some(p)
                if now >= p.deadline
                    || now.saturating_duration_since(p.sent_at) >= self.retention =>
            {
                Retransmit::Expired
            }
            Some(p) => {
                let mut reply = vec![0x80, 0xd6];
                reply.extend(seq.to_be_bytes());
                reply.extend(&p.bytes);
                Retransmit::Packet(reply)
            }
            None => Retransmit::Missing,
        }
    }
    pub fn retransmit_request(req: &[u8]) -> Option<(u16, u16)> {
        if req.len() != 8 || req[0] & 0xc0 != 0x80 || req[1] & 0x7f != 0x55 {
            return None;
        }
        Some((
            u16::from_be_bytes([req[4], req[5]]),
            u16::from_be_bytes([req[6], req[7]]),
        ))
    }
}
pub fn sync_packet(
    rtp: u32,
    latency_samples: u32,
    now_ns: u64,
    clock_id: Option<u64>,
    first: bool,
) -> Vec<u8> {
    let mut out = vec![
        if first { 0x90 } else { 0x80 },
        if clock_id.is_some() { 0xd7 } else { 0xd4 },
        0,
        if clock_id.is_some() { 6 } else { 7 },
    ];
    // latencyMin is applied by the receiver. Shifting this mapping as well
    // doubles the audible latency (confirmed by 500/200ms acoustic probes).
    out.extend(rtp.to_be_bytes());
    if clock_id.is_some() {
        out.extend(now_ns.to_be_bytes());
    } else {
        out.extend(ntp(now_ns));
    }
    out.extend(
        if clock_id.is_some() {
            rtp.wrapping_sub(latency_samples)
        } else {
            rtp
        }
        .to_be_bytes(),
    );
    if let Some(id) = clock_id {
        out.extend(id.to_be_bytes());
    }
    out
}
pub fn ntp(unix_ns: u64) -> [u8; 8] {
    let mut out = [0; 8];
    let sec = (unix_ns / 1_000_000_000 + 2_208_988_800) as u32;
    let frac = (((unix_ns % 1_000_000_000) as u128 * (1u128 << 32)) / 1_000_000_000) as u32;
    out[..4].copy_from_slice(&sec.to_be_bytes());
    out[4..].copy_from_slice(&frac.to_be_bytes());
    out
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn wrap_retransmits_exact_ciphertext() {
        let mut p = Packetizer::new([9; 32], 65535, u32::MAX - 100, 7);
        let a = p.packet(&vec![0; PCM_BYTES], true).unwrap();
        let b = p.packet(&vec![0; PCM_BYTES], false).unwrap();
        assert_eq!(p.seq, 1);
        assert_eq!(p.timestamp, 603);
        for (seq, original) in [(65535, a), (0, b)] {
            let Retransmit::Packet(reply) = p.retransmit_packet(seq, Instant::now()) else {
                panic!("missing packet")
            };
            assert_eq!(&reply[4..], original);
        }
        for _ in 0..600 {
            p.packet(&vec![0; PCM_BYTES], false).unwrap();
        }
        assert!(matches!(
            p.retransmit_packet(65535, Instant::now()),
            Retransmit::Missing
        ));
        assert_eq!(p.history.len(), MAX_HISTORY);
    }
    #[test]
    fn audio_format_encodes_codec_and_rate() {
        assert_eq!(audio_format(false, 44100), 1 << 11);
        assert_eq!(audio_format(false, 48000), 1 << 15);
        assert_eq!(audio_format(true, 44100), 1 << 18);
        assert_eq!(audio_format(true, 48000), 1 << 20);
    }
    #[test]
    fn timing_uses_same_sample_clock() {
        let p = sync_packet(20000, 8820, 123, Some(456), true);
        assert_eq!(p.len(), 28);
        assert_eq!(&p[4..8], &20000u32.to_be_bytes());
        assert_eq!(&p[20..], &456u64.to_be_bytes());
    }
}

#[cfg(test)]
mod codec_tests {
    use super::*;
    #[test]
    fn alac_decodes_bit_exact_with_independent_decoder() {
        for rate in SUPPORTED_RATES {
            let key = [17; 32];
            let mut sender = Packetizer::with_rate(key, 9, 11, 13, rate);
            sender.enable_alac();
            let cookie = sender.alac.as_ref().unwrap().magic_cookie();
            let info = alac::StreamInfo::from_cookie(&cookie).unwrap();
            assert_eq!(info.sample_rate(), rate);
            let mut decoder = alac::Decoder::new(info);
            let mut receiver = Cipher::new(key);
            for phase in 0..3 {
                let samples: Vec<i16> = (0..FRAMES * 2)
                    .map(|i| (((i * 197 + phase * 31) % 60001) as i32 - 30000) as i16)
                    .collect();
                let pcm: Vec<u8> = samples.iter().flat_map(|s| s.to_be_bytes()).collect();
                let packet = sender.packet(&pcm, phase == 0).unwrap();
                let payload = receiver
                    .decrypt(&packet[12..packet.len() - 8], &packet[4..12])
                    .unwrap();
                let mut decoded = vec![0i16; FRAMES * 2];
                let result = decoder.decode_packet(&payload, &mut decoded).unwrap();
                assert_eq!(result, samples);
                assert!(packet.len() <= 1472);
            }
        }
    }
}
