//! Bounded RTSP/HTTP parser and encrypted transport (no media or UI work here).
use crate::crypto::Cipher;
use anyhow::{Context, Result, ensure};
use std::{
    collections::BTreeMap,
    io::{Read, Write},
    net::{Shutdown, SocketAddr, TcpStream},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::{Duration, Instant},
};

#[derive(Clone, Default)]
pub struct Cancellation {
    stopped: Arc<AtomicBool>,
}
impl Cancellation {
    pub fn is_cancelled(&self) -> bool {
        self.stopped.load(Ordering::Acquire)
    }
    pub fn check(&self) -> Result<()> {
        if self.is_cancelled() {
            return Err(WireError::Cancelled.into());
        }
        Ok(())
    }
    pub fn cancel(&self) {
        self.stopped.store(true, Ordering::Release);
    }
}
#[derive(Debug, Clone, Copy)]
pub enum WireError {
    Cancelled,
    PeerClosed,
    ReadTimeout,
    WriteTimeout,
    Authentication,
    Poisoned,
}
impl std::fmt::Display for WireError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            Self::Cancelled => "session cancelled",
            Self::PeerClosed => "peer closed connection",
            Self::ReadTimeout => "control read timeout",
            Self::WriteTimeout => "control write timeout",
            Self::Authentication => "authentication tag mismatch",
            Self::Poisoned => "control write state is unusable",
        })
    }
}
impl std::error::Error for WireError {}

#[derive(Debug)]
pub struct Message {
    pub first: String,
    pub headers: BTreeMap<String, String>,
    pub body: Vec<u8>,
}
impl Message {
    pub fn status(&self) -> Result<u16> {
        self.first
            .split_whitespace()
            .nth(1)
            .context("missing status")?
            .parse()
            .context("invalid status")
    }
    pub fn plist(&self) -> Result<plist::Value> {
        plist::Value::from_reader(std::io::Cursor::new(&self.body)).context("invalid binary plist")
    }
}
const MAX_HEADER: usize = 16 * 1024;
const MAX_BODY: usize = 1024 * 1024;
pub struct Connection {
    socket: TcpStream,
    pending: Vec<u8>,
    encrypted_pending: Vec<u8>,
    poisoned: bool,
    tx: Option<Cipher>,
    rx: Option<Cipher>,
    cseq: u32,
    cancel: Cancellation,
    timeout: Duration,
}
impl Connection {
    pub fn connect(addr: SocketAddr, cancel: Cancellation) -> Result<Self> {
        cancel.check()?;
        let socket = TcpStream::connect_timeout(&addr, Duration::from_secs(3))
            .with_context(|| format!("connect {addr}"))?;
        Self::from_stream(socket, cancel)
    }
    pub fn from_stream(socket: TcpStream, cancel: Cancellation) -> Result<Self> {
        cancel.check()?;
        socket.set_nodelay(true)?;
        socket.set_nonblocking(true)?;
        Ok(Self {
            socket,
            pending: Vec::new(),
            encrypted_pending: Vec::new(),
            poisoned: false,
            tx: None,
            rx: None,
            cseq: 0,
            cancel,
            timeout: Duration::from_secs(4),
        })
    }
    pub fn local_addr(&self) -> Result<SocketAddr> {
        Ok(self.socket.local_addr()?)
    }
    pub fn set_timeout(&mut self, timeout: Duration) -> Result<()> {
        self.timeout = timeout;
        Ok(())
    }
    pub fn encrypt(&mut self, tx: [u8; 32], rx: [u8; 32]) {
        self.tx = Some(Cipher::new(tx));
        self.rx = Some(Cipher::new(rx));
    }
    pub fn write(&mut self, bytes: &[u8]) -> Result<()> {
        self.cancel.check()?;
        if self.poisoned {
            return Err(WireError::Poisoned.into());
        }
        let encoded = if let Some(tx) = &mut self.tx {
            tx.records(bytes)?
        } else {
            bytes.to_vec()
        };
        let result = (|| -> Result<()> {
            let deadline = Instant::now() + self.timeout;
            let mut offset = 0;
            while offset < encoded.len() {
                self.cancel.check()?;
                if Instant::now() >= deadline {
                    return Err(WireError::WriteTimeout.into());
                }
                match self.socket.write(&encoded[offset..]) {
                    Ok(0) => return Err(WireError::PeerClosed.into()),
                    Ok(n) => offset += n,
                    Err(e)
                        if matches!(
                            e.kind(),
                            std::io::ErrorKind::WouldBlock | std::io::ErrorKind::Interrupted
                        ) =>
                    {
                        std::thread::sleep(Duration::from_millis(2))
                    }
                    Err(e) => return Err(e.into()),
                }
            }
            Ok(())
        })();
        // A partial encrypted write cannot be retried as a fresh record/nonce.
        if result.is_err() {
            self.poisoned = true;
        }
        result
    }
    fn receive_available(&mut self) -> Result<bool> {
        let mut block = [0; 8192];
        let n = match self.socket.read(&mut block) {
            Ok(0) => return Err(WireError::PeerClosed.into()),
            Ok(n) => n,
            Err(e)
                if matches!(
                    e.kind(),
                    std::io::ErrorKind::WouldBlock | std::io::ErrorKind::Interrupted
                ) =>
            {
                return Ok(false);
            }
            Err(e) => return Err(e.into()),
        };
        if let Some(rx) = &mut self.rx {
            self.encrypted_pending.extend_from_slice(&block[..n]);
            while self.encrypted_pending.len() >= 2 {
                let length = [self.encrypted_pending[0], self.encrypted_pending[1]];
                let length_value = u16::from_le_bytes(length) as usize;
                ensure!(
                    length_value > 0 && length_value <= 1024,
                    "invalid HAP record length {length_value}"
                );
                let end = 2 + length_value + 16;
                if self.encrypted_pending.len() < end {
                    break;
                }
                let plaintext = rx
                    .decrypt(&self.encrypted_pending[2..end], &length)
                    .map_err(|_| WireError::Authentication)?;
                self.pending.extend(plaintext);
                self.encrypted_pending.drain(..end);
            }
        } else {
            self.pending.extend_from_slice(&block[..n]);
        }
        ensure!(
            self.pending.len() <= MAX_BODY + MAX_HEADER,
            "response too large"
        );
        Ok(true)
    }
    fn try_message(&mut self) -> Result<Option<Message>> {
        let Some(pos) = self.pending.windows(4).position(|x| x == b"\r\n\r\n") else {
            ensure!(self.pending.len() < MAX_HEADER, "headers too large");
            return Ok(None);
        };
        let end = pos + 4;
        ensure!(end <= MAX_HEADER, "headers too large");
        let header = std::str::from_utf8(&self.pending[..end])?;
        let mut lines = header.split("\r\n");
        let first = lines.next().context("missing first line")?.to_string();
        let mut headers = BTreeMap::new();
        for line in lines.filter(|s| !s.is_empty()) {
            let (key, value) = line.split_once(':').context("invalid header")?;
            let key = key.trim().to_lowercase();
            ensure!(!headers.contains_key(&key), "duplicate header {key}");
            headers.insert(key, value.trim().to_string());
        }
        ensure!(
            !headers.contains_key("transfer-encoding"),
            "chunked RTSP unsupported"
        );
        let length: usize = headers
            .get("content-length")
            .map(|s| s.parse())
            .transpose()?
            .unwrap_or(0);
        ensure!(length <= MAX_BODY, "body too large");
        if self.pending.len() < end + length {
            return Ok(None);
        }
        let body = self.pending[end..end + length].to_vec();
        self.pending.drain(..end + length);
        Ok(Some(Message {
            first,
            headers,
            body,
        }))
    }
    /// None means idle/incomplete, never EOF. All partial encrypted/plain bytes
    /// stay owned by this connection across polls, including the two-byte prefix.
    pub fn read_for(
        &mut self,
        wait: Duration,
        stop: Option<&Cancellation>,
    ) -> Result<Option<Message>> {
        let deadline = Instant::now() + wait;
        loop {
            self.cancel.check()?;
            if let Some(stop) = stop {
                stop.check()?;
            }
            if let Some(message) = self.try_message()? {
                return Ok(Some(message));
            }
            if Instant::now() >= deadline {
                return Ok(None);
            }
            if !self.receive_available()? {
                std::thread::sleep(Duration::from_millis(2));
            }
        }
    }
    pub fn read(&mut self) -> Result<Message> {
        self.read_for(self.timeout, None)?
            .ok_or_else(|| WireError::ReadTimeout.into())
    }
    pub fn begin_request(
        &mut self,
        method: &str,
        path: &str,
        headers: &[(&str, String)],
        body: &[u8],
    ) -> Result<()> {
        self.cseq = self.cseq.checked_add(1).context("CSeq exhausted")?;
        let protocol = if path.starts_with("/pair-") {
            "HTTP/1.1"
        } else {
            "RTSP/1.0"
        };
        let mut req = format!(
            "{method} {path} {protocol}\r\nCSeq: {}\r\nUser-Agent: AirPlay/550.10\r\nContent-Length: {}\r\n",
            self.cseq,
            body.len()
        );
        for (key, value) in headers {
            ensure!(!value.contains(['\r', '\n']), "invalid header value");
            req.push_str(&format!("{key}: {value}\r\n"));
        }
        req.push_str("\r\n");
        let mut bytes = req.into_bytes();
        bytes.extend(body);
        self.write(&bytes)
    }
    pub fn stale_response(&self, response: &Message) -> bool {
        response.headers.get("cseq").and_then(|s| s.parse::<u32>().ok()).is_some_and(|seq| seq < self.cseq)
    }
    pub fn validate_cseq(&self, response: &Message) -> Result<()> {
        if let Some(seq) = response.headers.get("cseq") {
            ensure!(seq.parse::<u32>()? == self.cseq, "mismatched CSeq");
        }
        Ok(())
    }
    pub fn request(
        &mut self,
        method: &str,
        path: &str,
        headers: &[(&str, String)],
        body: &[u8],
    ) -> Result<Message> {
        self.begin_request(method, path, headers, body)?;
        let deadline = Instant::now() + self.timeout;
        let response = loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            let response = self.read_for(remaining, None)?.ok_or(WireError::ReadTimeout)
                .with_context(|| format!("{method} {path}"))?;
            if !self.stale_response(&response) { break response; }
        };
        self.validate_cseq(&response)?;
        ensure!(
            response.status()? == 200,
            "{method} {path}: {}",
            response.first
        );
        Ok(response)
    }
    /// Cancellation stops media immediately, but leaves a short teardown budget.
    pub fn finish_session(&mut self, uri: &str) -> Result<()> {
        let cancellation = std::mem::take(&mut self.cancel);
        let timeout = std::mem::replace(&mut self.timeout, Duration::from_millis(300));
        let result = self.request("TEARDOWN", uri, &[], &[]).map(|_| ());
        self.cancel = cancellation;
        self.timeout = timeout;
        result
    }
    pub fn plist_request(
        &mut self,
        method: &str,
        path: &str,
        body: &plist::Value,
    ) -> Result<Message> {
        let mut bytes = Vec::new();
        body.to_writer_binary(&mut bytes)?;
        self.request(
            method,
            path,
            &[("Content-Type", "application/x-apple-binary-plist".into())],
            &bytes,
        )
    }
}
impl Drop for Connection {
    fn drop(&mut self) {
        let _ = self.socket.shutdown(Shutdown::Both);
    }
}
