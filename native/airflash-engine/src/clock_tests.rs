use super::*;

fn follower(now: Instant) -> PeerState {
    let mut state = PeerState {
        follow: true,
        active: true,
        ..Default::default()
    };
    followed_packet(&mut state, &announce(8, 0, 0), 0, now, 7);
    state
}

#[test]
fn reordered_pairs_preserve_sample_correction_and_reach_readiness() {
    let start = Instant::now();
    let mut state = follower(start);
    for seq in 0..3u16 {
        let now = start + Duration::from_secs(u64::from(seq));
        let base = 1_000_000_000 + u64::from(seq) * 1_000_000_000;
        let (sync, follow) = sync_pair(8, seq, base + 1_000_000);
        followed_packet(&mut state, &follow, base + 90_000, now, 7);
        followed_packet(&mut state, &sync, base + 100_000, now, 7);
        let mut unrelated_sync = sync_pair(8, seq + 100, base).0;
        unrelated_sync[8..16].copy_from_slice(&(90_000_000i64 * 65536).to_be_bytes());
        followed_packet(&mut state, &unrelated_sync, base + 150_000, now, 7);
        state.delay = Some((seq, base + 200_000));
        state.delay_age = Some(now);
        let mut response = header(9, 54, 8, seq, 0x0408, 0);
        timestamp(&mut response[34..44], base + 1_300_000);
        response[44..52].copy_from_slice(&7u64.to_be_bytes());
        response[52..54].copy_from_slice(&0x8005u16.to_be_bytes());
        followed_packet(&mut state, &response, base + 400_000, now, 7);
        followed_packet(&mut state, &response, base + 400_000, now, 7);
        assert_eq!(state.exchanges, u64::from(seq) + 1);
    }
    assert!(state.ready(start + Duration::from_secs(2)));
    assert_eq!(state.offset, Some(1_000_000.0));
    assert_eq!(state.round_trip_ns, Some(200_000.0));
}

#[test]
fn cache_handles_backlog_wrap_duplicates_expiry_and_identity_changes() {
    let start = Instant::now();
    let mut state = follower(start);
    let (sync_a, follow_a) = sync_pair(8, u16::MAX, 1_000_000);
    let (sync_b, follow_b) = sync_pair(8, 0, 2_000_000);
    followed_packet(&mut state, &sync_a, 100_000, start, 7);
    followed_packet(&mut state, &sync_b, 200_000, start, 7);
    followed_packet(&mut state, &follow_b, 210_000, start, 7);
    followed_packet(&mut state, &follow_a, 220_000, start, 7);
    assert_eq!(state.sync_pair.unwrap().origin, 2_000_000);
    followed_packet(&mut state, &sync_b, 300_000, start, 7);
    followed_packet(&mut state, &follow_b, 310_000, start, 7);
    assert_eq!(state.sync_pair.unwrap().received, 200_000);

    let (sync_c, follow_c) = sync_pair(8, 1, 3_000_000);
    followed_packet(&mut state, &sync_c, 400_000, start, 7);
    followed_packet(&mut state, &follow_c, 410_000, start + SAMPLE_LIFETIME, 7);
    assert_eq!(state.sync_pair.unwrap().origin, 2_000_000);
    for seq in 2..100u16 {
        followed_packet(
            &mut state,
            &sync_pair(8, seq, 0).0,
            u64::from(seq),
            start + SAMPLE_LIFETIME,
            7,
        );
    }
    assert_eq!(state.pending_syncs.len(), MAX_PENDING_SYNCS);
    followed_packet(
        &mut state,
        &announce(9, 0, 0),
        0,
        start + SAMPLE_LIFETIME,
        7,
    );
    assert!(state.pending_syncs.is_empty());
    assert!(state.sync_pair.is_none());
    followed_packet(&mut state, &follow_b, 0, start + SAMPLE_LIFETIME, 7);
    assert_eq!(state.packets.last_rejection, Some("source_identity"));
}

#[test]
fn new_unmatched_sync_cannot_refresh_an_expired_complete_sample() {
    let start = Instant::now();
    let mut state = follower(start);
    let (sync, follow) = sync_pair(8, 1, 1_000_000);
    followed_packet(&mut state, &sync, 100_000, start, 7);
    followed_packet(&mut state, &follow, 110_000, start, 7);
    let later = start + Duration::from_secs(3);
    followed_packet(
        &mut state,
        &sync_pair(8, 2, 3_001_000_000).0,
        3_000_100_000,
        later,
        7,
    );
    state.delay = Some((3, 3_000_200_000));
    state.delay_age = Some(later);
    let mut response = header(9, 54, 8, 3, 0x0408, 0);
    timestamp(&mut response[34..44], 3_001_300_000);
    response[44..52].copy_from_slice(&7u64.to_be_bytes());
    response[52..54].copy_from_slice(&0x8005u16.to_be_bytes());
    followed_packet(&mut state, &response, 3_000_400_000, later, 7);
    assert_eq!(state.exchanges, 0);
    assert_eq!(
        state.packets.last_rejection,
        Some("delay_identity_or_stale_sample")
    );
}

#[test]
fn short_retry_accepts_an_earlier_outstanding_grant_and_renews_short_leases() {
    let start = Instant::now();
    let mut state = follower(start);
    for seq in [1, 2] {
        state.subscriptions[0].push_back(Subscription {
            sequence: seq,
            interval: -3,
            sent: start + Duration::from_millis(u64::from(seq - 1) * 500),
        });
    }
    let mut request = subscription(7, Some((8, 0x8005)), 1, 0);
    request[50..54].copy_from_slice(&6u32.to_be_bytes());
    let grant = unicast_grants(&request, 8, 1).remove(0);
    let now = start + Duration::from_millis(700);
    followed_packet(&mut state, &grant, 0, now, 7);
    assert_eq!(state.received_grants, 1);
    state.offset = Some(0.0);
    for millis in [0, 125, 250] {
        state.exchanged(start + Duration::from_millis(millis));
    }
    assert!(!state.subscription_needed(0, now));
    assert!(state.subscription_needed(0, now + Duration::from_secs(1)));
    state.next_subscription = Some(now + SUBSCRIPTION_RETRY);
    assert!(!state.subscription_due(now));
    assert!(state.subscription_due(now + SUBSCRIPTION_RETRY));
}

#[test]
fn advertised_slow_exchange_streak_expires_with_a_bounded_timeout() {
    let start = Instant::now();
    let mut state = PeerState {
        exchange_interval: Some(Duration::from_secs(4)),
        ..Default::default()
    };
    for seconds in [0, 4, 8] {
        state.exchanged(start + Duration::from_secs(seconds));
    }
    assert!(state.ready(start + Duration::from_secs(8)));
    assert_eq!(state.startup_timeout(), Duration::from_secs(14));
    assert!(!state.ready(start + Duration::from_secs(20)));
    state.exchanged(start + Duration::from_secs(20));
    assert_eq!(state.exchanges, 1);
}

#[test]
fn packet_validation_explains_rejections_without_relaxing_identity_checks() {
    for (index, value, reason) in [
        (0, 0, "profile_or_version"),
        (1, 1, "profile_or_version"),
        (4, 1, "domain"),
        (3, 40, "length"),
    ] {
        let mut packet = sync_pair(8, 1, 0).0;
        packet[index] = value;
        assert_eq!(packet_rejection(&packet), Some(reason));
    }
    assert_eq!(packet_rejection(&[0; 12]), Some("short_header"));
    assert_eq!(packet_rejection(&header(2, 44, 8, 1, 0, 0)), Some("length"));
}

#[test]
fn loop_retries_dropped_subscription_and_answers_pdelay_while_following() {
    // A distinct loopback address avoids the existing session simulation's ports.
    let peer: IpAddr = "127.0.0.10".parse().unwrap();
    let remote_event = UdpSocket::bind((peer, 319)).unwrap();
    let remote_general = UdpSocket::bind((peer, 320)).unwrap();
    remote_event.set_nonblocking(true).unwrap();
    remote_general.set_nonblocking(true).unwrap();
    let event = UdpSocket::bind("127.0.0.1:0").unwrap();
    let general = UdpSocket::bind("127.0.0.1:0").unwrap();
    let local_event = event.local_addr().unwrap();
    let local_general = general.local_addr().unwrap();
    let timer =
        PtpMaster::from_sockets(vec![peer], vec![peer], Clock::new(), event, general).unwrap();
    thread::sleep(Duration::from_millis(150));
    let mut buffer = [0u8; 2048];
    assert!(remote_general.recv_from(&mut buffer).is_err());
    timer.activate(peer, "127.0.0.1".parse().unwrap());
    let started = Instant::now();
    let request = header(2, 54, 8, 77, 0x0408, 0);
    remote_event.send_to(&request, local_event).unwrap();
    // Invalid traffic must be visible without making the clock ready.
    remote_general.send_to(&[0; 12], local_general).unwrap();
    let mut requests = 0;
    let mut pdelay_event = false;
    let mut pdelay_follow = false;
    while started.elapsed() < Duration::from_secs(3) {
        while let Ok((n, _)) = remote_general.recv_from(&mut buffer) {
            if buffer[0] & 15 == 12 && n == 54 && buffer[48] == 0 {
                requests += 1; // Deliberately drop every subscription.
            }
            if buffer[0] & 15 == 10 {
                assert_eq!(&buffer[30..32], &77u16.to_be_bytes());
                assert_eq!(&buffer[44..54], &request[20..30]);
                pdelay_follow = true;
            }
        }
        while let Ok((_, _)) = remote_event.recv_from(&mut buffer) {
            if buffer[0] & 15 == 3 {
                assert_eq!(&buffer[44..54], &request[20..30]);
                pdelay_event = true;
            }
        }
        if requests >= 2 && pdelay_event && pdelay_follow {
            break;
        }
        thread::sleep(Duration::from_millis(5));
    }
    assert!(
        requests >= 2,
        "lost subscription must retry inside startup deadline"
    );
    assert!(pdelay_event && pdelay_follow);
    let snapshot = timer.snapshots().remove(0);
    assert_eq!(snapshot.packets.pdelay_responses, 1);
    assert_eq!(snapshot.packets.last_rejection, Some("short_header"));
    assert_eq!(snapshot.exchanges, 0);
    assert!(!snapshot.ready);
    assert_eq!(snapshot.phase, "waiting_announce");
    assert!(timer.network_snapshot().event_received > 0);
    assert!(timer.network_snapshot().general_received > 0);
}

#[test]
fn localhost_follower_locks_after_receiver_drops_the_first_subscription() {
    let peer: IpAddr = "127.0.0.11".parse().unwrap();
    let remote_event = UdpSocket::bind((peer, 319)).unwrap();
    let remote_general = UdpSocket::bind((peer, 320)).unwrap();
    remote_event.set_nonblocking(true).unwrap();
    remote_general.set_nonblocking(true).unwrap();
    let event = UdpSocket::bind("127.0.0.1:0").unwrap();
    let general = UdpSocket::bind("127.0.0.1:0").unwrap();
    let local_event = event.local_addr().unwrap();
    let local_general = general.local_addr().unwrap();
    let clock = Clock::new();
    let timer =
        PtpMaster::from_sockets(vec![peer], vec![peer], clock.clone(), event, general).unwrap();
    timer.activate(peer, "127.0.0.1".parse().unwrap());
    let started = Instant::now();
    let mut next_sync = started;
    let mut sync_sequence = 0;
    let mut subscriptions = 0;
    let mut buffer = [0u8; 2048];
    while started.elapsed() < Duration::from_secs(7) && !timer.ready(peer) {
        while let Ok((n, _)) = remote_general.recv_from(&mut buffer) {
            if n == 54 && buffer[0] & 15 == 12 && buffer[48] == 0 {
                subscriptions += 1;
            }
        }
        let now = Instant::now();
        if subscriptions >= 2 && now >= next_sync {
            remote_general
                .send_to(&announce(8, sync_sequence, 0), local_general)
                .unwrap();
            let (sync, follow) = sync_pair(8, sync_sequence, clock.now_ns() + 1_000_000);
            remote_event.send_to(&sync, local_event).unwrap();
            remote_general.send_to(&follow, local_general).unwrap();
            sync_sequence = sync_sequence.wrapping_add(1);
            next_sync = now + Duration::from_millis(125);
        }
        while let Ok((n, _)) = remote_event.recv_from(&mut buffer) {
            if subscriptions >= 2 && n == 44 && buffer[0] & 15 == 1 {
                let sequence = u16::from_be_bytes([buffer[30], buffer[31]]);
                let mut response = header(9, 54, 8, sequence, 0x0408, 0);
                timestamp(&mut response[34..44], clock.now_ns() + 1_000_000);
                response[44..54].copy_from_slice(&buffer[20..30]);
                remote_general.send_to(&response, local_general).unwrap();
            }
        }
        thread::sleep(Duration::from_millis(2));
    }
    assert!(subscriptions >= 2);
    assert!(
        timer.ready(peer),
        "follower must lock before the ordinary eight-second deadline"
    );
    let snapshot = timer.snapshots().remove(0);
    assert!(snapshot.exchanges >= 3);
    assert!(snapshot.offset_ns.is_some());
    assert_eq!(snapshot.phase, "ready");
}
