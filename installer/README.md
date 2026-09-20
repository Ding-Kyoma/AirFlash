# Installer

The installer is built with WiX Toolset 6.0.2.

- `AirFlash.msi` is a standalone per-machine installer.
- The MSI installs the self-contained `AirFlash.exe` under `Program Files\AirFlash`.
- The standard installer UI lets the user change the install directory and optionally creates desktop and Start menu shortcuts. Both shortcut options are enabled by default.
- The installed package appears in Windows Apps and Features and does not require a separate Setup.exe, .NET runtime, Rust runtime, or VC++ Redistributable.

Build the release outputs with:

```powershell
pwsh scripts/build.ps1
```

The release directory contains only `AirFlash.exe` and `AirFlash.msi`.

Each release build reserves an increasing three-field version in `artifacts/release-version.txt` before publishing; preserve this record. MSI takes its version from the published EXE. Old products sharing the UpgradeCode are removed inside the install transaction. Repair reinstalls files, and installing over a portable AirFlash.exe replaces that file only. User settings and pairing data survive uninstall. Close AirFlash when Windows Installer requests it; locked files may require a restart.
Installation, repair and removal request AirFlash to exit and verify that it has stopped before removing older products or replacing files. If shutdown fails, the MSI aborts with a retry message.
