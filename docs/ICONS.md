# AirFlash icon / 图标维护

The editable master is [app.svg](../desktop/AirFlash.App/Assets/app.svg).
It preserves the original blue rounded square, two white signal arcs and triangle.
Its 64-unit viewBox contains vector paths only; no embedded bitmap, fonts or external assets.

唯一可编辑的母版是上述 SVG，保留原有结构与配色。修改图标时编辑母版，再生成衍生资源，不手工修改 PNG、ICO 或 XAML。

## Generate and check / 生成与校验

On Windows with PowerShell 7 and WPF available:

```powershell
pwsh -NoProfile -STA -File scripts/generate-icons.ps1
pwsh -NoProfile -STA -File scripts/generate-icons.ps1 -Check
```

The generator supports the master's filled paths and solid strokes. It rejects unsupported SVG features rather than producing inconsistent browser and application images. `-Check` renders in memory, compares generated resources and validates ICO decoding, frame sizes and transparency; it does not rewrite files. CI and the release workflow run this check.

| Resource | Use |
| --- | --- |
| `app.svg` | Editable vector master; English and Chinese README headings |
| `app.xaml` | Generated WPF `DrawingImage`; application panel logo |
| `app.png` | Generated 512×512 transparent PNG for bitmap consumers |
| `app.ico` | EXE, native windows, notification area, MSI and shortcuts |

ICO sizes: **16, 20, 24, 28, 32, 40, 48, 56, 60, 64, 72, 80, 96, 128, 192, 256** pixels.
Every frame is rendered directly from the vector, not resized from another bitmap.
The application embeds the ICO separately so development hosts and single-file releases use identical resources.

## DPI behavior / DPI 行为

Windows taskbar and notification icons require native bitmap handles. `AppIconService` selects the smallest ICO frame at least as large as the requested size, then creates a handle at exactly that size; sizes beyond the largest frame use that frame. Native windows refresh their small and large handles when their DPI changes. The tray uses its icon rectangle to find the monitor DPI, falling back to the primary taskbar's DPI during registration. Display/settings/DPI notifications and Explorer's `TaskbarCreated` message trigger refresh or registration. Failed refreshes retain the previous usable icon, and replaced handles are released.

125%（120 DPI）下，常规小图标为 20px、大图标为 40px，均有原生尺寸帧。应用面板使用 WPF 矢量绘制；README 标题使用 SVG。文档截图仍为 PNG，其中面板和托盘的 Logo 从同一矢量资源按截图像素尺寸重新绘制。

## Verification / 验收

```powershell
pwsh scripts/dotnet.ps1 test AirFlash.sln '-p:RestoreLockedMode=true'
# Run the built AirFlash.exe with isolated mock services:
AirFlash.exe --ui-smoke --ui-language en-US --output artifacts/icon-smoke-en/report.json
AirFlash.exe --ui-smoke --ui-language zh-CN --output artifacts/icon-smoke-zh/report.json
```

UI smoke checks native HICON dimensions, window and tray handles, notification-driven tray refresh and simulated Explorer re-registration. It renders the panel and an icon comparison sheet at **100%, 125%, 150%, 175%, 200%** in both themes. Reports include the actual window DPI and monitor work area. Mock engine/audio/discovery/autostart services do not access HomePods or alter system audio.

Physical acceptance on **3440×1440 at 125%**:

1. Check the tray, settings window taskbar icon and title bar, desktop shortcut, Start menu shortcut, and large Explorer icons.
2. Check blue/white shapes and edges in light and dark Windows themes; compare the vector with the original design.
3. Change scaling between 100%, 125%, 150%, 175%, 200%, and move settings between monitors of different DPI. Icons should update without restarting AirFlash.
4. Restart Explorer during an approved test session. The tray icon should return once, with clicks and its context menu working.
5. Check the MSI's Apps and Features icon and installed shortcuts in a disposable Windows environment.

Native handle tests and simulated DPI renders do not replace these physical checks. Existing pinned shortcuts may retain Windows' cached icon; recreate the pin if it still shows the old resource after an upgrade.
