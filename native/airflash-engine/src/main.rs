//! Versioned JSONL qualification interface. stdout is exclusively structured IPC.
use airflash_engine::{
    rtsp::Cancellation,
    session::{ProbeOptions, probe_with_volume},
};
use serde::Deserialize;
use serde_json::{Value, json};
use std::{
    io::{self, BufRead, Write},
    thread::{self, JoinHandle},
};
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Request {
    version: u32,
    id: String,
    session_id: String,
    command: String,
    #[serde(default)]
    params: Value,
}
fn send(id: &str, session: &str, mut event: Value) {
    if event["event"] == "error" && event.get("code").is_none() {
        event["code"] = json!("command_error");
        event["host"] = Value::Null;
        event["channel"] = json!("ipc");
        event["retryable"] = json!(false);
    }
    event["version"] = json!(1);
    event["id"] = json!(id);
    event["session_id"] = json!(session);
    let mut stdout = io::stdout().lock();
    let _ = writeln!(stdout, "{event}");
    let _ = stdout.flush();
}
struct Running {
    id: String,
    cancel: Cancellation,
    worker: Option<JoinHandle<()>>,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    pending_pin: Option<std::sync::mpsc::Sender<String>>,
    gain_limit: f64,
    volume: airflash_engine::volume::Control,
}
impl Drop for Running {
    fn drop(&mut self) {
        self.cancel.cancel();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
fn main() {
    let mut running: Option<Running> = None;
    // Limit individual IPC commands; no unbounded read_line allocation.
    let mut input = io::stdin().lock();
    let mut bytes = Vec::new();
    loop {
        bytes.clear();
        let mut exceeded = false;
        loop {
            let available = match input.fill_buf() {
                Ok(b) => b,
                Err(_) => return,
            };
            if available.is_empty() {
                break;
            }
            let n = available
                .iter()
                .position(|b| *b == b'\n')
                .map_or(available.len(), |p| p + 1);
            let finished = available[n - 1] == b'\n';
            if bytes.len() + n > 65536 {
                exceeded = true;
            } else if !exceeded {
                bytes.extend_from_slice(&available[..n]);
            }
            input.consume(n);
            if finished {
                break;
            }
        }
        if bytes.is_empty() && !exceeded {
            break;
        }
        if exceeded {
            send(
                "",
                "",
                json!({"event":"error","message":"IPC command exceeds 64KiB"}),
            );
            continue;
        }
        let request: Request = match serde_json::from_slice(&bytes) {
            Ok(r) => r,
            Err(e) => {
                send("", "", json!({"event":"error","message":e.to_string()}));
                continue;
            }
        };
        if request.version != 1 {
            send(
                &request.id,
                &request.session_id,
                json!({"event":"error","message":"unsupported IPC version"}),
            );
            continue;
        }
        match request.command.as_str() {
            "hello" => send(
                &request.id,
                &request.session_id,
                json!({"event":"hello","engine_version":env!("CARGO_PKG_VERSION"),"mode":"native","qualification":"partial","live_loopback":true,"commands":["hello","start","probe","stop","set_gain","set_device_volume","pair","pair_pin"],"production_ready":false}),
            ),
            "probe" | "start" => {
                let options: ProbeOptions = match serde_json::from_value(request.params) {
                    Ok(o) => o,
                    Err(e) => {
                        send(
                            &request.id,
                            &request.session_id,
                            json!({"event":"error","message":e.to_string()}),
                        );
                        continue;
                    }
                };
                if let Err(e) = if request.command == "start" {
                    options.validate_start()
                } else {
                    options.validate()
                } {
                    send(
                        &request.id,
                        &request.session_id,
                        json!({"event":"error","message":e.to_string()}),
                    );
                    continue;
                }
                if let Some(old) = running.take() {
                    drop(old);
                }
                let cancel = Cancellation::default();
                let worker_cancel = cancel.clone();
                let session = request.session_id.clone();
                let gain =
                    std::sync::Arc::new(std::sync::atomic::AtomicU32::new(options.gain.to_bits()));
                let worker_gain = gain.clone();
                let volume = airflash_engine::volume::Control::default();
                let worker_volume = volume.clone();
                let gain_limit = if request.command == "probe" { 0.1 } else { 1.0 };
                let worker = thread::spawn(move || {
                    let emit = |e| send(&request.id, &request.session_id, e);
                    if let Err(error) = probe_with_volume(options, worker_cancel, worker_gain, worker_volume, emit) {
                        emit(airflash_engine::transport::error_event(&error));
                    }
                });
                running = Some(Running {
                    id: session,
                    cancel,
                    worker: Some(worker),
                    gain,
                    pending_pin: None,
                    gain_limit,
                    volume,
                });
            }
            "set_device_volume" => {
                if let Some(r) = running.as_ref().filter(|r| r.id == request.session_id && r.pending_pin.is_none()) {
                    if let (Some(percent), Some(sequence)) = (
                        request.params.get("volume").and_then(Value::as_u64).filter(|v| *v <= 100),
                        request.params.get("sequence").and_then(Value::as_u64).filter(|v| *v > 0)) {
                        r.volume.set(airflash_engine::volume::Command { sequence, percent: percent as u8 });
                    } else {
                        send(&request.id, &request.session_id, json!({"event":"device_volume_error","message":"invalid device volume command"}));
                    }
                }
            }
            "set_gain" => {
                if let Some(r) = running.as_ref().filter(|r| r.id == request.session_id) {
                    if let Some(g) = request
                        .params
                        .get("gain")
                        .and_then(Value::as_f64)
                        .filter(|g| g.is_finite() && (0.0..=r.gain_limit).contains(g))
                    {
                        r.gain
                            .store((g as f32).to_bits(), std::sync::atomic::Ordering::Release);
                        send(
                            &request.id,
                            &request.session_id,
                            json!({"event":"gain_changed","gain":g}),
                        );
                    } else {
                        send(
                            &request.id,
                            &request.session_id,
                            json!({"event":"error","message":"gain must be 0..1"}),
                        );
                    }
                }
            }
            "pair_pin" => {
                if let Some(r) = running.as_ref().filter(|r| r.id == request.session_id) {
                    if let (Some(channel), Some(pin)) = (
                        &r.pending_pin,
                        request.params.get("pin").and_then(Value::as_str),
                    ) {
                        let _ = channel.send(pin.to_owned());
                    }
                }
            }
            "pair" => {
                let peer: airflash_engine::session::Peer = match serde_json::from_value(
                    request.params.get("peer").cloned().unwrap_or(Value::Null),
                ) {
                    Ok(peer) => peer,
                    Err(e) => {
                        send(
                            &request.id,
                            &request.session_id,
                            json!({"event":"error","message":e.to_string()}),
                        );
                        continue;
                    }
                };
                if let Some(old) = running.take() {
                    drop(old);
                }
                let cancel = Cancellation::default();
                let child_cancel = cancel.clone();
                let session = request.session_id.clone();
                let (tx, rx) = std::sync::mpsc::channel::<String>();
                let worker = thread::spawn(move || {
                    let result = (|| -> anyhow::Result<()> {
                        let mut conn = airflash_engine::rtsp::Connection::connect(
                            std::net::SocketAddr::new(peer.host, peer.port),
                            child_cancel.clone(),
                        )?;
                        let info = conn.request("GET", "/info", &[], &[])?.plist()?;
                        let device_id = info
                            .as_dictionary()
                            .and_then(|d| d.get("deviceID"))
                            .and_then(plist::Value::as_string)
                            .ok_or_else(|| anyhow::anyhow!("missing device identifier"))?
                            .to_owned();
                        let credentials = airflash_engine::auth::pair_prompt(&mut conn, || {
                            send(
                                &request.id,
                                &request.session_id,
                                json!({"event":"pin_required","host":peer.host}),
                            );
                            let deadline =
                                std::time::Instant::now() + std::time::Duration::from_secs(120);
                            loop {
                                child_cancel.check()?;
                                anyhow::ensure!(
                                    std::time::Instant::now() < deadline,
                                    "PIN entry timed out"
                                );
                                match rx.recv_timeout(std::time::Duration::from_millis(100)) {
                                    Ok(pin) => return Ok(pin),
                                    Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {}
                                    Err(_) => anyhow::bail!("PIN entry cancelled"),
                                }
                            }
                        })?;
                        airflash_engine::credentials::save(
                            &airflash_engine::credentials::directory(),
                            &device_id,
                            &credentials,
                        )?;
                        send(
                            &request.id,
                            &request.session_id,
                            json!({"event":"paired","device_id":device_id}),
                        );
                        Ok(())
                    })();
                    if let Err(error) = result {
                        send(
                            &request.id,
                            &request.session_id,
                            airflash_engine::transport::error_event(&error),
                        );
                    }
                });
                running = Some(Running {
                    id: session,
                    cancel,
                    worker: Some(worker),
                    gain: std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0)),
                    pending_pin: Some(tx),
                    gain_limit: 0.0,
                    volume: airflash_engine::volume::Control::default(),
                });
            }
            "stop" => {
                if running.as_ref().is_some_and(|r| r.id == request.session_id) {
                    let old = running.take().unwrap();
                    drop(old);
                }
                send(&request.id, &request.session_id, json!({"event":"stopped"}));
            }
            _ => send(
                &request.id,
                &request.session_id,
                json!({"event":"error","message":"unknown command"}),
            ),
        }
    }
    if let Some(old) = running {
        drop(old);
    }
}
