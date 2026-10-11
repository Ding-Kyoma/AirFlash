# Native AirPlay engine

Windows-only Rust sidecar; JSONL v1 on stdin/stdout. Diagnostics must not contain keys or PINs.
Desktop/validation commands use `start` (live WASAPI); finite tests use `probe` (<=5000ms, gain<=0.1,
440Hz WAV peak<=0.05). EOF cancels all owned work. `stop` and gain changes apply only to the
matching session. `pair` emits `pin_required`; `pair_pin` completes pairing, validates the
accessory signature and writes version-1, user-bound DPAPI credentials. Old pyatv credentials
are neither read nor changed.

Live `start` accepts optional `equalizer` settings (`enabled`, `preamp_db`, ten
`band_gains_db` values). Missing settings bypass EQ. `set_equalizer` accepts the
same settings plus a positive increasing `sequence` for the matching live session;
`equalizer_changed` reports automatic attenuation and effective preamp, and
`equalizer_error` rejects invalid changes without ending playback. Gains must be
finite and within -12..12 dB. Finite probes reject enabled EQ. Native stereo members
receive the same PCM after resampling, EQ, and master gain; software members map it
to `[L,L]` / `[R,R]`. EQ updates crossfade over
20 ms with no additional buffering.

Build with `scripts/build-native.ps1 -Check`. The ignored `soak` test runs two localhost UDP
receivers for 30 wall-clock minutes; it never connects to a speaker. Other tests cover
independent SRP vectors, simulated HAP peers, invalid identities/signatures, HAP records,
RTSP fragmentation/bounds/cancellation, sequence wrap and independent ALAC decoding.

Protocol references (wire fields/behavior, not linked protocol implementations):
- HAP and AirPlay observations in postlund/pyatv (MIT).
- AirPlay PTP profile documented by owntone/libairptp (MIT).
- Apple ALAC codec format via alac-encoder (MIT OR Apache-2.0).
- IEEE 1588 message layouts and RTP sequence/time semantics.

Foundation libraries: RustCrypto sha2/hkdf/ChaCha20-Poly1305, dalek Ed25519/X25519,
num-bigint (SRP arithmetic), Rubato (resampling), alac-encoder, windows-rs.
The code contains no dependency on a third-party complete AirPlay sender.

Optional peer profiles select PCM/ALAC, realtime UDP or type-103 buffered TCP, and PTP/NTP.
Known format tables prefer ALAC in auto mode; forced profiles never silently fall back.
TCP frames retain partial writes in a bounded queue and use SETRATEANCHORTIME. The fixed
sample timeline fills bounded valid gaps with encoded silence or rebuilds after expiry.
WASAPI discontinuities clear stale queues/resampler state; shared continuity transitions
and gain ramps take about 5 ms. MMCSS status and per-peer clock exchanges are diagnostic events.
Password SRP uses a separate user-bound DPAPI store. Native groups keep unified hardware
volume; software groups read and write each hardware volume independently.

Scope: Windows x64, 16-bit 44.1/48 kHz, one receiver or exactly two native/software members.
New transport/clock/password combinations and mixed-brand groups are experimental and
have simulated qualification only. No new hardware/acoustic qualification was performed.
There is no cross-model latency or microsecond acoustic synchronization guarantee. PTP
supports bounded AirPlay unicast master/follower modes, without full BMCA. See
[schema v3, IPC examples and acceptance boundaries](../../docs/audio-stereo-v3.md).
