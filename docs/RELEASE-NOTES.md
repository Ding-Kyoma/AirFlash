# AirFlash 0.5.0

Changes since AirFlash 0.4.0.

## Features

- Capture a selected application and its child processes on Windows build 20348 or later.
- Add optional silence standby: release the AirPlay session during silence and reconnect when audio returns.
- Add experimental two-receiver software stereo, independent receiver volume, and per-receiver codec, sample rate, transport, and clock settings.
- Add German UI and **Reset and restart** to clear settings and pairing.

## Bug Fixes

- Retain active playback during passive receiver updates and apply short fades during audio recovery.
- Preserve configuration backups during recovery and migration; report save failures instead of silently losing changes.
- Normalize pairing PINs, bound pairing waits, clean up canceled sessions, and report credential and engine errors accurately.
- Ensure newly packaged MSIs include the current executable instead of a cached build.

Software stereo and mixed-brand playback still require hardware validation. Standby reconnection may miss opening audio.

Download **AirFlash-0.5.0.msi** or portable **AirFlash.exe**; verify with **SHA256SUMS.txt**. Binaries are unsigned.
