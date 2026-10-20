# <img src="desktop/AirFlash.App/Assets/app.svg" width="36" height="36" alt="" style="vertical-align:middle" /> AirFlash

<p align="center">
  <a href="README.md">English</a> ·
  <a href="docs/README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <img src="docs/images/airflash-en.png" alt="AirFlash English interface" width="550" />
</p>

AirFlash supports existing two-device stereo pairs and low-latency playback from Windows.
Stream Windows desktop audio to HomePod through a native AirPlay 2 sender,
with support for HomePod OS 27.

AirFlash is an AirPlay 2 alternative to TuneBlade (AirPlay 1), designed to bring
Windows audio streaming to HomePods running newer versions of HomePod software.

## Installation

Download the latest release from [GitHub Releases](https://github.com/Ding-Kyoma/AirFlash/releases/latest), run
`AirFlash-X.Y.Z.msi`, and launch AirFlash from the installed shortcut. Apps and Features provides repair and uninstall.

The minimum target is **Windows x64, Windows 10 1809 / build 17763** (LTSC 2019),
including LTSC 2021, 22H2 and Windows 11. Windows 10 qualification is pending
affected-user testing. See [Windows compatibility and startup diagnostics](docs/WINDOWS-COMPATIBILITY.md)
for the candidate status and the PowerShell 5.1 diagnostic collector.

AirFlash is distributed as a Windows x64 executable and standalone MSI. Windows may show
a SmartScreen warning because release binaries are not code-signed. Use
**More info** and **Run anyway** only when the downloaded files are from a
source you trust.

For audio capture, we recommend **Settings > Audio capture > Specific audio endpoint**.
If you do not have an unused audio endpoint available, install a virtual endpoint
with [Virtual Audio Cable (VAC Lite)](https://software.muzychenko.net/freeware/vac471lite.zip)
or a similar tool, and route the audio you want to stream to that endpoint.

## Key features

- Streams Windows system audio to HomePod over AirPlay 2.
- Supports synchronized playback on existing two-device HomePod stereo pairs.
- Adds experimental two-receiver software stereo groups, independent hardware volume,
  per-receiver PCM/ALAC and UDP/TCP profiles, and 5 ms continuity transitions.
- Offers automatic device discovery, PIN pairing, and manual device setup.
- Lets you filter device discovery by selecting a network adapter in Settings.
- Includes selectable latency modes, tray operation, mute restoration,
  automatic reconnection, and connection diagnostics.
- Provides a ten-band equalizer with presets and live preview in Settings.
- Captures a selected application or playback/recording endpoints, including virtual audio devices.
- Releases AirPlay sessions after configurable silence and reconnects when audio returns.
- Available as a portable Windows x64 app with English, Simplified Chinese, Traditional Chinese, and German UI.

## Feature plan

See [audio continuity and schema v4](docs/audio-stereo-v3.md) for setup, protocol details,
simulation results and remaining hardware acceptance. Samsung, Mac and mixed-brand
playback and acoustic synchronization have not been qualified by this implementation.

- [ ] Dolby Atmos support.

## Usage

1. Connect the PC and both HomePods to the same reachable local network.
2. Start AirFlash and wait for the receiver list to populate.
3. Select the complete `HomePod stereo · 2/2` entry and press play.
4. Complete PIN pairing in **Settings > Receivers** if the receiver requests it.
5. Choose the capture endpoint and latency profile in Settings when the defaults
   are not suitable.

Stream formats are configured in **Settings > Receivers** for each receiver:
codec, sample rate, UDP/TCP transport, and clock strategy. Choices are filtered by
the receiver's `/info` capabilities without starting playback or pairing. Automatic
prefers a supported 44.1 kHz profile, then 48 kHz. Its label shows the expected
selection before playback and the negotiated selection while playing; unapplied
edits continue to show predictions. Offline or undetected receivers offer Automatic
and retain existing explicit settings as pending verification. Native stereo pairs
use their members' common capabilities. Software stereo groups control both members'
common sample rate and default to 44.1 kHz.

The control panel's **Global latency mode** changes the shared default. While the
current connecting, playing, or standby receiver uses an independent latency mode,
including either software stereo member, the control is disabled and points to
**Settings > Receivers**. Stopping playback restores it; the global default remains
editable in Settings.

**Settings > General > Reset and restart** asks once before clearing all settings
and receiver pairing. AirFlash stops playback and discovery, restores local mute,
disables startup at login, deletes `config.json` and `native-credentials`, and restarts
with defaults. Unsaved edits are discarded; logs and engine caches are retained.
Schema v4 removes the global sample rate: v1–v3 inherited rates become Automatic,
while explicit receiver and software group rates are preserved.

In **Settings > Audio capture**, **Specific audio endpoint** lists active playback
and recording devices with their type. For Voicemeeter, select the recording
endpoint corresponding to its B1/B2/B3 bus (for example, Voicemeeter Output)
and enable that bus for the desired audio in Voicemeeter. Playback devices use
loopback; recording devices capture their input directly. Automatic local mute
applies only to playback devices and never mutes a recording input.

**Specific application** captures the selected application and its child processes;
browsers include the complete process tree, not individual tabs. This native mode
requires Windows build 20348 or later. Older Windows versions retain endpoint
capture. The application selection is saved by executable path and follows a
restart when exactly one matching instance is running. If several independent
instances match, select an instance again. A missing application never falls back
to system audio.

To hear the target application only on HomePod while games and notifications stay
on your headphones, install a virtual cable such as [VB-CABLE](https://vb-audio.com/Cable/)
and use **Open Windows volume mixer** to route only that application's output to
**CABLE Input**. Select the application, or use **Specific audio endpoint → CABLE
Output · Recording**. Disable **Listen to this device** and any local monitoring.
Application capture never mutes your entire playback device. The manual Windows
routing remains after AirFlash stops; change it back when local playback is needed.
Virtual routing cannot guarantee capture of DRM-protected content, and capture,
resampling, and stream-format conversion do not guarantee bit-perfect output.

Enable **AirFlash streaming → Standby** to release the complete AirPlay session
after 5–300 seconds of silence (default duration: 10 seconds; disabled by default).
Capture continues locally, and detected audio automatically starts a new session.
Reconnection resumes current audio; its opening or short notification sounds may
be missed. HomePod manages its own power state after the session is released.

Receiver discovery runs while the control panel or **Settings > Receivers** is
visible, and remains active for software stereo topology changes during playback.
Otherwise it pauses when both are hidden. Previous results remain available;
pausing discovery does not interrupt playback. If automatic connection is enabled
globally or for a receiver, AirFlash also discovers devices for ten seconds at
startup. Open a receiver list to resume discovery afterwards.

AirFlash intentionally uses fresh application storage under `%APPDATA%/AirFlash`
and `%LOCALAPPDATA%/AirFlash`. It does not read configuration, credentials, or
engine caches created by the previous product name; users must configure and pair
again after installing AirFlash.

Use **Settings > About > Check for updates** to check for a stable release and open its download page. Checks run only when requested.

The real-time profile targets 120 ms; target and local transport timings are not measurements of end-to-end acoustic latency.

See [Release workflow](docs/RELEASING.md) for version reservation and publishing.

See [Icon maintenance](docs/ICONS.md) for the vector source, generated resources, and high-DPI verification.

## Stack

- **C# / .NET 10 / WPF** for the Windows UI, tray integration, settings, and discovery
- **Rust** for WASAPI capture, AirPlay 2 sessions, HAP, PTP, RTP, and audio transport
- **Windows DNS-SD** for receiver discovery
- **WASAPI loopback** for system audio capture
- **PCM and ALAC** with encrypted RTP for HomePod playback
- **uv, pytest, and Ruff** for the finite validation tools and Python checks

The published application is self-contained and statically links the native MSVC runtime. It does not require Python, a separately installed .NET runtime, or a separately installed VC++ Redistributable.

## Development and build

Development requires Windows 10/11 x64, the .NET 10 SDK, the Rust MSVC toolchain,
and Visual Studio C++/Windows SDK components. Python 3.12+ and `uv` are used for
validation scripts only.

```powershell
uv sync --locked
pwsh scripts/build-native.ps1 -Check
pwsh scripts/dotnet.ps1 restore AirFlash.sln --locked-mode
pwsh scripts/dotnet.ps1 run --project AirFlash.App/AirFlash.App.csproj
pwsh scripts/build-dev.ps1
```

The development build produces:

- `dist/dev/AirFlash.exe`
- `dist/dev/AirFlash-X.Y.Z-dev.<commit>.msi`

The development package is labeled from the latest stable `vX.Y.Z` tag and the
current commit. It does not reserve a release version or push a tag. Uninstall an
existing same-version AirFlash package before installing a development MSI.

Use `pwsh scripts/build-release.ps1` only when performing a release; it requires a
clean release branch, reserves the next numeric version, and writes `dist/`.

Run the MSI directly. It installs the self-contained executable and provides optional desktop and Start menu shortcuts.
AirFlash uses the Windows DNS-SD API and does not require Bonjour or install a background service.
The Rust engine is embedded in the WPF executable and extracted to a content-addressed cache under `%LOCALAPPDATA%/AirFlash/engine/<hash>` at runtime.

## Tests

```powershell
pwsh scripts/dotnet.ps1 test AirFlash.sln
uv run pytest
uv run ruff check .
pwsh scripts/build-native.ps1 -Check
```

The ignored Rust soak test uses localhost UDP receivers only. Real HomePod tests
are intentionally limited to the repository's low-volume, five-second procedure.

See [Discovery stability](docs/DISCOVERY-STABILITY.md) for the isolated playback
retention regression check and repair validation notes.

## Contributing

Keep protocol behavior, pairing boundaries, and measurement limits explicit in
changes. Pull requests are welcome!

## License

[GPLv3+](LICENSE-GPLv3) or [Commercial License](LICENSE-COMMERCIAL.md).
