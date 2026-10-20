"""Select upgrade fixtures without downloading, installing, or changing endpoints."""

import json
import os
import shutil
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
PWSH = shutil.which("pwsh")
pytestmark = pytest.mark.skipif(PWSH is None, reason="PowerShell 7 is required")


def release(tag, **extra):
    return {"tag_name": tag, "draft": False, "prerelease": False, **extra}


@pytest.mark.parametrize(
    ("current", "releases", "expected"),
    [
        ("0.3.1", [release("v0.4.0"), release("v0.3.1"), release("v0.3.0")], "v0.3.0"),
        ("0.4.1", [release("v0.3.0"), release("v0.4.0"), release("v0.3.1")], "v0.4.0"),
        ("0.3.1", [release("v0.4.0"), release("v0.3.1")], None),
        ("0.3.1", [], None),
        ("0.3.1", [release("v0.3.0", draft=True), release("v0.2.9")], "v0.2.9"),
        ("0.3.1", [release("v0.3.0", prerelease=True), release("v0.2.9")], "v0.2.9"),
        (
            "0.3.1",
            [release("v0.3.0-rc.1"), release("v00.3.0"), release("unknown")],
            None,
        ),
        ("0.3.11", [release("v0.3.9"), release("v0.3.10")], "v0.3.10"),
        ("0.3.1", [release("v999999999999.0.0"), release("v0.3.0")], "v0.3.0"),
        ("0.3.1", [release(["v0.3.0", "v0.2.9"]), release("v0.2.8")], "v0.2.8"),
        ("0.3.1", [None, {}, release(None), release("v0.3.0", draft="false")], None),
        ("0.3.1", [release("v0.3.0\n"), release("v0.2.9")], "v0.2.9"),
    ],
)
@pytest.mark.parametrize("seed_match", [False, True])
def test_selects_only_latest_strictly_lower_stable_version(current, releases, expected, seed_match):
    result = subprocess.run(
        [
            PWSH,
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest; "
            "$case=$env:AIRFLASH_SELECTION_CASE|ConvertFrom-Json; "
            "if($case.seed_match){$null='v0.3.0' -match '^v(.*)$'}; "
            ". $env:AIRFLASH_SELECTION_SCRIPT; "
            "$selected=Select-AirFlashInstallerPredecessor -Releases @($case.releases) "
            "-CurrentVersion $case.current; "
            "@{tag=if($null -eq $selected){$null}else{$selected.tag_name}}"
            "|ConvertTo-Json -Compress",
        ],
        env={
            **os.environ,
            "AIRFLASH_SELECTION_SCRIPT": str(ROOT / "scripts/installer-test-common.ps1"),
            "AIRFLASH_SELECTION_CASE": json.dumps(
                {"current": current, "releases": releases, "seed_match": seed_match}
            ),
        },
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=30,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert json.loads(result.stdout)["tag"] == expected


@pytest.mark.parametrize(
    ("current", "expected"),
    [
        ("0.3.1", "0.2.3"),
        ("0.2.4", "0.2.3"),
        ("0.2.3", "0.2.2"),
        ("0.2.0", "0.1.65535"),
        ("0.1.0", "0.0.65535"),
        ("0.0.1", "0.0.0"),
        ("255.255.65535", "0.2.3"),
    ],
)
def test_synthetic_fixture_version_is_strictly_lower(current, expected):
    result = subprocess.run(
        [
            PWSH,
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest; "
            ". $env:AIRFLASH_SELECTION_SCRIPT; "
            "$current=[Version]$env:AIRFLASH_CURRENT_VERSION; "
            "$fixture=Get-AirFlashInstallerFixtureVersion -CurrentVersion $current; "
            "@{version=$fixture.ToString();lower=($fixture -lt $current)}|ConvertTo-Json",
        ],
        env={
            **os.environ,
            "AIRFLASH_SELECTION_SCRIPT": str(ROOT / "scripts/installer-test-common.ps1"),
            "AIRFLASH_CURRENT_VERSION": current,
        },
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=30,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert json.loads(result.stdout) == {"version": expected, "lower": True}


@pytest.mark.parametrize("current", ["0.0.0", "0.1", "0.1.0.0", "256.0.0", "0.256.0", "0.0.65536"])
def test_synthetic_fixture_rejects_no_lower_or_invalid_msi_version(current):
    result = subprocess.run(
        [
            PWSH,
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest; "
            ". $env:AIRFLASH_SELECTION_SCRIPT; "
            "try { Get-AirFlashInstallerFixtureVersion "
            "-CurrentVersion $env:AIRFLASH_CURRENT_VERSION; "
            "throw 'Unexpected accepted fixture.' } catch { $_.Exception.Message; exit 7 }",
        ],
        env={
            **os.environ,
            "AIRFLASH_SELECTION_SCRIPT": str(ROOT / "scripts/installer-test-common.ps1"),
            "AIRFLASH_CURRENT_VERSION": current,
        },
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=30,
    )
    assert result.returncode == 7, result.stdout + result.stderr
    message = (
        "No strictly lower MSI version"
        if current == "0.0.0"
        else "three components within MSI limits"
    )
    assert message in result.stdout


def test_minimum_version_is_rejected_before_any_installer_or_user_data_operation(tmp_path):
    fixture = tmp_path / "isolated checkout"
    scripts = fixture / "scripts"
    scripts.mkdir(parents=True)
    for name in ["test-installer.ps1", "installer-test-common.ps1"]:
        shutil.copy2(ROOT / "scripts" / name, scripts / name)
    dist = fixture / "dist"
    dist.mkdir()
    # Synthetic metadata has version components 0.0.0; neither file is executed.
    (dist / "AirFlash.exe").write_bytes(b"synthetic unversioned input")
    (dist / "AirFlash-0.0.0.msi").write_bytes(b"synthetic package")
    environment = {
        key: value
        for key, value in os.environ.items()
        if key.casefold() not in {"github_actions", "runner_environment", "appdata", "programfiles"}
    }
    # Flags exercise the real script's guarded entry; every process launch is
    # intercepted below, and all filesystem paths point into this fixture.
    environment.update(
        GITHUB_ACTIONS="true",
        RUNNER_ENVIRONMENT="github-hosted",
        APPDATA=str(fixture / "user-data"),
        ProgramFiles=str(fixture / "program-files"),
        AIRFLASH_LIFECYCLE_SCRIPT=str(scripts / "test-installer.ps1"),
    )
    result = subprocess.run(
        [
            PWSH,
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "$ErrorActionPreference='Stop'; "
            "$global:operations=[Collections.Generic.List[string]]::new(); "
            "function global:gh { $global:LASTEXITCODE=0; '[]' }; "
            "function global:Start-Process { "
            "param([string]$FilePath,$ArgumentList,[switch]$Wait,[switch]$PassThru,$WindowStyle); "
            "$global:operations.Add($FilePath); throw 'Fake installer invocation blocked.' }; "
            "try { & $env:AIRFLASH_LIFECYCLE_SCRIPT; $errorMessage=$null } "
            "catch { $errorMessage=$_.Exception.Message }; "
            "@{error=$errorMessage;operations=@($global:operations);"
            "user_data_created=(Test-Path -LiteralPath (Join-Path $env:APPDATA 'AirFlash'))}"
            "|ConvertTo-Json -Compress",
        ],
        env=environment,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=30,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    evidence = json.loads(result.stdout)
    assert "No strictly lower MSI version" in evidence["error"]
    assert evidence["operations"] == []
    assert evidence["user_data_created"] is False
