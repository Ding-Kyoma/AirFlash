# AirFlash 0.6.2

Changes since AirFlash 0.6.1.

## Bug Fixes

- Keep the control panel at a compact 400-DIP width when reopened, repositioned, or moved between displays with different scaling.
- Simplify normal playback details to one latency line per receiver, hiding duplicate status and policy summaries while keeping warnings and recovery prompts visible. Full status details remain available in monitoring and diagnostics.
- Clear latency rows and their spacing after playback stops, and restore the ready prompt.

Automated UI regression coverage includes single receivers and stereo groups, repeated panel opening, control layout, and stopping playback. No real HomePod playback test was performed for this release.

Download **AirFlash-0.6.2.msi** or portable **AirFlash.exe**; verify with **SHA256SUMS.txt**. Binaries are unsigned.
