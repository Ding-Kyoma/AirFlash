# AirFlash 0.4.0

Windows x64 · Minimum target: Windows 10 1809 / build 17763 · Windows 11
English, Simplified Chinese, and Traditional Chinese

Changes since AirFlash 0.3.0.

## Features

- Capture audio from recording/input endpoints as well as playback devices in Settings → Audio capture → Specific audio endpoint. Device labels identify their type; Voicemeeter users can select the recording endpoint for their B1/B2/B3 bus. Recording inputs are never muted by the local-speaker mute option. Resolves [#9](https://github.com/Ding-Kyoma/AirFlash/issues/9).
- Add Traditional Chinese localization, selected from the Windows UI language preferences.
- Pause AirPlay discovery when both the control panel and Settings → Receivers are hidden, retaining previous results and active playback. Automatic connection gets a ten-second discovery window at startup; opening a receiver list resumes discovery.
- Collect startup diagnostics with Windows PowerShell 5.1 when AirFlash cannot launch, and use `--compat-check --output <file>` to check the OS baseline, DNS-SD availability, and engine extraction/handshake without pairing or playback. See [Windows compatibility and startup diagnostics](https://github.com/Ding-Kyoma/AirFlash/blob/main/docs/WINDOWS-COMPATIBILITY.md).

## Bug Fixes

- Keep active AirPlay playback and its Stop control when discovery temporarily loses a receiver or returns an incomplete stereo group. Refresh the session after delayed identity persistence and skip passive receiver updates during pairing. These changes address the discovery-loss behavior reported in [#6](https://github.com/Ding-Kyoma/AirFlash/issues/6) and part of [#14](https://github.com/Ding-Kyoma/AirFlash/issues/14).
- Cancel native DNS-SD browse and resolve queries when discovery pauses, and release expired or removed service queries, preventing discovery queries from accumulating across scans.
- Keep Settings content clear of overlaid scrollbars. Mouse-wheel input over equalizer bands scrolls the page without changing band gains.
- Render control-panel icons with vector geometry so playback controls do not depend on installed symbol fonts.
- Keep the control panel, tray, and Settings available if the audio engine fails to initialize, with a retry action and diagnostics. Log early startup and engine launch failures, and report discovery, audio endpoint, and autostart initialization errors without blocking the shell.
- Skip Windows 11-only window attributes on Windows 10. Reject new MSI installations below build 17763 while preserving maintenance, uninstall, and user data for existing installations.
- Validate the selected .NET SDK before development or release builds reserve a version or clear existing build output.

## Known Limitations

- Windows 10 startup, discovery, pairing, and playback still require affected-user verification. The original reported startup failure has not been reproduced or assigned a confirmed cause.
- The discovery fix does not establish the cause of every HomePod disconnection. Intermittent crackling/audio gaps ([#7](https://github.com/Ding-Kyoma/AirFlash/issues/7)), socket timeouts ([#16](https://github.com/Ding-Kyoma/AirFlash/issues/16)), and the remaining delayed-session lifecycle cases in #14 remain open.
- Per-application audio capture ([#18](https://github.com/Ding-Kyoma/AirFlash/issues/18)) is on the feature plan.

## Downloads

- Download **AirFlash-0.4.0.msi** to install, or **AirFlash.exe** for portable use. Verify downloads with **SHA256SUMS.txt**.
- Check for stable updates in Settings → About.
- Binaries are unsigned. Target latency is not a measurement of end-to-end acoustic latency.
