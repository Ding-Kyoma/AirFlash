# Unreleased

- Add experimental support for one third-party AirPlay 2 receiver, preserving the HomePod stereo path and existing settings and credentials.
- Preserve AirPlay/RAOP service endpoints and capabilities, read `/info` before choosing authentication, clock, format, and transport, and provide actionable pairing/access errors.
- Add buffered TCP/type 103 alongside realtime UDP/type 96, automatic NTP/PTP and ALAC/PCM negotiation, bounded backpressure, playback anchors, and one fresh-connection fallback after supported setup rejection.
- Add per-receiver clock/transport overrides and adopted-buffer/format diagnostics. Third-party default buffering is 3 seconds, bounded to 10 seconds; acoustic latency remains unknown.
- Cover protocol order, independent media decoding, cancellation, sequence/nonce wrap, optional API rejection, HomePod stereo, and short localhost TCP stability in automated tests.
- Third-party hardware qualification is pending. Access passwords, home/current-user restrictions, and cross-brand multi-device synchronization are not supported.

# AirFlash 0.3.0

Windows x64 · Windows 10/11 · English and Simplified Chinese

## Features

- Choose a local network adapter for AirPlay discovery in Settings → Network. Discovery pauses when the selected adapter is unavailable and resumes when it reconnects; available adapters are listed first.
- Adjust a global ten-band equalizer with presets, preamp gain, and automatic attenuation to reduce clipping risk. Changes are previewed during playback; Apply saves them, while Cancel or closing Settings restores the saved sound.
- Support development through the coffee button in the Simplified Chinese About page.

## Bug Fixes

- Improve application, window, and tray icon clarity on high-DPI displays, including DPI changes between monitors.
- Recover from failed tray icon registration with a fallback identity and retries, and restore registration after Explorer restarts.
- Reconcile compatible AirPlay and RAOP discovery records to reduce duplicate receiver entries and preserve saved receiver settings and pairing associations when discovery identities change.
- Avoid restarting active playback for receiver metadata changes that do not change the connection or playback settings.

## Downloads

- Download **AirFlash-X.Y.Z.msi** to install, or **AirFlash.exe** for portable use. Verify downloads with **SHA256SUMS.txt**.
- Check for stable updates in Settings → About.
- Binaries are unsigned. Target latency is not a measurement of end-to-end acoustic latency.
