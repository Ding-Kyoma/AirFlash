"""Finite, quiet qualification of the independent Rust AirPlay sender.

This finite test harness is separate from live UI streaming. Transport success
is never reported as an acoustic latency or stereo certification.
"""

from __future__ import annotations

import asyncio
import json
import math
import os
import struct
import subprocess
import tempfile
import uuid
import wave
from pathlib import Path

DEFAULT_RATE = 44100
SUPPORTED_RATES = (44100, 48000)
MAX_SECONDS = 5.0
MAX_GAIN = 0.1


def engine_path() -> Path:
    path = (
        Path(__file__).resolve().parents[1]
        / "native"
        / "airflash-engine"
        / "target"
        / "release"
        / "airflash-engine.exe"
    )
    if not path.is_file():
        raise FileNotFoundError(f"Native engine missing: {path}; run scripts/build-native.ps1")
    return path


def write_probe_wav(path: Path, duration: float, rate: int = DEFAULT_RATE) -> list[dict]:
    """440 Hz, peak .05, with separated L/R/both onsets for acoustic analysis."""
    if not math.isfinite(duration) or not 0 < duration <= MAX_SECONDS:
        raise ValueError("duration must be between 0 and 5 seconds")
    if rate not in SUPPORTED_RATES:
        raise ValueError(f"rate must be one of {SUPPORTED_RATES}")
    markers = [
        {"offset_ms": t, "channel": channel}
        for t, channel in [(250, "left"), (1250, "right"), (2250, "both"), (3250, "both")]
        if t + 500 <= duration * 1000
    ]
    frames = bytearray()
    for i in range(round(rate * duration)):
        t = i / rate
        left = right = 0
        for marker in markers:
            elapsed = t - marker["offset_ms"] / 1000
            if 0 <= elapsed < 0.5:
                envelope = min(1.0, elapsed / 0.01, (0.5 - elapsed) / 0.01)
                value = round(32767 * 0.05 * envelope * math.sin(2 * math.pi * 440 * t))
                if marker["channel"] != "right":
                    left = value
                if marker["channel"] != "left":
                    right = value
        frames.extend(struct.pack("<hh", left, right))
    with wave.open(str(path), "wb") as out:
        out.setnchannels(2)
        out.setsampwidth(2)
        out.setframerate(rate)
        out.writeframes(frames)
    return markers


async def run_probe(
    hosts: list[str],
    duration: float,
    gain: float,
    latency_ms: int = 200,
    timing: str = "ptp",
    handshake_only: bool = False,
    report_path: Path | None = None,
    group_id: str | None = None,
    record_mic_path: Path | None = None,
    source: str = "wav",
    sample_rate: int = DEFAULT_RATE,
) -> int:
    if not math.isfinite(duration) or not 0 < duration <= MAX_SECONDS:
        raise ValueError("native probe duration must be >0 and <=5 seconds")
    if not math.isfinite(gain) or not 0 <= gain <= MAX_GAIN:
        raise ValueError("native probe gain must be between 0 and 0.1")
    if sample_rate not in SUPPORTED_RATES:
        raise ValueError(f"sample_rate must be one of {SUPPORTED_RATES}")
    executable = engine_path()
    session_id = str(uuid.uuid4())
    report = {
        "engine": "native-qualification",
        "hosts": hosts,
        "source": source,
        "requested_latency_ms": latency_ms,
        "timing": timing,
        "gain": gain,
        "duration_seconds": duration,
        "sample_rate": sample_rate,
        "acoustic_latency_ms": None,
        "stereo_verified": False,
        "qualified": False,
        "events": [],
    }
    previous_mute = None
    with tempfile.TemporaryDirectory(prefix="airplay-probe-") as directory:
        wav_path = Path(directory) / "quiet.wav"
        report["markers"] = write_probe_wav(wav_path, duration, sample_rate)
        command = {
            "version": 1,
            "id": "probe",
            "session_id": session_id,
            "command": "probe",
            "params": {
                "peers": [{"host": host, "port": 7000} for host in hosts],
                "source": source,
                "wav_path": str(wav_path),
                "duration_ms": round(duration * 1000),
                "latency_ms": latency_ms,
                "gain": gain,
                "timing": timing,
                "handshake_only": handshake_only,
                "group_id": group_id,
                "record_mic_path": str(record_mic_path.resolve()) if record_mic_path else None,
                "sample_rate": sample_rate,
            },
        }
        proc = await asyncio.create_subprocess_exec(
            str(executable),
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )
        stderr_task = asyncio.create_task(proc.stderr.read())
        success = False
        try:
            proc.stdin.write((json.dumps(command) + "\n").encode())
            await proc.stdin.drain()
            async with asyncio.timeout(45):
                while line := await proc.stdout.readline():
                    event = json.loads(line)
                    if event.get("session_id") != session_id:
                        continue
                    report["events"].append(event)
                    print(json.dumps(event, ensure_ascii=False), flush=True)
                    if event.get("event") == "streaming" and source == "loopback":
                        import winsound

                        from app import audio_volume

                        previous_mute = audio_volume.is_muted()
                        if previous_mute is None or not audio_volume.set_muted(True):
                            raise RuntimeError(
                                "Cannot mute local output for unambiguous acoustic test"
                            )
                        report["local_output_muted"] = True
                        winsound.PlaySound(
                            str(wav_path), winsound.SND_ASYNC | winsound.SND_FILENAME
                        )
                    if event.get("event") == "error":
                        break
                    if event.get("event") in {"stopped", "handshake_complete"}:
                        success = True
                        break
        except TimeoutError:
            report["error"] = "native qualification timed out"
            print(report["error"], flush=True)
        finally:
            if source == "loopback":
                import winsound

                winsound.PlaySound(None, 0)
                if previous_mute is not None:
                    from app import audio_volume

                    audio_volume.set_muted(previous_mute)
            if proc.returncode is None:
                stop = {"version": 1, "id": "stop", "session_id": session_id, "command": "stop"}
                try:
                    proc.stdin.write((json.dumps(stop) + "\n").encode())
                    await proc.stdin.drain()
                    proc.stdin.close()
                    await asyncio.wait_for(proc.wait(), timeout=10)
                except (TimeoutError, BrokenPipeError, ConnectionResetError):
                    proc.kill()
                    await proc.wait()
            stderr = (await stderr_task).decode(errors="replace")
            if stderr:
                report["stderr"] = stderr
            report["transport_completed"] = success and proc.returncode == 0
            if report_path is not None:
                report_path.parent.mkdir(parents=True, exist_ok=True)
                report_path.write_text(
                    json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
                )
    return 0 if report["transport_completed"] else 1
