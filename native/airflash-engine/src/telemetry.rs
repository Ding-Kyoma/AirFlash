//! Counters and histograms that explain a session without a debugger.
//!
//! The engine already reports per-second `capture_metrics` and `transport_metrics`.
//! What was missing (roadmap P0-5) is the ability to answer "why did this session
//! stutter": lateness distribution, capture age, retransmission gaps, an explicit
//! underflow classification, timeline recovery counters and a session epoch.
use serde_json::{Value, json};
use std::sync::{
    Mutex, OnceLock,
    atomic::{AtomicU64, Ordering},
};

/// Log-spaced buckets covering 0.05 ms .. 200 ms, which brackets everything a
/// scheduler, the network or the receiver can do to a packet.
pub const BUCKETS: usize = 64;
const LOW_MS: f64 = 0.05;
const HIGH_MS: f64 = 200.0;

#[derive(Clone)]
pub struct Histogram {
    buckets: [u32; BUCKETS],
    count: u64,
    below: u64,
    above: u64,
    sum_ms: f64,
    max_ms: f64,
}
impl Default for Histogram {
    fn default() -> Self {
        Self {
            buckets: [0; BUCKETS],
            count: 0,
            below: 0,
            above: 0,
            sum_ms: 0.0,
            max_ms: 0.0,
        }
    }
}
impl Histogram {
    fn index(ms: f64) -> usize {
        let t = (ms / LOW_MS).ln() / (HIGH_MS / LOW_MS).ln();
        ((t * BUCKETS as f64) as usize).min(BUCKETS - 1)
    }
    fn upper_ms(bucket: usize) -> f64 {
        LOW_MS * (HIGH_MS / LOW_MS).powf((bucket + 1) as f64 / BUCKETS as f64)
    }
    /// Negative or non-finite samples are clamped into the first bucket; callers
    /// that need a sign keep their own counters.
    pub fn observe(&mut self, ms: f64) {
        self.count += 1;
        let ms = if ms.is_finite() { ms.max(0.0) } else { 0.0 };
        self.sum_ms += ms;
        self.max_ms = self.max_ms.max(ms);
        if ms < LOW_MS {
            self.below += 1;
        } else if ms > HIGH_MS {
            self.above += 1;
            self.buckets[BUCKETS - 1] += 1;
        } else {
            self.buckets[Self::index(ms)] += 1;
        }
    }
    pub fn count(&self) -> u64 {
        self.count
    }
    pub fn mean_ms(&self) -> f64 {
        if self.count == 0 {
            0.0
        } else {
            self.sum_ms / self.count as f64
        }
    }
    /// Conservative quantile: the upper edge of the bucket the sample falls in,
    /// so the answer never under-reports lateness. Bucket granularity is about
    /// 13.6% (4000x range over 64 buckets). `fraction >= 1` returns the true max.
    pub fn percentile_ms(&self, fraction: f64) -> f64 {
        if self.count == 0 {
            return 0.0;
        }
        if fraction >= 1.0 {
            return self.max_ms;
        }
        if self.below as f64 / self.count as f64 >= fraction {
            return LOW_MS;
        }
        let target = (fraction * self.count as f64).ceil() as u64;
        let mut seen = self.below;
        for (bucket, hits) in self.buckets.iter().enumerate() {
            seen += u64::from(*hits);
            if seen >= target {
                return Self::upper_ms(bucket);
            }
        }
        self.max_ms
    }
    pub fn snapshot(&self) -> Value {
        json!({
            "count": self.count,
            "mean_ms": round3(self.mean_ms()),
            "p50_ms": round3(self.percentile_ms(0.50)),
            "p95_ms": round3(self.percentile_ms(0.95)),
            "p99_ms": round3(self.percentile_ms(0.99)),
            "max_ms": round3(self.max_ms),
            "below_floor": self.below,
            "above_ceiling": self.above,
            "buckets": self.buckets.to_vec(),
        })
    }
}

fn round3(value: f64) -> f64 {
    (value * 1000.0).round() / 1000.0
}

/// Why a packet had to be padded with silence.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum UnderflowCause {
    /// A packet had to be padded while real media was expected. These are the
    /// underruns that need musical concealment (decay instead of a hard cut).
    ActiveMedia,
    /// The source is genuinely idle (standby/keepalive), so plain silence is the
    /// correct output and must not be treated as a failure.
    IdleKeepalive,
}

/// One counter block per streaming session. Owned by the send loop; readers build
/// a JSON snapshot through [`SessionTelemetry::snapshot`].
#[derive(Default)]
pub struct SessionTelemetry {
    pub session_epoch: u64,
    pub packets: u64,
    /// Packets that were on time (within 0.5 ms of their deadline).
    pub on_time_packets: u64,
    /// Packets that woke after their deadline; the histogram keeps the magnitude.
    pub late_packets: u64,
    /// Timeline slots filled with encoded silence to keep seq/rtptime linear.
    pub gap_filled: u64,
    /// Times the schedule was re-anchored instead of expiring the session.
    pub reanchored: u64,
    /// Signed shift applied by the most recent re-anchor (positive = timeline advanced).
    pub reanchor_shift_us: i64,
    /// Timeline expiries that still require a fresh session (should stay near zero).
    pub expired: u64,
    /// Underruns while real media was playing; these are audible without concealment.
    pub underruns_active: u64,
    /// Underruns while the source was idle; these must be plain silence.
    pub underruns_idle: u64,
    /// Capture frames discarded because they were stale (input drops).
    pub input_drops: u64,
    /// Frames dropped between decode and the sender.
    pub decode_drops: u64,
    /// WASAPI discontinuity flags that were not ignored as benign wraparound.
    pub capture_glitches: u64,
    /// Event-channel messages that could not be parsed into a command.
    pub events_dropped: u64,
    pub servo_engagements: u64,
    pub servo_releases: u64,
    pub servo_step_resets: u64,
    pub servo_correction_ms: f64,
    pub servo_filtered_ms: f64,
    pub servo_drift_ppb: f64,
    pub lead_extra_ms: f64,
    pub lateness: Histogram,
    pub capture_age: Histogram,
    pub rtx_gap: Histogram,
}
impl SessionTelemetry {
    pub fn new() -> Self {
        Self {
            session_epoch: next_session_epoch(),
            ..Default::default()
        }
    }
    pub fn observe_lateness(&mut self, late_ms: f64) {
        self.packets += 1;
        self.lateness.observe(late_ms);
        if late_ms > 0.5 {
            self.late_packets += 1;
        } else {
            self.on_time_packets += 1;
        }
    }
    pub fn underflow(&mut self, cause: UnderflowCause) {
        match cause {
            UnderflowCause::ActiveMedia => self.underruns_active += 1,
            UnderflowCause::IdleKeepalive => self.underruns_idle += 1,
        }
    }
    pub fn snapshot(&self) -> Value {
        json!({
            "session_epoch": self.session_epoch,
            "packets": self.packets,
            "on_time_packets": self.on_time_packets,
            "late_packets": self.late_packets,
            "timeline_gap_filled": self.gap_filled,
            "timeline_reanchored": self.reanchored,
            "timeline_reanchor_shift_us": self.reanchor_shift_us,
            "timeline_expired": self.expired,
            "underruns_active": self.underruns_active,
            "underruns_idle": self.underruns_idle,
            "input_drops": self.input_drops,
            "decode_drops": self.decode_drops,
            "capture_glitches": self.capture_glitches,
            "events_dropped": self.events_dropped,
            "lead_extra_ms": round3(self.lead_extra_ms),
            "servo": {
                "engagements": self.servo_engagements,
                "releases": self.servo_releases,
                "step_resets": self.servo_step_resets,
                "correction_ms": round3(self.servo_correction_ms),
                "filtered_error_ms": round3(self.servo_filtered_ms),
                "drift_ppb": round3(self.servo_drift_ppb),
            },
            "lateness": self.lateness.snapshot(),
            "capture_age": self.capture_age.snapshot(),
            "retransmit_gap": self.rtx_gap.snapshot(),
        })
    }
}

static EPOCH: AtomicU64 = AtomicU64::new(0);
pub fn next_session_epoch() -> u64 {
    EPOCH.fetch_add(1, Ordering::Relaxed) + 1
}

static LATEST: OnceLock<Mutex<Value>> = OnceLock::new();
/// The most recent session snapshot, readable through the `diag_summary` command
/// after (or while) a session runs.
pub fn publish_diagnostics(value: Value) {
    let slot = LATEST.get_or_init(|| Mutex::new(Value::Null));
    *slot.lock().unwrap() = value;
}
pub fn diagnostics() -> Value {
    LATEST
        .get_or_init(|| Mutex::new(Value::Null))
        .lock()
        .unwrap()
        .clone()
}

/// Replace everything that identifies a network or a machine, keeping the shape and
/// the numbers.
///
/// A diagnostic dump is only useful if it can be attached to an issue, so hosts,
/// MAC addresses, paths and credential-looking keys are replaced by stable
/// pseudonyms (`host-1`, `host-2`, ...) rather than dropped: two members keep two
/// different names, and the same host keeps the same name across the document.
pub fn redact(value: &Value) -> Value {
    let mut names = std::collections::BTreeMap::new();
    redact_into(value, None, &mut names)
}
fn redact_into(
    value: &Value,
    key: Option<&str>,
    names: &mut std::collections::BTreeMap<String, String>,
) -> Value {
    match value {
        Value::Object(fields) => Value::Object(
            fields
                .iter()
                .map(|(name, inner)| {
                    let lowered = name.to_ascii_lowercase();
                    if matches!(
                        lowered.as_str(),
                        "secret" | "password" | "key" | "authorization" | "token"
                    ) {
                        (name.clone(), Value::String("[redacted]".into()))
                    } else {
                        (name.clone(), redact_into(inner, Some(&lowered), names))
                    }
                })
                .collect(),
        ),
        Value::Array(items) => Value::Array(
            items
                .iter()
                .map(|item| redact_into(item, key, names))
                .collect(),
        ),
        Value::String(text) if key.is_some_and(is_identity_key) => {
            Value::String(pseudonym(text, names))
        }
        Value::String(text) if looks_like_path(text) => Value::String(pseudonym(text, names)),
        Value::String(text) => Value::String(scrub_text(text, names)),
        other => other.clone(),
    }
}
/// Replace addresses that appear inside free text (log lines, messages).
fn scrub_text(text: &str, names: &mut std::collections::BTreeMap<String, String>) -> String {
    let mut out = String::with_capacity(text.len());
    let mut token = String::new();
    for ch in text.chars() {
        if ch.is_ascii_alphanumeric() || matches!(ch, '.' | ':' | '-') {
            token.push(ch);
        } else {
            flush_token(&mut token, &mut out, names);
            out.push(ch);
        }
    }
    flush_token(&mut token, &mut out, names);
    out
}
fn flush_token(
    token: &mut String,
    out: &mut String,
    names: &mut std::collections::BTreeMap<String, String>,
) {
    if token.is_empty() {
        return;
    }
    if is_ipv4(token) || is_mac(token) {
        out.push_str(&pseudonym(token, names));
    } else {
        out.push_str(token);
    }
    token.clear();
}
fn is_ipv4(token: &str) -> bool {
    let parts: Vec<_> = token.split('.').collect();
    parts.len() == 4
        && parts.iter().all(|part| {
            !part.is_empty()
                && part.len() <= 3
                && part.bytes().all(|byte| byte.is_ascii_digit())
                && part.parse::<u16>().is_ok_and(|value| value <= 255)
        })
}
fn is_mac(token: &str) -> bool {
    let separator = if token.contains(':') {
        ':'
    } else if token.contains('-') {
        '-'
    } else {
        return false;
    };
    let parts: Vec<_> = token.split(separator).collect();
    parts.len() == 6
        && parts
            .iter()
            .all(|part| part.len() == 2 && part.bytes().all(|byte| byte.is_ascii_hexdigit()))
}
fn is_identity_key(key: &str) -> bool {
    matches!(
        key,
        "host" | "ip" | "address" | "addr" | "mac" | "deviceid" | "receiver_id" | "id" | "path"
    )
}
fn looks_like_path(text: &str) -> bool {
    text.len() > 3 && (text.contains(":\\") || text.starts_with("\\\\") || text.starts_with("/home/") || text.starts_with("/Users/"))
}
fn pseudonym(text: &str, names: &mut std::collections::BTreeMap<String, String>) -> String {
    if let Some(existing) = names.get(text) {
        return existing.clone();
    }
    let label = format!("host-{}", names.len() + 1);
    names.insert(text.to_string(), label.clone());
    label
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn redaction_keeps_the_numbers_and_hides_the_identities() {
        let snapshot = json!({
            "session_epoch": 4,
            "packets": 1200,
            "members": [
                {"host": "192.168.1.20", "ip": "192.168.1.20", "mac": "02:57:32:41:50:01", "send_errors": 0},
                {"host": "192.168.1.21", "ip": "192.168.1.21", "send_errors": 3},
            ],
            "feedback": {"path": "C:\\Users\\alice\\AppData\\Local\\AirFlash\\log.txt", "rtt_ms": 4.5},
            "secret": "Events-Write-Encryption-Key",
            "nested": {"authorization": "Bearer abc", "note": "192.168.1.20 said hello"},
        });
        let redacted = redact(&snapshot);
        let text = redacted.to_string();
        assert!(!text.contains("192.168.1.20"));
        assert!(!text.contains("192.168.1.21"));
        assert!(!text.contains("alice"));
        assert!(!text.contains("Bearer"));
        assert_eq!(redacted["packets"], json!(1200));
        assert_eq!(redacted["feedback"]["rtt_ms"], json!(4.5));
        assert_eq!(redacted["secret"], json!("[redacted]"));
        // The same host keeps one pseudonym; a second host gets a different one.
        assert_eq!(redacted["members"][0]["host"], redacted["members"][0]["ip"]);
        assert_ne!(redacted["members"][0]["host"], redacted["members"][1]["host"]);
        assert_eq!(redacted["members"][1]["send_errors"], json!(3));
    }

    #[test]
    fn histogram_tracks_percentiles_and_bounds() {
        let mut h = Histogram::default();
        for i in 0..100 {
            h.observe(i as f64 / 10.0);
        }
        assert_eq!(h.count(), 100);
        assert!((h.mean_ms() - 4.95).abs() < 1e-9);
        // 13.6% bucket granularity, rounded up, so 5.0 ms may read as 5.4 ms.
        let p50 = h.percentile_ms(0.5);
        assert!((4.5..5.5).contains(&p50), "p50 {p50}");
        assert!(h.percentile_ms(0.99) >= 9.0);
        assert!((h.percentile_ms(1.0) - 9.9).abs() < 1e-9);
        h.observe(1000.0);
        assert_eq!(h.snapshot()["above_ceiling"], json!(1));
        assert!((h.percentile_ms(1.0) - 1000.0).abs() < 1e-9);
        h.observe(-5.0);
        assert!(h.snapshot()["below_floor"].as_u64().unwrap() >= 1);
    }
    #[test]
    fn telemetry_snapshot_reports_recovery_counters() {
        let mut t = SessionTelemetry::new();
        t.observe_lateness(0.2);
        t.observe_lateness(12.0);
        t.gap_filled = 3;
        t.reanchored = 1;
        t.underflow(UnderflowCause::ActiveMedia);
        t.underflow(UnderflowCause::IdleKeepalive);
        let value = t.snapshot();
        assert_eq!(value["packets"], json!(2));
        assert_eq!(value["late_packets"], json!(1));
        assert_eq!(value["timeline_gap_filled"], json!(3));
        assert_eq!(value["underruns_active"], json!(1));
        assert_eq!(value["underruns_idle"], json!(1));
        assert!(t.session_epoch > 0);
    }
    #[test]
    fn session_epoch_is_monotonic() {
        let first = next_session_epoch();
        let second = next_session_epoch();
        assert!(second > first);
    }
    #[test]
    fn published_diagnostics_round_trip() {
        publish_diagnostics(json!({"session_epoch": 7}));
        assert_eq!(diagnostics()["session_epoch"], json!(7));
    }
}
