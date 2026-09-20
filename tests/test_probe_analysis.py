"""Validate acoustic analysis against known delay and noise, not device settings."""

import json
import struct

import numpy as np

from scripts.probe_analysis import analyze


def make_recording(tmp_path, noise_only=False, timestamp_error=False):
    rate, seconds, delay = 48000, 6, 0.173
    t = np.arange(rate * seconds) / rate
    start = 1_000_000_000
    audio = np.random.default_rng(17).normal(0, 0.00001, len(t))
    markers = [
        {"offset_ms": offset, "channel": channel}
        for offset, channel in [(250, "left"), (1250, "right"), (2250, "both"), (3250, "both")]
    ]
    if not noise_only:
        for marker in markers:
            age = t - 0.5 - marker["offset_ms"] / 1000 - delay
            fade = np.minimum(np.clip(age / 0.01, 0, 1), np.clip((0.5 - age) / 0.01, 0, 1))
            amplitude = 0.005 if marker["channel"] == "both" else 0.0025
            audio += amplitude * fade * np.sin(2 * np.pi * 440 * t)
    data = audio.astype("<f4").tobytes()
    fmt = struct.pack("<HHIIHH", 3, 1, rate, rate * 4, 4, 32)
    body = (
        b"WAVEfmt "
        + struct.pack("<I", len(fmt))
        + fmt
        + b"data"
        + struct.pack("<I", len(data))
        + data
    )
    wav = tmp_path / "microphone.wav"
    wav.write_bytes(b"RIFF" + struct.pack("<I", len(body)) + body)
    metadata = {
        "sample_rate": rate,
        "frames": len(t),
        "blocks": [
            {
                "offset": 0,
                "frames": len(t),
                "qpc_ns": start - 500_000_000,
                "flags": 4 if timestamp_error else 0,
            }
        ],
    }
    wav.with_suffix(".timestamps.json").write_text(json.dumps(metadata))
    report = tmp_path / "report.json"
    report.write_text(
        json.dumps(
            {
                "duration_seconds": 5,
                "markers": markers,
                "events": [{"event": "streaming", "first_send_qpc_ns": start}],
            }
        )
    )
    return report, wav


def test_known_delay_is_recovered_but_stereo_not_certified(tmp_path):
    report, wav = make_recording(tmp_path)
    result = analyze(report, wav)
    assert result["valid"]
    assert abs(result["p95_ms"] - 173) < 10
    assert not result["qualified"]
    assert not result["stereo_verified"]


def test_noise_and_invalid_hardware_timestamps_cannot_pass(tmp_path):
    for noise, timestamp_error in [(True, False), (False, True)]:
        report, wav = make_recording(tmp_path, noise, timestamp_error)
        assert not analyze(report, wav)["valid"]
