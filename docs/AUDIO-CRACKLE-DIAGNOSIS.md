# Crackles after a while: capture rebind storms

A session that starts clean, plays for minutes and then develops clicks,
crackles or stutter is usually described as a receiver or Wi-Fi problem. One
sender-side failure produces exactly that shape and is visible in the log.

## The signature

`capture_endpoint_reopened` is a recovery warning, not a fatal error, so a few
of them are unremarkable. A storm is hundreds of them at a fixed cadence:

```
2026-11-08T15:48:04.577 Engine warning: {... "code":"capture_endpoint_reopened",
  "message":"Capture stream reopened after silent_probe (1 instance(s))" ...}
2026-11-08T15:48:06.876 ...
2026-11-08T15:48:09.230 ...
```

A 2.3–2.4 s interval means the silence probe re-armed itself after each rebind.
Each cycle flushes the resampler input and replays the tail the loopback still
holds, which is audible as a tick or a short stutter, and the pipeline rebuild
also drops whatever the capture queue had buffered.

The same sessions usually show it in the session summary as well:

| session | duration | packets | retransmit requests | capture underruns |
| --- | --- | --- | --- | --- |
| `a611359f…` (100 ms target) | 390 s | 48 931 | 52 | 49 |
| `04491dad…` (120 ms target) | 1392 s | 174 384 | 29 | 141 |
| `78388f99…` (250 ms target) | 7307 s | 915 490 | 0 | 0 |

Long sessions without a storm stay at zero retransmits and zero underruns, so a
clean multi-hour session is not evidence that this defect is absent; it is
evidence that the storm did not happen in that session.

## Why it repeated

The 0.6.3 probe had two cheap re-arm paths:

1. any single non-zero sample cleared `last_probe`, restoring the probe budget;
2. the "a break already happened" gate stayed true forever after the first one.

The loop was therefore: silence → probe → rebind → the fresh binding delivers a
short burst of stale loopback audio (looks like "audio is back") → silence →
probe. The window was two seconds and the rebind adds roughly 0.35 s, which is
the 2.36 s cadence in the log.

A second false positive came from zero-filled packets: a paused player keeps
delivering digitally silent buffers, which the old code counted as silence. The
endpoint is alive in that state and reopening it recovers nothing.

## What changed

- Only a stream that delivers **no packet at all** for `NO_PACKET_SILENCE_AFTER`
  is probed. Zero-filled packets are ignored: they are live audio.
- The silence window is ten seconds of *contiguous* silence, and the probe only
  arms after thirty seconds of contiguous audible audio, so a short stale burst
  never restores the budget.
- Repeated probes back off along a 60 s / 5 min / 20 min ladder and reset only
  after sustained audio returns.
- Three rebinds inside a minute raise a single `capture_rebind_storm` warning
  every five minutes, and `rebind_window_max` records the worst minute.

## Reading the telemetry

`wpf-YYYY-MM-DD.log` now carries one line per minute in every latency mode:

```
Session health: uptime_s=120; target_ms=200; p99_ms=0.515; max_send_us=1301;
rtx_req=0; capture_underruns=0; capture_rebinds=0; silent_probes=0;
feedback_rtt_ms=5.2; clock_offset_us=123.5; clock_jitter_us=40.0
```

| Field | What it rules in |
| --- | --- |
| `p99_ms`, `max_send_us` | sender scheduling: a late sender shows up here first |
| `rtx_req` | packets missing at the receiver (link loss or arrival after the playout point) |
| `capture_underruns` | the capture queue ran dry: a real gap in what was sent |
| `capture_rebinds`, `silent_probes` | the probe path above |
| `feedback_rtt_ms` | receiver control-plane round trip; 4–6 ms is healthy on a LAN, tens of ms is congestion or power save |
| `clock_offset_us`, `clock_jitter_us` | receiver PTP offset and jitter; a jump here is a clock or link event |

`Fixed latency looks tight: …` repeats the same fields when a fixed target no
longer fits the evidence (`p99 >= 3 ms`, 20 retransmits in a minute, audio loss
together with a rebind, or `feedback_rtt >= 50 ms`). It is advisory only: a
forced or custom latency stays authoritative.

## What to collect from a report

1. The local time (to the minute) when the artifact started and stopped.
2. `%APPDATA%\AirFlash\logs\wpf-*.log` and `session-summary.csv`, before the log
   rotates.
3. Whether the same audio played from an iPhone AirPlay session also degrades,
   and whether a 500 ms target pushes the artifact much later. Both answers
   separate the receiver/link from the sender.
