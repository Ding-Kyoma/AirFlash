//! Experimental Now Playing metadata, NOT a HomePod LED control API.
//! Uses the existing authenticated RTSP connection, off the audio thread.
use crate::rtsp::{Cancellation, Connection};
use anyhow::{Result, ensure};
use serde::Deserialize;
use serde_json::{Value, json};
use std::{
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, Ordering},
        mpsc,
    },
    thread::{self, JoinHandle},
    time::Duration,
};

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum Palette {
    MetadataOnly,
    Red,
    Blue,
    Green,
}
impl Palette {
    fn name(self) -> &'static str {
        match self {
            Self::MetadataOnly => "metadata_only",
            Self::Red => "red",
            Self::Blue => "blue",
            Self::Green => "green",
        }
    }
    fn artwork(self) -> Option<&'static [u8]> {
        match self {
            Self::MetadataOnly => None,
            Self::Red => Some(include_bytes!("../assets/sound-glow/red.jpg")),
            Self::Blue => Some(include_bytes!("../assets/sound-glow/blue.jpg")),
            Self::Green => Some(include_bytes!("../assets/sound-glow/green.jpg")),
        }
    }
}
#[derive(Clone, Copy, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Command {
    pub sequence: u64,
    pub palette: Palette,
}
#[derive(Clone, Debug, Default)]
pub struct Control(Arc<Mutex<Option<Command>>>);
impl Control {
    pub fn set(&self, command: Command) -> Result<()> {
        ensure!(command.sequence > 0, "sequence must be positive");
        let mut current = self.0.lock().unwrap();
        ensure!(
            current.is_none_or(|old| command.sequence > old.sequence),
            "stale sound glow command"
        );
        *current = Some(command);
        Ok(())
    }
    fn latest(&self) -> Option<Command> {
        *self.0.lock().unwrap()
    }
}

fn tag(name: &[u8; 4], value: &[u8], out: &mut Vec<u8>) {
    out.extend_from_slice(name);
    out.extend_from_slice(&(value.len() as u32).to_be_bytes());
    out.extend_from_slice(value);
}
fn metadata(palette: Palette) -> Vec<u8> {
    let mut fields = Vec::new();
    tag(
        b"minm",
        format!("AirFlash Sound Glow Test - {}", palette.name()).as_bytes(),
        &mut fields,
    );
    tag(b"asal", b"AirFlash Test Palettes", &mut fields);
    tag(b"asar", b"AirFlash", &mut fields);
    let mut out = Vec::new();
    tag(b"mlit", &fields, &mut out);
    out
}

pub struct Peer {
    pub host: String,
    pub uri: String,
    pub session: Option<String>,
    pub connection: Arc<Mutex<Connection>>,
    // Packed sequence/timestamp, updated by the media thread without locking.
    pub position: Arc<AtomicU64>,
}
pub fn position(sequence: u16, timestamp: u32) -> u64 {
    ((sequence as u64) << 32) | timestamp as u64
}
fn send_part(peer: &Peer, content_type: &str, body: &[u8]) -> Result<()> {
    let mut connection = peer.connection.lock().unwrap();
    let current = peer.position.load(Ordering::Acquire);
    let mut headers = vec![
        ("Content-Type", content_type.to_owned()),
        (
            "RTP-Info",
            format!("seq={};rtptime={}", current >> 32, current as u32),
        ),
    ];
    if let Some(session) = &peer.session {
        headers.push(("Session", session.clone()));
    }
    connection.request("SET_PARAMETER", &peer.uri, &headers, body)?;
    Ok(())
}

pub struct Worker {
    stop: Cancellation,
    handle: Option<JoinHandle<()>>,
    pub events: mpsc::Receiver<Value>,
}
impl Worker {
    pub fn start(peers: Vec<Peer>, control: Control) -> Result<Self> {
        let stop = Cancellation::default();
        let done = stop.clone();
        let (tx, events) = mpsc::sync_channel(32);
        let handle = thread::Builder::new().name("sound-glow-test".into()).spawn(move || {
            let mut last = 0;
            while !done.is_cancelled() {
                if let Some(command) = control.latest().filter(|c| c.sequence > last) {
                    last = command.sequence;
                    for peer in &peers {
                        if done.is_cancelled() { break; }
                        let data = metadata(command.palette);
                        let mut parts: Vec<(&str, &str, &[u8])> = vec![("metadata", "application/x-dmap-tagged", &data)];
                        if let Some(artwork) = command.palette.artwork() {
                            parts.push(("artwork", "image/jpeg", artwork));
                        }
                        // Each response is reported separately: 200 is acceptance, not proof of light.
                        for (part, content_type, body) in parts {
                            if done.is_cancelled() { break; }
                            let result = send_part(peer, content_type, body);
                            let event = json!({"event":"sound_glow_result", "sequence":command.sequence,
                                "host":peer.host,"palette":command.palette.name(),"part":part,
                                "accepted":result.is_ok(),"bytes":body.len(),
                                "message":result.as_ref().err().map(|e|format!("{e:#}")),"light_verified":false});
                            let _ = tx.try_send(event);
                            // Do not retry a rejected/failed request automatically. Allow another manual test.
                            if result.is_err() { break; }
                        }
                    }
                }
                thread::sleep(Duration::from_millis(50));
            }
        })?;
        Ok(Self {
            stop,
            handle: Some(handle),
            events,
        })
    }
}
impl Drop for Worker {
    fn drop(&mut self) {
        self.stop.cancel();
        if let Some(handle) = self.handle.take() {
            let _ = handle.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{io::Write, net::TcpListener};

    #[test]
    fn worker_sends_nothing_until_enabled_then_metadata_and_jpeg_with_rtp_position() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let connection =
            Connection::connect(listener.local_addr().unwrap(), Cancellation::default()).unwrap();
        let (socket, _) = listener.accept().unwrap();
        let (seen_tx, seen_rx) = mpsc::channel();
        let server = thread::spawn(move || {
            let mut writer = socket.try_clone().unwrap();
            let mut parser = Connection::from_stream(socket, Cancellation::default()).unwrap();
            for index in 0..2 {
                let request = parser
                    .read_for(Duration::from_secs(3), None)
                    .unwrap()
                    .unwrap();
                assert_eq!(
                    request.first,
                    "SET_PARAMETER rtsp://127.0.0.1/test RTSP/1.0"
                );
                assert_eq!(request.headers["rtp-info"], "seq=42;rtptime=123456");
                assert_eq!(request.headers["session"], "session-123");
                if index == 0 {
                    assert_eq!(request.headers["content-type"], "application/x-dmap-tagged");
                    assert_eq!(request.body, metadata(Palette::Blue));
                } else {
                    assert_eq!(request.headers["content-type"], "image/jpeg");
                    assert_eq!(request.body, Palette::Blue.artwork().unwrap());
                }
                seen_tx.send(index).unwrap();
                write!(
                    writer,
                    "RTSP/1.0 200 OK\r\nCSeq: {}\r\nContent-Length: 0\r\n\r\n",
                    request.headers["cseq"]
                )
                .unwrap();
            }
        });
        let control = Control::default();
        let worker = Worker::start(
            vec![Peer {
                host: "127.0.0.1".into(),
                uri: "rtsp://127.0.0.1/test".into(),
                session: Some("session-123".into()),
                connection: Arc::new(Mutex::new(connection)),
                position: Arc::new(AtomicU64::new(position(42, 123456))),
            }],
            control.clone(),
        )
        .unwrap();
        assert!(seen_rx.recv_timeout(Duration::from_millis(150)).is_err());
        control
            .set(Command {
                sequence: 1,
                palette: Palette::Blue,
            })
            .unwrap();
        for part in ["metadata", "artwork"] {
            let event = worker.events.recv_timeout(Duration::from_secs(3)).unwrap();
            assert_eq!(event["accepted"], true);
            assert_eq!(event["part"], part);
            assert_eq!(event["light_verified"], false);
        }
        drop(worker);
        server.join().unwrap();
    }

    #[test]
    fn receiver_rejection_is_reported_without_automatic_retry() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let connection =
            Connection::connect(listener.local_addr().unwrap(), Cancellation::default()).unwrap();
        let (socket, _) = listener.accept().unwrap();
        let server = thread::spawn(move || {
            let mut writer = socket.try_clone().unwrap();
            let mut parser = Connection::from_stream(socket, Cancellation::default()).unwrap();
            let request = parser
                .read_for(Duration::from_secs(3), None)
                .unwrap()
                .unwrap();
            write!(
                writer,
                "RTSP/1.0 415 Unsupported Media Type\r\nCSeq: {}\r\nContent-Length: 0\r\n\r\n",
                request.headers["cseq"]
            )
            .unwrap();
            assert!(
                parser
                    .read_for(Duration::from_millis(200), None)
                    .unwrap()
                    .is_none()
            );
        });
        let control = Control::default();
        control
            .set(Command {
                sequence: 1,
                palette: Palette::Red,
            })
            .unwrap();
        let worker = Worker::start(
            vec![Peer {
                host: "127.0.0.1".into(),
                uri: "rtsp://127.0.0.1/test".into(),
                session: None,
                connection: Arc::new(Mutex::new(connection)),
                position: Arc::new(AtomicU64::new(0)),
            }],
            control,
        )
        .unwrap();
        let event = worker.events.recv_timeout(Duration::from_secs(3)).unwrap();
        assert_eq!(event["accepted"], false);
        assert!(event["message"].as_str().unwrap().contains("415"));
        server.join().unwrap();
        drop(worker);
    }
    #[test]
    fn metadata_is_nested_dmap_with_big_endian_byte_lengths() {
        let body = metadata(Palette::Red);
        assert_eq!(&body[..4], b"mlit");
        assert_eq!(
            u32::from_be_bytes(body[4..8].try_into().unwrap()) as usize,
            body.len() - 8
        );
        let mut at = 8;
        let mut fields = Vec::new();
        while at < body.len() {
            let len = u32::from_be_bytes(body[at + 4..at + 8].try_into().unwrap()) as usize;
            fields.push((
                &body[at..at + 4],
                std::str::from_utf8(&body[at + 8..at + 8 + len]).unwrap(),
            ));
            at += 8 + len;
        }
        assert_eq!(
            fields,
            vec![
                (&b"minm"[..], "AirFlash Sound Glow Test - red"),
                (&b"asal"[..], "AirFlash Test Palettes"),
                (&b"asar"[..], "AirFlash")
            ]
        );
    }
    #[test]
    fn commands_are_explicit_bounded_and_monotonic() {
        let control = Control::default();
        assert!(control.latest().is_none());
        assert!(
            control
                .set(Command {
                    sequence: 0,
                    palette: Palette::Red
                })
                .is_err()
        );
        control
            .set(Command {
                sequence: 1,
                palette: Palette::Red,
            })
            .unwrap();
        assert!(
            control
                .set(Command {
                    sequence: 1,
                    palette: Palette::Blue
                })
                .is_err()
        );
        assert!(serde_json::from_str::<Command>(r#"{"sequence":2,"palette":"rainbow"}"#).is_err());
        control
            .set(Command {
                sequence: 2,
                palette: Palette::Blue,
            })
            .unwrap();
        assert_eq!(control.latest().unwrap().palette, Palette::Blue);
        assert!(Palette::MetadataOnly.artwork().is_none());
        for palette in [Palette::Red, Palette::Blue, Palette::Green] {
            let jpeg = palette.artwork().unwrap();
            assert!(jpeg.starts_with(&[0xff, 0xd8]) && jpeg.ends_with(&[0xff, 0xd9]));
            assert!(jpeg.len() < 16_384);
        }
    }
}
