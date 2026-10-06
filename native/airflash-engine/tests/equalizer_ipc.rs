//! IPC validation only: invalid requests never begin capture or connect to peers.
use serde_json::{Value, json};
use std::{
    io::{BufRead, BufReader, Write},
    process::{Command, Stdio},
};

#[test]
fn malformed_equalizer_updates_are_nonfatal_and_probes_reject_eq() {
    let mut engine = Command::new(env!("CARGO_BIN_EXE_airflash-engine"))
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let mut input = engine.stdin.take().unwrap();
    let requests = [
        ("hello", json!({})),
        (
            "set_equalizer",
            json!({"sequence":1,"equalizer":{"enabled":true,"band_gains_db":[1,2]}}),
        ),
        (
            "set_equalizer",
            json!({"sequence":2,"equalizer":{"preamp_db":13}}),
        ),
        (
            "set_equalizer",
            json!({"sequence":3,"equalizer":{"enabled":true}}),
        ),
        (
            "probe",
            json!({"peers":[{"host":"127.0.0.1"}],"duration_ms":100,"latency_ms":120,"gain":0.1,"equalizer":{"enabled":true}}),
        ),
        ("hello", json!({})),
    ];
    for (i, (command, params)) in requests.into_iter().enumerate() {
        writeln!(input, "{}", json!({"version":1,"id":i.to_string(),"session_id":"eq-test","command":command,"params":params})).unwrap();
    }
    drop(input);
    let events: Vec<Value> = BufReader::new(engine.stdout.take().unwrap())
        .lines()
        .map(|line| serde_json::from_str(&line.unwrap()).unwrap())
        .collect();
    assert!(engine.wait().unwrap().success());
    assert_eq!(events.len(), 6);
    assert!(
        events[0]["commands"]
            .as_array()
            .unwrap()
            .contains(&json!("set_equalizer"))
    );
    for (i, event) in events.iter().enumerate().take(4).skip(1) {
        assert_eq!(event["event"], "equalizer_error");
        assert_eq!(event["sequence"], i);
    }
    assert_eq!(events[4]["event"], "error");
    assert!(
        events[4]["message"]
            .as_str()
            .unwrap()
            .contains("equalizer to be disabled")
    );
    assert_eq!(events[5]["event"], "hello");
}
