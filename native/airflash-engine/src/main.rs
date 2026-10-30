//! Versioned JSONL qualification interface. stdout is exclusively structured IPC.
use airflash_engine::{
    rtsp::Cancellation,
    session::{ProbeOptions, probe_with_all_controls},
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
fn envelope(id: &str, session: &str, mut event: Value) -> Value {
    if event["event"] == "error" && event.get("code").is_none() {
        event["code"] = json!("command_error");
        event["host"] = Value::Null;
        event["channel"] = json!("ipc");
        event["retryable"] = json!(false);
    }
    event["version"] = json!(1);
    event["id"] = json!(id);
    event["session_id"] = json!(session);
    event
}
fn send(id: &str, session: &str, event: Value) {
    let event = envelope(id, session, event);
    let mut stdout = io::stdout().lock();
    let _ = writeln!(stdout, "{event}");
    let _ = stdout.flush();
}
struct Running {
    id: String,
    cancel: Cancellation,
    worker: Option<JoinHandle<()>>,
    gain: std::sync::Arc<std::sync::atomic::AtomicU32>,
    pending_pin: Option<std::sync::mpsc::Sender<zeroize::Zeroizing<String>>>,
    gain_limit: f64,
    sound_glow: airflash_engine::sound_glow::Control,
    volume: airflash_engine::volume::Control,
    standby: airflash_engine::standby::Control,
    equalizer: Option<(airflash_engine::equalizer::Control, u32)>,
}
impl Drop for Running {
    fn drop(&mut self) {
        self.cancel.cancel();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
fn capabilities() -> Vec<&'static str> {
    let mut values = vec![
        "receiver_profiles",
        "receiver_capabilities",
        "software_stereo",
        "continuity",
        "buffered_audio",
        "member_volume",
        "receiver_password",
        "standby_release",
        "sound_glow_test",
    ];
    if airflash_engine::applications::supported() {
        values.push("application_capture");
    }
    values
}

fn equalizer_event(running: Option<&Running>, session: &str, params: Value) -> Value {
    #[derive(Deserialize)]
    #[serde(deny_unknown_fields)]
    struct Update {
        sequence: u64,
        equalizer: airflash_engine::equalizer::Settings,
    }
    let sequence = params.get("sequence").cloned().unwrap_or(Value::Null);
    let result = (|| -> anyhow::Result<_> {
        let update: Update = serde_json::from_value(params)?;
        anyhow::ensure!(update.sequence > 0, "equalizer sequence must be positive");
        let running = running
            .filter(|r| r.id == session)
            .ok_or_else(|| anyhow::anyhow!("equalizer session is no longer active"))?;
        let (control, rate) = running
            .equalizer
            .as_ref()
            .ok_or_else(|| anyhow::anyhow!("equalizer updates require a live streaming session"))?;
        let prepared = airflash_engine::equalizer::Prepared::new(update.equalizer, *rate)?;
        let retained = control.set(update.sequence, prepared);
        match retained.outcome {
            airflash_engine::equalizer::SetOutcome::Accepted
            | airflash_engine::equalizer::SetOutcome::Idempotent => Ok(retained),
            airflash_engine::equalizer::SetOutcome::Stale => {
                anyhow::bail!(
                    "equalizer sequence is older than the retained sequence {}",
                    retained.sequence
                )
            }
            airflash_engine::equalizer::SetOutcome::Conflict => {
                anyhow::bail!(
                    "equalizer sequence {} conflicts with the retained preparation",
                    retained.sequence
                )
            }
        }
    })();
    match result {
        Ok(retained) => json!({
            "event": "equalizer_changed",
            "sequence": retained.sequence,
            "auto_attenuation_db": retained.prepared.auto_attenuation_db,
            "effective_preamp_db": retained.prepared.effective_preamp_db,
        }),
        Err(error) => json!({
            "event": "equalizer_error",
            "sequence": sequence,
            "message": error.to_string(),
        }),
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
        let parsed = serde_json::from_slice(&bytes);
        bytes.fill(0);
        let mut request: Request = match parsed {
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
                json!({"event":"hello","engine_version":env!("CARGO_PKG_VERSION"),"mode":"native","qualification":"experimental","live_loopback":true,"capabilities":capabilities(),"commands":["hello","get_receiver_capabilities","list_applications","start","probe","stop","set_gain","set_sound_glow","set_equalizer","set_standby","set_device_volume","pair","pair_pin"],"production_ready":false}),
            ),
            "get_receiver_capabilities" => {
                let result = (|| -> anyhow::Result<_> {
                    let peers: Vec<airflash_engine::session::Peer> = serde_json::from_value(
                        request.params.get("peers").cloned().unwrap_or(Value::Null),
                    )?;
                    anyhow::ensure!(
                        !peers.is_empty() && peers.len() <= 2,
                        "capability query requires one or two receivers"
                    );
                    peers
                        .iter()
                        .map(|peer| {
                            airflash_engine::receiver_capabilities::query(
                                peer,
                                Cancellation::default(),
                            )
                        })
                        .collect::<anyhow::Result<Vec<_>>>()
                })();
                match result {
                    Ok(members) => send(
                        &request.id,
                        &request.session_id,
                        json!({"event":"receiver_capabilities","members":members}),
                    ),
                    Err(error) => send(
                        &request.id,
                        &request.session_id,
                        json!({"event":"error","message":error.to_string()}),
                    ),
                }
            }
            "list_applications" => match airflash_engine::applications::list() {
                Ok(items) => send(
                    &request.id,
                    &request.session_id,
                    json!({"event":"applications","items":items}),
                ),
                Err(error) => send(
                    &request.id,
                    &request.session_id,
                    json!({"event":"error","message":error.to_string()}),
                ),
            },
            "set_standby" => {
                let result = (|| -> anyhow::Result<()> {
                    let settings = serde_json::from_value(request.params)?;
                    let running = running
                        .as_ref()
                        .filter(|r| r.id == request.session_id && r.equalizer.is_some())
                        .ok_or_else(|| anyhow::anyhow!("standby requires a live session"))?;
                    running.standby.set(settings)
                })();
                match result {
                    Ok(()) => send(
                        &request.id,
                        &request.session_id,
                        json!({"event":"standby_changed"}),
                    ),
                    Err(error) => send(
                        &request.id,
                        &request.session_id,
                        json!({"event":"error","message":error.to_string()}),
                    ),
                }
            }
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
                let standby = airflash_engine::standby::Control::new(options.standby);
                let worker_standby = standby.clone();
                let gain_limit = if request.command == "probe" { 0.1 } else { 1.0 };
                let rate = options.sample_rate;
                let equalizer =
                    airflash_engine::equalizer::Control::new(options.equalizer, rate).unwrap();
                let worker_equalizer = equalizer.clone();
                let live_equalizer = (request.command == "start").then_some((equalizer, rate));
                let sound_glow = options.sound_glow.clone();
                let worker = thread::spawn(move || {
                    let emit = |e| send(&request.id, &request.session_id, e);
                    if let Err(error) = probe_with_all_controls(
                        options,
                        worker_cancel,
                        worker_gain,
                        worker_volume,
                        worker_equalizer,
                        worker_standby,
                        emit,
                    ) {
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
                    sound_glow,
                    volume,
                    standby,
                    equalizer: live_equalizer,
                });
            }
            "set_device_volume" => {
                if let Some(r) = running
                    .as_ref()
                    .filter(|r| r.id == request.session_id && r.pending_pin.is_none())
                {
                    if let (Some(percent), Some(sequence)) = (
                        request
                            .params
                            .get("volume")
                            .and_then(Value::as_u64)
                            .filter(|v| *v <= 100),
                        request
                            .params
                            .get("sequence")
                            .and_then(Value::as_u64)
                            .filter(|v| *v > 0),
                    ) {
                        let command = airflash_engine::volume::Command {
                            sequence,
                            percent: percent as u8,
                        };
                        if let Some(host) = request.params.get("host").and_then(Value::as_str) {
                            r.volume.set_member(host, command);
                        } else {
                            r.volume.set(command);
                        }
                    } else {
                        send(
                            &request.id,
                            &request.session_id,
                            json!({"event":"device_volume_error","message":"invalid device volume command"}),
                        );
                    }
                }
            }
            "set_sound_glow" => {
                let result = (|| -> anyhow::Result<()> {
                    let command: airflash_engine::sound_glow::Command =
                        serde_json::from_value(request.params)?;
                    let active = running
                        .as_ref()
                        .filter(|r| r.id == request.session_id && r.equalizer.is_some())
                        .ok_or_else(|| {
                            anyhow::anyhow!("sound glow test requires an active live session")
                        })?;
                    active.sound_glow.set(command)
                })();
                send(
                    &request.id,
                    &request.session_id,
                    match result {
                        Ok(()) => json!({"event":"sound_glow_queued"}),
                        Err(error) => {
                            json!({"event":"sound_glow_error","message":error.to_string()})
                        }
                    },
                );
            }
            "set_equalizer" => {
                send(
                    &request.id,
                    &request.session_id,
                    equalizer_event(running.as_ref(), &request.session_id, request.params),
                );
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
                    if let (Some(channel), Some(pin)) =
                        (&r.pending_pin, request.params.get_mut("pin"))
                    {
                        if let Value::String(secret) = pin.take() {
                            let _ = channel.send(zeroize::Zeroizing::new(secret));
                        }
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
                let (tx, rx) = std::sync::mpsc::channel::<zeroize::Zeroizing<String>>();
                let password_required = request
                    .params
                    .get("password_required")
                    .and_then(Value::as_bool)
                    .unwrap_or(false);
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
                        let get_secret = || {
                            send(
                                &request.id,
                                &request.session_id,
                                json!({"event":if password_required {"password_required"}else{"pin_required"},"host":peer.host}),
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
                                    Ok(mut pin) => return Ok(std::mem::take(&mut *pin)),
                                    Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {}
                                    Err(_) => anyhow::bail!("PIN entry cancelled"),
                                }
                            }
                        };
                        if password_required {
                            let password = zeroize::Zeroizing::new(get_secret()?);
                            airflash_engine::auth::transient_password(&mut conn, &password)?;
                            airflash_engine::credentials::save_password(
                                &airflash_engine::credentials::directory(),
                                &device_id,
                                &password,
                            )?;
                        } else {
                            let credentials =
                                airflash_engine::auth::pair_prompt(&mut conn, get_secret)?;
                            airflash_engine::credentials::save(
                                &airflash_engine::credentials::directory(),
                                &device_id,
                                &credentials,
                            )?;
                        }
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
                    sound_glow: airflash_engine::sound_glow::Control::default(),
                    volume: airflash_engine::volume::Control::default(),
                    standby: airflash_engine::standby::Control::default(),
                    equalizer: None,
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

#[cfg(test)]
mod tests {
    use super::*;
    use airflash_engine::equalizer::{Control, Prepared, Settings};

    // A stored start slot exercises the production handler without sockets,
    // capture, or a worker-liveness contract.
    fn stored_start(rate: u32) -> Running {
        Running {
            id: "eq-session".to_owned(),
            cancel: Cancellation::default(),
            worker: None,
            gain: std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0)),
            pending_pin: None,
            gain_limit: 1.0,
            sound_glow: airflash_engine::sound_glow::Control::default(),
            volume: airflash_engine::volume::Control::default(),
            standby: airflash_engine::standby::Control::default(),
            equalizer: Some((Control::new(Settings::default(), rate).unwrap(), rate)),
        }
    }

    fn reply(running: Option<&Running>, session: &str, params: Value) -> Value {
        let event = envelope(
            "eq-request",
            session,
            equalizer_event(running, session, params),
        );
        let serialized = serde_json::to_string(&event).unwrap();
        let decoded: Value = serde_json::from_str(&serialized).unwrap();
        assert_eq!(decoded["version"], 1);
        assert_eq!(decoded["id"], "eq-request");
        assert_eq!(decoded["session_id"], session);
        // EQ errors use their existing nonfatal event rather than terminal IPC.
        assert!(decoded.get("retryable").is_none());
        assert!(decoded.get("code").is_none());
        decoded
    }

    #[test]
    fn equalizer_replies_match_retained_mailbox_through_rejection_and_replay() {
        for rate in [44100, 48000] {
            let running = stored_start(rate);
            let control = &running.equalizer.as_ref().unwrap().0;
            let flat = Settings {
                enabled: true,
                ..Settings::default()
            };
            let quiet = Settings {
                preamp_db: -12.0,
                ..flat
            };
            let flat_prepared = Prepared::new(flat, rate).unwrap();
            let quiet_prepared = Prepared::new(quiet, rate).unwrap();
            assert_ne!(
                flat_prepared.effective_preamp_db,
                quiet_prepared.effective_preamp_db
            );

            for (sequence, submitted, expected_event, retained_sequence, retained, message) in [
                (10, flat, "equalizer_changed", 10, flat_prepared, ""),
                (9, quiet, "equalizer_error", 10, flat_prepared, "older"),
                (10, quiet, "equalizer_error", 10, flat_prepared, "conflicts"),
                (10, flat, "equalizer_changed", 10, flat_prepared, ""),
                (11, quiet, "equalizer_changed", 11, quiet_prepared, ""),
            ] {
                let event = reply(
                    Some(&running),
                    "eq-session",
                    json!({"sequence": sequence, "equalizer": submitted}),
                );
                assert_eq!(event["event"], expected_event);
                assert_eq!(event["sequence"], sequence);
                if expected_event == "equalizer_changed" {
                    assert_eq!(event["auto_attenuation_db"], retained.auto_attenuation_db);
                    assert_eq!(event["effective_preamp_db"], retained.effective_preamp_db);
                } else {
                    assert!(event["message"].as_str().unwrap().contains(message));
                    assert!(event.get("auto_attenuation_db").is_none());
                    assert!(event.get("effective_preamp_db").is_none());
                }
                assert_eq!(control.latest(), Some((retained_sequence, retained)));
                assert!(running.cancel.check().is_ok());
            }
        }
    }

    #[test]
    fn rejected_equalizer_commands_leave_the_slot_usable() {
        let mut running = stored_start(44100);
        let control = running.equalizer.as_ref().unwrap().0.clone();
        let initial = control.latest().unwrap();
        for params in [
            Value::Null,
            json!({"sequence":0,"equalizer":{}}),
            json!({"sequence":-1,"equalizer":{}}),
            json!({"sequence":1,"equalizer":{"band_gains_db":[1,2]}}),
            json!({"sequence":2,"equalizer":{"preamp_db":13}}),
            json!({"sequence":3,"equalizer":{"band_gains_db":[0,0,0,0,0,0,0,0,0,-13]}}),
            json!({"sequence":4,"equalizer":{},"unexpected":true}),
        ] {
            let requested_sequence = params.get("sequence").cloned().unwrap_or(Value::Null);
            let event = reply(Some(&running), "eq-session", params);
            assert_eq!(event["event"], "equalizer_error");
            assert_eq!(event["sequence"], requested_sequence);
            assert!(!event["message"].as_str().unwrap().is_empty());
            assert_eq!(control.latest(), Some(initial));
            assert!(running.cancel.check().is_ok());
        }
        let valid = json!({"sequence":5,"equalizer":{"enabled":true,"preamp_db":-6}});
        assert_eq!(
            reply(Some(&running), "other-session", valid.clone())["event"],
            "equalizer_error"
        );
        assert_eq!(
            reply(None, "eq-session", valid.clone())["event"],
            "equalizer_error"
        );
        assert_eq!(control.latest(), Some(initial));

        // Probe and pairing entries both lack a live EQ control slot.
        let live_slot = running.equalizer.take();
        assert_eq!(
            reply(Some(&running), "eq-session", valid.clone())["event"],
            "equalizer_error"
        );
        let (pin, _received_pin) = std::sync::mpsc::channel();
        running.pending_pin = Some(pin);
        assert_eq!(
            reply(Some(&running), "eq-session", valid.clone())["event"],
            "equalizer_error"
        );
        assert_eq!(control.latest(), Some(initial));
        assert!(running.cancel.check().is_ok());

        running.pending_pin = None;
        running.equalizer = live_slot;
        let accepted = reply(Some(&running), "eq-session", valid);
        assert_eq!(accepted["event"], "equalizer_changed");
        assert_eq!(accepted["sequence"], 5);
        assert_eq!(accepted["effective_preamp_db"], -6.0);
        assert_eq!(control.latest().unwrap().0, 5);
    }
}
