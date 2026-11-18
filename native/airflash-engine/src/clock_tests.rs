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
fn a_sync_only_mapping_freezes_expires_and_never_survives_an_identity_change() {
    let start = Instant::now();
    let mut state = follower(start);
    for seq in 0..3u16 {
        let at = start + Duration::from_millis(u64::from(seq) * 125);
        let local = 1_000_000_000 + u64::from(seq) * 125_000_000;
        let (sync, follow) = sync_pair(8, seq, local + 5_000_000_000);
        followed_packet(&mut state, &sync, local + 90_000, at, 7);
        followed_packet(&mut state, &follow, local + 95_000, at, 7);
    }
    let last = start + Duration::from_millis(250);
    assert!(state.offset_at.is_none(), "no Delay response was needed");
    let (offset, frozen) = state.map_offset(last, Duration::from_secs(10)).unwrap();
    assert!(!frozen);
    assert_eq!(
        state.map_offset(last + Duration::from_secs(3), Duration::from_secs(10)),
        Some((offset, true))
    );
    assert_eq!(
        state.map_offset(last + Duration::from_secs(11), Duration::from_secs(10)),
        None
    );
    followed_packet(
        &mut state,
        &announce(9, 1, 0),
        0,
        last + Duration::from_secs(4),
        7,
    );
    assert_eq!(
        state.map_offset(last + Duration::from_secs(4), Duration::from_secs(10)),
        None
    );
}

#[test]
fn playback_holds_the_same_mapping_through_a_delay_offset_jump_and_reacquires() {
    let start = Instant::now();
    let grace = Duration::from_secs(10);
    let mut state = follower(start);
    let send_sync = |state: &mut PeerState, sequence: u16, offset_ms: u64| {
        let elapsed_ms = u64::from(sequence) * 125;
        let at = start + Duration::from_millis(elapsed_ms);
        let local = 1_000_000_000 + elapsed_ms * 1_000_000;
        let (sync, follow) = sync_pair(8, sequence, local + offset_ms * 1_000_000);
        followed_packet(state, &sync, local + 90_000, at, 7);
        followed_packet(state, &follow, local + 95_000, at, 7);
    };
    let send_delay_response =
        |state: &mut PeerState, sequence: u16, at: Instant, t3: u64, t4: u64| {
            state.delay = Some((sequence, t3));
            state.delay_age = Some(at);
            let mut response = header(9, 54, 8, sequence, 0x0408, 0);
            timestamp(&mut response[34..44], t4);
            response[44..52].copy_from_slice(&7u64.to_be_bytes());
            response[52..54].copy_from_slice(&0x8005u16.to_be_bytes());
            followed_packet(state, &response, t3 + 250_000, at, 7);
        };
    for sequence in 0..3 {
        send_sync(&mut state, sequence, 5000);
    }
    let last = start + Duration::from_millis(250);
    let t3 = 1_250_200_000;
    send_delay_response(&mut state, 19, last, t3, t3 + 5_000_090_000);
    let mapping = state.map_offset(last, Duration::ZERO).unwrap();
    assert_eq!(mapping, (5_000_000_000.0, false));

    // A negative RTT is rejected without weakening the validated mapping.
    let negative_at = start + Duration::from_millis(275);
    let t3 = 1_275_200_000;
    send_delay_response(&mut state, 20, negative_at, t3, t3 + 4_999_800_000);
    assert_eq!(state.packets.last_rejection, Some("round_trip"));
    assert_eq!(state.packets.last_round_trip_ns, Some(-110_000.0));
    assert_eq!(state.map_offset(negative_at, grace), Some(mapping));

    // A correlated response with a plausible positive RTT crosses the existing
    // offset jump threshold. This starts a new readiness streak on the same source.
    let jump_at = start + Duration::from_millis(300);
    let t3 = 1_300_200_000;
    send_delay_response(&mut state, 21, jump_at, t3, t3 + 5_014_090_000);
    assert_eq!(state.packets.last_rejection, Some("offset_jump"));
    assert_eq!(state.round_trip_ns, Some(14_180_000.0));
    assert_eq!(state.first, Some(jump_at));
    assert_eq!(state.last, Some(jump_at));
    assert!(!state.ready(jump_at));
    assert_eq!(state.map_offset(jump_at, Duration::ZERO), None);
    assert!(state.playback_timing_available(jump_at, grace));
    assert_eq!(state.map_offset(jump_at, grace), Some((mapping.0, true)));

    assert!(state.playback_timing_available(last + grace, grace));
    assert!(!state.playback_timing_available(last + grace + Duration::from_nanos(1), grace));
    assert_eq!(
        state.map_offset(last + grace + Duration::from_nanos(1), grace),
        None
    );

    for sequence in 3..6 {
        send_sync(&mut state, sequence, 5007);
        let now = start + Duration::from_millis(u64::from(sequence) * 125);
        assert!(state.playback_timing_available(now, grace));
        assert_eq!(state.map_offset(now, grace).unwrap().1, sequence < 5);
    }
    let reacquired = start + Duration::from_millis(625);
    assert_eq!(
        state.map_offset(reacquired, grace),
        Some((5_007_000_000.0, false))
    );

    // A new association never inherits the prior source's frozen mapping.
    let mut new_source = announce(8, 0, 0);
    new_source[29] ^= 1;
    followed_packet(&mut state, &new_source, 0, reacquired, 7);
    assert!(!state.playback_timing_available(reacquired, grace));
    assert_eq!(state.map_offset(reacquired, grace), None);
}

#[test]
fn sender_playback_still_requires_a_recent_receiver_exchange() {
    let start = Instant::now();
    let grace = Duration::from_secs(10);
    let mut state = PeerState::default();
    assert!(!state.playback_timing_available(start, grace));
    state.exchanged(start);
    assert!(state.playback_timing_available(start + grace, grace));
    assert!(!state.playback_timing_available(start + grace + Duration::from_nanos(1), grace));
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
fn a_changed_announce_identity_is_timestamped_once_per_restart() {
    let start = Instant::now();
    let mut state = follower(start);
    // The first Announce established the identity and is not a change.
    assert_eq!(state.identity_change_at(), None);
    note_announce_identity(
        &mut state,
        &announce(9, 0, 0),
        start + Duration::from_secs(1),
    );
    followed_packet(
        &mut state,
        &announce(9, 0, 0),
        0,
        start + Duration::from_secs(1),
        7,
    );
    assert_eq!(
        state.identity_change_at(),
        Some(start + Duration::from_secs(1))
    );
    assert_eq!(state.identity_changes, 1);
    // Repeating the same identity must not count again.
    note_announce_identity(
        &mut state,
        &announce(9, 1, 0),
        start + Duration::from_secs(2),
    );
    followed_packet(
        &mut state,
        &announce(9, 1, 0),
        0,
        start + Duration::from_secs(2),
        7,
    );
    assert_eq!(
        state.identity_change_at(),
        Some(start + Duration::from_secs(1))
    );
    assert_eq!(state.identity_changes, 1);
    // A second restart moves the timestamp.
    note_announce_identity(
        &mut state,
        &announce(10, 0, 0),
        start + Duration::from_secs(3),
    );
    assert_eq!(
        state.identity_change_at(),
        Some(start + Duration::from_secs(3))
    );
    assert_eq!(state.identity_changes, 2);
}

#[test]
fn a_silent_peer_reports_when_its_association_stalled() {
    let start = Instant::now();
    let state = PeerState {
        follow: true,
        active: true,
        last: Some(start),
        exchange_interval: Some(Duration::from_secs(1)),
        ..Default::default()
    };
    assert_eq!(state.stalled_at(start + Duration::from_millis(500)), None);
    assert_eq!(
        state.stalled_at(start + Duration::from_secs(3)),
        Some(start),
        "three missed 1 s exchanges is the same bounded timeout readiness uses"
    );
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
    // Only the first complete pair counts as an exchange; the later Sync has no
    // matching Follow_Up and must not refresh or re-arm the sample.
    assert_eq!(state.exchanges, 1);
    assert_eq!(state.sync_pair.map(|sample| sample.received), Some(100_000));
    assert_eq!(
        state.packets.last_rejection,
        Some("delay_identity_or_stale_sample")
    );
}

// Independent wire fixture: target 7/0x8005, source 8/0x8005, 30s lease.
// Do not build this from the production response or echo any request sequence.
fn literal_grant(sequence: u16, kind: u8, flags: u8) -> Vec<u8> {
    let mut bytes = b"\x1c\x02\x00\x38\x00\x00\x04\x08\x00\x00\x00\x00\x00\x00\x00\x00\
                      \x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x08\x80\x05\x00\x00\
                      \x05\x00\x00\x00\x00\x00\x00\x00\x00\x07\x80\x05\x00\x05\x00\x08\
                      \x00\xfd\x00\x00\x00\x1e\x00\x00"
        .to_vec();
    bytes[30..32].copy_from_slice(&sequence.to_be_bytes());
    bytes[48] = kind << 4;
    bytes[49] = if kind == 0 { 0xfd } else { 0 };
    bytes[55] = flags;
    bytes
}

fn pend(state: &mut PeerState, index: usize, interval: i8, sent: Instant) {
    state.subscriptions[index].push_back(Subscription { interval, sent });
}

fn append_tlv(packet: &mut Vec<u8>, tlv: &[u8]) {
    packet.extend_from_slice(tlv);
    let length = u16::try_from(packet.len()).unwrap();
    packet[2..4].copy_from_slice(&length.to_be_bytes());
}

#[test]
fn follower_accepts_literal_grants_and_ignores_reserved_flag_octets() {
    for (reserved, flags) in [(0, 0), (0, 1), (1, 0), (0, 0xfe), (0xff, 0xff)] {
        let now = Instant::now();
        let mut state = follower(now);
        // Request went out with this sender's own sequence.
        let request = state.signaling_subscription(7, Some((8, 0x8005)), 0);
        assert_eq!(packet_sequence(&request), 0);
        pend(&mut state, 0, -3, now);
        let mut grant = literal_grant(0x4321, 0, flags);
        grant[54] = reserved;
        followed_packet(&mut state, &grant, 0, now, 7);
        assert_eq!(state.received_grants, 1);
        assert_eq!(state.granted_until[0], Some(now + Duration::from_secs(30)));
        assert!(state.subscriptions[0].is_empty());
        assert!(!state.ready(now)); // A lease is not a clock exchange.
    }
}

#[test]
fn follower_grants_require_valid_fields_source_target_and_live_pending_request() {
    let now = Instant::now();
    let grant = literal_grant(0x4321, 0, 1);
    let mut state = follower(now);
    pend(&mut state, 0, -3, now);
    for (index, value, reason) in [
        (20, 1, "source_identity"),
        (29, 1, "source_identity"),
        (41, 1, "grant_target"),
        (43, 1, "grant_target"),
        (47, 7, "grant_framing"),
        (48, 0x10, "grant_fields"),
        (48, 1, "grant_fields"),
        (48, 0x90, "grant_no_pending"),
        (49, 0xfc, "grant_fields"),
        (49, 5, "grant_fields"),
        (53, 0, "grant_fields"),
        (53, 31, "grant_fields"),
    ] {
        let mut bad = grant.clone();
        bad[index] = value;
        followed_packet(&mut state, &bad, 0, now, 7);
        assert_eq!(state.packets.last_rejection, Some(reason), "byte {index}");
        assert_eq!(state.received_grants, 0);
        assert_eq!(state.subscriptions[0].len(), 1);
    }
    followed_packet(&mut state, &grant, 0, now + SAMPLE_LIFETIME, 7);
    assert_eq!(state.packets.last_rejection, Some("grant_no_pending"));
    assert_eq!(state.received_grants, 0);
    followed_packet(&mut state, &grant, 0, now + Duration::from_millis(1999), 7);
    assert_eq!(state.received_grants, 1);
    let counters = serde_json::to_value(&state.packets).unwrap();
    assert_eq!(counters["grant_tlv_checked"], 9);
    assert_eq!(counters["grant_accepted"], 1);
    assert_eq!(counters["grant_rejected_fields"], 9);
    assert_eq!(counters["grant_rejected_no_pending_or_stale"], 2);
    assert_eq!(counters["grant_rejected_replay"], 0);
}

#[test]
fn reordered_separate_grants_adopt_each_pending_type_and_ignore_unrequested_duplicates() {
    let now = Instant::now();
    let mut state = follower(now);
    for (index, interval) in [-3, 0, 0].into_iter().enumerate() {
        pend(&mut state, index, interval, now);
    }
    // Independent, valid sender sequences arrive out of order across types.
    for (sequence, kind) in [(12, 11), (10, 0), (11, 9)] {
        followed_packet(&mut state, &literal_grant(sequence, kind, 1), 0, now, 7);
    }
    assert_eq!(state.received_grants, 3);
    let leases = [Some(now + Duration::from_secs(30)); 3];
    assert_eq!(state.granted_until, leases);
    assert!(state.subscriptions.iter().all(|pending| pending.is_empty()));

    let later = now + SUBSCRIPTION_RETRY;
    // No request remains, so repeated or older packets cannot renew a lease.
    for (sequence, kind) in [(10, 0), (9, 0), (11, 9), (10, 9), (12, 11), (11, 11)] {
        followed_packet(&mut state, &literal_grant(sequence, kind, 1), 0, later, 7);
        assert_eq!(state.packets.last_rejection, Some("grant_no_pending"));
        assert_eq!(state.received_grants, 3);
        assert_eq!(state.granted_until, leases);
    }
    assert_eq!(state.packets.grant_rejected_no_pending_or_stale, 6);
    assert_eq!(state.packets.grant_rejected_replay, 0);

    // Consuming an unrequested TLV must not discard another current type in
    // the same packet, regardless of the sender's sequence.
    pend(&mut state, 1, 0, later);
    let mut mixed = literal_grant(0, 0, 1);
    append_tlv(&mut mixed, &literal_grant(0, 9, 1)[44..]);
    followed_packet(&mut state, &mixed, 0, later, 7);
    assert_eq!(state.received_grants, 4);
    assert!(state.subscriptions.iter().all(|pending| pending.is_empty()));
}

#[test]
fn grants_use_current_pending_across_sequence_wrap_and_apply_all_tlvs_once() {
    let now = Instant::now();
    let mut state = follower(now);
    for (index, interval) in [-3, 0, 0].into_iter().enumerate() {
        pend(&mut state, index, interval, now);
    }
    let mut packet = literal_grant(u16::MAX, 0, 1);
    append_tlv(&mut packet, &literal_grant(0, 9, 0)[44..]);
    append_tlv(&mut packet, &literal_grant(0, 11, 1)[44..]);
    followed_packet(&mut state, &packet, 0, now, 7);
    assert_eq!(state.received_grants, 3);
    assert_eq!(
        state.granted_until,
        [Some(now + Duration::from_secs(30)); 3]
    );

    let later = now + SUBSCRIPTION_RETRY;
    followed_packet(&mut state, &packet, 0, later, 7);
    assert_eq!(state.received_grants, 3);
    assert_eq!(
        state.granted_until,
        [Some(now + Duration::from_secs(30)); 3]
    );
    assert_eq!(state.packets.grant_rejected_no_pending_or_stale, 3);
    pend(&mut state, 0, -3, later);
    followed_packet(&mut state, &literal_grant(0, 0, 1), 0, later, 7);
    assert_eq!(state.received_grants, 4);
    assert_eq!(
        state.granted_until[0],
        Some(later + Duration::from_secs(30))
    );
    assert_eq!(state.packets.grant_tlv_checked, 7);
    assert_eq!(state.packets.grant_accepted, 4);
    assert_eq!(state.packets.grant_rejected_replay, 0);
}

#[test]
fn same_identity_grants_after_contract_expiry_use_fresh_pending_not_sender_sequence() {
    let now = Instant::now();
    for restarted_sequence in [100, 0] {
        let mut state = follower(now);
        pend(&mut state, 0, -3, now);
        let mut first = literal_grant(100, 0, 1);
        first[50..54].copy_from_slice(&1u32.to_be_bytes());
        followed_packet(&mut state, &first, 0, now, 7);
        assert_eq!(state.received_grants, 1);
        let original_lease = Some(now + Duration::from_secs(1));
        assert_eq!(state.granted_until[0], original_lease);

        // The lease and original request have both expired. No new source,
        // port or grandmaster identity is needed to negotiate another lease.
        let later = now + SAMPLE_LIFETIME + Duration::from_millis(1);
        let mut restarted = literal_grant(restarted_sequence, 0, 1);
        followed_packet(&mut state, &restarted, 0, later, 7);
        assert_eq!(state.packets.last_rejection, Some("grant_no_pending"));
        assert_eq!(state.granted_until[0], original_lease);

        pend(&mut state, 0, -3, now);
        followed_packet(&mut state, &restarted, 0, later, 7);
        assert_eq!(state.received_grants, 1); // An expired request is insufficient.
        assert_eq!(state.granted_until[0], original_lease);

        pend(&mut state, 0, -3, later);
        restarted[50..54].copy_from_slice(&2u32.to_be_bytes());
        followed_packet(&mut state, &restarted, 0, later, 7);
        assert_eq!(state.received_grants, 2);
        assert_eq!(state.granted_until[0], Some(later + Duration::from_secs(2)));
        assert!(state.subscriptions[0].is_empty());
        assert_eq!(state.packets.grant_rejected_replay, 0);
    }
}

#[test]
fn malformed_and_non_grant_signaling_do_not_consume_pending_or_change_leases() {
    let now = Instant::now();
    let mut state = follower(now);
    pend(&mut state, 0, -3, now);
    followed_packet(&mut state, &literal_grant(11, 0, 0), 0, now, 7);
    pend(&mut state, 0, -3, now);
    let rejection_count = state.packets.rejected;
    for sequence in [10, 40] {
        let request = subscription(8, None, sequence, 0);
        let mut unknown = request.clone();
        unknown[45] = 0x42;
        let mut malformed = request.clone();
        malformed[47] += 2;
        for other in [request, unknown, malformed] {
            followed_packet(&mut state, &other, 0, now, 7);
            assert_eq!(state.packets.rejected, rejection_count);
            assert_eq!(state.packets.grant_tlv_checked, 1);
            assert_eq!(state.packets.grant_accepted, 1);
        }
    }
    let mut bad = literal_grant(40, 0, 0);
    append_tlv(&mut bad, &[0, 5, 0, 8, 0]);
    followed_packet(&mut state, &bad, 0, now, 7);
    assert_eq!(state.packets.last_rejection, Some("grant_framing"));
    assert_eq!(state.received_grants, 1);
    assert_eq!(state.subscriptions[0].len(), 1);
    followed_packet(&mut state, &literal_grant(41, 11, 1), 0, now, 7);
    assert_eq!(state.packets.last_rejection, Some("grant_no_pending"));
    followed_packet(&mut state, &literal_grant(12, 0, 1), 0, now, 7);
    assert_eq!(state.received_grants, 2);
}

#[test]
fn grant_framing_rejects_complete_odd_length_tlv_before_or_after_grant() {
    let now = Instant::now();
    let mut state = follower(now);
    pend(&mut state, 0, -3, now);
    followed_packet(&mut state, &literal_grant(11, 0, 0), 0, now, 7);
    let lease = state.granted_until[0];
    let later = now + SUBSCRIPTION_RETRY;
    pend(&mut state, 0, -3, later);

    // Unknown TLV with exactly its advertised one-byte body. It is neither a
    // truncated header nor a truncated body; the length itself is invalid.
    let odd = [0, 0x42, 0, 1, 0xaa];
    for before_grant in [false, true] {
        let mut packet = literal_grant(40, 0, 1);
        append_tlv(&mut packet, &odd);
        if before_grant {
            packet[44..].rotate_right(odd.len());
        }
        followed_packet(&mut state, &packet, 0, later, 7);
        assert_eq!(state.packets.last_rejection, Some("grant_framing"));
        assert_eq!(state.received_grants, 1);
        assert_eq!(state.granted_until[0], lease);
        assert_eq!(state.subscriptions[0].len(), 1);
    }

    let mut even = literal_grant(12, 0, 1);
    append_tlv(&mut even, &[0, 0x42, 0, 2, 0xaa, 0xbb]);
    followed_packet(&mut state, &even, 0, later, 7);
    assert_eq!(state.received_grants, 2);
    assert_eq!(
        state.granted_until[0],
        Some(later + Duration::from_secs(30))
    );
}

fn packet_sequence(packet: &[u8]) -> u16 {
    u16::from_be_bytes([packet[30], packet[31]])
}

#[test]
fn requests_retries_and_generated_grants_use_per_peer_signaling_sequences() {
    let mut state = PeerState {
        signaling_sequence: u16::MAX - 1,
        ..Default::default()
    };
    let mut other_peer = PeerState::default();
    for (kind, expected) in [(11, u16::MAX - 1), (0, u16::MAX), (9, 0), (0, 1)] {
        assert_eq!(
            packet_sequence(&state.signaling_subscription(7, None, kind)),
            expected
        );
    }
    assert_eq!(
        packet_sequence(&other_peer.signaling_subscription(7, None, 0)),
        0
    );
    let mut request = subscription(8, Some((7, 0x8005)), 0x4321, 0);
    append_tlv(
        &mut request,
        &subscription(8, Some((7, 0x8005)), 0, 11)[44..],
    );
    let grants = unicast_grants(&request, 7, &mut state.signaling_sequence);
    assert_eq!(
        grants
            .iter()
            .map(|p| packet_sequence(p))
            .collect::<Vec<_>>(),
        [2, 3]
    );
    assert_eq!(
        packet_sequence(&state.signaling_subscription(7, None, 11)),
        4
    );
    assert_eq!(
        packet_sequence(&other_peer.signaling_subscription(7, None, 11)),
        1
    );
}

#[test]
fn source_port_or_grandmaster_change_expires_leases_and_pending_but_keeps_transmit_sequence() {
    let now = Instant::now();
    for index in [27, 29, 60] {
        let mut state = follower(now);
        state.signaling_sequence = 18;
        state.packets.subscription_requests_sent = 42;
        pend(&mut state, 0, -3, now);
        followed_packet(&mut state, &literal_grant(123, 0, 0), 0, now, 7);
        pend(&mut state, 0, -3, now);
        let mut new_announce = announce(8, 1, 0);
        new_announce[index] ^= 1;
        followed_packet(&mut state, &new_announce, 0, now, 7);
        assert!(state.subscriptions[0].is_empty());
        assert_eq!(state.granted_until[0], None);
        assert_eq!(state.signaling_sequence, 18);
        assert_eq!(state.packets.subscription_requests_sent, 42);
        assert_eq!(state.received_grants, 0);
        assert_eq!(state.packets.grant_accepted, 1);
        pend(&mut state, 0, -3, now);
        let mut new_grant = literal_grant(1, 0, 1);
        new_grant[20..30].copy_from_slice(&new_announce[20..30]);
        followed_packet(&mut state, &new_grant, 0, now, 7);
        assert_eq!(state.received_grants, 1);
        assert_eq!(state.packets.grant_accepted, 2);
    }
}

#[test]
fn subscription_send_failures_are_distinct_and_saturating() {
    let socket = UdpSocket::bind(("127.0.0.1", 0)).unwrap();
    // A v4 socket cannot send to a v6 destination. Neither address can leave the host.
    let destination = "[::1]:9".parse().unwrap();
    let mut state = PeerState::default();
    assert!(!send_ptp(
        &socket,
        &[0],
        destination,
        &mut state,
        "subscribe"
    ));
    assert!(!send_ptp(
        &socket,
        &[0],
        destination,
        &mut state,
        "delay_request"
    ));
    assert_eq!(state.packets.send_errors, 2);
    assert_eq!(state.packets.subscription_send_errors, 1);
    assert_eq!(state.packets.subscription_requests_sent, 0);
    let counters = serde_json::to_value(&state.packets).unwrap();
    assert_eq!(counters["last_send_error"]["operation"], "delay_request");
    assert_eq!(counters["subscription_send_errors"], 1);

    state.packets.send_errors = u64::MAX;
    state.packets.subscription_send_errors = u64::MAX;
    assert!(!send_ptp(
        &socket,
        &[0],
        destination,
        &mut state,
        "subscribe"
    ));
    assert_eq!(state.packets.send_errors, u64::MAX);
    assert_eq!(state.packets.subscription_send_errors, u64::MAX);
}

#[test]
fn short_retry_accepts_an_earlier_outstanding_grant_and_renews_short_leases() {
    let start = Instant::now();
    let mut state = follower(start);
    pend(&mut state, 0, -3, start);
    pend(&mut state, 0, -2, start + SUBSCRIPTION_RETRY);
    let mut grant = literal_grant(0x4321, 0, 0);
    grant[50..54].copy_from_slice(&6u32.to_be_bytes());
    let now = start + Duration::from_millis(700);
    followed_packet(&mut state, &grant, 0, now, 7);
    assert_eq!(state.received_grants, 1);
    state.offset = Some(0.0);
    state.offset_at = Some(start);
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
    // Protocol-fixed PTP ports on a fresh loopback address: parallel suites
    // cannot collide, and the sender still targets the real receiver ports.
    let (peer, remote_event, remote_general) = crate::test_net::ptp_receiver_sockets();
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
    assert!(snapshot.packets.subscription_requests_sent >= requests);
    assert_eq!(snapshot.packets.last_rejection, Some("short_header"));
    assert_eq!(snapshot.exchanges, 0);
    assert!(!snapshot.ready);
    assert_eq!(snapshot.phase, "waiting_announce");
    assert!(timer.network_snapshot().event_received > 0);
    assert!(timer.network_snapshot().general_received > 0);
}

#[test]
fn localhost_follower_locks_after_receiver_drops_the_first_subscription() {
    let (peer, remote_event, remote_general) = crate::test_net::ptp_receiver_sockets();
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

#[test]
fn stale_pair_cannot_rewind_the_sync_offset_or_exchange_streak() {
    let start = Instant::now();
    let mut state = follower(start);
    let receiver_offset: i128 = 5_000_000_000;
    let newest = 2_000_000_000u64;
    let (sync, follow) = sync_pair(8, 2, (newest as i128 + receiver_offset) as u64);
    followed_packet(&mut state, &sync, newest + 10_000, start, 7);
    followed_packet(&mut state, &follow, newest + 20_000, start, 7);
    let stale = 1_000_000_000u64;
    let later = start + Duration::from_millis(125);
    let (sync, follow) = sync_pair(8, 1, (stale as i128 + receiver_offset) as u64);
    // Both packets arrive after the newer pair, so local receive timestamps
    // alone cannot identify this older source sequence.
    followed_packet(&mut state, &sync, newest + 125_010_000, later, 7);
    followed_packet(&mut state, &follow, newest + 125_020_000, later, 7);
    assert_eq!(state.exchanges, 1);
    assert_eq!(
        state.sync_pair.map(|sample| sample.received),
        Some(newest + 10_000)
    );
    let offset = state.sync_offset.expect("sync offset");
    assert!((offset - (receiver_offset as f64 - 10_000.0)).abs() < 1.0);
}

#[test]
fn follower_locks_from_sync_pairs_when_delay_responses_stay_unusable() {
    // Receivers such as HomePod OS 27 answer the delay request with timestamps
    // that contradict their own sync timestamps. The session must still obtain a
    // usable timeline from the sync exchange instead of failing to start.
    let start = Instant::now();
    let mut state = follower(start);
    let receiver_offset: i128 = 4_200_000_000;
    for seq in 0..3u16 {
        let at = start + Duration::from_millis(u64::from(seq) * 125);
        let local = 1_000_000_000 + u64::from(seq) * 1_000_000_000;
        let (sync, follow) = sync_pair(8, seq, (local as i128 + receiver_offset) as u64);
        followed_packet(&mut state, &sync, local + 90_000, at, 7);
        followed_packet(&mut state, &follow, local + 95_000, at, 7);
        state.delay = Some((seq, local + 200_000));
        state.delay_age = Some(at);
        let mut response = header(9, 54, 8, seq, 0x0408, 0);
        timestamp(&mut response[34..44], 17);
        response[44..52].copy_from_slice(&7u64.to_be_bytes());
        response[52..54].copy_from_slice(&0x8005u16.to_be_bytes());
        followed_packet(&mut state, &response, local + 400_000, at, 7);
        assert_eq!(state.packets.last_rejection, Some("round_trip"));
    }
    assert_eq!(state.exchanges, 3);
    assert_eq!(state.packets.rejected, 3);
    assert!(state.offset.is_none(), "no delay response was accepted");
    let now = start + Duration::from_millis(250);
    assert_eq!(state.offset_source(now), Some("sync"));
    let negative_rtt = state
        .packets
        .last_round_trip_ns
        .expect("round trip recorded");
    assert!(negative_rtt < 0.0, "contradictory timestamps");
    let offset = state.effective_offset(now).expect("sync offset");
    assert!((offset - receiver_offset as f64).abs() < 200_000.0);
    assert!(state.ready(start + Duration::from_millis(250)));
}

#[test]
fn expired_delay_offset_tracks_fresh_syncs_and_recovers_without_stale_smoothing() {
    fn respond(state: &mut PeerState, seq: u16, local: u64, remote: u64, at: Instant) {
        state.delay = Some((seq, local + 200_000));
        state.delay_age = Some(at);
        let mut response = header(9, 54, 8, seq, 0x0408, 0);
        timestamp(&mut response[34..44], remote);
        response[44..52].copy_from_slice(&7u64.to_be_bytes());
        response[52..54].copy_from_slice(&0x8005u16.to_be_bytes());
        followed_packet(state, &response, local + 400_000, at, 7);
    }

    let start = Instant::now();
    let mut state = follower(start);
    let initial_offset = 5_000_000_000u64;
    // Simulate one minute of 100 ppm clock drift, with ongoing Sync pairs but
    // only the first delay response usable. Rejected responses cannot renew it.
    for seq in 0..483u16 {
        let at = start + Duration::from_millis(u64::from(seq) * 125);
        let local = 1_000_000_000 + u64::from(seq) * 125_000_000;
        let drift = u64::from(seq) * 12_500;
        let (sync, follow) = sync_pair(8, seq, local + initial_offset + drift);
        followed_packet(&mut state, &sync, local + 90_000, at, 7);
        followed_packet(&mut state, &follow, local + 95_000, at, 7);
        if seq % 8 == 0 {
            let remote = if seq == 0 {
                local + initial_offset + 290_000
            } else {
                17
            };
            respond(&mut state, seq, local, remote, at);
        }
        if seq < 16 {
            assert_eq!(state.offset_source(at), Some("delay"));
            assert_eq!(state.effective_offset(at), Some(initial_offset as f64));
        } else {
            assert_eq!(state.offset_source(at), Some("sync"));
            assert_eq!(state.effective_offset(at), state.sync_offset);
        }
    }
    let now = start + Duration::from_millis(482 * 125);
    let current_offset = initial_offset as f64 + 482.0 * 12_500.0;
    assert!(state.ready(now));
    assert_eq!(state.packets.last_rejection, Some("round_trip"));
    assert!((state.effective_offset(now).unwrap() - current_offset).abs() < 200_000.0);

    // Once delay responses recover, the old measurement must not bias the new
    // estimate or cause a false offset jump that clears the healthy Sync streak.
    let seq = 483;
    let at = start + Duration::from_millis(u64::from(seq) * 125);
    let local = 1_000_000_000 + u64::from(seq) * 125_000_000;
    let recovered_offset = initial_offset + u64::from(seq) * 12_500;
    let (sync, follow) = sync_pair(8, seq, local + recovered_offset);
    followed_packet(&mut state, &sync, local + 90_000, at, 7);
    followed_packet(&mut state, &follow, local + 95_000, at, 7);
    respond(
        &mut state,
        seq,
        local,
        local + recovered_offset + 290_000,
        at,
    );
    assert_eq!(state.offset_source(at), Some("delay"));
    assert_eq!(state.effective_offset(at), Some(recovered_offset as f64));
    assert_eq!(state.jitter_ns, None);
    assert!(state.ready(at));
}

#[test]
fn delayed_sync_after_its_follow_up_cannot_rewind_a_newer_pair() {
    for (older, newer) in [(1, 2), (u16::MAX, 0)] {
        let start = Instant::now();
        let mut state = follower(start);
        let offset = 5_000_000_000;
        let (old_sync, old_follow) = sync_pair(8, older, 1_000_000_000 + offset);
        followed_packet(&mut state, &old_follow, 1_000_090_000, start, 7);
        let (sync, follow) = sync_pair(8, newer, 1_125_000_000 + offset);
        let at = start + Duration::from_millis(125);
        followed_packet(&mut state, &sync, 1_125_090_000, at, 7);
        followed_packet(&mut state, &follow, 1_125_095_000, at, 7);
        let offset_before = state.sync_offset;
        followed_packet(
            &mut state,
            &old_sync,
            1_250_090_000,
            start + Duration::from_millis(250),
            7,
        );
        assert_eq!(state.sync_pair.unwrap().sequence, newer);
        assert_eq!(state.sync_offset, offset_before);
        assert_eq!(state.last, Some(at));
        assert_eq!(state.exchanges, 1);
    }
}

#[test]
fn sync_sequence_wrap_and_restart_after_timeout_can_reach_readiness() {
    let start = Instant::now();
    let mut state = follower(start);
    for (seq, millis) in [
        (u16::MAX, 0),
        (0, 125),
        (1, 250),
        (0, 4000),
        (1, 4125),
        (2, 4250),
    ] {
        let at = start + Duration::from_millis(millis);
        let local = 1_000_000_000 + millis * 1_000_000;
        let (sync, follow) = sync_pair(8, seq, local + 1_000_000);
        followed_packet(&mut state, &sync, local + 90_000, at, 7);
        followed_packet(&mut state, &follow, local + 95_000, at, 7);
        if millis == 250 || millis == 4250 {
            assert_eq!(state.exchanges, 3);
            assert!(state.ready(at));
        } else if millis == 4000 {
            assert_eq!(state.exchanges, 1);
            assert!(!state.ready(at));
        }
    }
}

#[test]
fn older_exchange_instants_cannot_refresh_or_inflate_readiness() {
    let start = Instant::now();
    let mut state = PeerState::default();
    state.exchanged(start);
    state.exchanged(start + Duration::from_millis(125));
    state.exchanged(start + Duration::from_millis(50));
    assert_eq!(state.exchanges, 2);
    assert_eq!(state.last, Some(start + Duration::from_millis(125)));
    assert!(!state.ready(start + Duration::from_millis(250)));
    state.exchanged(start + Duration::from_millis(250));
    assert!(state.ready(start + Duration::from_millis(250)));
}

/// An Announce whose grandmaster outranks ours on priority1 alone.
fn better_announce(identity: u64) -> Vec<u8> {
    let mut packet = announce(identity, 0, 0);
    packet[47] = 10;
    packet
}

#[test]
fn a_sender_role_announce_fills_the_bmca_dataset_and_stops_the_probes() {
    let start = Instant::now();
    let own = 7;
    let mut state = PeerState {
        active: true,
        ..Default::default()
    };
    assert!(!state.follow);
    assert!(state.bmca_probe_due(start));
    observe_announce(&mut state, &better_announce(9), start, own);
    assert_eq!(state.bmca.map(|b| b.priority1), Some(10));
    assert_eq!(state.bmca.map(|b| b.grandmaster), Some(9));
    assert!(state.better_master);
    assert_eq!(state.announced_clock_id, Some(9));
    assert!(
        !state.follow,
        "the dataset is diagnostic; the role never changes"
    );
    assert!(
        !state.bmca_probe_due(start + Duration::from_secs(1)),
        "an answered peer is not asked again"
    );
    // Equal rank on every field but identity: the larger identity loses.
    observe_announce(&mut state, &announce(u64::MAX >> 1, 1, 0), start, own);
    assert_eq!(state.bmca.map(|b| b.priority1), Some(96));
    assert!(!state.better_master);
    assert_eq!(state.identity_changes, 1);
    // Other message types never touch the dataset.
    let (sync, _) = sync_pair(9, 2, 0);
    observe_announce(&mut state, &sync, start, own);
    assert_eq!(state.bmca.map(|b| b.grandmaster), Some(u64::MAX >> 1));
}

#[test]
fn a_silent_master_role_peer_is_probed_a_bounded_number_of_times() {
    let start = Instant::now();
    let mut state = PeerState {
        bmca_probes: BMCA_PROBE_ATTEMPTS - 1,
        next_probe: Some(start + BMCA_PROBE_INTERVAL),
        ..Default::default()
    };
    assert!(!state.bmca_probe_due(start), "the interval is honoured");
    assert!(state.bmca_probe_due(start + BMCA_PROBE_INTERVAL));
    state.bmca_probes = BMCA_PROBE_ATTEMPTS;
    assert!(!state.bmca_probe_due(start + Duration::from_secs(60)));
}

#[test]
fn snapshots_report_a_sender_role_peers_announced_dataset() {
    let host: IpAddr = "192.0.2.50".parse().unwrap();
    // The peer stays inactive, so the worker neither sends nor accepts packets.
    let master = PtpMaster::from_sockets(
        vec![host],
        vec![],
        Clock::new(),
        UdpSocket::bind("127.0.0.1:0").unwrap(),
        UdpSocket::bind("127.0.0.1:0").unwrap(),
    )
    .unwrap();
    let before = master
        .snapshots()
        .into_iter()
        .find(|p| p.host == host)
        .unwrap();
    assert_eq!(before.bmca_priority1, None);
    assert!(!before.better_master);
    {
        let mut states = master.states.lock().unwrap();
        observe_announce(
            states.get_mut(&host).unwrap(),
            &better_announce(9),
            Instant::now(),
            master.clock_id,
        );
    }
    let after = master
        .snapshots()
        .into_iter()
        .find(|p| p.host == host)
        .unwrap();
    assert_eq!(after.mode, "sender");
    assert_eq!(after.bmca_priority1, Some(10));
    assert_eq!(after.bmca_grandmaster, Some(9));
    assert_eq!(after.bmca_clock_class, Some(248));
    assert!(after.better_master);
}
