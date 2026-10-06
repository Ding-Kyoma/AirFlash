"""Exercise the real reservation script with a fake git command and no network."""

import json
import os
import shutil
import subprocess
from pathlib import Path

import pytest

HEAD = "a" * 40
SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "release-version.ps1"
PWSH = shutil.which("pwsh")
pytestmark = pytest.mark.skipif(PWSH is None, reason="PowerShell 7 is required")

HARNESS = r"""
$ErrorActionPreference = 'Stop'
$script:releaseCase = $env:AIRFLASH_TEST_CASE | ConvertFrom-Json
$script:pushes = [Collections.Generic.List[object]]::new()
function git {
    $global:LASTEXITCODE = 0
    switch ($args[2]) {
        'branch' { $script:releaseCase.branch }
        'status' { if ($script:releaseCase.dirty) { ' M tracked.txt' } }
        'rev-parse' { $script:releaseCase.head }
        'remote' { 'https://github.com/Ding-Kyoma/AirFlash.git' }
        'ls-remote' { $script:releaseCase.refs }
        'push' {
            $script:pushes.Add(@($args))
            $global:LASTEXITCODE = $script:releaseCase.push_exit
        }
        default { throw "Unexpected git command: $args" }
    }
}
$env:GITHUB_ACTIONS = if ($script:releaseCase.actions) { 'true' } else { 'false' }
. $env:AIRFLASH_TEST_SCRIPT
$version = $null; $errorMessage = $null
try {
    $version = Reserve-ReleaseVersion $env:AIRFLASH_TEST_ROOT `
        $script:releaseCase.reserved $script:releaseCase.channel `
        -NewVersion $script:releaseCase.new_version
} catch { $errorMessage = $_.Exception.Message }
$statePath = Join-Path $env:AIRFLASH_TEST_ROOT 'artifacts/release-version.txt'
$state = if (Test-Path -LiteralPath $statePath) {
    (Get-Content -LiteralPath $statePath -Raw).Trim()
} else { $null }
[pscustomobject]@{
    version = $version; error = $errorMessage; state = $state; pushes = $script:pushes.ToArray()
} | ConvertTo-Json -Depth 5 -Compress
"""


def reserve(tmp_path, *, source="0.2.0", local=None, refs=None, **overrides):
    project = tmp_path / "desktop" / "AirFlash.App" / "AirFlash.App.csproj"
    project.parent.mkdir(parents=True)
    project.write_text(
        f"<Project><PropertyGroup><Version>{source}</Version></PropertyGroup></Project>",
        encoding="utf-8",
    )
    if local is not None:
        state = tmp_path / "artifacts" / "release-version.txt"
        state.parent.mkdir()
        state.write_text(local, encoding="utf-8")
    case = {
        "branch": "main",
        "dirty": False,
        "head": HEAD,
        "refs": refs if refs is not None else [f"{HEAD}\trefs/tags/reserved/0.2.18"],
        "push_exit": 0,
        "actions": False,
        "reserved": "",
        "channel": "stable",
        "new_version": "",
        **overrides,
    }
    result = subprocess.run(
        [PWSH, "-NoProfile", "-NonInteractive", "-Command", HARNESS],
        env={
            **os.environ,
            "AIRFLASH_TEST_ROOT": str(tmp_path),
            "AIRFLASH_TEST_SCRIPT": str(SCRIPT),
            "AIRFLASH_TEST_CASE": json.dumps(case),
        },
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=20,
    )
    assert result.returncode == 0, result.stderr
    return json.loads(result.stdout)


@pytest.mark.parametrize("requested, expected", [("0.3.0", "0.3.0"), ("", "0.2.19")])
def test_fresh_version_is_reserved_atomically(tmp_path, requested, expected):
    result = reserve(tmp_path, new_version=requested)
    assert result["error"] is None
    assert result["version"] == result["state"] == expected
    assert result["pushes"][0][2:] == [
        "push",
        "origin",
        f"{HEAD}:refs/tags/reserved/{expected}",
        f"--force-with-lease=refs/tags/reserved/{expected}:",
    ]


@pytest.mark.parametrize(
    "version",
    ["0.2.18", "0.2.17", "0.1.0", "0.3", "0.3.0.1", "0.03.0", "-1.0.0",
     "256.0.0", "0.256.0", "0.3.65536", "999999999999.0.0", "0.3.0-rc.1"],
)
def test_invalid_or_used_version_never_pushes(tmp_path, version):
    result = reserve(tmp_path, new_version=version)
    assert result["error"]
    assert result["pushes"] == []
    assert result["state"] is None


@pytest.mark.parametrize("ref", ["reserved/0.3.0", "v0.3.0", "reserved/0.3.1"])
def test_remote_version_floor_prevents_reuse(tmp_path, ref):
    result = reserve(tmp_path, new_version="0.3.0", refs=[f"{HEAD}\trefs/tags/{ref}"])
    assert result["error"]
    assert result["pushes"] == []


@pytest.mark.parametrize("floor", ["source", "local"])
def test_local_and_source_floors_prevent_regression(tmp_path, floor):
    result = reserve(tmp_path, new_version="0.3.0", **{floor: "0.3.0"})
    assert result["error"]
    assert result["pushes"] == []
    assert result["state"] == ("0.3.0" if floor == "local" else None)


def test_atomic_conflict_does_not_overwrite_local_record(tmp_path):
    result = reserve(tmp_path, new_version="0.3.0", local="0.2.18", push_exit=1)
    assert "reservation failed" in result["error"].lower()
    assert len(result["pushes"]) == 1
    assert result["state"] == "0.2.18"


def test_new_and_pre_reserved_versions_are_exclusive(tmp_path):
    result = reserve(tmp_path, new_version="0.3.1", reserved="0.3.0", actions=True)
    assert "mutually exclusive" in result["error"]
    assert result["pushes"] == []


def test_same_job_can_build_its_fresh_reservation(tmp_path):
    result = reserve(
        tmp_path, reserved="0.3.0", actions=True, local="0.3.0",
        refs=[f"{HEAD}\trefs/tags/reserved/0.3.0"],
    )
    assert result["error"] is None
    assert result["version"] == "0.3.0"
    assert result["pushes"] == []


@pytest.mark.parametrize("actions, local", [(False, "0.3.0"), (True, None)])
def test_old_reservation_cannot_be_used_by_local_or_fresh_job(tmp_path, actions, local):
    result = reserve(
        tmp_path, reserved="0.3.0", actions=actions, local=local,
        refs=[f"{HEAD}\trefs/tags/reserved/0.3.0"],
    )
    assert "this GitHub Actions job" in result["error"]
    assert result["pushes"] == []


@pytest.mark.parametrize(
    "refs",
    [
        [f"{'b' * 40}\trefs/tags/reserved/0.3.0"],
        [f"{HEAD}\trefs/tags/reserved/0.3.0", f"{HEAD}\trefs/tags/v0.3.0"],
        [f"{HEAD}\trefs/tags/reserved/0.3.0", f"{HEAD}\trefs/tags/reserved/0.3.1"],
    ],
)
def test_pre_reserved_build_checks_commit_publication_and_newer_versions(tmp_path, refs):
    result = reserve(tmp_path, reserved="0.3.0", actions=True, local="0.3.0", refs=refs)
    assert result["error"]
    assert result["pushes"] == []


@pytest.mark.parametrize("overrides", [{"branch": "codex/dev"}, {"dirty": True}])
def test_release_requires_clean_channel_branch(tmp_path, overrides):
    result = reserve(tmp_path, new_version="0.3.0", **overrides)
    assert result["error"]
    assert result["pushes"] == []


def test_preview_channel_still_reserves_without_publishing(tmp_path):
    result = reserve(
        tmp_path, branch="codex/nic-discovery-preview", channel="preview", new_version="0.3.0",
    )
    assert result["error"] is None
    assert result["version"] == "0.3.0"


@pytest.mark.parametrize(
    "previous, expected", [("0.2.65535", "0.3.0"), ("0.255.65535", "1.0.0")],
)
def test_automatic_increment_obeys_msi_component_limits(tmp_path, previous, expected):
    result = reserve(tmp_path, refs=[f"{HEAD}\trefs/tags/reserved/{previous}"])
    assert result["error"] is None
    assert result["version"] == expected


def test_exhausted_msi_range_never_pushes(tmp_path):
    result = reserve(tmp_path, refs=[f"{HEAD}\trefs/tags/reserved/255.255.65535"])
    assert "range exhausted" in result["error"]
    assert result["pushes"] == []
