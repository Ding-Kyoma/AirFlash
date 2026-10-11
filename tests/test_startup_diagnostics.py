"""Run the independent collector with Windows' stock PowerShell 5.1."""

import hashlib
import json
import os
import shutil
import subprocess
import zipfile
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts/collect-startup-diagnostics.ps1"
POWERSHELL = shutil.which("powershell.exe")
pytestmark = pytest.mark.skipif(POWERSHELL is None, reason="Windows PowerShell 5.1 required")


def test_launch_failure_still_produces_bounded_archive(tmp_path):
    exe = tmp_path / "invalid.exe"
    exe.write_bytes(b"This is not an executable")
    run = subprocess.run(
        [
            POWERSHELL,
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(SCRIPT),
            "-Executable",
            str(exe),
            "-OutputDirectory",
            str(tmp_path),
        ],
        capture_output=True,
        timeout=30,
    )
    assert run.returncode == 0, run.stderr.decode(errors="replace")
    archive = next(tmp_path.glob("AirFlash-diagnostics-*.zip"))
    with zipfile.ZipFile(archive) as zip_file:
        assert zip_file.namelist() == ["report.json"]
        report = json.loads(zip_file.read("report.json"))
    assert report["launch"]["win32_error"] in (193, 216)  # Bad image / machine mismatch.
    assert report["application"]["sha256"] == hashlib.sha256(exe.read_bytes()).hexdigest()
    assert report["os"]["build"] and report["os"]["collector_architecture"]
    assert str(tmp_path).lower() not in json.dumps(report).lower()


def test_redaction_preserves_json_and_removes_sensitive_labels(tmp_path):
    # Load the actual function AST without invoking the collector or starting any process.
    harness = r"""
    $ErrorActionPreference = 'Stop'
    $directory = $env:AIRFLASH_DIAGNOSTIC_TEST_ROOT
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $env:AIRFLASH_DIAGNOSTIC_TEST_SCRIPT, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    $function = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Protect-Text'
    }, $true)
    Invoke-Expression $function.Extent.Text
    $fixture = @{ pin = 123456; private_key = 'secret-material'; password = 'p"ass';
        path = $env:USERPROFILE; clean = 'startup failed' } | ConvertTo-Json
    $protected = Protect-Text $fixture
    $protected | ConvertFrom-Json | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory 'protected.json'), $protected)
    """
    run = subprocess.run(
        [POWERSHELL, "-NoProfile", "-NonInteractive", "-Command", harness],
        env={
            **os.environ,
            "AIRFLASH_DIAGNOSTIC_TEST_SCRIPT": str(SCRIPT),
            "AIRFLASH_DIAGNOSTIC_TEST_ROOT": str(tmp_path),
        },
        capture_output=True,
        timeout=20,
    )
    assert run.returncode == 0, run.stderr.decode(errors="replace")
    result = json.loads((tmp_path / "protected.json").read_text(encoding="utf-8-sig"))
    assert result["pin"] == result["private_key"] == result["password"] == "[redacted]"
    assert result["path"] == "[user-path]"
    assert result["clean"] == "startup failed"
