# Discovery CPU and resource verification

AirFlash browses `_airplay._tcp.local` and `_raop._tcp.local` only while a receiver
list is visible, or during the ten-second automatic-connection startup window.
It keeps one resolve operation per service instance and cancels every browse and
resolve operation on suspension. Result callbacks free their result allocations;
they do not discard the native query's cancellation handle. Device removals and
PTR TTL expiry cancel individual resolvers. Cached records survive suspension and
receive a ten-second confirmation window when discovery resumes.

The opt-in `--discovery-perf` check uses real Windows DNS-SD without creating an
audio session, reading saved settings or credentials, or registering a tray icon:

```powershell
./scripts/dotnet.ps1 build AirFlash.App/AirFlash.App.csproj
& ./desktop/AirFlash.App/bin/Debug/net10.0-windows/win-x64/AirFlash.exe --discovery-perf --output "$PWD/artifacts/discovery-perf/report.json"
```

Allow roughly five to seven minutes. The check samples 150 seconds of active discovery,
opens and closes 25 additional sessions, then samples 120 seconds with discovery
suspended. The report contains per-second CPU percentages normalized by logical
processor count, thread and handle counts, private bytes, receiver counts, and
active native query counts. `all_queries_released` must be true, and hidden-phase
query counts must be zero. Compare stable samples after initial runtime startup;
OS thread-pool and runtime caches can retain threads and memory after cancellation.

Unit tests inject both a DNS-SD adapter and the raw Windows API boundary to cover
early and repeated callbacks, cancellation callbacks on another thread, startup
failure, cancellation retry, interface changes, PTR expiry and 100 open/close
cycles. The existing `--ui-smoke` mode additionally verifies demand shared between
the panel and receiver settings, the startup deadline, and playback continuity.

## Local verification, October 11, 2026

On a machine with 16 logical processors, the installed 0.3.0 process showed a
5.37% maximum and 0.99% average CPU during a 120-second baseline sample. The new
isolated discovery host averaged 0.031%, with a 0.097% maximum, during its
120-second suspended phase. All 25 additional sessions released their queries;
the hidden phase had zero active queries, and threads fell from 22 to 12.
The 150-second active phase retained both aggregated receivers and used at most
eight native queries (two browsers and six service resolvers).

These are discovery-host measurements, not a full playback benchmark. The existing
UI performance check also misses its cached-page 100 ms target on this machine:
approximately 227 ms p95 after this change versus 221 ms on unchanged HEAD.
The network page already refreshes adapter information synchronously; dispatcher,
apply-feedback, equalizer and endpoint-enumeration targets passed in both runs.
