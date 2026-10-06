# Missing notification-area icon

Issue [#2](https://github.com/Ding-Kyoma/AirFlash/issues/2) reports that 0.2.13 Preview starts normally but has no tray icon, with both EXE and MSI distribution; 0.2.11 works for that user.

The published source commits are `c046a7d` (0.2.11) and `8df85f0` (0.2.13-rc.1). Their tray implementation, ICO asset, application project and MSI installation path are identical. Both published EXEs match their release SHA256 digests and return nonzero small and large icon handles from `ExtractIconEx` on the development Windows machine. This rules out a missing icon resource in those downloads; it does not establish what failed on the affected user's machine.

The old implementation ignores every `Shell_NotifyIcon` result. A failed registration therefore leaves a running application without a tray entry. One possible trigger is its fixed GUID: [Microsoft documents](https://learn.microsoft.com/en-us/windows/win32/shell/samples-notificationicon) that moving an unsigned executable can cause its GUID registration to be rejected. The development Windows machine accepted the GUID after a directory move, so this trigger remains unconfirmed for issue #2.

Registration now tries the existing GUID first, then HWND/icon-ID identity if the initial GUID registration fails. It retains the successful identity for updates, geometry queries, deletion and Explorer recovery. Both modes retry unsuccessful registration every two seconds, stopping after success or disposal. The latest main branch's DPI-aware embedded ICO loader remains the primary source; if it fails, executable small/large icons and then a private copy of the shared Windows application icon provide fallback. Version-negotiation failure keeps the icon usable with legacy callbacks. Repeated Explorer notifications update a surviving entry instead of creating a duplicate.

## Diagnose an affected machine

First check the taskbar's overflow menu: an icon placed there is registered, even when it is not visible directly beside the clock. Logs under `%APPDATA%\AirFlash\logs\wpf-YYYY-MM-DD.log` now contain `Tray path=...` entries with operation, identity and success. Look for:

- `LoadEmbeddedIcon`: whether the DPI-aware application icon was loaded; `ExtractIconEx` records executable small/large handles if fallback was necessary.
- `Add identity=Guid success=False` followed by successful `Add identity=WindowIconId`: GUID registration failed and the fallback recovered.
- Repeated failed `Add` operations in both modes: registration is still unavailable. Failures are throttled to one entry per operation/identity every 30 seconds.
- Successful registration with failed `GetRect`: include the HRESULT when reporting the problem.

`Shell_NotifyIcon` supplies a Boolean result, not a documented last-error code. Logs do not interpret an unrelated `GetLastError` value as a cause.

## Isolated Windows desktop verification

Run `pwsh scripts/test-tray.ps1` against the freshly published `dist/AirFlash.exe`. The script copies the application to two new artifact directories, uses one fresh verification GUID, and runs English/Chinese tray checks. It also exercises native HWND/icon-ID identity explicitly, regardless of whether that Windows version rejects a directory move. Add `-RequirePathRejection` only when testing an environment expected to reproduce the documented GUID restriction. Reports distinguish observed rejection from explicitly selected window identity.

The underlying `--tray-smoke` mode verifies real registration/geometry, callbacks, menu actions, deletion and language-window reconstruction with simulated audio, discovery, autostart and engine services. `--tray-guid` must use a test GUID; the production GUID is rejected. `--tray-window-id` selects native window identity for verification only. Full `--ui-smoke` includes these tray checks and always isolates its GUID.

Use a disposable Windows desktop when reproducing shell lifecycle problems. Tests do not clear production tray caches, change taskbar preferences or restart the user's Explorer process. Mock-Shell unit tests cover registration failures, identity consistency, retry recovery, Explorer reconstruction, default callbacks and disposal. Headless CI cannot substitute for desktop tray checks.
