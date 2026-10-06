Windows x64 · Windows 10/11 · Experimental third-party AirPlay 2 test release

This dev preview adds one third-party receiver alongside the existing HomePod
stereo path. It remains a draft change awaiting user hardware testing.

- Automatic negotiation chooses realtime UDP/type 96 or buffered TCP/type 103,
  NTP/PTP timing, and 16-bit stereo ALAC/PCM at 44.1/48 kHz.
- Third-party receivers default to a 3-second compatibility buffer, bounded to
  10 seconds. Settings → Receivers offers clock, transport, and latency overrides.
  Existing HomePod latency presets, saved settings, and credentials are retained.
- Settings → Monitor reports the actual format, clock, transport, adopted buffer,
  and fallback reasons. Clock exchanges and successful connection do not establish
  audible playback or measured acoustic latency.
- No third-party model has been hardware-qualified by this change. Access
  passwords, home/current-user restrictions, and cross-brand multiple targets
  are outside scope. Complete PIN pairing in Settings → Receivers if prompted.
- Please report brand, exact model, firmware, negotiated settings, fallback
  reasons, and whether sound actually plays. Exercise stop/reconnect, volume, EQ,
  and local mute restoration; existing HomePod stereo feedback is also needed.
- Keep real-device test probes low-volume and at most five seconds. Full procedure
  and report template: [hardware qualification](https://github.com/Ding-Kyoma/AirFlash/blob/dev/docs/AIRPLAY-COMPATIBILITY.md).

Download **AirFlash-X.Y.Z.msi** to install or **AirFlash.exe** for portable use.
Verify both downloads against **SHA256SUMS.txt**. Binaries are unsigned. This is a
prerelease; the stable Latest release and stable update check are unchanged.

中文：这是第三方 AirPlay 2 兼容性 dev 测试版，目前没有新增实机已验证型号。
默认采用 3 秒兼容缓冲，可在接收器设置中覆盖时钟、传输和延迟。
请反馈品牌、具体型号、固件、实际协商配置、回退原因和是否真实出声，
并检查停止/重连、音量、均衡器和本机静音恢复。HomePod 立体声也需要实机回归。
不支持访问密码、家庭/当前用户访问限制和跨品牌多目标同步；不保证所有 AirPlay 2 设备兼容或低延迟。
