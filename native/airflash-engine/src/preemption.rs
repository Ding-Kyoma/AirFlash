//! Preemption detection: another AirPlay controller took the receiver over.
//!
//! The roadmap asks for the opposite of a hair trigger. A receiver that is taken
//! over can send an unexpected `TEARDOWN`, answer RTSP with `403`, or ask for the
//! control connection to be closed; every one of those also happens when the
//! network or the receiver itself misbehaves. A single signal is therefore only
//! recorded as evidence, never as a verdict. Only a signal plus corroboration —
//! media really stopped, or the peer restarted its PTP association — inside a
//! short window counts as preemption. Anything else falls back to the generic
//! transport fault that existed before this module.
use std::time::{Duration, Instant};

/// A control signal and its corroboration must fall inside this window. The
/// receiver is expected to stop accepting media within a few seconds of the
/// takeover, so three seconds is wide enough for a real event and narrow enough
/// that an unrelated outage minutes later cannot be paired with it.
pub const WINDOW: Duration = Duration::from_secs(3);

/// One control-plane thing the receiver did that a takeover would explain.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Signal {
    /// The receiver sent a `TEARDOWN` we did not ask for.
    UnexpectedTeardown,
    /// The receiver answered an RTSP request with `403 Forbidden`.
    Forbidden,
    /// The receiver asked for the control connection to be closed.
    ConnectionClose,
}

impl Signal {
    pub fn name(self) -> &'static str {
        match self {
            Self::UnexpectedTeardown => "unexpected_teardown",
            Self::Forbidden => "forbidden",
            Self::ConnectionClose => "connection_close",
        }
    }
}

/// A signal the receiver sent us. `None` means the message was a response to a
/// request this sender made — including the reply to our own TEARDOWN, which is
/// part of every normal session ending and must never look like a takeover.
pub fn incoming_teardown(method: Option<&str>) -> Option<Signal> {
    (method == Some("TEARDOWN")).then_some(Signal::UnexpectedTeardown)
}

/// A status code that can be part of a takeover. `403` is the only one: `401`
/// and `454` are authentication/session rejections and stay on their existing
/// non-retryable path whether or not the media side also stopped.
pub fn status_signal(status: u16) -> Option<Signal> {
    (status == 403).then_some(Signal::Forbidden)
}

/// Everything observed so far. Times are kept so a signal and a corroboration
/// that are minutes apart cannot be combined.
#[derive(Clone, Copy, Debug, Default)]
pub struct Evidence {
    teardown_at: Option<Instant>,
    forbidden_at: Option<Instant>,
    closed_at: Option<Instant>,
    association_at: Option<Instant>,
    media_stopped_at: Option<Instant>,
}

impl Evidence {
    pub fn record(&mut self, signal: Signal, at: Instant) {
        let slot = match signal {
            Signal::UnexpectedTeardown => &mut self.teardown_at,
            Signal::Forbidden => &mut self.forbidden_at,
            Signal::ConnectionClose => &mut self.closed_at,
        };
        keep_latest(slot, at);
    }

    /// The peer restarted its PTP association (new clock identity or source).
    pub fn association_changed(&mut self, at: Instant) {
        keep_latest(&mut self.association_at, at);
    }

    /// Media stopped arriving: the first failed health window after a healthy one.
    pub fn media_stopped(&mut self, at: Instant) {
        keep_latest(&mut self.media_stopped_at, at);
    }

    /// True while at least one signal is still inside the window.
    pub fn has_fresh_signal(&self, now: Instant, window: Duration) -> bool {
        self.fresh_signals(now, window).next().is_some()
    }

    /// The verdict form of this evidence. `None` means "not enough to blame
    /// another controller", and the caller must keep the generic fault.
    pub fn classify(&self, now: Instant, window: Duration) -> Option<Verdict> {
        let signals: Vec<(Signal, Instant)> = self.fresh_signals(now, window).collect();
        if signals.is_empty() {
            return None;
        }
        let mut corroboration = Vec::new();
        if let Some(at) = self.association_at
            && signals.iter().any(|(_, signal)| near(*signal, at, window))
        {
            corroboration.push("association_changed");
        }
        if let Some(at) = self.media_stopped_at
            && signals.iter().any(|(_, signal)| near(*signal, at, window))
        {
            corroboration.push("media_stopped");
        }
        if corroboration.is_empty() {
            return None;
        }
        Some(Verdict {
            signals: signals.iter().map(|(signal, _)| signal.name()).collect(),
            corroboration,
        })
    }

    fn fresh_signals(
        &self,
        now: Instant,
        window: Duration,
    ) -> impl Iterator<Item = (Signal, Instant)> + '_ {
        [
            (Signal::UnexpectedTeardown, self.teardown_at),
            (Signal::Forbidden, self.forbidden_at),
            (Signal::ConnectionClose, self.closed_at),
        ]
        .into_iter()
        .filter_map(move |(signal, at)| {
            let at = at?;
            (now.saturating_duration_since(at) <= window).then_some((signal, at))
        })
    }
}

/// Why the verdict was reached, carried into the warning so a log alone explains
/// the decision without a replay of the session.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Verdict {
    pub signals: Vec<&'static str>,
    pub corroboration: Vec<&'static str>,
}

impl Verdict {
    pub fn summary(&self) -> String {
        format!(
            "signals=[{}] corroboration=[{}]",
            self.signals.join(","),
            self.corroboration.join(",")
        )
    }
}

fn keep_latest(slot: &mut Option<Instant>, at: Instant) {
    *slot = Some(slot.map_or(at, |old| old.max(at)));
}

/// Absolute distance between two instants, without a panic when they arrive out
/// of order (media can stop just before the 403 that answers the next probe).
fn near(a: Instant, b: Instant, window: Duration) -> bool {
    a.saturating_duration_since(b)
        .max(b.saturating_duration_since(a))
        <= window
}

#[cfg(test)]
mod tests {
    use super::*;

    const TABLE_WINDOW: Duration = WINDOW;

    fn at(base: Instant, ms: u64) -> Instant {
        base + Duration::from_millis(ms)
    }

    #[test]
    fn every_signal_alone_is_only_evidence_not_a_verdict() {
        let base = Instant::now();
        for signal in [
            Signal::UnexpectedTeardown,
            Signal::Forbidden,
            Signal::ConnectionClose,
        ] {
            let mut evidence = Evidence::default();
            evidence.record(signal, at(base, 0));
            assert_eq!(
                evidence.classify(at(base, 500), TABLE_WINDOW),
                None,
                "{} alone must stay a generic fault",
                signal.name()
            );
        }
    }

    #[test]
    fn corroboration_without_a_signal_is_not_preemption() {
        let base = Instant::now();
        for corroborate in ["media", "association"] {
            let mut evidence = Evidence::default();
            if corroborate == "media" {
                evidence.media_stopped(at(base, 100));
            } else {
                evidence.association_changed(at(base, 100));
            }
            assert_eq!(
                evidence.classify(at(base, 500), TABLE_WINDOW),
                None,
                "{corroborate} alone must stay a generic fault"
            );
        }
    }

    #[test]
    fn a_signal_plus_corroboration_inside_the_window_is_preemption() {
        let base = Instant::now();
        for signal in [
            Signal::UnexpectedTeardown,
            Signal::Forbidden,
            Signal::ConnectionClose,
        ] {
            for corroborate in ["media", "association"] {
                let mut evidence = Evidence::default();
                evidence.record(signal, at(base, 1_000));
                if corroborate == "media" {
                    evidence.media_stopped(at(base, 2_000));
                } else {
                    evidence.association_changed(at(base, 0));
                }
                let verdict = evidence
                    .classify(at(base, 2_200), TABLE_WINDOW)
                    .unwrap_or_else(|| panic!("{} + {corroborate} must be preemption", signal.name()));
                assert_eq!(verdict.signals, vec![signal.name()]);
                let expected = if corroborate == "media" {
                    "media_stopped"
                } else {
                    "association_changed"
                };
                assert_eq!(verdict.corroboration, vec![expected]);
            }
        }
    }

    #[test]
    fn corroboration_outside_the_window_does_not_count() {
        let base = Instant::now();
        let mut evidence = Evidence::default();
        evidence.record(Signal::UnexpectedTeardown, at(base, 0));
        evidence.media_stopped(at(base, 4_000));
        assert_eq!(evidence.classify(at(base, 4_100), TABLE_WINDOW), None);
        let mut evidence = Evidence::default();
        evidence.record(Signal::Forbidden, at(base, 4_000));
        evidence.association_changed(at(base, 0));
        assert_eq!(evidence.classify(at(base, 4_100), TABLE_WINDOW), None);
    }

    #[test]
    fn an_expired_signal_does_not_pair_with_a_fresh_fact() {
        let base = Instant::now();
        let mut evidence = Evidence::default();
        evidence.record(Signal::UnexpectedTeardown, at(base, 0));
        evidence.media_stopped(at(base, 5_000));
        assert!(!evidence.has_fresh_signal(at(base, 5_000), TABLE_WINDOW));
        assert_eq!(evidence.classify(at(base, 5_000), TABLE_WINDOW), None);
    }

    #[test]
    fn an_unrelated_403_during_setup_is_never_preemption() {
        // The SETUP path never reaches this classifier; even if a lone 403 were
        // recorded here, it has no corroboration and must stay generic.
        let base = Instant::now();
        let mut evidence = Evidence::default();
        evidence.record(status_signal(403).unwrap(), at(base, 0));
        assert_eq!(evidence.classify(at(base, 100), TABLE_WINDOW), None);
        assert_ne!(status_signal(401), Some(Signal::Forbidden));
        assert_ne!(status_signal(454), Some(Signal::Forbidden));
    }

    #[test]
    fn our_own_teardown_response_is_not_an_incoming_teardown() {
        // `Member::close` sends TEARDOWN and reads `RTSP/1.0 200 OK`; only an
        // incoming request line is a signal.
        assert_eq!(incoming_teardown(None), None);
        assert_eq!(
            incoming_teardown(Some("TEARDOWN")),
            Some(Signal::UnexpectedTeardown)
        );
        let mut evidence = Evidence::default();
        // Nothing was recorded for our own teardown, so a later media stop has
        // nothing to pair with.
        evidence.media_stopped(Instant::now());
        assert_eq!(
            evidence.classify(Instant::now(), TABLE_WINDOW),
            None
        );
    }

    #[test]
    fn repeated_signals_keep_the_latest_timestamp() {
        let base = Instant::now();
        let mut evidence = Evidence::default();
        evidence.record(Signal::Forbidden, at(base, 0));
        evidence.record(Signal::Forbidden, at(base, 2_000));
        assert!(evidence.has_fresh_signal(at(base, 4_000), TABLE_WINDOW));
        assert!(!evidence.has_fresh_signal(at(base, 5_100), TABLE_WINDOW));
    }
}
