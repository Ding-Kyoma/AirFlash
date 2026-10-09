# <img src="desktop/AirFlash.App/Assets/app.svg" width="36" height="36" alt="" style="vertical-align:middle" /> AirFlash

<p align="center">
  <a href="README.md">English</a> ·
  <a href="docs/README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <img src="docs/images/airflash-en.png" alt="AirFlash English interface" width="550" />
</p>

Stream Windows desktop audio through a native AirPlay 2 sender. AirFlash supports
HomePod, including existing two-device stereo pairs and HomePod OS 27, and adds
experimental support for a single third-party AirPlay 2 receiver.

AirFlash is an AirPlay 2 alternative to TuneBlade (AirPlay 1), designed to bring
Windows audio streaming to HomePods running newer versions of HomePod software.

## Installation

Download the latest release from [GitHub Releases](https://github.com/Ding-Kyoma/AirFlash/releases/latest), run
`AirFlash-X.Y.Z.msi`, and launch AirFlash from the installed shortcut. Apps and Features provides repair and uninstall.

AirFlash is distributed as a Windows x64 executable and standalone MSI. Windows may show
a SmartScreen warning because release binaries are not code-signed. Use
**More info** and **Run anyway** only when the downloaded files are from a
source you trust.

## Key features

- Streams Windows system audio over AirPlay 2, with experimental third-party receiver support.
- Supports synchronized playback on existing two-device HomePod stereo pairs.
- Offers automatic device discovery, PIN pairing, and manual device setup.
- Lets you filter device discovery by selecting a network adapter in Settings.
- Includes selectable latency modes, tray operation, mute restoration,
  automatic reconnection, and connection diagnostics.
- Negotiates realtime UDP or buffered TCP, NTP/PTP timing, and 16-bit stereo
  ALAC/PCM. Per-receiver transport and clock overrides are available in Settings.
- Provides a ten-band equalizer with presets and live preview in Settings.
- Available as a portable Windows x64 app with English and Simplified Chinese UI.

## Feature plan

- [ ] Dolby Atmos support.

## Usage

1. Connect the PC and receiver to the same reachable local network.
2. Start AirFlash and wait for the receiver list to populate.
3. Select a receiver, or the complete `HomePod stereo · 2/2` entry, and press play.
4. Complete PIN pairing in **Settings > Receivers** if the receiver requests it.
5. Choose the capture endpoint and latency profile in Settings when the defaults
   are not suitable.

AirFlash intentionally uses fresh application storage under `%APPDATA%/AirFlash`
and `%LOCALAPPDATA%/AirFlash`. It does not read configuration, credentials, or
engine caches created by the previous product name; users must configure and pair
again after installing AirFlash.

Use **Settings > About > Check for updates** to check for a stable release and open its download page. Checks run only when requested.

HomePod latency presets are preserved. Third-party receivers use a 3-second
compatibility buffer by default, with a sender limit of 10 seconds. Automatic
mode prefers buffered TCP when buffered audio and PTP are advertised; otherwise
it uses realtime UDP. Unknown capabilities start with NTP and ALAC at 44.1 kHz.
An explicit per-receiver latency or global custom/realtime setting overrides the
compatibility default. Settings → Monitor shows the requested latency, adopted
buffer, actual format, transport, and clock. Acoustic latency remains unmeasured.

No third-party model has been hardware-verified by this change. Access passwords,
home/current-user restrictions, and cross-brand multi-device playback are outside
this release's scope. An AirPlay 2 logo alone does not guarantee compatibility.
See [Compatibility and hardware qualification](docs/AIRPLAY-COMPATIBILITY.md).

See [Release workflow](docs/RELEASING.md) for version reservation and publishing.

See [Icon maintenance](docs/ICONS.md) for the vector source, generated resources, and high-DPI verification.

## Stack

- **C# / .NET 10 / WPF** for the Windows UI, tray integration, settings, and discovery
- **Rust** for WASAPI capture, AirPlay 2 sessions, HAP, PTP, RTP, and audio transport
- **Windows DNS-SD** for receiver discovery
- **WASAPI loopback** for system audio capture
- **PCM and ALAC** with encrypted RTP over UDP or TCP
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
pwsh scripts/build.ps1
```

The build produces:

- `dist/AirFlash.exe`
- `dist/AirFlash-X.Y.Z.msi`

Run `AirFlash-X.Y.Z.msi` directly. It installs the self-contained executable and provides optional desktop and Start menu shortcuts.
AirFlash uses the Windows DNS-SD API and does not require Bonjour or install a background service.
The Rust engine is embedded in the WPF executable and extracted to a content-addressed cache under `%LOCALAPPDATA%/AirFlash/engine/<hash>` at runtime.

## Tests

```powershell
pwsh scripts/dotnet.ps1 test AirFlash.sln
uv run pytest
uv run ruff check .
pwsh scripts/build-native.ps1 -Check
```

Rust stability checks use localhost UDP/TCP receivers, including a short TCP test
and ignored 30-minute soaks. Real receiver tests use the repository's low-volume,
five-second procedure; simulated success does not certify audible playback.

## Contributing

Keep protocol behavior, pairing boundaries, and measurement limits explicit in
changes. Pull requests are welcome!

## License

[GPLv3+](LICENSE-GPLv3) or [Commercial License](LICENSE-COMMERCIAL.md).
