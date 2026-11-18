"""Exercise the collector with synthetic OS/events and disposable profile paths."""

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

COLLECTOR_HARNESS = r"""
$ErrorActionPreference = 'Stop'
$env:PROCESSOR_ARCHITECTURE = 'AMD64'
$env:PROCESSOR_ARCHITEW6432 = ''
$global:diagnosticCalls = [Collections.Generic.List[string]]::new()
function global:Get-ItemProperty {
    [CmdletBinding()]
    param([string]$Path)
    if ($Path -cne 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion') {
        throw 'Unexpected synthetic registry request.'
    }
    $global:diagnosticCalls.Add('synthetic-os')
    [pscustomobject]@{ CurrentBuildNumber='17763'; UBR=123; DisplayVersion='Synthetic';
        EditionID='FixtureOnly' }
}
function global:Get-WinEvent {
    [CmdletBinding()]
    param([hashtable]$FilterHashtable, [int]$MaxEvents)
    if ($FilterHashtable.LogName -cne 'Application' -or $MaxEvents -ne 30) {
        throw 'Unexpected synthetic event request.'
    }
    $global:diagnosticCalls.Add('synthetic-events')
    [pscustomobject]@{ TimeCreated=[DateTime]::Parse('2026-01-01T00:00:00Z').ToUniversalTime();
        Id=1000; ProviderName='AirFlashFixture'
        Message=('AirFlash synthetic failure ' + $env:USERPROFILE) }
}
$root = [IO.Path]::GetFullPath($env:AIRFLASH_DIAGNOSTIC_TEST_ROOT)
$callsPath = Join-Path $root 'service-calls.json'
if (-not [IO.Path]::IsPathRooted($env:AIRFLASH_DIAGNOSTIC_TEST_ROOT) -or
    [IO.Path]::GetDirectoryName($callsPath) -cne $root) { throw 'Invalid fixture output boundary.' }
try {
    if ($env:AIRFLASH_DIAGNOSTIC_MODE -ceq 'validate-mocks') {
        $null = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        $null = Get-WinEvent -FilterHashtable @{LogName='Application'} -MaxEvents 30
    } else {
        # Fail before invoking the collector if a future implementation bypasses
        # the unqualified service calls intercepted by this fixture.
        $tokens=$null; $errors=$null
        $ast=[Management.Automation.Language.Parser]::ParseFile(
            $env:AIRFLASH_DIAGNOSTIC_TEST_SCRIPT,[ref]$tokens,[ref]$errors)
        if ($errors.Count) { throw $errors[0] }
        $services=@($ast.FindAll({param($node)
            $node -is [Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -match '(?:^|\\)(?:Get-ItemProperty|Get-WinEvent)$'
        },$true))
        if ($services.Count -ne 2 -or @($services | Where-Object {
            $_.GetCommandName() -cnotin @('Get-ItemProperty','Get-WinEvent')
        }).Count) { throw 'Collector service boundary changed; review fixture before execution.' }
        $arguments=@{Executable=(Join-Path $root 'invalid.exe');OutputDirectory=$root}
        & $env:AIRFLASH_DIAGNOSTIC_TEST_SCRIPT @arguments
    }
} finally {
    $json=ConvertTo-Json -InputObject @($global:diagnosticCalls.ToArray()) -Compress
    $stream=[IO.File]::Open($callsPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $bytes=[Text.Encoding]::UTF8.GetBytes($json); $stream.Write($bytes,0,$bytes.Length) }
    finally { $stream.Dispose() }
}
"""


def fixture_environment(tmp_path):
    paths = {
        "USERPROFILE": tmp_path / "synthetic-profile",
        "APPDATA": tmp_path / "synthetic-roaming",
        "LOCALAPPDATA": tmp_path / "synthetic-local",
    }
    for path in paths.values():
        path.mkdir()
    return {
        **os.environ,
        **{key: str(path) for key, path in paths.items()},
        "PROCESSOR_ARCHITECTURE": "AMD64",
        "PROCESSOR_ARCHITEW6432": "",
        "AIRFLASH_DIAGNOSTIC_TEST_SCRIPT": str(SCRIPT),
        "AIRFLASH_DIAGNOSTIC_TEST_ROOT": str(tmp_path),
    }


def test_collector_service_mocks_are_available_without_invoking_collector(tmp_path):
    run = subprocess.run(
        [POWERSHELL, "-NoProfile", "-NonInteractive", "-Command", COLLECTOR_HARNESS],
        env={**fixture_environment(tmp_path), "AIRFLASH_DIAGNOSTIC_MODE": "validate-mocks"},
        capture_output=True,
        timeout=20,
    )
    assert run.returncode == 0, run.stderr.decode(errors="replace")
    assert json.loads((tmp_path / "service-calls.json").read_text()) == [
        "synthetic-os", "synthetic-events"
    ]
    assert not list(tmp_path.glob("AirFlash-diagnostics-*"))


def test_launch_failure_still_produces_bounded_archive(tmp_path):
    exe = tmp_path / "invalid.exe"
    with exe.open("xb") as output:
        output.write(b"This is not an executable")
    run = subprocess.run(
        [POWERSHELL, "-NoProfile", "-NonInteractive", "-Command", COLLECTOR_HARNESS],
        env={**fixture_environment(tmp_path), "AIRFLASH_DIAGNOSTIC_MODE": "collect"},
        capture_output=True,
        timeout=30,
    )
    assert run.returncode == 0, run.stderr.decode(errors="replace")
    assert json.loads((tmp_path / "service-calls.json").read_text()) == [
        "synthetic-os", "synthetic-events"
    ]
    archive = next(tmp_path.glob("AirFlash-diagnostics-*.zip"))
    with zipfile.ZipFile(archive) as zip_file:
        assert zip_file.namelist() == ["report.json"]
        report = json.loads(zip_file.read("report.json"))
    assert report["launch"]["win32_error"] in (193, 216)  # Bad image / machine mismatch.
    assert report["application"]["sha256"] == hashlib.sha256(exe.read_bytes()).hexdigest()
    assert report["os"] == {
        "build": "17763", "ubr": 123, "display_version": "Synthetic", "edition": "FixtureOnly",
        "os_architecture": "AMD64", "collector_architecture": "AMD64",
    }
    assert len(report["events"]) == 1
    assert report["events"][0]["provider"] == "AirFlashFixture"
    assert report["events"][0]["message"] == "AirFlash synthetic failure [user-path]"
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
    $path=Join-Path $directory 'protected.json'
    if ([IO.Path]::GetDirectoryName($path) -cne $directory) {
        throw 'Invalid fixture output boundary.'
    }
    $stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $bytes=[Text.Encoding]::UTF8.GetBytes($protected); $stream.Write($bytes,0,$bytes.Length) }
    finally { $stream.Dispose() }
    """
    run = subprocess.run(
        [POWERSHELL, "-NoProfile", "-NonInteractive", "-Command", harness],
        env=fixture_environment(tmp_path),
        capture_output=True,
        timeout=20,
    )
    assert run.returncode == 0, run.stderr.decode(errors="replace")
    result = json.loads((tmp_path / "protected.json").read_text(encoding="utf-8-sig"))
    assert result["pin"] == result["private_key"] == result["password"] == "[redacted]"
    assert result["path"] == "[user-path]"
    assert result["clean"] == "startup failed"
