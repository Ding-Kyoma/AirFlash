# Audio quality: what the signal passes through

This page answers one question: when AirFlash streams Windows audio to a
HomePod, which stages can change the samples? It lists every stage, the
evidence for it, and what AirFlash itself does about it.

The short answer: encoding, transport and the equalizer are clean. The capture
used to be resampled even when both sides already agreed on 44.1 kHz, which
filtered every sample for no reason. Bit-exact passthrough (§3) removes that.

## 1. The chain

| # | Stage | Code | What it does | Changes samples? |
|---|---|---|---|---|
| 1 | WASAPI loopback | `live.rs` `Binding::open_endpoint` | `GetMixFormat()` opened natively (no `AUTOCONVERTPCM`) | No, but Windows already reduced the mix to the endpoint format |
| 2 | Sample decode | `Binding::sample` | `i16 / 32768.0`, mono is duplicated, >2 channels ignored | No |
| 3 | Clock alignment | `Pipeline` | Copies (bit-exact) or resamples when the rates differ | Only without bit-exact |
| 4 | Servo | `servo.rs` | PI on queue depth + lateness servo, ±400/±50 ppm | By design, inaudible |
| 5 | Equalizer | `equalizer.rs` | 10 peaking bands; a disabled or flat curve is a bypass | Only when enabled |
| 6 | Splice + gain | `continuity.rs` | 5 ms crossfade on boundary/gain change/resume, 10 ms concealment | Only at boundaries |
| 7 | Quantizer | `continuity.rs` | Outputs 16-bit or 24-bit PCM, with TPDF dither when converting processed or higher-resolution samples | See §3.4 |
| 8 | ALAC encode | `rtp.rs` | 16-bit ALAC, or 24-bit ALAC over buffered TCP | No (lossless, checked by independent decoding in tests) |
| 9 | Transport | `transport.rs` | RTP over UDP (realtime) or type-103 (buffered) | No |

Nothing in stages 8–9 can alter a sample: `session_simulation.rs` encodes,
decrypts and decodes every packet and asserts the decoded integers equal the
input.

## 2. Where quality was lost before bit-exact mode

The 1:1 case is the common one: a 44.1 kHz endpoint with a 44.1 kHz stream.
AirFlash used to build `SincFixedIn` unconditionally and ask it for a ratio of
exactly 1.0. Rubato always convolves with its windowed sinc, so a ratio of 1.0
still ran a 64-tap low-pass with a cutoff at 0.9 × Nyquist.

Measured with the production parameters (44.1 kHz in and out, Blackman-Harris²,
128× oversampling):

| Frequency | Gain |
|---|---|
| 15 kHz | -0.01 dB |
| 18 kHz | -0.35 dB |
| 19 kHz | -2.08 dB |
| 19.8 kHz | -6.02 dB |

That is a real, measurable loss of the top half-octave. It is inaudible in most
content, but it is also pointless: nothing needed resampling.

## 3. Bit-exact passthrough

On by default (`Settings › Audio source › Bit-exact passthrough`,
`schema: bit_exact_passthrough`, request parameter `bit_exact`).

### 3.1 When it applies

`bit_exact && capture_rate == stream_rate`. The pipeline then skips the
resampler. The Monitor row calls this **Sample rate passthrough**.

When the rates differ, the resampler runs with 256 taps and a 0.947
cutoff instead of 64 taps and 0.9, which is flat through 20 kHz.

### 3.2 Clock drift

Capturing at 44.1 kHz from one oscillator and playing at 44.1 kHz from another
is never exactly synchronous. The two clocks typically differ by a few parts
per million, so the capture slowly gets ahead of (or behind) the timeline. The
difference has to go somewhere, and it cannot go into the samples without
either resampling or dropping a frame.

AirFlash absorbs it with a **one-frame slip**: the stream gains or loses
exactly one frame, interpolated with a Catmull-Rom window over 64 frames
(1.45 ms) placed where the signal is quietest and flattest in that 10 ms chunk.
Everything outside the window is copied verbatim.

| Drift | Slips per second |
|---|---|
| 1 ppm | 0.04 |
| 20 ppm | 0.9 |
| 100 ppm | 4.4 |

If the correction stays above 200 ppm for five seconds the pipeline hands the
stream back to the resampler and says `bit_exact: false` in telemetry. Clocks
inside one machine are usually much closer than that.

### 3.3 What "bit-exact" guarantees

When capture is integer PCM, the encoder can preserve the samples WASAPI
delivered at matching rates if the output bit depth can represent them and
the samples pass through without processing.

1. **Start and resume.** The first 5 ms after playback starts, after a rebind,
   or after at least one second of digital silence is faded in. This is the
   click guard and it is deliberate.
2. **Clock slips.** One frame out of 64 is interpolated, about once a second
   at 20 ppm and never when the clocks agree.
3. **Processing and conversion.** Equalization, master gain, and conversion
   from floating-point or higher-resolution input can change output samples.

### 3.4 Why the dither is skipped

The dithered path scales by 32767, which maps `-32768` to `-32767`. A 16-bit
capture that was never touched is already on the grid, so it is scaled by
32768 and rounded, which is exact for every one of the 65536 codes. Adding
TPDF noise to a signal that has already been quantised would only raise the
noise floor.

In Receivers → Advanced settings, **Buffered TCP** exposes 24-bit ALAC at
44.1 or 48 kHz when the receiver advertises the corresponding format and can
use PTP. A group uses one common rate and bit depth. Automatic prefers an
available 16-bit stream for compatibility. Monitor shows the WASAPI input
format separately from the negotiated output rate and depth.

## 4. Proving it

Set `AIRFLASH_PCM_DUMP` to a path before starting a stream; the engine writes
`<path>.raw` (little-endian interleaved PCM, capped at two minutes) and
`<path>.json` with the format and frame count. The tap is taken after the
splice and quantizer, so it is exactly what the encoder sees.

```bash
uv run python -m scripts.audio_report wrap C:/tmp/tap C:/tmp/tap.wav
uv run python -m scripts.audio_report compare original.wav C:/tmp/tap.wav
```

`compare` reports whether every sample matched, the level difference, the
per-octave difference and the null residual. Identical files print
`identical: True` and a residual below -100 dB. The comparison excludes the
first 5 ms and any slip window; if it does not, the dump is telling you
something the telemetry is not.

## 5. When iPhone → HomePod still sounds better

The two paths can sound different even at the same negotiated rate and bit
depth. Match playback level and compare the source mix, capture format and
processing along each path. Comparing how the HomePod renders each stream
requires listening or acoustic measurement.

1. **Volume.** AirPlay volume is on a 0–100 scale mapped to dB; `43` is about
   -17 dB. A 1–3 dB mismatch reads as "fuller" or "brighter". Match the two
   paths before comparing.
2. **Source.** Apple Music on iPhone may play a Dolby Atmos mix or apply Sound
   Check; the Windows app may play a stereo master. Spotify normalisation does
   the same. Compare the same file, not the same song title.
3. **Windows processing.** A capture endpoint that does not set
   `Disable_SysFx` lets Realtek and friends run their effects. Turn off
   "Audio enhancements" for the endpoint under Sound settings → the device →
   Advanced, and disable any spatial audio.
4. **Endpoint format.** A 16-bit endpoint means Windows quantised the mix
   before AirFlash saw it. That is CD resolution and is fine; a 24-bit endpoint
   removes any doubt.
5. **What the equalizer and master gain say.** Anything below a flat curve or
   100 % master gain is processing by definition.

The `Audio chain:` line in the desktop log records
`<capture format> → rate passthrough|resample → <codec>/<transport> <rate>/<depth> → gain →
eq → slips`.
