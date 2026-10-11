import struct
from pathlib import Path

import pytest

from scripts.compatibility_audit import PEError, audit, classify, inspect_pe


def pe_fixture(path: Path, symbol="GetMachineTypeAttributes", delayed=False, ordinal=False):
    """A real minimal import directory, independently built from the PE layout."""
    data = bytearray(2048)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 60, 128)
    data[128:132] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", data, 132, 0x8664, 1, 0, 0, 0, 240, 0)
    opt = 152
    struct.pack_into("<H", data, opt, 0x20B)
    struct.pack_into("<Q", data, opt + 24, 0x140000000)
    struct.pack_into("<HH", data, opt + 40, 6, 0)
    struct.pack_into("<HH", data, opt + 48, 6, 0)
    struct.pack_into("<I", data, opt + 60, 512)
    struct.pack_into("<I", data, opt + 108, 16)
    size, index = (64, 13) if delayed else (40, 1)
    struct.pack_into("<II", data, opt + 112 + index * 8, 4096, size)
    struct.pack_into("<IIII", data, opt + 240 + 8, 1536, 4096, 1536, 512)
    if delayed:
        struct.pack_into("<8I", data, 512, 1, 4200, 0, 0, 4256, 0, 0, 0)
    else:
        struct.pack_into("<5I", data, 512, 4256, 0, 0, 4200, 4256)
    data[616:629] = b"kernel32.dll\0"
    struct.pack_into("<Q", data, 672, (1 << 63) | 21 if ordinal else 4352)
    encoded = b"\0\0" + symbol.encode() + b"\0"
    data[768 : 768 + len(encoded)] = encoded
    path.write_bytes(data)
    return path


def test_required_new_api_blocks_build(tmp_path):
    report = audit([pe_fixture(tmp_path / "engine.exe")])
    assert not report["ok"]
    assert "Required API exceeds 17763" in report["errors"][0]


def test_delay_import_requires_callsite_review_not_loader_failure(tmp_path):
    report = audit([pe_fixture(tmp_path / "engine.exe", delayed=True)])
    assert report["ok"]
    entry = report["images"][0]["imports"][0]
    assert entry["delayed"] and not entry["mandatory"]
    assert entry["status"] == "above_baseline"


def test_ordinal_and_unknown_api_are_unconfirmed(tmp_path):
    report = audit([pe_fixture(tmp_path / "engine.exe", ordinal=True)])
    assert report["ok"] and report["unconfirmed_imports"] == 1
    assert report["images"][0]["imports"][0]["symbol"] == "#21"
    assert classify("kernel32.dll", "FutureApi", {"apis": {}})["status"] == "unconfirmed"


def test_old_loader_header_does_not_prove_api_support(tmp_path):
    image = inspect_pe(pe_fixture(tmp_path / "engine.exe"))
    assert image["os_header"] == [6, 0]
    assert image["imports"][0]["symbol"] == "GetMachineTypeAttributes"


def test_supported_api_is_documented(tmp_path):
    report = audit([pe_fixture(tmp_path / "engine.exe", symbol="GetSystemTimePreciseAsFileTime")])
    assert report["ok"]
    entry = report["images"][0]["imports"][0]
    assert entry["status"] == "supported" and entry["source"].startswith(
        "https://learn.microsoft.com/"
    )


def test_truncated_and_unmapped_imports_fail_closed(tmp_path):
    path = tmp_path / "broken.exe"
    path.write_bytes(b"MZ")
    with pytest.raises(PEError):
        inspect_pe(path)
    pe_fixture(path)
    data = bytearray(path.read_bytes())
    struct.pack_into("<I", data, 512, 0xFFFFFFF0)
    path.write_bytes(data)
    assert not audit([path])["ok"]


def test_no_images_cannot_pass():
    assert not audit([])["ok"]


def test_dynamic_source_and_unknown_rust_calls_are_not_silently_missed(tmp_path):
    from scripts.compatibility_audit import source_inventory

    native = tmp_path / "native/airflash-engine/src"
    native.mkdir(parents=True)
    (native / "audio.rs").write_text(
        "use windows::Win32;\nclient.FutureAudioApi();\nGetProcAddress(handle);", encoding="utf-8"
    )
    entries = source_inventory(tmp_path, {"apis": {}})
    assert any(
        e.get("symbol") == "FutureAudioApi" and e["status"] == "unconfirmed" for e in entries
    )
    assert any(e["kind"] == "dynamic-load" for e in entries)
