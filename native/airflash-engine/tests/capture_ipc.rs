//! Invalid live/probe requests never capture audio or connect to a receiver.
use serde_json::{Value, json};
use std::{
    io::{BufRead, BufReader, Write},
    process::{Command, Stdio},
};

#[test]
fn capabilities_and_capture_validation_preserve_finite_probe_boundary() {
    let mut engine = Command::new(env!("CARGO_BIN_EXE_airflash-engine"))
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()
        .unwrap();
    let mut input = engine.stdin.take().unwrap();
    let base = json!({"peers":[{"host":"127.0.0.1"}],"source":"loopback","duration_ms":0,"latency_ms":120,"gain":0.1});
    let mut missing = base.clone();
    missing["source"] = json!("application");
    let mut bad_standby = base.clone();
    bad_standby["standby"] = json!({"enabled":true,"silence_seconds":4});
    let mut probe = base.clone();
    probe["duration_ms"] = json!(100);
    probe["standby"] = json!({"enabled":true,"silence_seconds":10});
    let requests = [
        ("hello", json!({})),
        ("start", missing),
        ("start", bad_standby),
        ("probe", probe),
        ("set_standby", json!({"enabled":true,"silence_seconds":10})),
        ("hello", json!({})),
    ];
    for (id, (command, params)) in requests.into_iter().enumerate() {
        writeln!(input, "{}", json!({"version":1,"id":id.to_string(),"session_id":"capture-test","command":command,"params":params})).unwrap();
    }
    drop(input);
    let events: Vec<Value> = BufReader::new(engine.stdout.take().unwrap())
        .lines()
        .map(|line| serde_json::from_str(&line.unwrap()).unwrap())
        .collect();
    assert!(engine.wait().unwrap().success());
    assert_eq!(events.len(), 6);
    let capabilities = events[0]["capabilities"].as_array().unwrap();
    for capability in [
        "standby_release",
        "software_stereo",
        "continuity",
        "buffered_audio",
        "member_volume",
    ] {
        assert!(capabilities.contains(&json!(capability)));
    }
    assert_eq!(
        capabilities.contains(&json!("application_capture")),
        airflash_engine::applications::supported()
    );
    for event in &events[1..5] {
        assert_eq!(event["event"], "error");
    }
    assert!(
        events[3]["message"]
            .as_str()
            .unwrap()
            .contains("finite probes")
    );
    assert_eq!(events[5]["event"], "hello");
}

#[test]
fn absent_application_keeps_reporting_and_live_settings_and_stop_remain_available() {
    if !airflash_engine::applications::supported() {
        return;
    }
    let mut engine = Command::new(env!("CARGO_BIN_EXE_airflash-engine"))
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()
        .unwrap();
    let mut input = engine.stdin.take().unwrap();
    let output = engine.stdout.take().unwrap();
    let (events_tx, events_rx) = std::sync::mpsc::channel::<Value>();
    let reader = std::thread::spawn(move || {
        for line in BufReader::new(output).lines() {
            let event = serde_json::from_str(&line.unwrap()).unwrap();
            if events_tx.send(event).is_err() {
                break;
            }
        }
    });
    let write = |input: &mut std::process::ChildStdin, command: &str, params: Value| {
        writeln!(input, "{}", json!({"version":1,"id":command,"session_id":"waiting-test","command":command,"params":params})).unwrap();
        input.flush().unwrap();
    };
    write(
        &mut input,
        "start",
        json!({
            "peers":[{"host":"127.0.0.1","port":1}],
            "source":"application", "capture_application_path":"C:\\airflash-tests-missing\\never-running.exe",
            "duration_ms":0, "latency_ms":120, "gain":0.0,
            "standby":{"enabled":true,"silence_seconds":5}
        }),
    );
    let read = || {
        events_rx
            .recv_timeout(std::time::Duration::from_secs(5))
            .expect("IPC report timed out")
    };
    let mut metrics = 0;
    let mut waiting = false;
    while metrics < 2 {
        let event = read();
        assert_ne!(event["event"], "error", "{event}");
        assert_ne!(event["event"], "streaming");
        waiting |= event["event"] == "standby" && event["reason"] == "application_unavailable";
        metrics += usize::from(event["event"] == "capture_metrics");
    }
    assert!(waiting);
    write(
        &mut input,
        "set_standby",
        json!({"enabled":false,"silence_seconds":20}),
    );
    loop {
        let event = read();
        assert_ne!(event["event"], "error", "{event}");
        assert_ne!(event["event"], "streaming");
        if event["event"] == "standby_changed" {
            break;
        }
    }
    write(&mut input, "stop", json!({}));
    drop(input);
    reader.join().unwrap();
    let remaining: Vec<Value> = events_rx.try_iter().collect();
    assert!(engine.wait().unwrap().success());
    assert!(remaining.iter().any(|event| event["event"] == "stopped"));
    assert!(!remaining.iter().any(|event| event["event"] == "streaming"));
}
