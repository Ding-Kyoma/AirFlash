//! Device volume is receiver state, independent of PCM gain. All RTSP I/O stays
//! off the media thread and shares the feedback connection's transaction lock.
use crate::rtsp::{Cancellation, Connection};
use anyhow::{Context, Result, ensure};
use serde_json::{Value, json};
use std::{
    sync::{Arc, Mutex, mpsc},
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

#[derive(Clone, Copy, Debug)]
pub struct Command {
    pub sequence: u64,
    pub percent: u8,
}
#[derive(Clone, Default)]
pub struct Control(Arc<Mutex<Option<Command>>>);
impl Control {
    pub fn set(&self, command: Command) {
        let mut latest = self.0.lock().unwrap();
        if latest.is_none_or(|old| command.sequence > old.sequence) {
            *latest = Some(command);
        }
    }
    fn latest(&self) -> Option<Command> {
        *self.0.lock().unwrap()
    }
}
pub type Peer = (String, String, Arc<Mutex<Connection>>);
pub fn to_db(percent: u8) -> f64 {
    if percent == 0 {
        -144.0
    } else {
        f64::from(percent) * 0.3 - 30.0
    }
}
pub fn from_db(db: f64) -> Result<u8> {
    ensure!(db.is_finite() && db <= 0.0, "invalid receiver volume");
    Ok(if db <= -30.0 {
        0
    } else {
        ((db + 30.0) / 0.3).round() as u8
    })
}
fn read(connection: &mut Connection) -> Result<u8> {
    let info = connection.request("GET", "/info", &[], &[])?.plist()?;
    let value = info
        .as_dictionary()
        .and_then(|d| d.get("initialVolume"))
        .context("receiver does not report initialVolume")?;
    let db = value
        .as_real()
        .or_else(|| value.as_signed_integer().map(|n| n as f64))
        .context("invalid initialVolume type")?;
    from_db(db)
}
#[derive(Default)]
struct Pending {
    command: Option<Command>,
    sent: Option<Instant>,
    write_failed: bool,
    settled: bool,
}
impl Pending {
    fn status(&mut self, values: &[Option<u8>], now: Instant) -> &'static str {
        let readable = values.iter().all(Option::is_some);
        if let Some(command) = self.command {
            if !self.settled {
                if !self.write_failed
                    && readable
                    && values.iter().all(|v| *v == Some(command.percent))
                {
                    self.settled = true;
                    return "confirmed";
                }
                if !self.write_failed
                    && self
                        .sent
                        .is_some_and(|at| now.duration_since(at) < Duration::from_secs(3))
                {
                    return "pending";
                }
                self.settled = true;
                return "unconfirmed";
            }
        }
        if readable { "confirmed" } else { "unsynced" }
    }
}
pub struct Worker {
    stop: Cancellation,
    worker: Option<JoinHandle<()>>,
    pub events: mpsc::Receiver<Value>,
}
impl Worker {
    /// Peers are ordered with the stereo leader first by the desktop controller.
    pub fn start(peers: Vec<Peer>, control: Control) -> Result<Self> {
        ensure!(!peers.is_empty(), "volume worker requires a receiver");
        let stop = Cancellation::default();
        let done = stop.clone();
        let (tx, events) = mpsc::sync_channel(32);
        let worker = thread::Builder::new().name("airplay-volume".into()).spawn(move || {
            let mut pending = Pending::default();
            let mut next = Instant::now();
            while !done.is_cancelled() {
                let command = control.latest();
                let changed = command.map(|c| c.sequence) != pending.command.map(|c| c.sequence);
                if !changed && Instant::now() < next {
                    thread::sleep(Duration::from_millis(20));
                    continue;
                }
                let mut errors = Vec::new();
                if changed {
                    pending = Pending { command, sent: Some(Instant::now()), ..Pending::default() };
                    if let Some(command) = command {
                        for (host, uri, connection) in &peers {
                            if done.is_cancelled() { return; }
                            let result = connection.lock().unwrap().request("SET_PARAMETER", uri,
                                &[("Content-Type", "text/parameters".into())],
                                format!("volume: {:.6}\r\n", to_db(command.percent)).as_bytes());
                            if let Err(error) = result {
                                pending.write_failed = true;
                                errors.push(format!("{host}: {error:#}"));
                            }
                        }
                    }
                }
                let mut values = Vec::new();
                for (host, _, connection) in &peers {
                    if done.is_cancelled() { return; }
                    match read(&mut connection.lock().unwrap()) {
                        Ok(value) => values.push(Some(value)),
                        Err(error) => { values.push(None); errors.push(format!("{host}: {error:#}")); }
                    }
                }
                let status = pending.status(&values, Instant::now());
                let event = json!({"event":"device_volume", "host":peers[0].0,
                    "sequence":pending.command.map_or(0, |c| c.sequence),
                    "volume":values[0], "available":values.iter().all(Option::is_some),
                    "status":status, "message":errors.join("; "),
                    "members":peers.iter().zip(&values).map(|((host,_,_),v)| json!({"host":host,"volume":v})).collect::<Vec<_>>()});
                let _ = tx.try_send(event);
                next = Instant::now() + Duration::from_secs(1);
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
    #[test]
    fn conversion_and_invalid_values() {
        for percent in 0..=100 {
            assert_eq!(from_db(to_db(percent)).unwrap(), percent);
        }
        assert_eq!(from_db(-144.0).unwrap(), 0);
        assert!(from_db(f64::NAN).is_err());
        assert!(from_db(f64::INFINITY).is_err());
        assert!(from_db(1.0).is_err());
    }
    #[test]
    fn delayed_reads_and_pair_confirmation() {
        let now = Instant::now();
        let mut pending = Pending {
            command: Some(Command {
                sequence: 1,
                percent: 40,
            }),
            sent: Some(now),
            ..Pending::default()
        };
        assert_eq!(pending.status(&[Some(20), Some(20)], now), "pending");
        assert_eq!(pending.status(&[Some(40), Some(20)], now), "pending");
        assert_eq!(pending.status(&[Some(40), Some(40)], now), "confirmed");
        // External physical changes are authoritative once the write is confirmed.
        assert_eq!(pending.status(&[Some(45), Some(45)], now), "confirmed");
    }
    #[test]
    fn timeout_and_missing_reads_do_not_confirm() {
        let now = Instant::now();
        let mut pending = Pending {
            command: Some(Command {
                sequence: 1,
                percent: 40,
            }),
            sent: Some(now),
            ..Pending::default()
        };
        assert_eq!(pending.status(&[None], now), "pending");
        assert_eq!(
            pending.status(&[Some(20)], now + Duration::from_secs(3)),
            "unconfirmed"
        );
        assert_eq!(
            pending.status(&[None], now + Duration::from_secs(4)),
            "unsynced"
        );
    }
    #[test]
    fn latest_command_wins() {
        let control = Control::default();
        control.set(Command {
            sequence: 2,
            percent: 30,
        });
        control.set(Command {
            sequence: 1,
            percent: 80,
        });
        assert_eq!(control.latest().unwrap().percent, 30);
    }
}
