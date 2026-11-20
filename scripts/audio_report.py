"""Compare PCM taps to prove (or disprove) bit-exact passthrough.

`wrap` turns the engine's raw little-endian tap (``AIRFLASH_PCM_DUMP``) plus its
JSON sidecar into a WAV any analyser can open. `compare` reports level and
per-octave differences between two files and, when the lengths match, the
residual left after subtracting one from the other.

The interesting answer is usually the loudest one: ``identical`` says whether
every sample matched, and ``max_difference_lsb`` says how far apart they were
when they did not.
"""

from __future__ import annotations

import argparse
import json
import sys
import wave
from pathlib import Path

import numpy as np

# One-third of an octave is fine for reporting, but the bands below are octaves:
# they line up with how a listener describes "more air" or "less body".
BAND_CENTERS = (125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0)


def read_wav(path: Path) -> tuple[np.ndarray, int]:
    """Stereo frames as int16 or float32 plus the sample rate."""
    with wave.open(str(path), "rb") as handle:
        channels, width, rate, frames = (
            handle.getnchannels(),
            handle.getsampwidth(),
            handle.getframerate(),
            handle.getnframes(),
        )
        raw = handle.readframes(frames)
    if width == 2:
        data = np.frombuffer(raw, dtype="<i2").astype(np.float64) / 32768.0
    elif width == 4:
        data = np.frombuffer(raw, dtype="<i4").astype(np.float64) / 2147483648.0
    else:
        raise ValueError(f"unsupported sample width {width} bytes")
    return data.reshape(-1, channels), rate


def read_raw(path: Path, sidecar: Path) -> tuple[np.ndarray, int]:
    """Read an ``AIRFLASH_PCM_DUMP`` tap and the format its sidecar recorded."""
    spec = json.loads(sidecar.read_text(encoding="utf-8"))
    channels, bits = int(spec["channels"]), int(spec["bits"])
    if bits != 16:
        raise ValueError(f"only 16-bit taps are supported, got {bits}")
    data = np.frombuffer(path.read_bytes(), dtype="<i2").astype(np.float64) / 32768.0
    usable = len(data) - len(data) % channels
    return data[:usable].reshape(-1, channels), int(spec["rate"])


def wrap(raw: Path, out: Path) -> dict:
    """Write the tap as a WAV next to its sidecar and return a short summary."""
    sidecar = raw.with_suffix(".json")
    data, rate = read_raw(raw, sidecar)
    codes = np.clip(np.round(data * 32768.0), -32768, 32767).astype("<i2")
    with wave.open(str(out), "wb") as handle:
        handle.setnchannels(data.shape[1])
        handle.setsampwidth(2)
        handle.setframerate(rate)
        handle.writeframes(codes.tobytes())
    return {"wav": str(out), "rate": rate, "frames": int(data.shape[0])}


def band_levels(data: np.ndarray, rate: int) -> dict[str, float]:
    """Octave-band energy in dBFS from a Hann-windowed spectrum of the mono mix."""
    mono = data.mean(axis=1)
    if len(mono) == 0:
        return {}
    window = np.hanning(len(mono))
    spectrum = np.abs(np.fft.rfft(mono * window)) ** 2
    frequencies = np.fft.rfftfreq(len(mono), 1.0 / rate)
    total = float(spectrum.sum()) or 1.0
    levels = {}
    for center in BAND_CENTERS:
        low, high = center / np.sqrt(2.0), center * np.sqrt(2.0)
        mask = (frequencies >= low) & (frequencies < high)
        share = float(spectrum[mask].sum()) / total
        levels[f"{center:.0f}"] = float(10.0 * np.log10(max(share, 1e-15)))
    return levels


def compare(first: Path, second: Path) -> dict:
    """Level, band and null-residual differences between two captures."""
    a, rate_a = read_wav(first)
    b, rate_b = read_wav(second)
    if rate_a != rate_b:
        raise ValueError(f"sample rates differ: {rate_a} vs {rate_b}")
    codes_a = np.round(a * 32768.0)
    codes_b = np.round(b * 32768.0)
    frames = min(codes_a.shape[0], codes_b.shape[0])
    identical = codes_a.shape == codes_b.shape and bool(np.array_equal(codes_a, codes_b))
    overlap_a, overlap_b = a[:frames], b[:frames]
    rms_a = float(np.sqrt(np.mean(overlap_a**2))) if frames else 0.0
    rms_b = float(np.sqrt(np.mean(overlap_b**2))) if frames else 0.0
    band_a, band_b = band_levels(overlap_a, rate_a), band_levels(overlap_b, rate_b)
    residual = overlap_a - overlap_b
    difference = {
        "identical": identical,
        "rate": rate_a,
        "frames": [int(codes_a.shape[0]), int(codes_b.shape[0])],
    }
    if frames and rms_a > 0:
        residual_rms = float(np.sqrt(np.mean(residual**2)))
        difference["null_db"] = float(20.0 * np.log10(max(residual_rms, 1e-12) / rms_a))
        difference["max_difference_lsb"] = int(np.max(np.abs(codes_a[:frames] - codes_b[:frames])))
    if rms_a > 0 and rms_b > 0:
        difference["level_delta_db"] = float(20.0 * np.log10(rms_b / rms_a))
    difference["bands"] = {
        name: round(band_b[name] - band_a[name], 2) for name in band_a if name in band_b
    }
    return difference


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    wrap_parser = commands.add_parser("wrap", help="raw tap + sidecar -> WAV")
    wrap_parser.add_argument("raw", type=Path)
    wrap_parser.add_argument("out", type=Path)
    compare_parser = commands.add_parser("compare", help="two WAVs -> level/band/null report")
    compare_parser.add_argument("first", type=Path)
    compare_parser.add_argument("second", type=Path)
    compare_parser.add_argument("--json", action="store_true", help="print the full report as JSON")
    args = parser.parse_args(argv)
    if args.command == "wrap":
        print(json.dumps(wrap(args.raw, args.out), ensure_ascii=False))
        return 0
    report = compare(args.first, args.second)
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 0
    print(
        f"identical: {report['identical']}  frames: {report['frames']}  rate: {report['rate']} Hz"
    )
    if "level_delta_db" in report:
        print(f"level: {report['level_delta_db']:+.2f} dB")
    if "null_db" in report:
        print(
            f"null residual: {report['null_db']:.1f} dB re the first file; "
            f"max {report['max_difference_lsb']} LSB"
        )
    for name, delta in report["bands"].items():
        print(f"  {name:>6} Hz band: {delta:+.2f} dB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
