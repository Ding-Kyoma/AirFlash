//! Real command-process fixtures. Hello never opens receivers or starts capture.
use serde_json::{Value, json};
use std::{
    io::{BufRead, BufReader, Write},
    process::{Child, Command, Stdio},
};

struct Engine(Child);
impl Drop for Engine {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

fn hello(id: &str, padding: &str) -> Vec<u8> {
    let mut bytes = serde_json::to_vec(&json!({
        "version":1,
        "id":id,
        "session_id":"synthetic-ipc",
        "command":"hello",
        "params":{"padding":padding},
    }))
    .unwrap();
    bytes.push(b'\n');
    bytes
}

fn assert_terminal_ipc(event: &Value) {
    assert_eq!(event["version"], 1);
    assert_eq!(event["event"], "error");
    assert_eq!(event["id"], "");
    assert_eq!(event["session_id"], "");
    assert_eq!(event["code"], "command_error");
    assert_eq!(event["channel"], "ipc");
    assert_eq!(event["host"], Value::Null);
    assert_eq!(event["retryable"], false);
    assert!(!event["message"].as_str().unwrap().is_empty());
}

#[test]
fn oversized_and_malformed_hello_reject_with_original_ipc_contract_then_recover() {
    let mut engine = Engine(
        Command::new(env!("CARGO_BIN_EXE_airflash-engine"))
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .unwrap(),
    );
    let mut input = engine.0.stdin.take().unwrap();
    let boundary = hello("at-limit", &"a".repeat(65536 - hello("at-limit", "").len()));
    assert_eq!(boundary.len(), 65536); // LF is part of the native byte budget.
    input.write_all(&boundary).unwrap();

    let oversized = hello(
        "too-large",
        &"a".repeat(65537 - hello("too-large", "").len()),
    );
    assert_eq!(oversized.len(), 65537);
    input.write_all(&oversized).unwrap();

    let unicode = hello("utf8-too-large", &"é".repeat(33000));
    assert!(unicode.len() > 65536);
    assert!(std::str::from_utf8(&unicode).unwrap().chars().count() < 65536);
    input.write_all(&unicode).unwrap();

    input.write_all(b"{\n").unwrap();
    writeln!(
        input,
        "{}",
        json!({
            "version":1,"id":"invalid-envelope","session_id":"synthetic-ipc",
            "command":"hello","unexpected":true,
        })
    )
    .unwrap();
    input.write_all(&hello("after-errors", "")).unwrap();
    drop(input);

    let events: Vec<Value> = BufReader::new(engine.0.stdout.take().unwrap())
        .lines()
        .map(|line| serde_json::from_str(&line.unwrap()).unwrap())
        .collect();
    assert!(engine.0.wait().unwrap().success());
    assert_eq!(events.len(), 6);
    for (event, id) in [(&events[0], "at-limit"), (&events[5], "after-errors")] {
        assert_eq!(event["version"], 1);
        assert_eq!(event["event"], "hello");
        assert_eq!(event["id"], id);
        assert_eq!(event["session_id"], "synthetic-ipc");
    }
    for event in &events[1..5] {
        assert_terminal_ipc(event);
    }
    for event in &events[1..3] {
        assert_eq!(event["message"], "IPC command exceeds 64KiB");
    }
    assert!(
        events[4]["message"]
            .as_str()
            .unwrap()
            .contains("unknown field")
    );
}
