//! Live `statusFlags` probe and TCP liveness contract against localhost
//! listeners. No real receiver and no HomePod.
use airflash_engine::{
    preemption::{
        self, action_for, action_for_result, LivenessSample, ProbeAction, ProbeOutcome,
        ReceiverLiveness, Verdict, PASSWORD_REQUIRED, RECEIVER_SESSION_ACTIVE,
        STEREO_PARTNER_UNREACHABLE,
    },
    rtsp::{Cancellation, Connection},
};
use plist::{Dictionary, Value};
use std::{
    io::ErrorKind,
    net::{SocketAddr, TcpListener},
    thread::{self, JoinHandle},
    time::{Duration, Instant},
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
    // Q42: a closed port answers with a RST, and a RST means the *host* is
    // alive and its stack answered. The timeout is deliberately generous — the
    // point is the state, not how fast the local stack rejects, so a slow
    // loopback must not turn the RST into a timeout.
    let refused =
        preemption::receiver_liveness(address.ip(), address.port(), Duration::from_secs(10));
    assert_eq!(
        refused,
        ReceiverLiveness::Refused,
        "a closed control port is refused, never dead: the host answered"
    );
    assert!(
        !refused.reachable(),
        "a refusal cannot serve statusFlags either"
    );
    assert!(
        refused.host_answered(),
        "the RST is proof of life, which is why refused is not dead"
    );
    assert_eq!(refused.name(), "refused");
    // Action-wise it is still a fail-open reclaim, never another sender's
    // session, whatever statusFlags would have said.
    for hold in [false, true] {
        let outcome = preemption::combine(true, refused, Some(RECEIVER_SESSION_ACTIVE), hold);
        assert_eq!(outcome, ProbeOutcome::Gone);
        assert_eq!(action_for(outcome), ProbeAction::Reclaim);
        assert_ne!(outcome, ProbeOutcome::BusyElsewhere);
        assert_ne!(action_for(outcome), ProbeAction::Requeue);
    }
}

#[test]
fn timeout_host_unreachable_and_network_unreachable_are_dead() {
    // The three errors Q42 names as "the host is not there". They are tested
    // through the same classifier the socket probe uses, so no real network is
    // needed to pin the state.
    for kind in [
        ErrorKind::TimedOut,
        ErrorKind::HostUnreachable,
        ErrorKind::NetworkUnreachable,
    ] {
        let liveness = preemption::liveness_from_error(&std::io::Error::from(kind));
        assert_eq!(
            liveness,
            ReceiverLiveness::Dead,
            "{kind:?} means the host is not there"
        );
        assert!(!liveness.host_answered());
        assert!(!liveness.reachable());
        assert_eq!(
            preemption::combine(true, liveness, None, true),
            ProbeOutcome::Gone
        );
        assert_eq!(action_for(ProbeOutcome::Gone), ProbeAction::Reclaim);
    }
    // A refusal is the opposite: the host answered, so it must not be folded
    // into dead (Q42's correction of Q34).
    assert_eq!(
        preemption::liveness_from_error(&std::io::Error::from(ErrorKind::ConnectionRefused)),
        ReceiverLiveness::Refused
    );
    assert_ne!(
        preemption::liveness_from_error(&std::io::Error::from(ErrorKind::ConnectionRefused)),
        preemption::liveness_from_error(&std::io::Error::from(ErrorKind::TimedOut))
    );
    // Everything else is only "temporarily unknown".
    for kind in [ErrorKind::ConnectionReset, ErrorKind::PermissionDenied] {
        assert_eq!(
            preemption::liveness_from_error(&std::io::Error::from(kind)),
            ReceiverLiveness::Unknown,
            "{kind:?} proves neither life nor death"
        );
    }
}

#[test]
fn an_unknown_chain_escalates_to_dead_and_one_ok_resets_it() {
    let base = Instant::now();
    let mut tracker = preemption::Tracker::default();
    tracker.observe(preemption::Signal::ConnectionClose, base);
    assert!(tracker.due(base));
    let mut now = base;
    for step in 1..preemption::LIVENESS_PATIENCE {
        now += preemption::LIVENESS_RETRY;
        let note = tracker.observe_liveness(
            LivenessSample::Probe(ReceiverLiveness::Unknown),
            now,
        );
        assert_eq!(
            note.verdict,
            Verdict::Unknown,
            "unknown sample {step} is still temporary"
        );
        assert_eq!(tracker.liveness_failures(), step);
        // The session loop keeps sampling while the verdict is undecided;
        // without it the patience limit could never be reached.
        tracker.await_liveness(now);
        assert_eq!(tracker.scheduled(), Some(now + preemption::LIVENESS_RETRY));
    }
    now += preemption::LIVENESS_RETRY;
    let escalated = tracker.observe_liveness(
        LivenessSample::Probe(ReceiverLiveness::Unknown),
        now,
    );
    assert_eq!(
        escalated.verdict,
        Verdict::Dead,
        "the patience limit turns a chain of unknowns into a death"
    );
    assert_eq!(escalated.verdict.liveness(), ReceiverLiveness::Dead);
    assert_eq!(escalated.verdict.name(), "dead");
    let outcome = preemption::combine(true, escalated.verdict.liveness(), None, true);
    assert_eq!(outcome, ProbeOutcome::Gone);
    assert_eq!(action_for(outcome), ProbeAction::Reclaim);
    // One successful probe resets the counter, so the next unknown starts over.
    assert_eq!(
        tracker
            .observe_liveness(LivenessSample::Probe(ReceiverLiveness::Alive), now)
            .verdict,
        Verdict::Alive
    );
    assert_eq!(tracker.liveness_failures(), 0);
    assert_eq!(
        tracker
            .observe_liveness(LivenessSample::Probe(ReceiverLiveness::Unknown), now)
            .verdict,
        Verdict::Unknown,
        "after a reset the patience limit has to be earned again"
    );
}

#[test]
fn all_receivers_failing_at_once_is_a_local_problem_and_marks_none_dead() {
    // Two receivers, both probes failed in the same round. Q42: the common
    // cause is our own routing/interface, so no device is marked dead and the
    // healthy session keeps playing.
    let probes = [ReceiverLiveness::Dead, ReceiverLiveness::Unknown];
    assert!(preemption::local_network_suspected(&probes, 2));
    for probe in probes {
        let mut tracker = preemption::Tracker::default();
        let note = tracker.observe_liveness(LivenessSample::LocalSuspect, Instant::now());
        assert_eq!(
            note.verdict,
            Verdict::Unknown,
            "a group-wide failure is not this device's death"
        );
        assert_eq!(
            tracker.liveness_failures(),
            0,
            "the failure is not counted against the device"
        );
        assert_eq!(
            preemption::combine(true, note.verdict.liveness(), None, true),
            ProbeOutcome::OursActive,
            "healthy media + unknown liveness keeps our session"
        );
        assert_eq!(action_for(ProbeOutcome::OursActive), ProbeAction::FallThrough);
        // The raw probe still says what the socket said.
        assert!(!probe.reachable());
    }
    // One receiver failing is that receiver's problem, never "all failed".
    assert!(!preemption::local_network_suspected(
        &[ReceiverLiveness::Dead],
        1
    ));
    // One live receiver proves the local network works.
    assert!(!preemption::local_network_suspected(
        &[ReceiverLiveness::Dead, ReceiverLiveness::Alive],
        2
    ));
    // A refusal proves the host answered: an all-refused round is not local.
    assert!(!preemption::local_network_suspected(
        &[ReceiverLiveness::Refused, ReceiverLiveness::Refused],
        2
    ));
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
