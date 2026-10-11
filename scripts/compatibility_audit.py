"""Bounded PE import audit. Unknown APIs are evidence gaps, never claimed compatible."""

import argparse
import hashlib
import json
import re
import struct
from pathlib import Path

BASELINE = 17763
CATALOG = Path(__file__).with_name("windows-api-baseline.json")


class PEError(ValueError):
    pass


def inspect_pe(path: Path) -> dict:
    data = path.read_bytes()

    def unpack(fmt, offset):
        try:
            return struct.unpack_from(fmt, data, offset)
        except struct.error as error:
            raise PEError("Truncated PE structure") from error

    if data[:2] != b"MZ":
        raise PEError("Not a PE image")
    pe = unpack("<I", 60)[0]
    if data[pe : pe + 4] != b"PE\0\0":
        raise PEError("Invalid PE signature")
    machine, sections, _, _, _, optional_size, _ = unpack("<HHIIIHH", pe + 4)
    if sections > 96:
        raise PEError("Unreasonable section count")
    opt = pe + 24
    magic = unpack("<H", opt)[0]
    if magic not in (0x10B, 0x20B):
        raise PEError("Unknown optional header")
    bits = 64 if magic == 0x20B else 32
    directories = opt + (112 if bits == 64 else 96)
    count = unpack("<I", directories - 4)[0]
    headers = unpack("<I", opt + 60)[0]
    image_base = unpack("<Q" if bits == 64 else "<I", opt + (24 if bits == 64 else 28))[0]
    section_map = [unpack("<IIII", opt + optional_size + n * 40 + 8) for n in range(sections)]

    def offset(rva):
        if 0 <= rva < min(headers, len(data)):
            return rva
        for virtual_size, address, raw_size, raw in section_map:
            if address <= rva < address + max(virtual_size, raw_size):
                relative = rva - address
                if relative < raw_size and raw + relative < len(data):
                    return raw + relative
        raise PEError(f"Unmapped RVA 0x{rva:x}")

    def cstring(rva):
        start = offset(rva)
        end = data.find(b"\0", start, min(len(data), start + 1024))
        if end < 0:
            raise PEError("Unterminated import string")
        return data[start:end].decode("ascii", errors="strict")

    def directory(index):
        if index >= count:
            return 0, 0
        if directories + (index + 1) * 8 > opt + optional_size:
            raise PEError("Directory exceeds optional header")
        return unpack("<II", directories + index * 8)

    imports = []
    for index, delayed, size in ((1, False, 20), (13, True, 32)):
        rva, length = directory(index)
        if not rva:
            continue
        if length < size or length > 1024 * 1024:
            raise PEError("Invalid import directory size")
        terminated = False
        for n in range(min(length // size, 4096)):
            values = unpack("<" + "I" * (size // 4), offset(rva + n * size))
            if not any(values):
                terminated = True
                break
            if delayed:
                attributes, name, _, _, thunk, _, _, _ = values
                if not attributes & 1:
                    name -= image_base
                    thunk -= image_base
            else:
                thunk, _, _, name, iat = values
                thunk = thunk or iat
            dll = cstring(name).lower()
            for entry in range(65536):
                value = unpack("<Q" if bits == 64 else "<I", offset(thunk + entry * bits // 8))[0]
                if not value:
                    break
                symbol = f"#{value & 0xFFFF}" if value & (1 << (bits - 1)) else cstring(value + 2)
                imports.append({"dll": dll, "symbol": symbol, "delayed": delayed})
            else:
                raise PEError("Unterminated import thunk")
        if not terminated:
            raise PEError("Unterminated import directory")
    os_version = unpack("<HH", opt + 40)
    subsystem_version = unpack("<HH", opt + 48)
    return {
        "file": path.name,
        "sha256": hashlib.sha256(data).hexdigest(),
        "machine": hex(machine),
        "managed": bool(directory(14)[0]),
        "os_header": list(os_version),
        "subsystem_header": list(subsystem_version),
        "imports": imports,
    }


def classify(dll: str, symbol: str, catalog: dict) -> dict:
    rule = catalog["apis"].get(f"{dll.lower()}!{symbol}")
    if not rule:
        return {"status": "unconfirmed", "minimum_build": None, "source": None}
    minimum = rule["minimum_build"]
    return {"status": "supported" if minimum <= BASELINE else "above_baseline", **rule}


def source_inventory(root: Path, catalog: dict) -> list:
    entries = []
    pattern = re.compile(
        r'\[(?:DllImport|LibraryImport)\("([^"]+)"([^\]]*)\)\]\s*'
        r"(?:private|internal|public|protected|static|extern|partial|unsafe|\s)+"
        r"\S+\s+(\w+)\s*\(",
        re.MULTILINE,
    )
    for path in sorted((root / "desktop").rglob("*.cs")):
        if {"bin", "obj"} & set(path.parts):
            continue
        text = path.read_text(encoding="utf-8-sig")
        for match in pattern.finditer(text):
            dll, attributes, name = match.groups()
            explicit = re.search(r'EntryPoint\s*=\s*"([^"]+)"', attributes)
            symbol = explicit[1] if explicit else name
            if (
                not explicit
                and "CharSet.Unicode" in attributes
                and "ExactSpelling = true" not in attributes
            ):
                symbol += "W"
            entries.append(
                {
                    "file": path.relative_to(root).as_posix(),
                    "line": text[: match.start()].count("\n") + 1,
                    "kind": "pinvoke",
                    "dll": dll,
                    "symbol": symbol,
                    **classify(dll, symbol, catalog),
                }
            )
        for match in re.finditer(
            r"NativeLibrary\.(?:TryLoad|TryGetExport)|GetProcAddress|LoadLibrary", text
        ):
            entries.append(
                {
                    "file": path.relative_to(root).as_posix(),
                    "line": text[: match.start()].count("\n") + 1,
                    "kind": "dynamic-load",
                    "status": "unconfirmed",
                }
            )
    for path in sorted((root / "native" / "airflash-engine" / "src").rglob("*.rs")):
        text = path.read_text(encoding="utf-8")
        if "use windows::" not in text:
            continue
        # Include unknown Windows/COM call candidates, not only names in our catalog.
        # Constructors are intentionally retained for human review rather than silently missed.
        for match in re.finditer(r"\b([A-Z][A-Za-z0-9_]+)\s*\(", text):
            symbol = match[1]
            if symbol in {"Ok", "Err", "Some", "None", "Self", "Vec", "String", "Box"}:
                continue
            rules = [
                (key, rule) for key, rule in catalog["apis"].items() if key.endswith("!" + symbol)
            ]
            key = rules[0][0] if rules else "unknown!" + symbol
            entries.append(
                {
                    "file": path.relative_to(root).as_posix(),
                    "line": text[: match.start()].count("\n") + 1,
                    "kind": "rust-api" if rules else "rust-call-candidate",
                    "symbol": symbol,
                    **classify(key.split("!")[0], symbol, catalog),
                }
            )
        for match in re.finditer(r"GetProcAddress|LoadLibrary[AW]?", text):
            entries.append(
                {
                    "file": path.relative_to(root).as_posix(),
                    "line": text[: match.start()].count("\n") + 1,
                    "kind": "dynamic-load",
                    "status": "unconfirmed",
                }
            )
    return entries


def audit(paths: list[Path], root: Path | None = None) -> dict:
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    images, errors = [], []
    for path in dict.fromkeys(paths):
        if path.suffix.lower() not in (".exe", ".dll"):
            continue
        try:
            image = inspect_pe(path)
            for entry in image["imports"]:
                entry.update(classify(entry["dll"], entry["symbol"], catalog))
                # CLR-loaded managed assemblies do not use their PE loader thunk.
                entry["mandatory"] = not image["managed"] and not entry["delayed"]
            images.append(image)
            if not image["managed"] and image["machine"] != "0x8664":
                errors.append(f"Non-x64 native image: {path.name}")
            if not image["managed"] and any(
                version > [10, 0] for version in (image["os_header"], image["subsystem_header"])
            ):
                errors.append(f"PE loader minimum exceeds Windows 10: {path.name}")
            for entry in image["imports"]:
                if entry["mandatory"] and entry["status"] == "above_baseline":
                    errors.append(
                        f"Required API exceeds 17763: {path.name}: {entry['dll']}!{entry['symbol']}"
                    )
                if entry["mandatory"] and re.match(
                    r"(?:vcruntime|msvcp)140|ucrtbase", entry["dll"]
                ):
                    # .NET runtime DLLs have their own documented deployment requirements.
                    if path.name.lower() == "airflash-engine.exe":
                        errors.append(f"Engine has dynamic CRT dependency: {entry['dll']}")
        except (OSError, ValueError, UnicodeError) as error:
            errors.append(f"Cannot audit {path.name}: {error}")
    sources = source_inventory(root, catalog) if root else []
    for entry in sources:
        if entry["status"] == "above_baseline":
            errors.append(
                f"Source API exceeds 17763; guard review required: "
                f"{entry['file']}:{entry['line']}: {entry['symbol']}"
            )
    configuration = build_configuration(root) if root else None
    if configuration:
        errors.extend(configuration["errors"])
    return {
        "schema": 1,
        "baseline_build": BASELINE,
        "ok": bool(images) and not errors,
        "qualification": "Static audit only; unknown APIs and Win10 execution remain pending.",
        "images": images,
        "source_calls": sources,
        "build_configuration": configuration,
        "errors": errors,
        "unconfirmed_imports": sum(
            e["status"] == "unconfirmed" for i in images for e in i["imports"]
        ),
    }


def build_configuration(root: Path) -> dict:
    """Review CPU/deployment flags as well as loader imports; PE headers cannot prove ISA."""
    project = root / "desktop/AirFlash.App/AirFlash.App.csproj"
    native_script = root / "scripts/build-native.ps1"
    cargo_config = root / ".cargo/config.toml"
    files = [p for p in (project, native_script, cargo_config) if p.exists()]
    texts = {p.name: p.read_text(encoding="utf-8-sig") for p in files}
    errors = []
    app = texts.get(project.name, "")
    if (
        "<SelfContained>true</SelfContained>" not in app
        or "<RuntimeIdentifier>win-x64</RuntimeIdentifier>" not in app
    ):
        errors.append("Application must publish self-contained win-x64.")
    native = texts.get(native_script.name, "")
    if "target-feature=+crt-static" not in native:
        errors.append("Native build must explicitly select static CRT.")
    # The build script replaces encoded flags; reject accidental host-specific ISA requirements.
    if re.search(
        r"target-cpu[=\s]+(?:native|x86-64-v[234])|target-feature[^\r\n]*\+(?:avx|sse4|fma)",
        native,
        re.I,
    ):
        errors.append("Native build selects an ISA above generic x86-64.")
    return {
        "files": [
            {
                "file": p.relative_to(root).as_posix(),
                "sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
            }
            for p in files
        ],
        "runtime_support_source": "https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md",
        "cpu_requirement": (
            "Generic Rust x86-64 target; .NET JIT selects supported CPU features. "
            "Bundled native runtime ISA requires vendor support / execution verification."
        ),
        "callsite_review": [
            {
                "file": "desktop/AirFlash.App/Services/WindowThemeService.cs",
                "requirement": "DWM attributes 20/35/36 only on build >=22000",
                "guard": "WindowsCapabilities.WindowColors in Apply and ApplyAttributes",
                "source": "https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute",
            },
            {
                "file": "desktop/AirFlash.App/Services/WindowsCapabilities.cs",
                "requirement": "DNS-SD exports loaded from System32 and checked before discovery",
                "guard": "WindowsDiscovery.Start uses DnsSdAvailable",
            },
            {
                "file": "native/airflash-engine/src/live.rs",
                "requirement": "WASAPI COM interfaces/capture require real audio regression",
                "status": "unconfirmed",
            },
        ],
        "errors": errors,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--file", type=Path, action="append", default=[])
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--source-root", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    paths = args.file
    if args.manifest:
        paths += [
            Path(line)
            for line in args.manifest.read_text(encoding="utf-8-sig").splitlines()
            if line
        ]
    report = audit(paths, args.source_root)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    for error in report["errors"]:
        print(error)
    print(
        f"Audited {len(report['images'])} images; "
        f"{report['unconfirmed_imports']} unconfirmed imports"
    )
    return 0 if report["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
