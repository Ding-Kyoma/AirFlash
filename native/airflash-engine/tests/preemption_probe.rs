//! Live `statusFlags` probe contract against a localhost `/info` server.
//! No real receiver and no HomePod.
use airflash_engine::{
    preemption::{
        self, action_for, action_for_result, another_sender, ProbeAction, ProbeOutcome,
    },
    rtsp::{Cancellation, Connection},
};
use plist::{Dictionary, Value};
use std::{
    net::{SocketAddr, TcpListener},
    thread::{self, JoinHandle},
    time::Duration,
};

fn info_body(status_flags: Option<u32>) -> Vec<u8> {
    let mut dictionary = Dictionary::new();
    dictionary.insert("initialVolume".into(), Value::Real(-15.0));
    if let Some(flags) = status_flags {
        dictionary.insert(
            "statusFlags".into(),
            Value::Integer(u64::from(flags).into()),
        );
    }
    let mut bytes = Vec::new();
    Value::Dictionary(dictionary)
        .to_writer_binary(&mut bytes)
        .unwrap();
    bytes
}

fn serve_info(status_flags: Option<u32>, responses: usize) -> (SocketAddr, JoinHandle<()>) {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    let worker = thread::spawn(move || {
        for _ in 0..responses {
            let (socket, _) = listener.accept().unwrap();
            let mut connection = Connection::from_stream(socket, Cancellation::default()).unwrap();
            let request = connection.read().unwrap();
            assert!(
                request.first.starts_with("GET /info"),
                "probe must ask for /info, got {}",
                request.first
            );
            let body = info_body(status_flags);
            let mut response = format!(
                "RTSP/1.0 200 OK\r\nCSeq: {}\r\nContent-Length: {}\r\n\r\n",
                request.headers["cseq"],
                body.len()
            )
            .into_bytes();
            response.extend(body);
            connection.write(&response).unwrap();
        }
    });
    (address, worker)
}

#[test]
fn probe_reads_the_live_status_flags_on_a_fresh_connection_each_time() {
    let (address, server) = serve_info(Some(0), 2);
    let first = preemption::probe_report(
        address,
        &Cancellation::default(),
        preemption::PROBE_TIMEOUT,
    )
    .unwrap();
    assert_eq!(first.outcome, ProbeOutcome::Free);
    assert_eq!(first.status_flags, Some(0));
    // A second probe opens a second connection: nothing is cached from mDNS.
    let second = preemption::probe(
        address,
        &Cancellation::default(),
        preemption::PROBE_TIMEOUT,
    )
    .unwrap();
    assert_eq!(second, ProbeOutcome::Free);
    server.join().unwrap();
}

#[test]
fn a_status_flags_bitmap_whose_bits_are_unknown_can_never_be_busy_elsewhere() {
    for flags in [0, 1, 0x0002_0000, 0x8000_0000, u32::MAX] {
        let (address, server) = serve_info(Some(flags), 1);
        let outcome = preemption::probe(
            address,
            &Cancellation::default(),
            preemption::PROBE_TIMEOUT,
        )
        .unwrap();
        assert_eq!(
            outcome,
            ProbeOutcome::Free,
            "statusFlags=0x{flags:08x} must not be guessed as another sender"
        );
        assert!(!another_sender(flags));
        assert_eq!(action_for(outcome), ProbeAction::Reclaim);
        server.join().unwrap();
    }
}

#[test]
fn missing_status_flags_is_unknown_and_still_reclaims() {
    let (address, server) = serve_info(None, 1);
    let report = preemption::probe_report(
        address,
        &Cancellation::default(),
        preemption::PROBE_TIMEOUT,
    )
    .unwrap();
    assert_eq!(report.outcome, ProbeOutcome::Unknown);
    assert_eq!(report.status_flags, None);
    assert_eq!(action_for(report.outcome), ProbeAction::Reclaim);
    server.join().unwrap();
}

#[test]
fn probe_errors_time_out_and_fail_open() {
    // A port that was bound and released has no receiver on it.
    let probe = TcpListener::bind("127.0.0.1:0").unwrap();
    let dead = probe.local_addr().unwrap();
    drop(probe);
    let error = preemption::probe_report(dead, &Cancellation::default(), Duration::from_millis(50));
    assert!(error.is_err());
    assert_eq!(action_for_result(&error), ProbeAction::Reclaim);

    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let silent = listener.local_addr().unwrap();
    let server = thread::spawn(move || {
        let (_socket, _) = listener.accept().unwrap();
        thread::sleep(Duration::from_millis(250));
    });
    let timeout = preemption::probe_report(
        silent,
        &Cancellation::default(),
        Duration::from_millis(50),
    );
    assert!(timeout.is_err(), "a silent receiver must time out");
    assert_eq!(action_for_result(&timeout), ProbeAction::Reclaim);
    server.join().unwrap();
}
