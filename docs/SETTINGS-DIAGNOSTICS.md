# Settings scrolling verification

Settings reserves a gutter between page content and Fluent's overlaid vertical scrollbar. Equalizer frequency labels also have space above the horizontal scrollbar at narrow widths. Its horizontal band viewer forwards ordinary wheel input to the surrounding page, using WPF's normal wheel distance. Horizontal band scrolling, slider dragging and keyboard editing remain available.

The full `--ui-smoke` suite includes scrolling checks. From the directory containing the built executable, run only the focused checks with in-memory settings and simulated discovery, audio, autostart and engine services:

```powershell
.\AirFlash.exe --ui-smoke --ui-scroll-smoke --ui-language en-US --output artifacts/settings-scroll-en/report.json
.\AirFlash.exe --ui-smoke --ui-scroll-smoke --ui-language zh-CN --output artifacts/settings-scroll-zh/report.json
```

The fixture visits all eight pages in light and dark themes, requesting window sizes of 880×640 and 640×440. Reports record the realized sizes, which may be constrained by the desktop. It measures clearance from realized scrollbars, checks access to the last content and frequency band, and sends routed wheel input over Equalizer labels and sliders. That input must move the surrounding page exactly once without changing band gains, horizontal position, saved settings or playback. Reports include measurements, failures and Monitor/Equalizer renders.

For a bounded manual pointer check, add `--ui-scroll-hold` to a focused command. After the automated checks, the same isolated Settings window remains open on Equalizer for 60 seconds. Check wheel scrolling over bands, drag the page scrollbar to both ends, and visit other pages. The fixture checks settings and playback again after the hold, then closes its own window.

These checks use the installed WPF runtime and do not access real accessories. Routed events and rendered layouts do not replace physical checks with different monitors, display scaling or touchpads.

## Pairing error guidance

The control panel adds Home app speaker-access guidance only for `authentication_failed`
whose message contains `pairing rejected (TLV error [2])`. Other authentication errors
retain their existing messages. The hint is shown beneath the current error, clears
on stop or reconnect, and appears in the copied summary. Raw fault details, monitor
history and the JSON diagnostic export are retained separately.

Run the isolated fixture for each supported UI language (`en-US`, `zh-CN`, `zh-TW`,
`de-DE`, `es-ES`):

```powershell
.\AirFlash.exe --ui-smoke --ui-pairing-hint-smoke --ui-language de-DE --output artifacts/pairing-hint-de/report.json
```

The fixture checks both playback and explicit pairing failures, raw error details,
clearing on stop and reconnect, the copied summary, JSON export, and the settings
monitor. It renders the wrapped hint in light and dark themes at 100% and 200%.
These images do not replace physical display-scaling or screen-reader testing.
