# AirFlash 0.7.0

Changes since AirFlash 0.6.3.

## Features

- Add multi-room groups: group multiple receivers in **Settings > Receivers** for synchronized playback, with a shared group sample rate, per-speaker offsets up to ±500 ms, independent hardware volume, and per-receiver codec and transport profiles.
- Check for a newer release at startup, enabled by default and configurable in Settings, and show an **Update** entry in the control panel when one is available.
- Restore each receiver's last audible hardware volume when a session starts, including per-speaker values for software stereo and multi-room groups.
- Add Spanish, French, Italian, Japanese, Korean, Brazilian Portuguese, and Russian UI languages, and make Traditional Chinese selectable. AirFlash now offers 11 interface languages.
- Show localized donation QR codes: WeChat and Alipay for Chinese, PayPal for other interface languages.
- Add an opt-in Sound Glow experiment in Settings, above About, that sends test song information and artwork only when explicitly requested. It is not an official Apple lighting interface and may not change the lights on every model or firmware.
- Move **Audio capture** directly below **General** in Settings, and collapse the experimental stream and clock status on the monitor page into a footer.

## Bug Fixes

- Fix sessions failing with `clock_not_ready` on HomePod mini 2 (AudioAccessory11,1) by sending the timing-peer (SETPEERSX) form so the PTP exchange completes.
- Stop treating clock or feedback symptoms as proof that another controller owns the receiver. A locally held session now survives them, ownership stays latched after clock recovery, and only a receiver-initiated TEARDOWN outvotes the sender. A PTP path that stays silent for 30 seconds now ends with the retryable `clock_lost` instead of a permanent reconnect prompt.
- Avoid false ownership preemption when the receiver's clock offset jumps.
- Keep an authorized recovery sequence running through transient host and network errors instead of aborting after the first failed reconnect attempt.
- Stop a paused or muted audio endpoint from reopening capture every 2.4 seconds, which caused clicks and stutter after several minutes. The silence probe now requires a stream that delivers no packets and ten seconds of continuous silence, re-arms after thirty seconds of audio, and backs off across repeated probes. Three rebinds within a minute now raise a `capture_rebind_storm` warning.
- Retry one media send when the socket buffer is temporarily full instead of reporting an error immediately.
- Extend transport metrics with the receiver's PTP offset, round trip, jitter and last-exchange age, log a session health line once per minute, name the host in clock recovery warnings, and warn when a fixed latency target no longer matches the measurements.
- Hide receiver volume controls while a receiver is disconnected, and render receiver volume status as fixed inline icons so longer translations no longer resize or truncate them.
- Keep the master volume slider full width in languages with longer labels.

Automated .NET, native, Python and UI checks cover these changes, including localhost receivers for the Sound Glow experiment. Multi-room synchronization, HomePod light output and the HomePod mini 2 timing change depend on receiver and firmware behavior and were not verified with real HomePods for this release.

Download **AirFlash-0.7.0.msi** or portable **AirFlash.exe**; verify with **SHA256SUMS.txt**. Binaries are unsigned.
