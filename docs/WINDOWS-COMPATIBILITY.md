# Windows compatibility and startup diagnostics

AirFlash targets Windows x64, Windows 10 version 1809 / build 17763 or newer.
The qualification matrix is LTSC 2019 (17763), LTSC 2021 (19044), Windows 10 22H2
(19045), and Windows 11. The application is self-contained; users do not need to
install .NET, Python, PowerShell 7, or a VC++ Redistributable.

This change is a compatibility candidate. Static audit and Windows 11 regression
checks do not establish successful execution on Windows 10. The reported user's
startup failure has not yet been reproduced or assigned a confirmed cause.

## What changed

- The application logs before WPF resource initialization. Initialization failures
  include their stage and a diagnostic location.
- The control panel, tray and instance-activation listener start before the engine
  probe. A failed engine does not close the application. Playback, pairing and
  automatic connection stay disabled until a successful probe; Retry audio engine
  repeats the bounded probe without changing saved settings or credentials.
- Optional Windows 11 DWM window attributes are skipped on Windows 10. Native
  DNS-SD availability is checked; discovery failures are recoverable and reported.
- The MSI enforces build 17763 or newer for new installations. Maintenance and
  uninstall of an existing installation remain available.

Font and icon changes are maintained separately and are not part of this patch.

## Collect diagnostics on an affected PC

Download the candidate EXE/MSI and `collect-startup-diagnostics.ps1` from the same
verified build. Open Windows PowerShell and run, substituting your actual paths:

```powershell
powershell.exe -NoProfile -File .\collect-startup-diagnostics.ps1 -Executable "C:\Program Files\AirFlash\AirFlash.exe" -OutputDirectory "$env:USERPROFILE\Desktop"
```

No administrator session is required. If script execution is restricted by an
organization, use its approved script execution process; do not disable security
software or change machine-wide execution policy.

The script creates a uniquely named report directory and ZIP. It runs
`--compat-check --output <file>`, collects process-local .NET host traces and
relevant application events, and exports only startup log lines from that check.
It bounds collected text, redacts user paths and labelled secrets, and excludes
settings and pairing files. Review the ZIP before sharing it.

The compatibility check verifies system architecture, native DNS-SD exports,
engine extraction and the JSONL v1 hello. It does not discover devices, pair,
play sound, change local mute, or register autostart. A successful report therefore
does not qualify those features. Normal logs are in `%APPDATA%\AirFlash\logs`.

## Build and audit evidence

The native build audits its actual PE import table. A publish-time target audits
the real `FilesToBundle` inputs before bundling, plus the embedded Rust engine.
Reports record file hashes, named and ordinal imports, delay imports, managed
loader thunks, and source P/Invoke/dynamic-loading calls.

`artifacts/compatibility/bundle-audit.json` is linked to the final EXE hash and
version by the release build. `engine-audit.json` audits the engine separately;
`host-check.json` records the release host's compatibility check. CI retains these
reports as artifacts; the release workflow also retains the diagnostic collector.

The checked-in API catalog links individual entries to Microsoft documentation.
Unknown exports, ordinal imports and dynamic-loading sites remain **unconfirmed**.
DLL presence and PE version headers are not proof of API availability. Known
mandatory native imports above the baseline, invalid images and a dynamic engine
CRT block the build. Delay-loaded newer APIs still require call-site review;
neither unknown nor delayed entries are certified by a passing audit.

## Windows 10 acceptance checklist

Record the candidate SHA256, exact Windows build/UBR and the diagnostic ZIP.
Verify MSI installation and normal launch, tray hide/show and second-launch
activation, opening settings, automatic receiver discovery, PIN pairing where
required, playback and stopping with the original local mute restored. Test the
portable EXE as well if installation and portable startup differ.

Do not report full compatibility if only manual receiver entry works. Confirm
LTSC 2019, LTSC 2021 and 22H2 separately. The existing installer lifecycle tests
remain restricted to disposable GitHub-hosted runners, never a user's PC.

Real HomePod qualification follows the repository's existing five-second,
low-volume `scripts/e2e_stream.py` procedure. No acoustic or latency claim follows
from a successful hello, simulated UI check or static import audit.

References: [.NET 10 supported systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md),
[DWM attribute requirements](https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute),
[Windows DNS-SD](https://learn.microsoft.com/windows/win32/api/windns/nf-windns-dnsservicebrowse).
