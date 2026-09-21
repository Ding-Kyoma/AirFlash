use airflash_engine::rtsp::{Cancellation, Connection};
use std::{
    io::{Read, Write},
    net::TcpListener,
    thread,
    time::Duration,
};
#[test]
fn fragmented_response_and_cseq_validation() {
    for wrong in [false, true] {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let server = thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            let mut buffer = [0; 4096];
            let received = socket.read(&mut buffer).unwrap();
            assert!(received > 0);
            let response = format!(
                "RTSP/1.0 200 OK\r\nCSeq: {}\r\nContent-Length: 5\r\n\r\nhello",
                if wrong { 99 } else { 1 }
            );
            for bytes in response.as_bytes().chunks(3) {
                if socket.write_all(bytes).is_err() {
                    break;
                }
            }
        });
        let mut connection = Connection::connect(addr, Cancellation::default()).unwrap();
        let result = connection.request("GET", "/info", &[], &[]);
        assert_eq!(result.is_err(), wrong);
        if !wrong {
            assert_eq!(result.unwrap().body, b"hello");
        }
        server.join().unwrap();
    }
}
#[test]
fn cancellation_interrupts_blocked_io() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = listener.local_addr().unwrap();
    let token = Cancellation::default();
    let child = token.clone();
    let worker = thread::spawn(move || {
        let mut c = Connection::connect(addr, child).unwrap();
        c.read()
    });
    let (socket, _) = listener.accept().unwrap();
    thread::sleep(Duration::from_millis(20));
    let start = std::time::Instant::now();
    token.cancel();
    assert!(worker.join().unwrap().is_err());
    assert!(start.elapsed() < Duration::from_secs(1));
    drop(socket);
}
#[test]
fn oversized_or_duplicate_length_rejected() {
    for response in [
        "RTSP/1.0 200 OK\r\nContent-Length: 999999999\r\n\r\n",
        "RTSP/1.0 200 OK\r\nContent-Length: 0\r\nContent-Length: 0\r\n\r\n",
    ] {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let server = thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            socket.write_all(response.as_bytes()).unwrap();
        });
        let mut c = Connection::connect(addr, Cancellation::default()).unwrap();
        assert!(c.read().is_err());
        server.join().unwrap();
    }
}

#[test]
fn cancelled_session_still_sends_teardown() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    let server = thread::spawn(move || {
        let (socket, _) = listener.accept().unwrap();
        let mut connection = Connection::from_stream(socket, Cancellation::default()).unwrap();
        let message = connection.read().unwrap();
        assert!(message.first.starts_with("TEARDOWN "));
        let seq = &message.headers["cseq"];
        connection
            .write(
                format!("RTSP/1.0 200 OK\r\nCSeq: {seq}\r\nContent-Length: 0\r\n\r\n").as_bytes(),
            )
            .unwrap();
    });
    let token = Cancellation::default();
    let mut connection = Connection::connect(address, token.clone()).unwrap();
    token.cancel();
    connection.finish_session("rtsp://127.0.0.1/test").unwrap();
    server.join().unwrap();
}

#[test]
fn encrypted_records_survive_idle_and_partial_prefix_body_and_tag() {
    use airflash_engine::crypto::Cipher;
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let mut client =
        Connection::connect(listener.local_addr().unwrap(), Cancellation::default()).unwrap();
    let (mut peer, _) = listener.accept().unwrap();
    client.encrypt([1; 32], [2; 32]);
    let body = "x".repeat(1500);
    let message = format!(
        "POST /event HTTP/1.1\r\nCSeq: 7\r\nContent-Length: {}\r\n\r\n{body}",
        body.len()
    );
    let encoded = Cipher::new([2; 32]).records(message.as_bytes()).unwrap();
    for _ in 0..3 {
        assert!(
            client
                .read_for(Duration::from_millis(3), None)
                .unwrap()
                .is_none()
        );
    }
    // Prefix, ciphertext and tag all arrive across different polls.
    let mut offset = 0;
    for end in [1, 2, 19, 1030, 1041, encoded.len() - 1] {
        peer.write_all(&encoded[offset..end]).unwrap();
        offset = end;
        assert!(
            client
                .read_for(Duration::from_millis(5), None)
                .unwrap()
                .is_none()
        );
    }
    peer.write_all(&encoded[offset..]).unwrap();
    let result = client
        .read_for(Duration::from_secs(1), None)
        .unwrap()
        .unwrap();
    assert_eq!(result.body, body.as_bytes());
    assert_eq!(result.headers["cseq"], "7");
}
#[test]
fn idle_eof_and_authentication_failure_are_distinct() {
    use airflash_engine::{crypto::Cipher, rtsp::WireError};
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let mut client =
        Connection::connect(listener.local_addr().unwrap(), Cancellation::default()).unwrap();
    let (peer, _) = listener.accept().unwrap();
    assert!(
        client
            .read_for(Duration::from_millis(3), None)
            .unwrap()
            .is_none()
    );
    drop(peer);
    let error = client.read().unwrap_err();
    assert!(matches!(
        error.downcast_ref::<WireError>(),
        Some(WireError::PeerClosed)
    ));
    let mut client =
        Connection::connect(listener.local_addr().unwrap(), Cancellation::default()).unwrap();
    let (mut peer, _) = listener.accept().unwrap();
    client.encrypt([1; 32], [2; 32]);
    let bytes = Cipher::new([3; 32])
        .records(b"RTSP/1.0 200 OK\r\n\r\n")
        .unwrap();
    peer.write_all(&bytes).unwrap();
    let error = client.read().unwrap_err();
    assert!(matches!(
        error.downcast_ref::<WireError>(),
        Some(WireError::Authentication)
    ));
}

#[test]
fn delayed_reply_from_timed_out_volume_query_is_not_next_response() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let mut connection = Connection::connect(listener.local_addr().unwrap(), Cancellation::default()).unwrap();
    let (socket, _) = listener.accept().unwrap();
    let server = thread::spawn(move || {
        let mut peer = Connection::from_stream(socket, Cancellation::default()).unwrap();
        let first = peer.read().unwrap();
        let second = peer.read().unwrap();
        peer.write(format!("RTSP/1.0 200 OK\r\nCSeq: {}\r\nContent-Length: 3\r\n\r\noldRTSP/1.0 200 OK\r\nCSeq: {}\r\nContent-Length: 3\r\n\r\nnew", first.headers["cseq"], second.headers["cseq"]).as_bytes()).unwrap();
    });
    connection.set_timeout(Duration::from_millis(30)).unwrap();
    assert!(connection.request("GET", "/info", &[], &[]).is_err());
    connection.set_timeout(Duration::from_secs(2)).unwrap();
    assert_eq!(connection.request("POST", "/feedback", &[], &[]).unwrap().body, b"new");
    server.join().unwrap();
}
