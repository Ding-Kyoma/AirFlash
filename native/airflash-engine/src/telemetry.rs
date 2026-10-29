//! Counters and histograms that explain a session without a debugger.
//!
//! The engine already reports per-second `capture_metrics` and `transport_metrics`.
//! What was missing (roadmap P0-5) is the ability to answer "why did this session
//! stutter": lateness distribution, capture age, retransmission gaps, an explicit
//! underflow classification, timeline recovery counters and a session epoch.
use serde_json::{Value, json};
use std::{
    io::Write,
    path::{Path, PathBuf},
    sync::{
        Mutex, OnceLock,
        atomic::{AtomicU64, Ordering},
    },
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

/// The roadmap §12.3 field list, in order. The CSV header is generated from this
/// array, so changing the order is a format change and must be deliberate.
pub const SUMMARY_COLUMNS: [&str; 18] = [
    "session_id",
    "topology",
    "codec",
    "timing",
    "applied_lead_ms",
    "packets",
    "rtx_req",
    "rtx_resent",
    "underruns",
    "gap_filled",
    "reanchored",
    "servo_engagements",
    "baseline_resets",
    "mean_lateness_ms",
    "p99_lateness_ms",
    "max_lateness_ms",
    "duration_s",
    "rebuild_count",
];

/// One finished streaming session, in the shape roadmap §12.3 asks for.
///
/// Every value is copied from a counter that already existed before this struct:
/// the scheduler telemetry, `MediaMetrics`, the servo counters and the negotiated
/// `CompatProfile`. Nothing here is sampled on the audio path.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct SessionSummary {
    pub session_id: String,
    pub topology: &'static str,
    pub codec: &'static str,
    pub timing: &'static str,
    pub applied_lead_ms: i64,
    pub packets: u64,
    pub rtx_req: u64,
    pub rtx_resent: u64,
    pub underruns: u64,
    pub gap_filled: u64,
    pub reanchored: u64,
    pub servo_engagements: u64,
    pub baseline_resets: u64,
    pub mean_lateness_ms: f64,
    pub p99_lateness_ms: f64,
    pub max_lateness_ms: f64,
    pub duration_s: f64,
    pub rebuild_count: u64,
}
impl SessionSummary {
    /// The additive IPC event. Consumers of `transport_metrics`/`capture_metrics`
    /// never see a changed shape; this is a new `event` value they can ignore.
    pub fn event(&self) -> Value {
        json!({
            "event": "session_summary",
            "session_id": self.session_id,
            "topology": self.topology,
            "codec": self.codec,
            "timing": self.timing,
            "applied_lead_ms": self.applied_lead_ms,
            "packets": self.packets,
            "rtx_req": self.rtx_req,
            "rtx_resent": self.rtx_resent,
            "underruns": self.underruns,
            "gap_filled": self.gap_filled,
            "reanchored": self.reanchored,
            "servo_engagements": self.servo_engagements,
            "baseline_resets": self.baseline_resets,
            "mean_lateness_ms": self.mean_lateness_ms,
            "p99_lateness_ms": self.p99_lateness_ms,
            "max_lateness_ms": self.max_lateness_ms,
            "duration_s": self.duration_s,
            "rebuild_count": self.rebuild_count,
        })
    }
    pub fn csv_header() -> String {
        SUMMARY_COLUMNS.join(",")
    }
    /// One CSV row. Only the opaque `session_id` can ever need quoting; every
    /// other field is a number or a fixed enum label.
    pub fn csv_row(&self) -> String {
        [
            csv_field(&self.session_id),
            csv_field(self.topology),
            csv_field(self.codec),
            csv_field(self.timing),
            self.applied_lead_ms.to_string(),
            self.packets.to_string(),
            self.rtx_req.to_string(),
            self.rtx_resent.to_string(),
            self.underruns.to_string(),
            self.gap_filled.to_string(),
            self.reanchored.to_string(),
            self.servo_engagements.to_string(),
            self.baseline_resets.to_string(),
            format!("{:.3}", self.mean_lateness_ms),
            format!("{:.3}", self.p99_lateness_ms),
            format!("{:.3}", self.max_lateness_ms),
            format!("{:.3}", self.duration_s),
            self.rebuild_count.to_string(),
        ]
        .join(",")
    }
}

fn csv_field(value: &str) -> String {
    if value.contains([',', '"', '\n', '\r']) {
        format!("\"{}\"", value.replace('"', "\"\""))
    } else {
        value.to_owned()
    }
}

/// Where the per-user summary lives: next to the WPF logs, which is the same
/// `%APPDATA%\AirFlash` root the engine already uses for credentials. This
/// mirrors `AppPaths.LogDirectory` on the engine side instead of inventing a
/// second location.
pub fn summary_path() -> PathBuf {
    std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_default()
        .join("AirFlash")
        .join("logs")
        .join("session-summary.csv")
}

/// Append one row, writing the header first when the file is new. Callers treat
/// every error as a warning: a bad profile must never fail a session.
pub fn append_summary_row(path: &Path, summary: &SessionSummary) -> std::io::Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let mut file = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(path)?;
    let mut line = String::new();
    if file.metadata()?.len() == 0 {
        line.push_str(&SessionSummary::csv_header());
        line.push('\n');
    }
    line.push_str(&summary.csv_row());
    line.push('\n');
    file.write_all(line.as_bytes())?;
    file.flush()
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

    fn known_summary() -> SessionSummary {
        SessionSummary {
            session_id: "session-42".into(),
            topology: "software_stereo",
            codec: "alac",
            timing: "ptp",
            applied_lead_ms: 45,
            packets: 1500,
            rtx_req: 7,
            rtx_resent: 5,
            underruns: 2,
            gap_filled: 11,
            reanchored: 3,
            servo_engagements: 4,
            baseline_resets: 1,
            mean_lateness_ms: 1.234,
            p99_lateness_ms: 9.876,
            max_lateness_ms: 12.345,
            duration_s: 30.5,
            rebuild_count: 0,
        }
    }

    #[test]
    fn summary_event_carries_the_declared_fields_and_redaction_keeps_its_shape() {
        let summary = known_summary();
        let event = summary.event();
        assert_eq!(event["event"], json!("session_summary"));
        for column in SUMMARY_COLUMNS {
            assert!(event.get(column).is_some(), "missing field {column}");
        }
        // Only the event discriminator is extra; the declared list is complete.
        assert_eq!(event.as_object().unwrap().len(), SUMMARY_COLUMNS.len() + 1);
        // Redaction never changes the shape of an already identity-free record.
        assert_eq!(redact(&event), event);
    }

    #[test]
    fn summary_csv_uses_the_declared_column_order_and_hides_identities() {
        let summary = known_summary();
        assert_eq!(SessionSummary::csv_header(), SUMMARY_COLUMNS.join(","));
        let row = summary.csv_row();
        let fields: Vec<_> = row.split(',').collect();
        assert_eq!(fields.len(), SUMMARY_COLUMNS.len(), "row: {row}");
        for (index, column) in SUMMARY_COLUMNS.iter().enumerate() {
            match *column {
                "session_id" => assert_eq!(fields[index], "session-42"),
                "topology" => assert_eq!(fields[index], "software_stereo"),
                "codec" => assert_eq!(fields[index], "alac"),
                "timing" => assert_eq!(fields[index], "ptp"),
                "applied_lead_ms" => assert_eq!(fields[index], "45"),
                "packets" => assert_eq!(fields[index], "1500"),
                "rtx_req" => assert_eq!(fields[index], "7"),
                "rtx_resent" => assert_eq!(fields[index], "5"),
                "underruns" => assert_eq!(fields[index], "2"),
                "gap_filled" => assert_eq!(fields[index], "11"),
                "reanchored" => assert_eq!(fields[index], "3"),
                "servo_engagements" => assert_eq!(fields[index], "4"),
                "baseline_resets" => assert_eq!(fields[index], "1"),
                "mean_lateness_ms" => assert_eq!(fields[index], "1.234"),
                "p99_lateness_ms" => assert_eq!(fields[index], "9.876"),
                "max_lateness_ms" => assert_eq!(fields[index], "12.345"),
                "duration_s" => assert_eq!(fields[index], "30.500"),
                "rebuild_count" => assert_eq!(fields[index], "0"),
                other => panic!("unexpected column {other}"),
            }
        }
        // No host, MAC, path or secret can survive the numeric/enumerated shape.
        for forbidden in ["127.0.0.1", "192.168.", "C:\\", "/home/", "02:57:32", "secret"] {
            assert!(!row.contains(forbidden), "CSV leaked {forbidden}: {row}");
        }
    }

    #[test]
    fn summary_csv_append_writes_one_header_and_one_row_per_session() {
        let root = std::env::temp_dir().join(format!("airflash-summary-{}", uuid::Uuid::new_v4()));
        let path = root.join("logs").join("session-summary.csv");
        let first = known_summary();
        append_summary_row(&path, &first).unwrap();
        let mut second = first.clone();
        second.session_id = "session-43".into();
        append_summary_row(&path, &second).unwrap();
        let text = std::fs::read_to_string(&path).unwrap();
        let lines: Vec<_> = text.lines().collect();
        assert_eq!(lines.len(), 3, "header plus one row per session: {text}");
        assert_eq!(lines[0], SessionSummary::csv_header());
        assert!(lines[1].starts_with("session-42,"));
        assert!(lines[2].starts_with("session-43,"));
        let _ = std::fs::remove_dir_all(root);
    }
}
