//! Bounded, nonblocking type-103 TCP framing. Partial writes retain exact bytes.
use anyhow::{Result, ensure};
use std::{
    collections::VecDeque,
    io::Write,
    net::{SocketAddr, TcpStream},
    time::{Duration, Instant},
};
const MAX_PENDING: usize = 256;
struct Pending {
    bytes: Vec<u8>,
    offset: usize,
    deadline: Instant,
}
pub struct Sender {
    socket: TcpStream,
    pending: VecDeque<Pending>,
}
impl Sender {
    pub fn connect(address: SocketAddr) -> Result<Self> {
        let socket = TcpStream::connect_timeout(&address, Duration::from_secs(3))?;
        Self::from_stream(socket)
    }
    pub fn from_stream(socket: TcpStream) -> Result<Self> {
        socket.set_nodelay(true)?;
        socket.set_nonblocking(true)?;
        Ok(Self {
            socket,
            pending: VecDeque::new(),
        })
    }
    pub fn queued(&self) -> usize {
        self.pending.len()
    }
    pub fn enqueue(&mut self, packet: Vec<u8>, deadline: Instant) -> Result<()> {
        ensure!(
            self.pending.len() < MAX_PENDING,
            "buffered media queue full"
        );
        let length = u16::try_from(packet.len() + 2)?;
        let mut bytes = length.to_be_bytes().to_vec();
        bytes.extend(packet);
        self.pending.push_back(Pending {
            bytes,
            offset: 0,
            deadline,
        });
        Ok(())
    }
    pub fn service(&mut self, now: Instant) -> Result<()> {
        service_pending(&mut self.pending, &mut self.socket, now)
    }
}
fn service_pending(
    pending: &mut VecDeque<Pending>,
    socket: &mut impl Write,
    now: Instant,
) -> Result<()> {
    let until = Instant::now() + Duration::from_millis(1);
    let mut budget = 65536;
    while let Some(frame) = pending.front_mut() {
        ensure!(
            now < frame.deadline,
            "buffered frame missed its presentation deadline"
        );
        if budget == 0 || Instant::now() >= until {
            break;
        }
        let tail = &frame.bytes[frame.offset..];
        match socket.write(&tail[..tail.len().min(budget)]) {
            Ok(0) => anyhow::bail!("buffered peer closed connection"),
            Ok(n) => {
                frame.offset += n;
                budget -= n;
                if frame.offset == frame.bytes.len() {
                    pending.pop_front();
                }
            }
            Err(e)
                if matches!(
                    e.kind(),
                    std::io::ErrorKind::WouldBlock | std::io::ErrorKind::Interrupted
                ) =>
            {
                break;
            }
            Err(e) => return Err(e.into()),
        }
    }
    Ok(())
}
#[cfg(test)]
mod tests {
    use super::*;
    use std::{io::Read, net::TcpListener};
    #[test]
    fn short_writes_and_repeated_backpressure_preserve_every_byte() {
        struct Throttled {
            bytes: Vec<u8>,
            blocked: bool,
        }
        impl Write for Throttled {
            fn write(&mut self, b: &[u8]) -> std::io::Result<usize> {
                if self.blocked {
                    self.blocked = false;
                    return Err(std::io::ErrorKind::WouldBlock.into());
                }
                self.blocked = true;
                let n = b.len().min(3);
                self.bytes.extend_from_slice(&b[..n]);
                Ok(n)
            }
            fn flush(&mut self) -> std::io::Result<()> {
                Ok(())
            }
        }
        let now = Instant::now();
        let bytes = (0..200u8).collect::<Vec<_>>();
        let mut pending = VecDeque::from([Pending {
            bytes: bytes.clone(),
            offset: 0,
            deadline: now + Duration::from_secs(1),
        }]);
        let mut writer = Throttled {
            bytes: Vec::new(),
            blocked: false,
        };
        while !pending.is_empty() {
            service_pending(&mut pending, &mut writer, now).unwrap();
        }
        assert_eq!(writer.bytes, bytes);
    }
    #[test]
    fn frames_remain_ordered_and_length_counts_itself() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let mut sender = Sender::connect(listener.local_addr().unwrap()).unwrap();
        let (mut peer, _) = listener.accept().unwrap();
        peer.set_read_timeout(Some(Duration::from_secs(1))).unwrap();
        for value in [7, 8] {
            sender
                .enqueue(vec![value; 100], Instant::now() + Duration::from_secs(1))
                .unwrap();
        }
        while sender.queued() > 0 {
            sender.service(Instant::now()).unwrap();
        }
        let mut data = [0; 204];
        peer.read_exact(&mut data).unwrap();
        assert_eq!(&data[..2], &102u16.to_be_bytes());
        assert_eq!(&data[2..102], &[7; 100]);
        assert_eq!(&data[102..104], &102u16.to_be_bytes());
        assert_eq!(&data[104..], &[8; 100]);
    }
    #[test]
    fn backpressure_is_bounded_and_expired_live_frames_are_not_written() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let mut sender = Sender::connect(listener.local_addr().unwrap()).unwrap();
        let (_peer, _) = listener.accept().unwrap();
        for _ in 0..MAX_PENDING {
            sender
                .enqueue(vec![1; 1400], Instant::now() + Duration::from_secs(10))
                .unwrap();
        }
        assert!(sender.enqueue(vec![1], Instant::now()).is_err());
        assert!(
            sender
                .service(Instant::now() + Duration::from_secs(11))
                .is_err()
        );
        assert_eq!(sender.queued(), MAX_PENDING);
    }
}
