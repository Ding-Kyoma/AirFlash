# Native AirPlay engine

Windows-only Rust sidecar; JSONL v1 on stdin/stdout. Diagnostics must not contain keys or PINs.
Python UI commands use `start` (live WASAPI); finite tests use `probe` (<=5000ms, gain<=0.1,
440Hz WAV peak<=0.05). EOF cancels all owned work. `stop` and gain changes apply only to the
matching session. `pair` emits `pin_required`; `pair_pin` completes pairing, validates the
accessory signature and writes version-1, user-bound DPAPI credentials. Old pyatv credentials
are neither read nor changed.

Live `start` accepts optional `equalizer` settings (`enabled`, `preamp_db`, ten
`band_gains_db` values). Missing settings bypass EQ. `set_equalizer` accepts the
same settings plus a positive increasing `sequence` for the matching live session;
`equalizer_changed` reports automatic attenuation and effective preamp, and
`equalizer_error` rejects invalid changes without ending playback. Gains must be
finite and within -12..12 dB. Finite probes reject enabled EQ. Both stereo members
receive the same PCM after resampling, EQ, and master gain. Updates crossfade over
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

Limitations: first-device scope is a single native HomePod stereo pair, Windows x64, stereo PCM.
ALAC and persistent pairing have independent/simulated tests; hardware qualification covers
PCM transient authentication. No cross-model 200ms guarantee. The PTP implementation is
an AirPlay unicast master, not a general-purpose IEEE 1588 daemon or full BMCA implementation.
