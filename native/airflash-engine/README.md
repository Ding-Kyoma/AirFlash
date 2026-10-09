# Native AirPlay engine

Windows-only Rust sidecar; JSONL v1 on stdin/stdout. Diagnostics must not contain keys or PINs.
Desktop commands use `start` (live WASAPI); finite tests use `probe` (<=5000ms, gain<=0.1,
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

Build with `scripts/build-native.ps1 -Check`. Localhost stability tests include short TCP
continuity and ignored 30-minute UDP/TCP soaks; they never connect to a speaker. Other tests cover
independent SRP vectors, simulated HAP peers, invalid identities/signatures, HAP records,
RTSP fragmentation/bounds/cancellation, sequence wrap and independent ALAC decoding.
The finite loopback qualification tool uses a standard-library Windows Core Audio
mute guard, retaining the original endpoint for restoration after default-device
changes. It no longer depends on the removed Python UI modules.

## JSONL v1 negotiation

`start` and `probe` accept optional `transport` (`auto`, `realtime`, `buffered`,
or `legacy`) and `timing` (`auto`, `ptp`, `ntp`). Omission preserves the prior
`legacy` realtime/PTP behavior. The desktop explicitly requests `auto` for both.
Each peer accepts optional `services`: entries contain `service_type`, IPv4
`host`, `port`, and a string-valued `txt` dictionary. Original service endpoints
are retained; an alternate service endpoint can be used when `/info` is unavailable
on the primary endpoint. `/info` refines advertised capabilities before setup.

Third-party `auto` prefers type 103 when buffered audio, PTP, and ALAC are supported.
Other third-party routes use type 96; unknown timing starts with NTP. Formats are
16-bit stereo ALAC (preferred) or explicitly supported PCM, at 44.1/48 kHz.
Unknown 48 kHz support falls back to 44.1 kHz and reconfigures capture/resampling/EQ.
If a stream rejects 48 kHz with 415 while 44.1 kHz is advertised, one fresh
connection retries that format and reports `sample_rate_rejected_fallback`.
HomePod stereo retains its PCM preference and previous setup order.

`compatibility_buffer_ms` optionally overrides the third-party default of 3000 ms
(0..10000). HomePod/legacy routes continue using `latency_ms`. A realtime receiver's
reported buffer window clamps the target; buffered TCP uses an independent PTP
playback anchor and omits realtime sync, latency fields, and retransmission.
TCP partial frames survive backpressure; the queue is bounded to 10 seconds and
10 seconds without write progress is a retryable failure. Late live audio is
flushed and reanchored. Explicit setup rejection permits one new connection for
an advertised alternate route; authentication rejection never triggers fallback.

`negotiated` reports `timing`, `transport`, `codec`, `sample_rate`,
`requested_latency_ms`, `effective_latency_ms`, `fallback_reasons`, and
`measured_latency_ms` (null). PTP metrics count received packets and exchanges;
`clock_locked` remains null. A successful handshake or PTP exchange does not
prove clock lock or audible playback.

Temporary HAP goes directly to `/pair-setup`; persistent PIN pairing keeps
`/pair-pin-start` and signature validation. `pairing_required`, `password_required`,
`access_restricted`, and `signature_invalid` are terminal errors. Explicit
404/405/501 responses disable optional feedback or volume queries; authentication
refusal and connection failure remain faults.

Protocol references (wire fields/behavior, not linked protocol implementations):
- [HAP and capability observations in postlund/pyatv](https://github.com/postlund/pyatv/blob/master/pyatv/protocols/airplay/utils.py) (MIT).
- [OwnTone sender session order](https://github.com/owntone/owntone-server/blob/master/src/outputs/airplay.c).
- [Music Assistant buffered media design](https://github.com/music-assistant/airplay-cli/blob/main/DESIGN.md).
- AirPlay PTP profile documented by owntone/libairptp (MIT).
- Apple ALAC codec format via alac-encoder (MIT OR Apache-2.0).
- IEEE 1588 message layouts and RTP sequence/time semantics.

Foundation libraries: RustCrypto sha2/hkdf/ChaCha20-Poly1305, dalek Ed25519/X25519,
num-bigint (SRP arithmetic), Rubato (resampling), alac-encoder, windows-rs.
The code contains no dependency on a third-party complete AirPlay sender.

Limitations: Windows x64, one third-party receiver or an existing HomePod stereo pair,
16-bit stereo PCM/ALAC. Third-party support is experimental; no third-party model
has been qualified by this change. Previous hardware qualification covers HomePod
PCM transient authentication. Access passwords, home/current-user restrictions,
and cross-brand multi-target synchronization are outside scope. No cross-model
low-latency guarantee. See [hardware qualification](../../docs/AIRPLAY-COMPATIBILITY.md).
The PTP implementation is
an AirPlay unicast master, not a general-purpose IEEE 1588 daemon or full BMCA implementation.
