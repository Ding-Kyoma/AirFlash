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
