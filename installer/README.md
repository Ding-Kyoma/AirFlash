# Installer

The installer is built with WiX Toolset 6.0.2.

- `AirFlash-X.Y.Z.msi` is a standalone per-machine installer.
- The MSI installs the self-contained `AirFlash.exe` under `Program Files\AirFlash`.
- The standard installer UI lets the user change the install directory and optionally creates desktop and Start menu shortcuts. Both shortcut options are enabled by default.
- The installed package appears in Windows Apps and Features and does not require a separate Setup.exe, .NET runtime, Rust runtime, or VC++ Redistributable.
- New installations require an x64 system and Windows build 17763 or newer. The installer reads `CurrentBuildNumber` from the 64-bit registry view; maintenance and removal remain available for installed products.

Windows 10 execution is pending affected-user verification. See [compatibility diagnostics and acceptance](../docs/WINDOWS-COMPATIBILITY.md).

Build the release outputs with:

```powershell
pwsh scripts/build-release.ps1
```

The release directory contains only `AirFlash.exe` and `AirFlash-X.Y.Z.msi`.
The filename uses the release version, for example `AirFlash-0.3.0.msi`.

For a local development package, run `pwsh scripts/build-dev.ps1`. It writes
`dist/dev/AirFlash.exe` and a filename such as
`AirFlash-0.4.0-dev.3f2a9c1.msi`. Development packages use the current stable
numeric ProductVersion, so uninstall an existing same-version package before
installing one.

The installer helper rebuilds its WiX intermediates on every invocation so a changed publish path or executable with the same version cannot reuse an older payload. To verify repeated packaging on Windows with PowerShell 7, after restoring WiX and publishing the app, run:

```powershell
pwsh scripts/test-installer-refresh.ps1 -PublishedExe build/wpf-publish/AirFlash.exe
```

The check packages two different, backdated copies with the same file version through the default and explicit SDK paths, then extracts each MSI and compares its executable checksum. It also checks that the first package and source files survive unchanged. The synthetic copies are never executed or installed; reports and logs remain under `artifacts/installer-refresh/`. Use `-DotnetPath` to select an SDK explicitly.

For a standalone MSI build, `scripts/build-installer.ps1 -DotnetPath <host>` selects an explicit .NET executable (relative paths are rooted at the repository). Both explicit and default hosts are checked and invoked from `desktop`, using `desktop/global.json`'s SDK policy. SDK failure occurs before the output directory is created. The release build passes its already selected host to the installer.

Each release build reserves an increasing three-field version in `artifacts/release-version.txt` before publishing; preserve this record. The MSI receives that numeric ProductVersion from the release build. Old products sharing the UpgradeCode are removed inside the install transaction. Repair reinstalls files, and installing over a portable AirFlash.exe replaces that file only. User settings and pairing data survive uninstall. Close AirFlash when Windows Installer requests it; locked files may require a restart.
Installation, repair and removal request AirFlash to exit and verify that it has stopped before removing older products or replacing files. If shutdown fails, the MSI aborts with a retry message.
