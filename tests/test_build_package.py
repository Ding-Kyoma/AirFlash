"""Exercise real package guards and filename consumers with disposable fake operations."""

import json
import os
import re
import shutil
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path(os.environ.get("AIRFLASH_PACKAGE_SOURCE_ROOT", ROOT))
PWSH = shutil.which("pwsh")
pytestmark = pytest.mark.skipif(PWSH is None, reason="PowerShell 7 is required")

HARNESS = r"""
$ErrorActionPreference = 'Stop'
$global:packageCase = $env:AIRFLASH_PACKAGE_CASE | ConvertFrom-Json
$global:packageEvents = [Collections.Generic.List[object]]::new()
$global:packageRemovals = [Collections.Generic.List[object]]::new()
$global:packageMetadataSubstitutions = 0
function Assert-FixturePath([string]$Path) {
    $root = [IO.Path]::GetFullPath($global:packageCase.fixture).TrimEnd('\')
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Fixture operation escaped its disposable directory: $full"
    }
}
function global:Remove-Item {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$LiteralPath, [switch]$Recurse, [switch]$Force)
    Assert-FixturePath $LiteralPath
    $global:packageRemovals.Add([pscustomobject]@{
        path = $LiteralPath; recurse = [bool]$Recurse; force = [bool]$Force
        provider_called = $false
    })
    # Never call the native provider, including for disposable directories.
}
function global:Get-Item {
    [CmdletBinding()]
    param([string[]]$LiteralPath, [switch]$Force)
    if ($global:packageCase.deny_item -and $global:packageCase.deny_item -in $LiteralPath) {
        throw [UnauthorizedAccessException]::new('Synthetic item metadata denial.')
    }
    Microsoft.PowerShell.Management\Get-Item @PSBoundParameters
}
function global:Get-ChildItem {
    [CmdletBinding()]
    param([string]$LiteralPath, [switch]$Force, [switch]$Recurse,
          [switch]$Directory, [switch]$File)
    if ($global:packageCase.deny_children -eq $LiteralPath) {
        throw [UnauthorizedAccessException]::new('Synthetic child enumeration denial.')
    }
    foreach ($entry in Microsoft.PowerShell.Management\Get-ChildItem @PSBoundParameters) {
        if ($entry.FullName -in $global:packageCase.reparse_files) {
            $global:packageMetadataSubstitutions++
            [pscustomobject]@{
                FullName = $entry.FullName; Name = $entry.Name; PSIsContainer = $false
                Attributes = $entry.Attributes -bor [IO.FileAttributes]::ReparsePoint
            }
        } else { $entry }
    }
}
function global:git { throw 'A package fixture must not invoke Git or reserve a version.' }
function Invoke-AirFlashDotnet {
    param($Context, [string[]]$Arguments)
    if ($Arguments[0] -ne 'publish') { throw 'Unexpected fake SDK operation.' }
    $index = [Array]::IndexOf($Arguments, '-o') + 1
    if ($index -le 0) { throw 'The fake publish requires an output argument.' }
    $publish = $Arguments[$index]
    Assert-FixturePath $publish
    New-Item -ItemType Directory -Path $publish -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $publish 'AirFlash.exe'), 'Synthetic text, never executed.')
    $global:packageEvents.Add([pscustomobject]@{ operation = 'publish'; arguments = $Arguments })
    $global:LASTEXITCODE = 0
}

$errorMessage = $null; $setupError = $null; $files = @(); $consumer = $null
try {
    foreach ($link in $global:packageCase.links) {
        Assert-FixturePath $link.path; Assert-FixturePath $link.target
        try {
            New-Item -ItemType $link.kind -Path $link.path -Target $link.target | Out-Null
        } catch {
            $setupError = $_.Exception.Message
            throw
        }
    }
    . $global:packageCase.package_script
    if ($global:packageCase.mode -eq 'cleanup') {
        Remove-AirFlashBuildDirectory -RepoRoot $global:packageCase.root `
            -Path $global:packageCase.target
    } else {
        $context = [pscustomobject]@{
            RepoRoot = $global:packageCase.root; Path = 'synthetic-never-executed-dotnet'
        }
        $parameters = @{
            Context = $context; Version = $global:packageCase.version
            InformationalVersion = $global:packageCase.informational
            BuildDirectory = (Join-Path $global:packageCase.root 'build')
            OutputDirectory = (Join-Path $global:packageCase.root 'dist')
            CompatibilityArtifacts = (Join-Path $global:packageCase.root 'artifacts/compatibility')
            Development = [bool]$global:packageCase.development
        }
        $null = New-AirFlashPackage @parameters
        $files = @(Get-ChildItem -LiteralPath $parameters.OutputDirectory -File |
            Select-Object -ExpandProperty Name | Sort-Object)
        if (-not $global:packageCase.development) {
            # Execute only two pure assignments from the actual consumer. Never
            # invoke verify-release's COM, process, filesystem cleanup or body.
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                $global:packageCase.verify_script, [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw 'Cannot parse the actual release consumer.' }
            $assignments = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
                $node.Left.VariablePath.UserPath -in @('artifactVersion', 'msi')
            }, $true))
            $msiAssignments = @($assignments | Where-Object {
                $_.Left.VariablePath.UserPath -eq 'msi'
            })
            if ($msiAssignments.Count -ne 1) {
                throw 'Expected one release MSI path assignment.'
            }
            foreach ($assignment in $assignments) {
                foreach ($command in @($assignment.FindAll({ param($node)
                    $node -is [Management.Automation.Language.CommandAst]
                }, $true))) {
                    $allowed = @('Join-Path', 'Get-AirFlashReleaseArtifactVersion')
                    if ($command.GetCommandName() -notin $allowed) {
                        throw 'Unexpected command in the isolated release filename expression.'
                    }
                }
            }
            $consumer = & {
                param($Version, $ReleaseChannel, $Directory, $Assignments)
                foreach ($assignment in $Assignments) {
                    . ([ScriptBlock]::Create($assignment.Extent.Text))
                }
                [pscustomobject]@{
                    path = $msi; exists = (Test-Path -LiteralPath $msi -PathType Leaf)
                }
            } $global:packageCase.version $global:packageCase.channel `
                $parameters.OutputDirectory $assignments
        }
    }
} catch { $errorMessage = $_.Exception.Message }
[pscustomobject]@{
    error = $errorMessage; setup_error = $setupError; files = $files; consumer = $consumer
    events = $global:packageEvents.ToArray(); removals = $global:packageRemovals.ToArray()
    metadata_substitutions = $global:packageMetadataSubstitutions
    native_delete_provider_calls = 0; real_build_release_install_calls = 0
} | ConvertTo-Json -Depth 12 -Compress | ForEach-Object { "AIRFLASH_PACKAGE_RESULT=$_" }
"""

NATIVE = r"""
param([string]$CompatibilityArtifacts)
Assert-FixturePath $CompatibilityArtifacts
$global:packageEvents.Add([pscustomobject]@{ operation = 'native' })
[IO.File]::WriteAllText((Join-Path $CompatibilityArtifacts 'bundle-audit.json'), '{"ok":true}')
$global:LASTEXITCODE = 0
"""

INSTALLER = r"""
param($PublishPath, $OutputPath, $ProductVersion, $ArtifactVersion, $DotnetPath)
Assert-FixturePath $OutputPath
$global:packageEvents.Add([pscustomobject]@{
    operation = 'installer'; product_version = $ProductVersion; artifact_version = $ArtifactVersion
})
[IO.File]::WriteAllText(
    (Join-Path $OutputPath "AirFlash-$ArtifactVersion.msi"), 'Synthetic text, never installed.')
$global:LASTEXITCODE = 0
"""


class Fixture:
    def __init__(self, tmp_path, *, root=None, create_root=True):
        self.directory = tmp_path
        self.root = root or tmp_path / "checkout with spaces"
        if create_root:
            self.root.mkdir()
        self.scripts = tmp_path / "copied scripts"
        self.scripts.mkdir()
        for name in ("build-package.ps1", "verify-release.ps1"):
            shutil.copyfile(SOURCE / "scripts" / name, self.scripts / name)
        (self.scripts / "build-native.ps1").write_text(NATIVE, encoding="utf-8")
        (self.scripts / "build-installer.ps1").write_text(INSTALLER, encoding="utf-8")

    def run(self, **overrides):
        case = {
            "fixture": str(self.directory),
            "root": str(self.root),
            "package_script": str(self.scripts / "build-package.ps1"),
            "verify_script": str(self.scripts / "verify-release.ps1"),
            "mode": "cleanup",
            "target": str(self.root / "build"),
            "links": [],
            "reparse_files": [],
            "version": "0.4.0",
            "informational": "0.4.0",
            "channel": "stable",
            "development": False,
            **overrides,
        }
        result = subprocess.run(
            [PWSH, "-NoProfile", "-NonInteractive", "-Command", HARNESS],
            env={**os.environ, "AIRFLASH_PACKAGE_CASE": json.dumps(case)},
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=20,
        )
        assert result.returncode == 0, result.stderr
        value = json.loads(result.stdout.rsplit("AIRFLASH_PACKAGE_RESULT=", 1)[1].strip())
        if output := os.environ.get("AIRFLASH_PACKAGE_OBSERVATIONS"):
            destination = Path(output)
            destination.mkdir(parents=True, exist_ok=True)
            (destination / (self.directory.name + ".json")).write_text(
                json.dumps({"case": case, "result": value}, indent=2), encoding="utf-8"
            )
        assert value["native_delete_provider_calls"] == 0
        assert value["real_build_release_install_calls"] == 0
        return value


def assert_rejected_before_remove(result):
    assert result["setup_error"] is None, result
    assert result["error"], result
    assert result["removals"] == [], result
    assert result["events"] == [], result


def sentinel(directory):
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / "preserve.txt"
    path.write_text("synthetic sentinel must survive", encoding="utf-8")
    return path


@pytest.mark.parametrize("relative", ["build", "build/dev", "dist/dev"])
def test_ordinary_cleanup_calls_only_the_interception_once(tmp_path, relative):
    fixture = Fixture(tmp_path)
    target = fixture.root / relative
    kept = sentinel(target / "ordinary child")
    result = fixture.run(target=str(target))
    assert result["error"] is None, result
    assert result["removals"] == [
        {"path": str(target), "recurse": True, "force": True, "provider_called": False}
    ]
    assert kept.read_text(encoding="utf-8") == "synthetic sentinel must survive"


def test_missing_leaf_under_ordinary_existing_parents_needs_no_cleanup(tmp_path):
    fixture = Fixture(tmp_path)
    (fixture.root / "build").mkdir()
    result = fixture.run(target=str(fixture.root / "build/dev/missing"))
    assert result["error"] is None, result
    assert result["removals"] == []


@pytest.mark.parametrize("case", ["root", "sibling", "escape", "volume-root"])
def test_cleanup_rejects_paths_outside_its_strict_checkout_boundary(tmp_path, case):
    fixture = Fixture(tmp_path)
    target = fixture.root
    overrides = {}
    if case == "sibling":
        target = tmp_path / (fixture.root.name + "-sibling") / "build"
        sentinel(target)
    elif case == "escape":
        target = fixture.root / ".." / "outside" / "build"
        sentinel(tmp_path / "outside/build")
    elif case == "volume-root":
        target = fixture.root / "build"
        sentinel(target)
        overrides["root"] = fixture.root.anchor
    assert_rejected_before_remove(fixture.run(target=str(target), **overrides))


def test_cleanup_rejects_a_case_variant_checkout_prefix(tmp_path):
    fixture = Fixture(tmp_path)
    # A case-sensitive NTFS parent can have both directory spellings.
    # Model the ambiguous path without changing filesystem case settings.
    alternate = fixture.root.with_name(fixture.root.name.upper())
    target = alternate / "build"
    kept = sentinel(target)

    result = fixture.run(target=str(target))

    assert_rejected_before_remove(result)
    assert result["error"].startswith("Unsafe cleanup target:"), result
    assert kept.read_text(encoding="utf-8") == "synthetic sentinel must survive"


@pytest.mark.parametrize("kind", ["missing", "file"])
def test_cleanup_requires_an_existing_directory_checkout_even_when_leaf_is_missing(
    tmp_path, kind
):
    root = tmp_path / "unusable-checkout"
    fixture = Fixture(tmp_path, root=root, create_root=False)
    if kind == "file":
        root.write_text("ordinary file, not a checkout", encoding="utf-8")
    assert_rejected_before_remove(fixture.run())


@pytest.mark.skipif(os.name != "nt", reason="Requires real Windows directory junctions")
@pytest.mark.parametrize(
    "kind",
    ["leaf", "parent", "parent-missing-leaf", "root", "above-root", "above-root-missing"],
)
def test_cleanup_rejects_reparse_components_including_checkout_ancestors(tmp_path, kind):
    external = tmp_path / "disposable external"
    external.mkdir()
    if kind in ("root", "above-root", "above-root-missing"):
        alias = tmp_path / "linked checkout parent"
        root = alias if kind == "root" else alias / "ordinary checkout"
        fixture = Fixture(tmp_path, root=root, create_root=False)
        physical_root = external if kind == "root" else external / "ordinary checkout"
        physical_root.mkdir(exist_ok=True)
        physical_target = physical_root / "build"
        if kind != "above-root-missing":
            kept = sentinel(physical_target)
        else:
            kept = sentinel(physical_root / "keep")
        link = {"path": str(alias), "target": str(external), "kind": "Junction"}
        target = root / "build"
    else:
        fixture = Fixture(tmp_path)
        link = {
            "path": str(fixture.root / "build"),
            "target": str(external),
            "kind": "Junction",
        }
        if kind == "leaf":
            target = fixture.root / "build"
            kept = sentinel(external)
        else:
            target = fixture.root / "build/dev"
            kept = sentinel(external / ("keep" if kind == "parent-missing-leaf" else "dev"))
    result = fixture.run(target=str(target), links=[link])
    assert kept.read_text(encoding="utf-8") == "synthetic sentinel must survive"
    assert_rejected_before_remove(result)


@pytest.mark.skipif(os.name != "nt", reason="Requires a real Windows junction or symlink")
@pytest.mark.parametrize("kind", ["directory", "file"])
def test_cleanup_rejects_nested_reparse_entries_without_any_remove(tmp_path, kind):
    fixture = Fixture(tmp_path)
    target = fixture.root / "build"
    sentinel(target / "ordinary child")
    kept = sentinel(tmp_path / "disposable external")
    link = {
        "path": str(target / ("nested-link" if kind == "directory" else "linked.txt")),
        "target": str(kept.parent if kind == "directory" else kept),
        "kind": "Junction" if kind == "directory" else "SymbolicLink",
    }
    result = fixture.run(links=[link])
    if kind == "file" and result["setup_error"]:
        pytest.skip("Creating a Windows file symlink is unavailable: " + result["setup_error"])
    assert kept.read_text(encoding="utf-8") == "synthetic sentinel must survive"
    assert_rejected_before_remove(result)


def test_cleanup_rejects_nested_file_reparse_metadata_without_symlink_privilege(tmp_path):
    fixture = Fixture(tmp_path)
    target = fixture.root / "build"
    kept = sentinel(target / "ordinary child")
    # Model the provider metadata of a file symlink without requiring Developer
    # Mode or administrative privileges. The separate Windows case uses a real link.
    result = fixture.run(reparse_files=[str(kept)])
    assert kept.read_text(encoding="utf-8") == "synthetic sentinel must survive"
    assert_rejected_before_remove(result)
    assert result["metadata_substitutions"] == 1


def test_unsafe_dist_preserves_an_existing_build_before_packaging(tmp_path):
    fixture = Fixture(tmp_path)
    previous_build = sentinel(fixture.root / "build")
    linked_output = sentinel(fixture.root / "dist")

    result = fixture.run(mode="package", reparse_files=[str(linked_output)])

    assert_rejected_before_remove(result)
    assert result["metadata_substitutions"] == 1
    assert previous_build.exists()


@pytest.mark.parametrize("denial", ["root-item", "target-item", "children"])
def test_cleanup_propagates_io_failures_instead_of_treating_them_as_absence(tmp_path, denial):
    fixture = Fixture(tmp_path)
    target = fixture.root / "build"
    sentinel(target)
    key = "deny_children" if denial == "children" else "deny_item"
    denied = fixture.root if denial == "root-item" else target
    result = fixture.run(**{key: str(denied)})
    assert_rejected_before_remove(result)
    assert "Synthetic" in result["error"]


@pytest.mark.parametrize(
    "channel,informational,development",
    [
        ("stable", "0.4.0", False),
        ("preview", "0.4.0-rc.1", False),
        ("stable", "0.4.0-dev.synthetic.dirty", True),
    ],
)
def test_package_producer_and_actual_release_lookup_agree(
    tmp_path, channel, informational, development
):
    fixture = Fixture(tmp_path)
    result = fixture.run(
        mode="package", channel=channel, informational=informational, development=development
    )
    assert result["setup_error"] is None and result["error"] is None, result
    assert result["files"] == [f"AirFlash-{informational}.msi", "AirFlash.exe"]
    assert [event["operation"] for event in result["events"]] == [
        "native", "publish", "installer"
    ]
    installer = result["events"][-1]
    assert installer["product_version"] == "0.4.0"
    assert installer["artifact_version"] == informational
    arguments = result["events"][1]["arguments"]
    assert "-p:Version=0.4.0" in arguments
    assert f"-p:InformationalVersion={informational}" in arguments
    if not development:
        assert result["consumer"]["exists"], result
        assert Path(result["consumer"]["path"]).name == f"AirFlash-{informational}.msi"


@pytest.mark.parametrize(
    "version",
    [
        "0.4.0-rc.1", "0.4.0\n", "0.4.0\r\n", "０.４.０", "0.04.0",
        "0.4.65535", "255.255.65535",
    ],
)
def test_invalid_numeric_version_fails_before_fake_build_or_cleanup(tmp_path, version):
    fixture = Fixture(tmp_path)
    result = fixture.run(mode="package", version=version, informational=version)
    assert_rejected_before_remove(result)


@pytest.mark.parametrize("version", ["6.0.0", "255.255.65534"])
def test_application_version_reaches_publish_and_matching_installer(tmp_path, version):
    result = Fixture(tmp_path).run(mode="package", version=version, informational=version)
    assert result["setup_error"] is None and result["error"] is None, result
    assert result["files"] == [f"AirFlash-{version}.msi", "AirFlash.exe"]
    assert [event["operation"] for event in result["events"]] == [
        "native", "publish", "installer"
    ]
    publish = result["events"][1]["arguments"]
    for argument in (
        f"-p:Version={version}", f"-p:FileVersion={version}.0", f"-p:AssemblyVersion={version}.0"
    ):
        assert argument in publish
    assert result["events"][-1]["product_version"] == version
    assert result["events"][-1]["artifact_version"] == version
    assert result["consumer"]["exists"] is True
    assert Path(result["consumer"]["path"]).name == f"AirFlash-{version}.msi"


def test_release_workflow_passes_channel_to_the_actual_release_verifier():
    workflow = (SOURCE / ".github/workflows/release.yml").read_text(encoding="utf-8")
    invocation = next(
        line for line in workflow.splitlines() if "./scripts/verify-release.ps1" in line
    )
    assert "-ReleaseChannel $env:RELEASE_CHANNEL" in invocation


def test_preview_workflow_upload_uses_the_shared_artifact_version():
    workflow = (SOURCE / ".github/workflows/release.yml").read_text(encoding="utf-8")
    preview = workflow.split("- name: Preserve verified preview assets", 1)[1]
    pattern = r"dist/AirFlash-\$\{\{\s*steps\.version\.outputs\.artifact_version\s*}}\.msi"
    assert re.search(pattern, preview)
    assert "Get-AirFlashReleaseArtifactVersion" in workflow
    assert "artifact_version=" in workflow
