# AirFlash 0.5.4

Changes since AirFlash 0.5.0.

## Features

- Show the configured custom latency in the control panel and open receiver settings from the latency display.
- Include per-receiver clock phases, packet counts, rejected-packet reasons, socket errors, and the audio engine path in PTP startup failure diagnostics.

## Bug Fixes

- Start PTP timing after session peer setup and retry lost clock subscriptions within the startup window.
- Answer peer-delay requests while following a receiver's clock.
- Match out-of-order Sync and Follow_Up messages without overwriting pending pairs or refreshing expired timing samples.
- Account for bounded advertised exchange intervals when waiting for clock readiness and detecting lost timing exchanges.

These PTP changes have automated and localhost simulation coverage. Confirmation on affected HomePod setups is still needed; network or firewall restrictions can still prevent timing traffic.

Download **AirFlash-0.5.4.msi** or portable **AirFlash.exe**; verify with **SHA256SUMS.txt**. Binaries are unsigned.
