//! Live `statusFlags` probe and TCP liveness contract against localhost
//! listeners. No real receiver and no HomePod.
use airflash_engine::{
    preemption::{
        self, action_for, action_for_result, ProbeAction, ProbeOutcome, ReceiverLiveness,
        PASSWORD_REQUIRED, RECEIVER_SESSION_ACTIVE, STEREO_PARTNER_UNREACHABLE,
    },
    rtsp::{Cancellation, Connection},
};
use plist::{Dictionary, Value};
use std::{
    net::{SocketAddr, TcpListener},
    thread::{self, JoinHandle},
    time::Duration,
};

fn info_body(status_flags: Option<u64>) -> Vec<u8> {
    let mut dictionary = Dictionary::new();
    dictionary.insert("initialVolume".into(), Value::Real(-15.0));
    if let Some(flags) = status_flags {
        dictionary.insert("statusFlags".into(), Value::Integer(flags.into()));
    }
    let mut bytes = Vec::new();
    Value::Dictionary(dictionary)
        .to_writer_binary(&mut bytes)
        .unwrap();
    bytes
}

fn serve_info(status_flags: Option<u64>, responses: usize) -> (SocketAddr, JoinHandle<()>) {
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
        false,
    )
    .unwrap();
    assert_eq!(first.outcome, ProbeOutcome::Free);
    assert_eq!(first.status_flags, Some(0));
    // A second probe opens a second connection: nothing is cached from mDNS.
    let second = preemption::probe(
        address,
        &Cancellation::default(),
        preemption::PROBE_TIMEOUT,
        false,
    )
    .unwrap();
    assert_eq!(second, ProbeOutcome::Free);
    server.join().unwrap();
}

#[test]
fn an_active_session_is_ours_only_when_we_hold_it() {
    for flags in [
        RECEIVER_SESSION_ACTIVE,
        RECEIVER_SESSION_ACTIVE | STEREO_PARTNER_UNREACHABLE | PASSWORD_REQUIRED,
    ] {
        let (address, server) = serve_info(Some(flags), 1);
        let ours = preemption::probe(
            address,
            &Cancellation::default(),
            preemption::PROBE_TIMEOUT,
            true,
        )
        .unwrap();
        assert_eq!(
            ours,
            ProbeOutcome::OursActive,
            "statusFlags=0x{flags:016x} is our own live session"
        );
        assert_eq!(action_for(ours), ProbeAction::FallThrough);
        assert_ne!(action_for(ours), ProbeAction::Reclaim);
        server.join().unwrap();

        // Same bitmap, but we do not hold the session: another sender's.
        let (address, server) = serve_info(Some(flags), 1);
        let busy = preemption::probe(
            address,
            &Cancellation::default(),
            preemption::PROBE_TIMEOUT,
            false,
        )
        .unwrap();
        assert_eq!(busy, ProbeOutcome::BusyElsewhere);
        assert_eq!(action_for(busy), ProbeAction::Requeue);
        server.join().unwrap();
    }
}

#[test]
fn other_bits_and_high_64_bit_values_never_change_the_verdict() {
    // No session bit: free regardless of the telemetry bits and of anything
    // above `u32::MAX` (the old 32-bit mask could not see these).
    for flags in [
        0,
        PASSWORD_REQUIRED,
        STEREO_PARTNER_UNREACHABLE,
        PASSWORD_REQUIRED | STEREO_PARTNER_UNREACHABLE,
        1 << 40,
        (1 << 40) | PASSWORD_REQUIRED,
        !RECEIVER_SESSION_ACTIVE,
    ] {
        for we_hold_session in [false, true] {
            let (address, server) = serve_info(Some(flags), 1);
            let outcome = preemption::probe(
                address,
                &Cancellation::default(),
                preemption::PROBE_TIMEOUT,
                we_hold_session,
            )
            .unwrap();
            assert_eq!(
                outcome,
                ProbeOutcome::Free,
                "statusFlags=0x{flags:016x} has no active session"
            );
            assert_eq!(action_for(outcome), ProbeAction::Reclaim);
            server.join().unwrap();
        }
    }
    // The session bit above `u32::MAX` still has to be seen.
    for we_hold_session in [false, true] {
        let flags = (1u64 << 40) | RECEIVER_SESSION_ACTIVE;
        let (address, server) = serve_info(Some(flags), 1);
        let outcome = preemption::probe(
            address,
            &Cancellation::default(),
            preemption::PROBE_TIMEOUT,
            we_hold_session,
        )
        .unwrap();
        assert_eq!(
            outcome,
            if we_hold_session {
                ProbeOutcome::OursActive
            } else {
                ProbeOutcome::BusyElsewhere
            },
            "the full 64-bit value must be classified"
        );
        server.join().unwrap();
    }
}

#[test]
fn missing_status_flags_is_unknown_and_still_reclaims() {
    for we_hold_session in [false, true] {
        let (address, server) = serve_info(None, 1);
        let report = preemption::probe_report(
            address,
            &Cancellation::default(),
            preemption::PROBE_TIMEOUT,
            we_hold_session,
        )
        .unwrap();
        assert_eq!(report.outcome, ProbeOutcome::Unknown);
        assert_eq!(report.status_flags, None);
        assert_eq!(action_for(report.outcome), ProbeAction::Reclaim);
        server.join().unwrap();
    }
}

#[test]
fn probe_errors_time_out_and_fail_open() {
    // A port that was bound and released has no receiver on it.
    let probe = TcpListener::bind("127.0.0.1:0").unwrap();
    let dead = probe.local_addr().unwrap();
    drop(probe);
    let error = preemption::probe_report(
        dead,
        &Cancellation::default(),
        Duration::from_millis(50),
        true,
    );
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
        true,
    );
    assert!(timeout.is_err(), "a silent receiver must time out");
    assert_eq!(action_for_result(&timeout), ProbeAction::Reclaim);
    server.join().unwrap();
}

#[test]
fn the_liveness_probe_is_a_bare_tcp_connect_to_the_control_port() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    assert_eq!(
        preemption::receiver_liveness(address.ip(), address.port(), preemption::LIVENESS_TIMEOUT),
        ReceiverLiveness::Alive,
        "a listening control port is the reference sender's TCP reachable"
    );
    // Consume the probe connection, then take the port away.
    let (socket, _) = listener.accept().unwrap();
    drop(socket);
    drop(listener);
    assert_eq!(
        preemption::receiver_liveness(address.ip(), address.port(), preemption::LIVENESS_TIMEOUT),
        ReceiverLiveness::Dead,
        "a refused connect means the device is gone"
    );
}

#[test]
fn dead_liveness_reclaims_with_healthy_media_and_is_never_busy() {
    // Q34's combine table, with the liveness value a real refused connect
    // produced (not a hand-written enum).
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    drop(listener);
    let liveness =
        preemption::receiver_liveness(address.ip(), address.port(), preemption::LIVENESS_TIMEOUT);
    assert_eq!(liveness, ReceiverLiveness::Dead);
    // Even with statusFlags claiming an active session, a dead receiver while
    // our media is healthy is a reclaim — not another sender's session.
    for hold in [false, true] {
        let outcome = preemption::combine(true, liveness, Some(RECEIVER_SESSION_ACTIVE), hold);
        assert_eq!(outcome, ProbeOutcome::Gone);
        assert_eq!(action_for(outcome), ProbeAction::Reclaim);
        assert_ne!(outcome, ProbeOutcome::BusyElsewhere);
        assert_ne!(action_for(outcome), ProbeAction::Requeue);
    }
}

#[test]
fn unknown_liveness_with_healthy_media_is_still_our_active_session() {
    // The conservative row: a probe that errored without proving death does not
    // disturb a session whose media is flowing.
    for flags in [Some(0), Some(RECEIVER_SESSION_ACTIVE), None] {
        assert_eq!(
            preemption::combine(true, ReceiverLiveness::Unknown, flags, false),
            ProbeOutcome::OursActive
        );
        assert_eq!(
            action_for(preemption::combine(
                true,
                ReceiverLiveness::Unknown,
                flags,
                false
            )),
            ProbeAction::FallThrough
        );
    }
    // With unhealthy media the same unknown liveness falls back to the old
    // statusFlags classification / fail-open.
    assert_eq!(
        preemption::combine(false, ReceiverLiveness::Unknown, Some(0), false),
        ProbeOutcome::Free
    );
    assert_eq!(
        preemption::combine(false, ReceiverLiveness::Unknown, None, false),
        ProbeOutcome::Unknown
    );
}
