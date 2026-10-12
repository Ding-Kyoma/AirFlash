# Playback retention during discovery loss

Discovery records describe receiver availability for browsing. An absent mDNS
record, or an incomplete stereo discovery group, does not by itself establish
that an existing AirPlay transport has failed. The desktop leaves disconnect
detection and recovery to the transport controller, retains the active receiver
row and Stop action, and removes an unavailable row after Stop. Offline or
incomplete receivers remain ineligible for new automatic playback.

## Integration and scope

[PR #19](https://github.com/Ding-Kyoma/AirFlash/pull/19), contributed by DANVEZ,
reproduced discovery-triggered stopping on its baseline `d4aa068`. Main later
added transport retention and active-row handling independently. An isolated
review reproduced the failure on `d4aa068`; the original 24-check matrix passed
on both PR head `5f1dc47` and main `937fa34` before this integration.

This integration preserves main's discovery demand, startup and compatibility
checks and incorporates these additional protections and diagnostics:

- Refresh the current session snapshot after awaited identity persistence.
- Skip passive receiver updates while the current session is Pairing.
- Read the live session when retaining active controls and mark a synthetic
  retained receiver row unavailable to discovery.
- Log native Streaming confirmation with target latency and retry count.
- Add an isolated discovery stability command to CI and strengthen the existing
  stereo UI assertion to verify that the engine remains alive.

The work relates to [#6](https://github.com/Ding-Kyoma/AirFlash/issues/6) and the
discovery-loss portion of [#14](https://github.com/Ding-Kyoma/AirFlash/issues/14).
Reading a fresh snapshot and skipping Pairing are bounded protections; they do
not provide serialized lifecycle ownership checks for all delayed work in #14.
The native engine, protocol, credentials, settings schema and reconnect policy
are unchanged.

## Reproduce the automated checks

Run from the repository root on Windows with the .NET 10 SDK:

```powershell
pwsh scripts/dotnet.ps1 restore AirFlash.sln --locked-mode
pwsh scripts/dotnet.ps1 test AirFlash.sln '-p:RestoreLockedMode=true'
pwsh scripts/dotnet.ps1 run --project AirFlash.App/AirFlash.App.csproj --no-restore -- --stability-check --output ../artifacts/discovery-stability.json
```

The stability check uses simulated discovery, audio, autostart, settings and
engine services with isolated data. It does not access real speakers or require
an embedded native engine. Success produces `ok: true`, 28 named checks and
exit code 0.

The original 24 checks cover single/stereo receivers with all adapters or one
selected adapter: empty or incomplete discovery, retained Stop controls,
rediscovery without duplicate workers, explicit Stop releasing the engine and
removing unavailable rows, and no automatic restart after explicit Stop.
Four additional assertions hold identity persistence while a newer Streaming or
Pairing session takes over, then verify that discovery reconciliation preserves
its target and engine. Existing .NET tests cover transport error/retry behavior;
the simulated stability matrix does not qualify every native failure or every
session-state interleaving.

## Validation limits

The contributor's earlier hardware observations used a separate v0.3.0-based
repair. They are not hardware qualification of this main integration, a
long-duration reliability guarantee, or an acoustic latency measurement. The
integration is validated with automated and simulated checks. No raw logs,
credentials, device identifiers, LAN addresses or local binaries are committed.
