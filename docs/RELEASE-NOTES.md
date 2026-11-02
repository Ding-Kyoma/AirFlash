# AirFlash 0.6.0

Changes since AirFlash 0.5.4.

## Features

- Add an optional **Force fixed latency** policy for global and per-receiver settings. Software stereo groups require the same forced target on every member.
- Distinguish the requested latency target, the engine's scheduling plan, and the receiver's reported minimum in the control panel.
- Send available Windows Now Playing information to receivers and forward supported receiver playback commands to Windows media controls.
- Add JSON diagnostic export with network identifiers and file paths redacted, alongside more detailed session, timing, and recovery metrics.

## Bug Fixes

- Keep Stop and Cancel responsive when engine commands are blocked, and limit cleanup time for an unresponsive engine process.
- Recover from missing latency-change acknowledgements even when a system timer wakes before the confirmation deadline.
- Yield when another controller takes over a receiver. When session ownership cannot be confirmed, restore local audio, pause automatic recovery, and offer manual reconnect.
- Check that receivers are available before automatic reconnection, and replenish retry budgets only after sustained, verified receiver health.
- Correct PTP clock-offset calculation and reject stale or reordered timing samples.
- Recover failed event channels without restarting audio, and rebind failed audio capture where possible. Reconnect when buffered playback can no longer use its existing timeline.
- Preserve existing stereo groups during settings migration and report known timing incompatibilities before playback.
- Keep application capture bound to the selected process instance and avoid selecting an unrelated instance when a restarted application is ambiguous.
- Default new and reset settings to **Normal · 200 ms**. Migrate the retired adaptive latency setting to Normal while preserving other fixed modes and custom targets.
- Show localized Home app speaker-access guidance for pairing failures with `TLV error [2]`.

Latency targets describe sender scheduling, not measured end-to-end acoustic latency. A forced target below the receiver's reported minimum may cause playback to fail or remain silent.

These changes have automated and localhost simulation coverage. No real HomePod playback test was performed for this release.

Download **AirFlash-0.6.0.msi** or portable **AirFlash.exe**; verify with **SHA256SUMS.txt**. Binaries are unsigned.
