# AirPlay receiver compatibility

Third-party support is **experimental**. This change has no third-party hardware
qualification results. Localhost tests verify wire behavior and decode received
audio; they cannot certify a real speaker's clock lock, audible playback, or
acoustic latency. An AirPlay 2 logo alone is insufficient evidence of compatibility.

The release supports one third-party target, without an access password or with
HAP PIN credentials, and preserves existing HomePod stereo playback. Home/current-user
access restrictions, access-password authentication, and cross-brand multi-target
synchronization are outside scope. Third-party buffering defaults to 3000 ms,
bounded to 10000 ms; HomePod presets are preserved.

## Hardware qualification procedure

1. Record brand, exact model, firmware, access settings, and network conditions.
2. Start with automatic transport/clock and record all `negotiated` fields and
   fallback reasons. Test NTP and PTP paths across at least two brands. Exercise
   realtime UDP and buffered TCP only when supported by the target.
3. Follow the repository's existing quiet procedure: at most five seconds of
   440 Hz WAV with peak <=0.05 and sender gain <=0.1. Check speaker volume before
   playing; use handshake-only first if uncertain. Do not run hardware soaks.
4. Confirm actual sound, stop/teardown, volume control, mute restoration, and
   reconnect. Check WASAPI capture, EQ, and negotiated 44.1/48 kHz where supported.
5. Regress the existing HomePod stereo configuration. Enter a device in the
   verified list only after actual sound is confirmed. Keep failures and firmware
   dependencies in the same record.

Example finite probe (substitute the receiver's actual IPv4 address and port):

```powershell
uv run python scripts/e2e_stream.py --host 192.0.2.1 --port 7000 --timing auto --transport auto --duration 5 --gain 0.1 --report artifacts/receiver-probe.json
```

`--handshake-only` negotiates without sending audio. `--timing ntp|ptp`,
`--transport realtime|buffered`, and `--compatibility-buffer-ms 3000` are optional
overrides. This probe uses the existing Rust engine, not a Python playback backend.

## Qualification record template

| Field | Result |
| --- | --- |
| Brand / exact model | Pending |
| Firmware / test date | Pending |
| Access settings / persistent PIN pairing | Pending |
| Advertised service endpoints and capabilities | Pending |
| Requested transport, clock, sample rate and buffer | Pending |
| Actual transport / clock / encoding / sample rate / target buffer | Pending |
| Fallback reasons / PTP exchanges | Pending |
| Actual audible playback confirmed | Pending |
| Stop / reconnect / volume / mute restoration / EQ | Pending |
| Acoustic latency with measurement method | Unknown unless measured |
| HomePod stereo regression | Pending |

No third-party verified models are listed yet. Protocol references:
[pyatv capabilities](https://github.com/postlund/pyatv/blob/master/pyatv/protocols/airplay/utils.py),
[OwnTone sender](https://github.com/owntone/owntone-server/blob/master/src/outputs/airplay.c),
[Music Assistant design](https://github.com/music-assistant/airplay-cli/blob/main/DESIGN.md).
