"""Check the PCM tap report against signals with known level and band changes."""

import json
import wave

import numpy as np

from scripts.audio_report import compare, wrap


def write_wav(path, data, rate=44100):
    codes = np.clip(np.round(np.asarray(data) * 32768.0), -32768, 32767).astype("<i2")
    channels = codes.shape[1] if codes.ndim > 1 else 1
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(channels)
        handle.setsampwidth(2)
        handle.setframerate(rate)
        handle.writeframes(codes.reshape(-1).tobytes())
    return path


def tone(frequency, amplitude=0.25, rate=44100, seconds=0.5):
    t = np.arange(int(rate * seconds)) / rate
    return np.stack([amplitude * np.sin(2 * np.pi * frequency * t)] * 2, axis=1)


def test_wrap_reproduces_the_raw_tap(tmp_path):
    rate, frames = 44100, 1024
    codes = ((np.arange(frames * 2) * 37) % 40000 - 20000).astype("<i2")
    raw = tmp_path / "tap.raw"
    raw.write_bytes(codes.tobytes())
    raw.with_suffix(".json").write_text(
        json.dumps({"rate": rate, "channels": 2, "bits": 16, "frames": frames, "capped": False}),
        encoding="utf-8",
    )
    summary = wrap(raw, tmp_path / "tap.wav")
    assert summary == {"wav": str(tmp_path / "tap.wav"), "rate": rate, "frames": frames}
    with wave.open(str(tmp_path / "tap.wav"), "rb") as handle:
        assert handle.getframerate() == rate
        assert handle.getnchannels() == 2
        assert handle.getnframes() == frames
        assert handle.readframes(frames) == codes.tobytes()


def test_24_bit_tap_preserves_and_compares_the_lowest_bit(tmp_path):
    raw = tmp_path / "tap.raw"
    raw.with_suffix(".json").write_text(
        json.dumps({"rate": 48000, "channels": 2, "bits": 24}), encoding="utf-8"
    )
    pcm = bytes.fromhex("000080 ffff7f 0100ff 020001")
    raw.write_bytes(pcm)
    reference = tmp_path / "a.wav"
    assert wrap(raw, reference)["frames"] == 2
    with wave.open(str(reference), "rb") as handle:
        assert handle.getsampwidth() == 3
        assert handle.readframes(2) == pcm

    altered = tmp_path / "b.wav"
    raw.write_bytes(pcm[:-3] + bytes.fromhex("030001"))
    wrap(raw, altered)
    report = compare(reference, altered)
    assert report["identical"] is False
    assert report["max_difference_lsb"] == 1


def test_identical_files_report_as_identical(tmp_path):
    first = write_wav(tmp_path / "a.wav", tone(1000))
    second = write_wav(tmp_path / "b.wav", tone(1000))
    report = compare(first, second)
    assert report["identical"] is True
    assert report["max_difference_lsb"] == 0
    assert report["null_db"] < -100
    assert abs(report["level_delta_db"]) < 0.01


def test_a_level_change_and_a_band_change_show_up(tmp_path):
    reference = tone(1000) + tone(12000, amplitude=0.25)
    quieter = reference * 0.5
    first = write_wav(tmp_path / "reference.wav", reference)
    second = write_wav(tmp_path / "quieter.wav", quieter)
    report = compare(first, second)
    assert report["identical"] is False
    assert -6.1 < report["level_delta_db"] < -5.9
    assert report["null_db"] > -10.0


def test_a_single_lsb_difference_is_reported_rather_than_hidden(tmp_path):
    reference = tone(1000)
    altered = reference.copy()
    altered[100, 0] += 1.0 / 32768.0
    first = write_wav(tmp_path / "reference.wav", reference)
    second = write_wav(tmp_path / "altered.wav", altered)
    report = compare(first, second)
    assert report["identical"] is False
    assert report["max_difference_lsb"] == 1


def test_the_high_band_only_moves_for_high_frequency_content(tmp_path):
    low = write_wav(tmp_path / "low.wav", tone(1000))
    low_again = write_wav(tmp_path / "low_again.wav", tone(1000))
    assert abs(compare(low, low_again)["bands"]["16000"]) < 0.01
    filtered = tone(1000) + tone(16000, amplitude=0.0025)
    high = write_wav(tmp_path / "high.wav", filtered)
    assert compare(low, high)["bands"]["16000"] > 6.0
