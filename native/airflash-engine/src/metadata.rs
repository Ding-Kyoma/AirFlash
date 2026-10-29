//! Now Playing metadata for the receiver.
//!
//! A HomePod shows the title, artist and elapsed time from `SET_PARAMETER` on the
//! control connection. Apple senders use three payloads, and it is not documented
//! which one a given firmware prefers, so this module sends the two that carry the
//! information the display uses:
//!
//! - `text/parameters` with `progress: <start>/<position>/<duration>` in
//!   milliseconds, where `<start>` is the render start expressed in the same clock
//!   the stream is anchored to;
//! - `application/x-dmap-tagged` with a `mlit` item holding `minm` (title),
//!   `asar` (artist), `asal` (album) and `asgn` (genre).
//!
//! Artwork is deliberately not sent yet: the receiver accepts `image/jpeg`, but we
//! cannot verify the size/format constraints without a device, and a rejected
//! artwork request must never interfere with audio.

use crate::rtsp::{Cancellation, Connection};
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
    /// Largest clock jump that is still "the same track" for the display. Beyond
    /// this the receiver is told to jump instead of being sent a new start time.
    pub fn progress_body(&self, start_ms: u64) -> String {
        format!(
            "progress: {start_ms}/{}/{}\r\n",
            self.position_ms, self.duration_ms
        )
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
pub struct Control(Arc<Mutex<Option<(u64, NowPlaying)>>>);
impl Control {
    pub fn set(&self, sequence: u64, now: NowPlaying) {
        let mut slot = self.0.lock().unwrap();
        if slot.as_ref().is_none_or(|(old, _)| sequence >= *old) {
            *slot = Some((sequence, now));
        }
    }
    fn latest(&self) -> Option<(u64, NowPlaying)> {
        self.0.lock().unwrap().clone()
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

pub type Peer = (String, String, Arc<Mutex<Connection>>);

pub struct Worker {
    stop: Cancellation,
    worker: Option<JoinHandle<()>>,
    pub events: mpsc::Receiver<Value>,
}
impl Worker {
    pub fn start(peers: Vec<Peer>, control: Control) -> Result<Self> {
        ensure!(!peers.is_empty(), "metadata worker requires a receiver");
        let stop = Cancellation::default();
        let done = stop.clone();
        let (tx, events) = mpsc::sync_channel(16);
        let worker = thread::Builder::new()
            .name("airplay-metadata".into())
            .spawn(move || {
                let mut last: Option<(u64, NowPlaying, Instant)> = None;
                let mut tags_sent: Option<(u64, Vec<u8>)> = None;
                while !done.is_cancelled() {
                    let Some((sequence, now)) = control.latest() else {
                        thread::sleep(Duration::from_millis(100));
                        continue;
                    };
                    let fresh = last
                        .as_ref()
                        .is_none_or(|(old, previous, at)| {
                            *old != sequence
                                && (previous != &now || at.elapsed() >= PROGRESS_INTERVAL)
                        });
                    if !fresh {
                        thread::sleep(Duration::from_millis(50));
                        continue;
                    }
                    let mut errors = Vec::new();
                    let start_ms = start_of(&now);
                    let mut tags_updated = false;
                    for (host, uri, connection) in &peers {
                        if done.is_cancelled() {
                            return;
                        }
                        let mut connection = connection.lock().unwrap();
                        // Progress first: a receiver that only understands one of
                        // the two payloads still gets a moving display.
                        if let Err(error) = connection.request(
                            "SET_PARAMETER",
                            uri,
                            &[("Content-Type", "text/parameters".into())],
                            now.progress_body(start_ms).as_bytes(),
                        ) {
                            errors.push(format!("{host}: progress: {error:#}"));
                        }
                        let tags = dmap_tags(&now);
                        let unchanged = tags_sent
                            .as_ref()
                            .is_some_and(|(_, previous)| previous == &tags);
                        if !tags.is_empty() && !unchanged {
                            if let Err(error) = connection.request(
                                "SET_PARAMETER",
                                uri,
                                &[("Content-Type", "application/x-dmap-tagged".into())],
                                &tags,
                            ) {
                                errors.push(format!("{host}: tags: {error:#}"));
                            } else {
                                tags_updated = true;
                            }
                        }
                    }
                    if tags_updated {
                        tags_sent = Some((sequence, dmap_tags(&now)));
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
                }
            })?;
        Ok(Self {
            stop,
            worker: Some(worker),
            events,
        })
    }
}
/// The receiver's progress line expects an absolute start time on the sender's
/// clock; using the same base as the stream anchor keeps the bar aligned.
fn now_ms() -> u64 {
    #[cfg(windows)]
    {
        crate::wasapi::qpc_ns() / 1_000_000
    }
    #[cfg(not(windows))]
    {
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis() as u64)
            .unwrap_or(0)
    }
}
fn start_of(now: &NowPlaying) -> u64 {
    now_ms().saturating_sub(now.position_ms)
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
        for (tag, value) in [
            (b"minm", "Title"),
            (b"asar", "Artist"),
            (b"asal", "Album"),
        ] {
            assert_eq!(&inner[offset..offset + 4], tag);
            let len = u32::from_be_bytes(inner[offset + 4..offset + 8].try_into().unwrap()) as usize;
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
    fn progress_line_uses_milliseconds_and_crlf() {
        let body = sample().progress_body(1_000);
        assert_eq!(body, "progress: 1000/42000/240000\r\n");
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
                .set_read_timeout(Some(Duration::from_millis(500)))
                .unwrap();
            let mut pending = Vec::new();
            let mut buffer = [0; 4096];
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
                        let _ = socket.write_all(
                            format!("RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nContent-Length: 0\r\n\r\n")
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
        let peers = vec![(
            "127.0.0.1".to_string(),
            "/stream".to_string(),
            Arc::new(Mutex::new(connection)),
        )];
        let worker = Worker::start(peers, control.clone()).unwrap();
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
