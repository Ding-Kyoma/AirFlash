//! Real localhost RTSP transactions. No speaker or audio device is opened.
use airflash_engine::{
    rtsp::{Cancellation, Connection},
    volume::{Command, Control, Worker},
};
use plist::Value;
use std::{
    net::TcpListener,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU8, AtomicUsize, Ordering},
    },
    thread,
    time::Duration,
};

type Fake = (
    airflash_engine::volume::Peer,
    Arc<AtomicU8>,
    Arc<AtomicUsize>,
    Cancellation,
    thread::JoinHandle<()>,
);
fn fake(initial: u8, reject: bool) -> Fake {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let connection =
        Connection::connect(listener.local_addr().unwrap(), Cancellation::default()).unwrap();
    let (socket, _) = listener.accept().unwrap();
    let value = Arc::new(AtomicU8::new(initial));
    let sets = Arc::new(AtomicUsize::new(0));
    let stop = Cancellation::default();
    let (v, n, done) = (value.clone(), sets.clone(), stop.clone());
    let server = thread::spawn(move || {
        let mut c = Connection::from_stream(socket, done.clone()).unwrap();
        let mut delayed = None;
        while !done.is_cancelled() {
            let msg = match c.read_for(Duration::from_millis(20), None) {
                Ok(Some(m)) => m,
                Ok(None) => continue,
                Err(_) => break,
            };
            let mut body = Vec::new();
            if msg.first.starts_with("GET /info ") {
                let mut info = plist::Dictionary::new();
                if !reject {
                    info.insert(
                        "initialVolume".into(),
                        Value::Real(airflash_engine::volume::to_db(v.load(Ordering::Relaxed))),
                    );
                }
                Value::Dictionary(info).to_writer_binary(&mut body).unwrap();
                // First read after SET still returns the old value.
                if let Some(next) = delayed.take() {
                    v.store(next, Ordering::Relaxed);
                }
            } else if msg.first.starts_with("SET_PARAMETER ") {
                assert_eq!(msg.headers["content-type"], "text/parameters");
                let text = String::from_utf8(msg.body).unwrap();
                let db: f64 = text
                    .trim()
                    .strip_prefix("volume: ")
                    .unwrap()
                    .parse()
                    .unwrap();
                delayed = Some(airflash_engine::volume::from_db(db).unwrap());
                n.fetch_add(1, Ordering::Relaxed);
            } else {
                panic!("unexpected transaction: {}", msg.first);
            }
            let mut response = format!(
                "RTSP/1.0 200 OK\r\nCSeq: {}\r\nContent-Length: {}\r\n\r\n",
                msg.headers["cseq"],
                body.len()
            )
            .into_bytes();
            response.extend(body);
            if c.write(&response).is_err() {
                break;
            }
        }
    });
    (
        (
            "127.0.0.1".into(),
            "rtsp://127.0.0.1/test".into(),
            Arc::new(Mutex::new(connection)),
        ),
        value,
        sets,
        stop,
        server,
    )
}
#[test]
fn initial_read_physical_change_pair_write_and_delayed_confirmation() {
    let (leader, volume, sets1, stop1, server1) = fake(28, false);
    let (member, _, sets2, stop2, server2) = fake(25, false);
    let control = Control::default();
    let worker = Worker::start(vec![leader, member], control.clone()).unwrap();
    let initial = worker.events.recv_timeout(Duration::from_secs(2)).unwrap();
    assert_eq!(initial["volume"], 28);
    assert_eq!(sets1.load(Ordering::Relaxed), 0);
    assert_eq!(sets2.load(Ordering::Relaxed), 0);
    volume.store(32, Ordering::Relaxed);
    let external = worker.events.recv_timeout(Duration::from_secs(2)).unwrap();
    assert_eq!(external["volume"], 32);
    control.set(Command {
        sequence: 1,
        percent: 40,
    });
    let old = worker.events.recv_timeout(Duration::from_secs(2)).unwrap();
    assert_eq!(old["sequence"], 1);
    assert_eq!(old["status"], "pending");
    assert_eq!(old["volume"], 32);
    let confirmed = worker.events.recv_timeout(Duration::from_secs(2)).unwrap();
    assert_eq!(confirmed["status"], "confirmed");
    assert_eq!(confirmed["volume"], 40);
    assert_eq!(sets1.load(Ordering::Relaxed), 1);
    assert_eq!(sets2.load(Ordering::Relaxed), 1);
    stop1.cancel();
    stop2.cancel();
    drop(worker);
    server1.join().unwrap();
    server2.join().unwrap();
}
#[test]
fn unsupported_query_does_not_invent_volume() {
    let (peer, _, sets, stop, server) = fake(28, true);
    let worker = Worker::start(vec![peer], Control::default()).unwrap();
    let event = worker.events.recv_timeout(Duration::from_secs(2)).unwrap();
    assert!(event["volume"].is_null());
    assert_eq!(event["available"], false);
    assert_eq!(event["status"], "unsynced");
    assert_eq!(sets.load(Ordering::Relaxed), 0);
    stop.cancel();
    drop(worker);
    server.join().unwrap();
}
