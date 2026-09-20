"""Conservative acoustic onset analysis for finite native probe recordings.

Results cover scheduled PCM to microphone, not WASAPI loopback end-to-end.
A single microphone cannot certify stereo separation or inter-speaker skew.
"""

from __future__ import annotations

import json
import struct
from pathlib import Path

import numpy as np


def _float_wav(path: Path) -> np.ndarray:
    raw = path.read_bytes()
    if raw[:4] != b"RIFF" or raw[8:12] != b"WAVE":
        raise ValueError("not a RIFF WAVE file")
    offset = 12
    valid_format = False
    while offset + 8 <= len(raw):
        tag, size = struct.unpack_from("<4sI", raw, offset)
        end = offset + 8 + size
        if end > len(raw):
            raise ValueError("truncated WAV")
        if tag == b"fmt " and size >= 16:
            format_tag, channels, _, _, _, bits = struct.unpack_from("<HHIIHH", raw, offset + 8)
            valid_format = format_tag == 3 and channels == 1 and bits == 32
        if tag == b"data":
            if not valid_format or size % 4:
                raise ValueError("expected mono float32 microphone WAV")
            return np.frombuffer(raw[offset + 8 : end], dtype="<f4").astype(np.float64)
        offset = end + size % 2
    raise ValueError("WAV has no data")


def analyze(report_path: Path, recording_path: Path) -> dict:
    report = json.loads(report_path.read_text(encoding="utf-8"))
    recording = json.loads(recording_path.with_suffix(".timestamps.json").read_text())
    start = next(e["first_send_qpc_ns"] for e in report["events"] if e["event"] == "streaming")
    samples = _float_wav(recording_path)
    rate = recording["sample_rate"]
    window = round(rate * 0.01)
    hop = round(rate * 0.002)
    # Narrow-band envelope: synchronous 440Hz detection in 10ms windows.
    carrier = np.exp(-2j * np.pi * 440 * np.arange(len(samples)) / rate)
    integral = np.r_[0j, np.cumsum(samples * carrier)]
    indices = np.arange(0, len(samples) - window, hop)
    envelope = 2 * np.abs(integral[indices + window] - integral[indices]) / window
    centers = indices + window // 2
    times = np.full(len(indices), np.nan)
    invalid = False
    for block in recording["blocks"]:
        mask = (centers >= block["offset"]) & (centers < block["offset"] + block["frames"])
        if block["flags"] & 4:
            invalid = True
            continue
        times[mask] = (block["qpc_ns"] - start) / 1e9 + (centers[mask] - block["offset"]) / rate
    usable = np.isfinite(times)
    times, envelope = times[usable], envelope[usable]
    if not len(times):
        return {"valid": False, "qualified": False, "reason": "No valid hardware timestamps"}
    markers = report["markers"]
    reference = "scheduled PCM to microphone; excludes loopback capture"
    if report.get("source") == "loopback":
        onsets = [e["capture_qpc_ns"] for e in report["events"] if e["event"] == "audio_onset"]
        if len(onsets) != len(markers) or not report.get("local_output_muted"):
            return {
                "valid": False,
                "qualified": False,
                "reason": "Missing capture markers or unmuted local output",
            }
        markers = [
            dict(marker, offset_ms=(timestamp - start) / 1e6)
            for marker, timestamp in zip(markers, onsets, strict=True)
        ]
        reference = "WASAPI capture timestamp to microphone; local output muted"
    baseline = envelope[times < 0]
    noise = float(np.percentile(baseline, 95)) if len(baseline) else 0.0
    best = (-1.0, 0.0)
    fit = (times >= 0) & (times < report["duration_seconds"])
    actual = envelope[fit]
    actual = actual - actual.mean()
    for delay in np.arange(0, 2.001, 0.002):
        predicted = np.zeros(np.count_nonzero(fit))
        for marker in markers:
            age = times[fit] - delay - marker["offset_ms"] / 1000
            amplitude = 1.0 if marker["channel"] == "both" else 0.5
            predicted += amplitude * np.minimum(
                np.clip(age / 0.01, 0, 1), np.clip((0.5 - age) / 0.01, 0, 1)
            )
        predicted -= predicted.mean()
        denominator = np.linalg.norm(predicted) * np.linalg.norm(actual)
        score = float(np.dot(predicted, actual) / denominator) if denominator else 0.0
        if score > best[0]:
            best = (score, delay)
    observations = []
    for marker in markers:
        expected = marker["offset_ms"] / 1000
        onset = expected + best[1]
        plateau = envelope[(times >= onset + 0.07) & (times < onset + 0.4)]
        if not len(plateau):
            continue
        level = float(np.median(plateau))
        threshold = (level + float(np.median(baseline)) if len(baseline) else level) / 2
        candidates = np.flatnonzero(
            (times >= onset - 0.08) & (times < onset + 0.08) & (envelope >= threshold)
        )
        if not len(candidates):
            continue
        index = int(candidates[0])
        # The 10ms source fade reaches half amplitude 5ms after the marker.
        delay_ms = (times[index] - expected - 0.005) * 1000
        observations.append(
            {
                "channel": marker["channel"],
                "delay_ms": round(delay_ms, 2),
                "signal_to_noise": round(level / max(noise, 1e-9), 2),
            }
        )
    valid = (
        not invalid
        and len(observations) == len(markers)
        and len(markers) >= 3
        and best[0] >= 0.85
        and all(o["signal_to_noise"] >= 4 for o in observations)
    )
    delays = [o["delay_ms"] for o in observations]
    return {
        "measurement": reference,
        "valid": valid,
        "pattern_correlation": round(best[0], 4),
        "observations": observations,
        "p95_ms": round(float(np.percentile(delays, 95)), 2) if valid else None,
        "analysis_resolution_ms": 2,
        "onset_window_ms": 10,
        "includes_acoustic_propagation": True,
        "stereo_verified": False,
        "qualified": False,
    }
