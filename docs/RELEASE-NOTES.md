# AirFlash — Windows 10 compatibility candidate

Windows x64 · Minimum target: Windows 10 1809 / build 17763 · Windows 11

Windows 10 startup, discovery, pairing and playback still require affected-user verification.
The original startup failure has not yet been reproduced or assigned a confirmed cause.

## Features

- Collect startup diagnostics with Windows PowerShell 5.1, even when AirFlash cannot launch. Reports include OS/build, executable hash, process errors, isolated host traces and related application events, with user paths and labelled secrets redacted.
- Run `--compat-check --output <file>` to check the OS baseline, DNS-SD availability and engine extraction/handshake without pairing, playback, mute or autostart changes.
- Audit actual bundled runtime libraries and the embedded engine before single-file packaging. Reports identify named, ordinal and delayed imports and retain unconfirmed APIs for review.

- Choose a local network adapter for AirPlay discovery in Settings → Network. Discovery pauses when the selected adapter is unavailable and resumes when it reconnects; available adapters are listed first.
- Adjust a global ten-band equalizer with presets, preamp gain, and automatic attenuation to reduce clipping risk. Changes are previewed during playback; Apply saves them, while Cancel or closing Settings restores the saved sound.
- Support development through the coffee button in the Simplified Chinese About page.

## Bug Fixes

- Keep active AirPlay playback and its Stop control when discovery temporarily loses a receiver or a stereo member. Refresh the session after delayed identity persistence and skip passive receiver updates during pairing.

- Keep the control panel, tray and settings available when the engine fails to initialize; disable playback and pairing until a successful retry, and coordinate retry with shutdown.
- Log startup stages before WPF resource initialization, plus engine launch errors, exit codes and bounded stderr.
- Skip Windows 11 DWM attributes on Windows 10 and report discovery, audio endpoint and autostart initialization failures without blocking the shell.
- Reject new MSI installations below build 17763 while preserving existing maintenance, uninstall and user data.

- Improve application, window, and tray icon clarity on high-DPI displays, including DPI changes between monitors.
- Recover from failed tray icon registration with a fallback identity and retries, and restore registration after Explorer restarts.
- Reconcile compatible AirPlay and RAOP discovery records to reduce duplicate receiver entries and preserve saved receiver settings and pairing associations when discovery identities change.
- Avoid restarting active playback for receiver metadata changes that do not change the connection or playback settings.

## Downloads

- Download **AirFlash-X.Y.Z.msi** to install, or **AirFlash.exe** for portable use. Verify downloads with **SHA256SUMS.txt**.
- Check for stable updates in Settings → About.
- Binaries are unsigned. Target latency is not a measurement of end-to-end acoustic latency.
