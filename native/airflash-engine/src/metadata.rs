//! Now Playing metadata for the receiver.
//!
//! A HomePod shows the title, artist and elapsed time from `SET_PARAMETER` on the
//! control connection. Apple senders use three payloads, and it is not documented
//! which one a given firmware prefers, so this module sends the two that carry the
//! information the display uses:
//!
//! - `text/parameters` with `progress: <start>/<current>/<end>` in
//!   wrapping RTP sample timestamps, using the same clock as the audio stream;
//! - `application/x-dmap-tagged` with a `mlit` item holding `minm` (title),
//!   `asar` (artist), `asal` (album) and `asgn` (genre).
//!
//! Opt-in player JPEGs follow matching track tags. Synthetic Sound Glow tests
//! take exclusive ownership until normal playback or player artwork is selected.

use crate::{rtsp::Cancellation, sound_glow::Peer};
#[cfg(test)]
use crate::rtsp::Connection;
use anyhow::{Result, ensure};
use serde::Deserialize;
use serde_json::{Value, json};
use std::{
    sync::{Arc, Mutex, OnceLock, mpsc},
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

/// Track information as the desktop side knows it.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
pub struct NowPlaying {
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub artist: String,
    #[serde(default)]
    pub album: String,
    #[serde(default)]
    pub genre: String,
    #[serde(default)]
    pub duration_ms: u64,
    #[serde(default)]
    pub position_ms: u64,
    /// Paused tracks still show their tags; the progress bar is what stops.
    #[serde(default = "playing_default")]
    pub playing: bool,
}
fn playing_default() -> bool {
    true
}
impl NowPlaying {
    /// Nothing to show: the receiver keeps its previous display, which is what
    /// Apple senders do when the source goes away.
    pub fn is_empty(&self) -> bool {
        self.title.is_empty() && self.artist.is_empty() && self.album.is_empty()
    }
    /// All three values use the current stream's wrapping 32-bit RTP sample clock.
    pub fn progress_body(&self, current: u32, rate: u32, elapsed_ms: u64) -> String {
        let position = self
            .position_ms
            .saturating_add(if self.playing { elapsed_ms } else { 0 });
        let ticks =
            |ms: u64| ((u128::from(ms) * u128::from(rate) / 1000) & u128::from(u32::MAX)) as u32;
        let start = current.wrapping_sub(ticks(position));
        let end = if self.duration_ms == 0 {
            current
        } else {
            start.wrapping_add(ticks(self.duration_ms))
        };
        format!("progress: {start}/{current}/{end}\r\n")
    }
}

/// DMAP is a flat TLV format: four ASCII bytes of tag, a big-endian byte length,
/// then the payload. The outer `mlit` item wraps the text items.
pub fn dmap_tags(now: &NowPlaying) -> Vec<u8> {
    let mut inner = Vec::new();
    for (tag, value) in [
        (b"minm", now.title.as_str()),
        (b"asar", now.artist.as_str()),
        (b"asal", now.album.as_str()),
        (b"asgn", now.genre.as_str()),
    ] {
        if value.is_empty() {
            continue;
        }
        inner.extend_from_slice(tag);
        inner.extend_from_slice(&(value.len() as u32).to_be_bytes());
        inner.extend_from_slice(value.as_bytes());
    }
    if inner.is_empty() {
        return Vec::new();
    }
    let mut out = Vec::with_capacity(inner.len() + 8);
    out.extend_from_slice(b"mlit");
    out.extend_from_slice(&(inner.len() as u32).to_be_bytes());
    out.extend_from_slice(&inner);
    out
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Command {
    pub sequence: u64,
    pub sent_at: Instant,
}

#[derive(Clone, Default)]
pub struct Control(Arc<Mutex<Option<(u64, NowPlaying, Instant)>>>);
impl Control {
    pub fn set(&self, sequence: u64, now: NowPlaying) {
        let mut slot = self.0.lock().unwrap();
        if slot.as_ref().is_none_or(|(old, _, _)| sequence >= *old) {
            *slot = Some((sequence, now, Instant::now()));
        }
    }
    pub fn clear(&self) {
        *self.0.lock().unwrap() = None;
    }
    fn latest(&self) -> Option<(u64, NowPlaying, Instant)> {
        self.0.lock().unwrap().clone()
    }
    fn is_current(&self, sequence: u64) -> bool {
        self.0.lock().unwrap().as_ref().is_some_and(|(latest, _, _)| *latest == sequence)
    }
}

/// Process-wide control, mirroring the volume and lead controls: the engine runs
/// one session at a time and every IPC command can reach it without threading
/// another parameter through the call stack.
pub fn global() -> &'static Control {
    static CONTROL: OnceLock<Control> = OnceLock::new();
    CONTROL.get_or_init(Control::default)
}

/// Progress changes faster than the display can use; Apple senders coalesce to
/// roughly one update every second and a half.
pub const PROGRESS_INTERVAL: Duration = Duration::from_millis(1500);

#[derive(Clone)]
pub struct Timeline {
    timestamp: Arc<std::sync::atomic::AtomicU32>,
    rate: u32,
}
impl Timeline {
    pub fn new(timestamp: u32, rate: u32) -> Self {
        Self {
            timestamp: Arc::new(std::sync::atomic::AtomicU32::new(timestamp)),
            rate,
        }
    }
    pub fn set(&self, timestamp: u32) {
        self.timestamp
            .store(timestamp, std::sync::atomic::Ordering::Release);
    }
    fn current(&self) -> u32 {
        self.timestamp.load(std::sync::atomic::Ordering::Acquire)
    }
}

pub struct Worker {
    stop: Cancellation,
    worker: Option<JoinHandle<()>>,
    pub events: mpsc::Receiver<Value>,
}
impl Worker {
    pub fn start(
        peers: Vec<Peer>,
        control: Control,
        timeline: Timeline,
        glow: crate::sound_glow::Control,
    ) -> Result<Self> {
        ensure!(!peers.is_empty(), "metadata worker requires a receiver");
        let stop = Cancellation::default();
        let done = stop.clone();
        let (tx, events) = mpsc::sync_channel(16);
        let worker = thread::Builder::new()
            .name("airplay-metadata".into())
            .spawn(move || {
                let mut last: Option<(u64, NowPlaying, Instant)> = None;
                let mut tags_sent: Vec<Option<Vec<u8>>> = vec![None; peers.len()];
                let mut progress_enabled = vec![true; peers.len()];
                let mut tags_enabled = vec![true; peers.len()];
                let mut artwork_attempt = vec![0; peers.len()];
                let mut last_revision = 0;
                let mut last_test = 0;
                while !done.is_cancelled() {
                    if glow.is_active() {
                        thread::sleep(Duration::from_millis(50));
                        continue;
                    }
                    let Some((sequence, now, sampled)) = control.latest() else {
                        thread::sleep(Duration::from_millis(100));
                        continue;
                    };
                    let (test, revision, artwork) = glow.player_snapshot();
                    if test != last_test {
                        // A colour test can replace the receiver's tags between polls.
                        tags_sent.fill(None);
                        last = None;
                        last_test = test;
                    }
                    let fresh = revision != last_revision || last.as_ref().is_none_or(|(old, previous, at)| {
                        *old != sequence && (previous != &now || at.elapsed() >= PROGRESS_INTERVAL)
                    });
                    if !fresh {
                        thread::sleep(Duration::from_millis(50));
                        continue;
                    }
                    let mut errors = Vec::new();
                    let mut tags_updated = false;
                    for (index, peer) in peers.iter().enumerate() {
                        if done.is_cancelled() {
                            return;
                        }
                        let (host, uri) = (&peer.host, &peer.uri);
                        let mut connection = peer.connection.lock().unwrap();
                        // Check again under the transaction lock so an in-flight
                        // normal update cannot overwrite the experiment's tags.
                        if glow.is_active() {
                            break;
                        }
                        let current = timeline.current();
                        let progress = now.progress_body(
                            current,
                            timeline.rate,
                            sampled.elapsed().as_millis() as u64,
                        );
                        let stamp = ("RTP-Info", format!("rtptime={current}"));
                        if progress_enabled[index] {
                            if let Err(error) = connection.request(
                                "SET_PARAMETER",
                                uri,
                                &[("Content-Type", "text/parameters".into()), stamp.clone()],
                                progress.as_bytes(),
                            ) {
                                errors.push(format!("{host}: progress: {error:#}"));
                                // A complete rejection is persistent for this session. An I/O
                                // failure must not monopolise the shared control connection.
                                progress_enabled[index] = false;
                            }
                        }
                        let tags = dmap_tags(&now);
                        let pending_art = artwork.as_ref().filter(|art| {
                            artwork_attempt[index] != revision
                                && (art.title.as_str(), art.artist.as_str(), art.album.as_str())
                                    == (now.title.as_str(), now.artist.as_str(), now.album.as_str())
                                && glow.is_current(revision)
                                && control.is_current(sequence)
                        });
                        if let Some(art) = pending_art {
                            // Repeat the real tags with the same session and RTP header format
                            // as the colour tests, even when the thumbnail arrived a poll late.
                            let tag_result = crate::sound_glow::send_locked(
                                peer, &mut connection, "application/x-dmap-tagged", &tags, &done,
                            );
                            let (part, bytes, result) = match tag_result {
                                Ok(()) => {
                                    tags_sent[index] = Some(tags);
                                    tags_updated = true;
                                    if !glow.is_current(revision) || !control.is_current(sequence) { continue; }
                                    let result = crate::sound_glow::send_locked(
                                        peer, &mut connection, "image/jpeg", &art.jpeg, &done,
                                    );
                                    ("artwork", art.jpeg.len(), result)
                                }
                                Err(error) => ("metadata", tags.len(), Err(error)),
                            };
                            artwork_attempt[index] = revision;
                            let _ = tx.try_send(json!({"event":"sound_glow_result","sequence":revision,
                                "palette":"player_artwork","part":part,"host":host,"bytes":bytes,
                                "accepted":result.is_ok(),"light_verified":false,
                                "message":result.err().map(|e| format!("{e:#}"))}));
                        } else if tags_enabled[index]
                            && !tags.is_empty()
                            && tags_sent[index].as_ref() != Some(&tags)
                        {
                            if let Err(error) = connection.request(
                                "SET_PARAMETER",
                                uri,
                                &[("Content-Type", "application/x-dmap-tagged".into()), stamp],
                                &tags,
                            ) {
                                errors.push(format!("{host}: tags: {error:#}"));
                                tags_enabled[index] = false;
                            } else {
                                tags_sent[index] = Some(tags);
                                tags_updated = true;
                            }
                        }
                    }

                    let _ = tx.try_send(json!({
                        "event": "metadata_sent",
                        "title": now.title,
                        "artist": now.artist,
                        "position_ms": now.position_ms,
                        "duration_ms": now.duration_ms,
                        "playing": now.playing,
                        "tags_updated": tags_updated,
                        "message": errors.join("; "),
                    }));
                    last = Some((sequence, now, Instant::now()));
                    last_revision = revision;
                }
            })?;
        Ok(Self {
            stop,
            worker: Some(worker),
            events,
        })
    }
}
impl Drop for Worker {
    fn drop(&mut self) {
        self.stop.cancel();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample() -> NowPlaying {
        NowPlaying {
            title: "Title".into(),
            artist: "Artist".into(),
            album: "Album".into(),
            genre: String::new(),
            duration_ms: 240_000,
            position_ms: 42_000,
            playing: true,
        }
    }

    #[test]
    fn dmap_items_are_tagged_length_prefixed_and_nested_in_mlit() {
        let bytes = dmap_tags(&sample());
        assert_eq!(&bytes[..4], b"mlit");
        let inner_len = u32::from_be_bytes(bytes[4..8].try_into().unwrap()) as usize;
        assert_eq!(inner_len, bytes.len() - 8);
        let inner = &bytes[8..];
        // Three non-empty tags, in a fixed order, each with its own length.
        let mut offset = 0;
        for (tag, value) in [(b"minm", "Title"), (b"asar", "Artist"), (b"asal", "Album")] {
            assert_eq!(&inner[offset..offset + 4], tag);
            let len =
                u32::from_be_bytes(inner[offset + 4..offset + 8].try_into().unwrap()) as usize;
            assert_eq!(&inner[offset + 8..offset + 8 + len], value.as_bytes());
            offset += 8 + len;
        }
        assert_eq!(offset, inner.len(), "no trailing bytes");
    }

    #[test]
    fn empty_tracks_produce_no_dmap_item() {
        assert!(dmap_tags(&NowPlaying::default()).is_empty());
        assert!(NowPlaying::default().is_empty());
        assert!(!sample().is_empty());
    }

    #[test]
    fn progress_line_uses_wrapping_rtp_samples_and_crlf() {
        let body = sample().progress_body(1_852_200, 44100, 0);
        assert_eq!(body, "progress: 0/1852200/10584000\r\n");
    }

    #[test]
    fn progress_wraps_at_the_selected_sample_rate_and_pause_stops_elapsed_time() {
        let mut now = sample();
        now.position_ms = 1_000;
        now.duration_ms = 2_000;
        let start = 10u32.wrapping_sub(72_000);
        let end = start.wrapping_add(96_000);
        assert_eq!(
            now.progress_body(10, 48_000, 500),
            format!("progress: {start}/10/{end}\r\n")
        );
        now.playing = false;
        let start = 10u32.wrapping_sub(48_000);
        let end = start.wrapping_add(96_000);
        assert_eq!(
            now.progress_body(10, 48_000, 500),
            format!("progress: {start}/10/{end}\r\n")
        );
    }

    #[test]
    fn control_keeps_the_newest_sequence() {
        let control = Control::default();
        control.set(2, sample());
        control.set(1, NowPlaying::default());
        assert_eq!(control.latest().unwrap().0, 2);
        assert_eq!(control.latest().unwrap().1.title, "Title");
        control.set(3, NowPlaying::default());
        assert_eq!(control.latest().unwrap().0, 3);
    }

    #[test]
    fn worker_sends_progress_then_tags_and_only_repeats_progress() {
        use std::io::{Read, Write};
        use std::net::TcpListener;
        use std::sync::mpsc;

        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let (requests, received) = mpsc::channel();
        let server = std::thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            socket
                .set_read_timeout(Some(Duration::from_secs(3)))
                .unwrap();
            let mut pending = Vec::new();
            let mut buffer = [0; 4096];
            let mut cover_count = 0;
            loop {
                let read = socket.read(&mut buffer).unwrap_or(0);
                if read == 0 {
                    break;
                }
                pending.extend_from_slice(&buffer[..read]);
                // Every request in this test has an empty or self-contained body, so
                // a complete read is one shot of two requests back to back.
                while let Some(end) = find_request_end(&pending) {
                    let request = String::from_utf8_lossy(&pending[..end]).to_string();
                    let body_len = request
                        .lines()
                        .find_map(|line| {
                            line.to_ascii_lowercase()
                                .strip_prefix("content-length:")
                                .map(|value| value.trim().parse::<usize>().unwrap_or(0))
                        })
                        .unwrap_or(0);
                    let total = end + body_len;
                    if pending.len() < total {
                        break;
                    }
                    let body = pending[end..total].to_vec();
                    pending.drain(..total);
                    let cseq = request.lines().find_map(|line| {
                        line.to_ascii_lowercase()
                            .strip_prefix("cseq:")
                            .map(|v| v.trim().to_string())
                    });
                    if let Some(cseq) = cseq {
                        let status = if request.to_ascii_lowercase().contains("content-type: image/jpeg") {
                            cover_count += 1;
                            if cover_count == 2 { "415 Unsupported Media Type" } else { "200 OK" }
                        } else { "200 OK" };
                        let _ = socket.write_all(
                            format!("RTSP/1.0 {status}\r\nCSeq: {cseq}\r\nContent-Length: 0\r\n\r\n")
                                .as_bytes(),
                        );
                    }
                    if requests.send((request, body)).is_err() {
                        return;
                    }
                }
            }
        });
        let control = Control::default();
        control.set(1, sample());
        let connection = Connection::connect(addr, Cancellation::default()).unwrap();
        let peers = vec![Peer {
            host: "127.0.0.1".into(), uri: "/stream".into(), session: Some("test-session".into()),
            connection: Arc::new(Mutex::new(connection)), position: Arc::new(0.into()),
        }];
        let glow = crate::sound_glow::Control::default();
        let worker = Worker::start(
            peers,
            control.clone(),
            Timeline::new(42_000 * 441 / 10, 44100),
            glow.clone(),
        )
        .unwrap();
        let first = received
            .recv_timeout(Duration::from_secs(2))
            .expect("progress payload");
        assert!(first.0.starts_with("SET_PARAMETER /stream RTSP/1.0"));
        assert!(
            first
                .0
                .to_ascii_lowercase()
                .contains("content-type: text/parameters")
        );
        assert!(
            String::from_utf8_lossy(&first.1).starts_with("progress: "),
            "{:?}",
            String::from_utf8_lossy(&first.1)
        );
        let second = received
            .recv_timeout(Duration::from_secs(2))
            .expect("tags payload");
        assert!(
            second
                .0
                .to_ascii_lowercase()
                .contains("content-type: application/x-dmap-tagged")
        );
        assert_eq!(&second.1[..4], b"mlit");
        // Same track, new position: progress is re-sent, tags are not.
        let mut moved = sample();
        moved.position_ms += 2_000;
        control.set(2, moved.clone());
        let third = received
            .recv_timeout(Duration::from_secs(3))
            .expect("progress after a position change");
        assert!(String::from_utf8_lossy(&third.1).starts_with("progress: "));
        glow.set(crate::sound_glow::Command {
            sequence: 1,
            palette: crate::sound_glow::Palette::Red,
        })
        .unwrap();
        moved.title = "Normal metadata must not overwrite the experiment".into();
        control.set(3, moved);
        assert!(received.recv_timeout(Duration::from_millis(250)).is_err());
        // Artwork arrives before its track. Resume real tags, but do not pair the
        // pending image with whatever track the previous poll still described.
        use base64::{Engine, engine::general_purpose::STANDARD};
        let jpeg = include_bytes!("../assets/sound-glow/blue.jpg");
        let actual = sample();
        glow.set_request(json!({"sequence":2,"palette":"player_artwork","track":{
            "title":actual.title,"artist":actual.artist,"album":actual.album,"jpeg":STANDARD.encode(jpeg)
        }})).unwrap();
        let next = || received.recv_timeout(Duration::from_secs(2)).unwrap();
        assert!(next().0.to_ascii_lowercase().contains("text/parameters"));
        assert!(next().0.to_ascii_lowercase().contains("application/x-dmap-tagged"));
        control.set(4, actual.clone());
        assert!(next().0.to_ascii_lowercase().contains("text/parameters"));
        let (tag_header, tag_body) = next();
        assert_eq!(tag_body, dmap_tags(&actual));
        assert!(tag_header.contains("Session: test-session"));
        assert!(tag_header.contains("RTP-Info: seq=0;rtptime=0"));
        let (header, body) = next();
        assert!(header.to_ascii_lowercase().contains("content-type: image/jpeg"));
        assert!(header.contains("Session: test-session"));
        assert!(header.contains("RTP-Info: seq=0;rtptime=0"));
        assert_eq!(body, jpeg);
        let mut advancing = actual.clone();
        advancing.position_ms += 2000;
        control.set(5, advancing);
        assert!(next().0.to_ascii_lowercase().contains("text/parameters"));
        assert!(received.recv_timeout(Duration::from_millis(100)).is_err());
        glow.set_request(json!({"sequence":3,"palette":"player_artwork","track":{
            "title":actual.title,"artist":actual.artist,"album":actual.album,"jpeg":STANDARD.encode(jpeg)
        }})).unwrap();
        assert!(next().0.to_ascii_lowercase().contains("text/parameters"));
        assert_eq!(next().1, dmap_tags(&actual));
        assert_eq!(next().1, jpeg);
        let rejection = loop {
            let event = worker.events.recv_timeout(Duration::from_secs(2)).unwrap();
            if event["event"] == "sound_glow_result" && event["sequence"] == 3 { break event; }
        };
        assert_eq!(rejection["accepted"], false);
        let mut different = actual;
        different.title = "Track after rejected cover".into();
        control.set(6, different.clone());
        assert!(next().0.to_ascii_lowercase().contains("text/parameters"));
        assert_eq!(next().1, dmap_tags(&different));
        assert!(received.recv_timeout(Duration::from_millis(100)).is_err());
        drop(worker);
        drop(received);
        let _ = server.join();
    }

    /// Index just past the blank line that ends the RTSP header block.
    fn find_request_end(bytes: &[u8]) -> Option<usize> {
        bytes
            .windows(4)
            .position(|window| window == b"\r\n\r\n")
            .map(|at| at + 4)
    }
}
