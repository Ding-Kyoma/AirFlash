# Playback retention during discovery loss

## Report and diagnosis

On 2026-10-11, a Windows user reported that AirFlash 0.3.0 intermittently
disconnected from a two-HomePod mini stereo pair during continuous music.
No other device was taking over playback. The user had previously moved from
TuneBlade after its initial AirPlay negotiation was rejected; that compatibility
problem is separate from this AirFlash change.

Inspection found that `AppViewModel.ReconcileDiscoveryAsync` called
`Session.StopAsync()` when the current discovered receiver was missing or a
stereo group was incomplete. Discovery observations describe availability for
browsing; absence alone does not establish that an existing transport has failed.
Stopping here can also interrupt the session controller's existing recovery.

An isolated test reproduced the defect on both v0.3.0 (`b50b6ae`) and upstream
main (`d4aa0686fc411de6655aac313243f0f6bf71e439`): after a mock engine reported
Streaming, publishing an empty discovery list failed the assertion
`all adapters, single: empty discovery retains the working transport`.
The upstream control build contained only the new test and its startup entry;
its production session/discovery code was unchanged. The test exited with code 1.

This is related to [#6](https://github.com/Ding-Kyoma/AirFlash/issues/6) and the
discovery-loss portion of [#14](https://github.com/Ding-Kyoma/AirFlash/issues/14).
The broader lifecycle proposal in [#15](https://github.com/Ding-Kyoma/AirFlash/pull/15)
also covers this area. This narrow patch does not implement all of that proposal
or claim to resolve every cause of intermittent disconnects.

## Changes

- Do not stop an active transport solely because its discovery record is absent
  or a stereo discovery record has only one member.
- Keep the active receiver's row available, including when an explicitly selected
  discovery adapter removes the record from the catalog. Retain its Stop action;
  remove an offline row after the session becomes inactive. User-hidden rows
  retain their existing visibility policy; the global Stop command remains available.
- Read the current session snapshot after awaited identity persistence, and avoid
  retargeting an ongoing Pairing operation from passive discovery updates. This
  is not a general solution for all delayed-work ownership races in #14.
- Log native Streaming confirmation with target latency and retry count, to
  distinguish a confirmed recovery from merely starting a worker process.
- Update the existing stereo regression assertion and add `--stability-check`.

The native engine, wire protocol, pairing credentials, settings schema, and
reconnect policy are unchanged. No watchdog, scheduled restart, firewall rule,
or audio-endpoint reconfiguration is introduced.

## Reproduce the automated check

From the repository root on Windows with the .NET 10 SDK:

```powershell
pwsh scripts/dotnet.ps1 restore AirFlash.sln --locked-mode
pwsh scripts/dotnet.ps1 test AirFlash.sln '-p:RestoreLockedMode=true'
pwsh scripts/dotnet.ps1 run --project AirFlash.App/AirFlash.App.csproj --no-restore -- --stability-check --output ../artifacts/discovery-stability.json
```

The final command is also included in CI. It uses the existing simulated
discovery, audio, autostart, settings, and engine fixtures. It does not access
real speakers, use production settings, or require an embedded native engine.
Success produces `ok: true`, 24 named checks, and exit code 0.

The matrix covers single/stereo receivers with all adapters/one selected adapter:
empty discovery, incomplete stereo discovery, retained Stop controls, rediscovery
without duplicate workers, explicit Stop releasing the engine, and no automatic
restart after explicit Stop. These tests exercise simulated Streaming sessions;
they do not qualify every native failure or all session-state interleavings.

## Local validation and limits

The patch adapted to upstream `d4aa068` built in Release with .NET SDK 10.0.401
with zero warnings/errors. All 165 Core tests, 342 WPF checks with Chinese UI,
and 24 isolated stability checks passed. The original-runtime control described
above failed as expected. Native/Rust tests and MSI lifecycle tests were not
rerun for this desktop-only contribution.

The original v0.3.0-based local repair passed 163 Core tests, 149 existing WPF
checks, and 24 new stability checks. The published executable passed the same
24 stability checks and the native-engine hello check. The original executable
and user configuration were backed up locally before replacement; the stored
configuration and native engine were unchanged.

During the user's playback validation, both stereo members sent 12,193 media
packets in an approximately 89-second observation, with zero media/sync send
errors. One requested retransmission was served. A controlled termination of
the owned native worker was followed by a new Streaming confirmation after
approximately 2.83 seconds on retry 1. The user subsequently confirmed audible
output from both speakers and no further disconnection at that point.

These hardware observations belong to the v0.3.0-based local repair, not a new
hardware qualification of upstream main. They are short observations, not a
long-duration reliability guarantee or an acoustic latency measurement.

Raw logs, settings, pairing data, device identifiers, LAN addresses, machine
paths, and unsigned local binaries are intentionally excluded from this report
and contribution. The committed sources and isolated checks are the reproducible
repair artifacts. Local deployment can be rolled back using the backed-up
executable without changing user settings.
