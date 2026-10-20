"""Exercise local development-version discovery with a fake Git command."""

import json
import os
import shutil
import subprocess
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "dev-version.ps1"
PWSH = shutil.which("pwsh")
pytestmark = pytest.mark.skipif(PWSH is None, reason="PowerShell 7 is required")

HARNESS = r"""
$ErrorActionPreference = 'Stop'
$script:versionCase = $env:AIRFLASH_VERSION_CASE | ConvertFrom-Json
function git {
    $global:LASTEXITCODE = 0
    if ($args -contains 'tag') {
        $global:LASTEXITCODE = [int]$script:versionCase.tag_exit
        $script:versionCase.tags
    } elseif ($args -contains 'rev-parse') {
        $script:versionCase.commit
    } elseif ($args -contains 'status') {
        $script:versionCase.status
    } else {
        throw "Unexpected git command: $args"
    }
}
. $env:AIRFLASH_VERSION_SCRIPT
$value = $null; $errorMessage = $null
try {
    $value = Get-DevelopmentVersion -Root $env:AIRFLASH_VERSION_ROOT
} catch {
    $errorMessage = $_.Exception.Message
}
[pscustomobject]@{ version = $value; error = $errorMessage } |
    ConvertTo-Json -Depth 5 -Compress
"""


def version(tmp_path, **overrides):
    case = {
        "tags": ["v0.3.3-rc.1", "v0.4.0", "v0.3.12", "reserved/0.4.7"],
        "commit": "3f2a9c1e",
        "status": "",
        "tag_exit": 0,
        **overrides,
    }
    result = subprocess.run(
        [PWSH, "-NoProfile", "-NonInteractive", "-Command", HARNESS],
        env={
            **os.environ,
            "AIRFLASH_VERSION_ROOT": str(tmp_path),
            "AIRFLASH_VERSION_SCRIPT": str(SCRIPT),
            "AIRFLASH_VERSION_CASE": json.dumps(case),
        },
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=20,
    )
    assert result.returncode == 0, result.stderr
    return json.loads(result.stdout)


def test_uses_latest_stable_tag_and_commit(tmp_path):
    result = version(tmp_path)
    assert result["error"] is None
    assert result["version"] == {
        "Numeric": "0.4.0",
        "Informational": "0.4.0-dev.3f2a9c1e",
        "BaseTag": "v0.4.0",
        "Commit": "3f2a9c1e",
        "Dirty": False,
    }


def test_tracked_changes_are_visible_in_the_development_suffix(tmp_path):
    result = version(tmp_path, status=" M desktop/AirFlash.App/Program.cs")
    assert result["error"] is None
    assert result["version"]["Informational"] == "0.4.0-dev.3f2a9c1e.dirty"
    assert result["version"]["Dirty"] is True


def test_missing_stable_tag_is_an_actionable_error(tmp_path):
    result = version(tmp_path, tags=["v0.4.0-rc.1", "reserved/0.4.1"])
    assert "No stable vX.Y.Z release tag" in result["error"]
    assert result["version"] is None


def test_tag_lookup_failure_is_not_treated_as_an_empty_release_history(tmp_path):
    result = version(tmp_path, tags=[], tag_exit=1)
    assert result["error"] == "Cannot read local release tags."
    assert result["version"] is None


def test_missing_commit_is_an_actionable_error(tmp_path):
    result = version(tmp_path, commit="")
    assert result["error"] == "Cannot read the current commit."
    assert result["version"] is None
