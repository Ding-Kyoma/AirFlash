"""Run the real build entry points with fake hosts and disposable outputs."""

import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
PWSH = shutil.which("pwsh")
pytestmark = pytest.mark.skipif(PWSH is None, reason="PowerShell 7 is required")

HARNESS = r"""
$ErrorActionPreference = 'Stop'
$global:airflashTestCase = $env:AIRFLASH_PREFLIGHT_CASE | ConvertFrom-Json
$global:airflashTestEvents = [Collections.Generic.List[object]]::new()
$global:airflashTestRoot = $env:AIRFLASH_PREFLIGHT_ROOT
$global:airflashTestIntegration = $env:AIRFLASH_PREFLIGHT_INTEGRATION
$global:PSNativeCommandUseErrorActionPreference = [bool]$global:airflashTestCase.native_errors
function global:git {
    $global:LASTEXITCODE = 0
    if (($args -join ' ') -notmatch 'rev-parse.*--git-common-dir') {
        throw "Unexpected git operation: $args"
    }
    Join-Path $global:airflashTestIntegration '.git'
}
function global:Get-Command {
    [CmdletBinding()]
    param([string[]]$Name, $CommandType)
    if ($Name.Count -eq 1 -and $Name[0] -eq 'dotnet') {
        if ($global:airflashTestCase.hosts.path) {
            [pscustomobject]@{ Source = $global:airflashTestCase.hosts.path.path }
        }
        return
    }
    Microsoft.PowerShell.Core\Get-Command @PSBoundParameters
}
foreach ($property in $global:airflashTestCase.hosts.PSObject.Properties) {
    if ($property.Value.native) { continue }
    $hostPath = [string]$property.Value.path
    $hostCase = $property.Value
    $hostRoot = $global:airflashTestRoot
    $eventList = $global:airflashTestEvents
    $fakeCommand = {
        $arguments = @($args | ForEach-Object { [string]$_ })
        $eventList.Add([pscustomobject]@{
            operation = 'host'; path = $hostPath; cwd = (Get-Location).Path; arguments = $arguments
        })
        if ($arguments.Count -eq 1 -and $arguments[0] -eq '--version') {
            if ((Get-Location).Path -ne (Join-Path $hostRoot 'desktop')) {
                Write-Output '11.0.100'
                $global:LASTEXITCODE = 0
            } else {
                if ($hostCase.version) { Write-Output $hostCase.version }
                if ($hostCase.error) { Write-Error $hostCase.error -ErrorAction Continue }
                $global:LASTEXITCODE = [int]$hostCase.resolve_exit
            }
        } elseif ($arguments.Count -eq 1 -and $arguments[0] -eq '--list-sdks') {
            if ($hostCase.list_sdks) { Write-Output $hostCase.list_sdks }
            $global:LASTEXITCODE = 0
        } elseif ($arguments.Count -eq 1 -and
                  $arguments[0] -in @('--info','--list-runtimes','--help','-h')) {
            Write-Output 'fake host diagnostic'
            $global:LASTEXITCODE = 0
        } else {
            $eventList.Add([pscustomobject]@{ operation = 'command'; arguments = $arguments })
            if ($hostCase.publish_output -and $arguments[0] -eq 'publish') {
                $outputIndex = [Array]::IndexOf($arguments, '-o') + 1
                $publishRoot = $arguments[$outputIndex]
                New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $publishRoot 'AirFlash.exe') 'fake application'
            }
            if ($hostCase.installer_output -and $arguments[0] -eq 'build') {
                $versionArgument = $arguments | Where-Object { $_.StartsWith('-p:ProductVersion=') }
                $outputArgument = $arguments | Where-Object { $_.StartsWith('-p:OutputPath=') }
                $version = $versionArgument.Substring('-p:ProductVersion='.Length)
                $outputRoot = $outputArgument.Substring('-p:OutputPath='.Length)
                Set-Content -LiteralPath (Join-Path $outputRoot "AirFlash-$version.msi") 'fake MSI'
            }
            Write-Output 'original command output'
            if ($hostCase.command_error) { [Console]::Error.WriteLine($hostCase.command_error) }
            $global:LASTEXITCODE = [int]$hostCase.command_exit
        }
    }.GetNewClosure()
    Set-Item -Path "Function:global:$hostPath" -Value $fakeCommand
}
function global:Remove-Item {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$LiteralPath, [switch]$Recurse, [switch]$Force)
    $global:airflashTestEvents.Add([pscustomobject]@{ operation = 'cleanup'; path = $LiteralPath })
    # A fixture spy deliberately keeps even temporary output sentinels intact.
}
$capturedOutput = @(); $errorMessage = $null; $capturedExit = $null; $resolved = $null
try {
    $arguments = @($global:airflashTestCase.arguments | ForEach-Object { [string]$_ })
    if ($global:airflashTestCase.mode -eq 'context') {
        . (Join-Path $global:airflashTestRoot 'scripts/dotnet-common.ps1')
        $parameters = @{ RepoRoot = $global:airflashTestRoot }
        if ($global:airflashTestCase.override) {
            $parameters.DotnetPath = [string]$global:airflashTestCase.override
        }
        $context = Get-AirFlashDotnetContext @parameters
        $resolved = Assert-AirFlashDotnetSdk -Context $context
    } else {
        $entry = Join-Path $global:airflashTestRoot ("scripts/" + $global:airflashTestCase.entry)
        $parameters = @{}
        if ($global:airflashTestCase.parameters) {
            foreach ($property in $global:airflashTestCase.parameters.PSObject.Properties) {
                $parameters[$property.Name] = $property.Value
            }
        }
        $capturedOutput = @(& $entry @parameters @arguments)
        $capturedExit = $LASTEXITCODE
    }
} catch { $errorMessage = $_.Exception.Message; $capturedExit = $LASTEXITCODE }
[pscustomobject]@{
    error = $errorMessage; exit = $capturedExit; output = $capturedOutput; resolved = $resolved
    events = $global:airflashTestEvents.ToArray(); cwd = (Get-Location).Path
    cli_home = $env:DOTNET_CLI_HOME; nuget = $env:NUGET_PACKAGES
    telemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
} | ConvertTo-Json -Depth 10 -Compress | Set-Content -LiteralPath $env:AIRFLASH_PREFLIGHT_REPORT
exit 0
"""

RESERVATION_STUB = r"""
function Reserve-ReleaseVersion {
    param($RepoRoot, $ReservedVersion, $ReleaseChannel, $NewVersion)
    $global:airflashTestEvents.Add([pscustomobject]@{ operation = 'reserve' })
    '0.3.0'
}
"""

NATIVE_STUB = r"""
$global:airflashTestEvents.Add([pscustomobject]@{ operation = 'native' })
if ($global:airflashTestCase.add_checkout_host) {
    $path = $global:airflashTestCase.hosts.checkout.path
    New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
    Set-Content -LiteralPath $path 'host appearing after preflight'
}
$global:LASTEXITCODE = [int]$global:airflashTestCase.native_exit
"""

INSTALLER_STUB = r"""
param($PublishPath, $OutputPath, $DotnetPath)
$global:airflashTestEvents.Add([pscustomobject]@{
    operation = 'installer'; path = $DotnetPath
})
throw 'Fixture stopped after installer admission.'
"""


class Fixture:
    def __init__(self, tmp_path):
        self.root = tmp_path / "checkout with spaces"
        self.integration = tmp_path / "integration with spaces"
        self.report = tmp_path / "report.json"
        self.root.mkdir()
        self.integration.mkdir()
        shutil.copytree(ROOT / "scripts", self.root / "scripts")
        (self.root / "desktop").mkdir()
        shutil.copyfile(ROOT / "desktop" / "global.json", self.root / "desktop" / "global.json")
        for name in ("build", "dist"):
            directory = self.root / name
            directory.mkdir()
            (directory / "preserve.txt").write_text("keep fixture output", encoding="utf-8")
        (self.root / "scripts" / "release-version.ps1").write_text(
            RESERVATION_STUB, encoding="utf-8"
        )
        (self.root / "scripts" / "build-native.ps1").write_text(NATIVE_STUB, encoding="utf-8")
        self.hosts = {}

    def host(self, name, *, version="10.0.301", resolve_exit=0, command_exit=0, **details):
        if name == "checkout":
            path = self.root / "artifacts" / "dotnet" / "dotnet.exe"
        elif name == "integration":
            path = self.integration / "artifacts" / "dotnet" / "dotnet.exe"
        else:
            path = self.root.parent / name / "dotnet.exe"
        path.parent.mkdir(parents=True, exist_ok=True)
        # PowerShell's full-path function seam intercepts this placeholder executable.
        path.write_text("fake host; never execute a real compiler", encoding="utf-8")
        self.hosts[name] = {
            "path": str(path),
            "version": version,
            "resolve_exit": resolve_exit,
            "command_exit": command_exit,
            **details,
        }
        return path

    def run(self, entry="dotnet.ps1", arguments=(), *, environment=None, **details):
        result = subprocess.run(
            [PWSH, "-NoProfile", "-NonInteractive", "-Command", HARNESS],
            env={
                **os.environ,
                "AIRFLASH_PREFLIGHT_CASE": json.dumps(
                    {
                        "mode": "entry",
                        "entry": entry,
                        "arguments": arguments,
                        "hosts": self.hosts,
                        **details,
                    }
                ),
                "AIRFLASH_PREFLIGHT_ROOT": str(self.root),
                "AIRFLASH_PREFLIGHT_INTEGRATION": str(self.integration),
                "AIRFLASH_PREFLIGHT_REPORT": str(self.report),
                "DOTNET_CLI_HOME": str(self.root.parent / "cli-home"),
                "NUGET_PACKAGES": str(self.root.parent / "nuget"),
                **(environment or {}),
            },
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=20,
        )
        assert result.returncode == 0, result.stderr
        report = json.loads(self.report.read_text(encoding="utf-8-sig"))
        report["stdout"] = result.stdout
        report["stderr"] = result.stderr
        return report

    def installer(self):
        publish = self.root / "published app"
        publish.mkdir()
        # Only read version-resource metadata; never launch this fixture executable.
        shutil.copyfile(sys.executable, publish / "AirFlash.exe")
        return publish, self.root / "installer output"

    def native_host(self, *, resolve_exit=0, command_exit=37):
        path = self.root.parent / "native fake host.cmd"
        path.write_text(
            "@echo off\n"
            'if "%~1"=="--version" (\n'
            + (
                "echo 10.0.301\n"
                if not resolve_exit
                else "echo native SDK resolution detail 1>&2\n"
            )
            + f"exit /b {resolve_exit}\n)\n"
            "echo original native command output\n"
            "echo original native command stderr 1>&2\n"
            f"exit /b {command_exit}\n",
            encoding="utf-8",
        )
        self.hosts["path"] = {"path": str(path), "native": True}
        return path

    def stop_after_installer(self):
        (self.root / "scripts" / "build-installer.ps1").write_text(INSTALLER_STUB, encoding="utf-8")


@pytest.fixture
def fixture(tmp_path):
    return Fixture(tmp_path)


def host_calls(result):
    return [event for event in result["events"] if event["operation"] == "host"]


def side_effects(result):
    return [event for event in result["events"] if event["operation"] != "host"]


@pytest.mark.parametrize(
    "details",
    [
        {"version": "", "resolve_exit": 42, "error": "No SDK is installed."},
        {"version": "", "resolve_exit": 43, "error": "SDK policy cannot resolve installed 9.0."},
        {"version": "", "resolve_exit": 0},
    ],
)
def test_failed_selected_sdk_precedes_all_release_and_build_operations(fixture, details):
    selected = fixture.host("checkout", **details)
    fixture.host("path")
    result = fixture.run("build.ps1", native_errors=True)
    assert result["error"]
    assert side_effects(result) == []
    assert [call["path"] for call in host_calls(result)] == [str(selected)]
    assert host_calls(result)[0]["arguments"] == ["--version"]
    assert host_calls(result)[0]["cwd"] == str(fixture.root / "desktop")
    assert not (fixture.root / "artifacts" / "release.lock").exists()
    for directory in ("build", "dist"):
        assert (fixture.root / directory / "preserve.txt").read_text() == "keep fixture output"


@pytest.mark.parametrize(
    "available, selected",
    [
        (["checkout", "integration", "path"], "checkout"),
        (["integration", "path"], "integration"),
        (["path"], "path"),
    ],
)
def test_preflight_checks_the_selected_host_in_desktop(fixture, available, selected):
    paths = {name: fixture.host(name) for name in available}
    result = fixture.run("check-dotnet.ps1")
    assert result["error"] is None
    assert side_effects(result) == []
    calls = host_calls(result)
    assert len(calls) == 1
    assert calls[0]["path"] == str(paths[selected])
    assert calls[0]["cwd"] == str(fixture.root / "desktop")
    assert calls[0]["arguments"] == ["--version"]


@pytest.mark.parametrize("version", ["10.0.100", "10.0.301", "10.0.400"])
def test_later_compatible_feature_bands_pass_the_host_resolver(fixture, version):
    fixture.host("checkout", version=version)
    result = fixture.run(mode="context")
    assert result["error"] is None
    assert result["resolved"] == version
    assert [call["arguments"] for call in host_calls(result)] == [["--version"]]


@pytest.mark.parametrize(
    "details",
    [
        {"version": "", "resolve_exit": 42, "error": "Runtime installed; no SDKs found."},
        {"version": "", "resolve_exit": 43, "error": "Requested SDK 10.0.100 not found."},
        {"version": "", "resolve_exit": 0},
    ],
)
def test_unresolved_selected_host_has_actionable_policy_diagnostics(fixture, details):
    selected = fixture.host("checkout", list_sdks="10.0.100 [ignored listing]", **details)
    fixture.host("path")
    result = fixture.run("check-dotnet.ps1")
    assert result["error"]
    for expected in (
        str(selected),
        str(fixture.root / "desktop"),
        "10.0.100",
        "latestFeature",
        "https://dotnet.microsoft.com/download/dotnet/10.0",
    ):
        assert expected in result["error"]
    if details.get("error"):
        assert details["error"] in result["error"]
    assert side_effects(result) == []
    assert [call["arguments"] for call in host_calls(result)] == [["--version"]]


def test_missing_host_is_distinct_from_failed_sdk_resolution(fixture):
    result = fixture.run("build.ps1")
    assert "No .NET host" in result["error"]
    assert "https://dotnet.microsoft.com/download/dotnet/10.0" in result["error"]
    assert result["events"] == []
    assert not (fixture.root / "artifacts").exists()


def test_missing_explicit_host_never_falls_back_to_available_hosts(fixture):
    fixture.host("checkout")
    fixture.host("path")
    missing = fixture.root / "missing sdk" / "dotnet.exe"
    result = fixture.run(mode="context", override=str(missing))
    assert "Selected .NET host does not exist" in result["error"]
    assert str(missing) in result["error"]
    assert result["events"] == []


@pytest.mark.parametrize("resolve_exit", [0, 42])
def test_preflight_restores_the_callers_working_directory(fixture, resolve_exit):
    fixture.host("checkout", version="" if resolve_exit else "10.0.301", resolve_exit=resolve_exit)
    result = fixture.run(mode="context")
    assert result["cwd"] == str(ROOT)
    assert bool(result["error"]) == bool(resolve_exit)


def test_context_preserves_user_cli_home_and_nuget_overrides(fixture):
    fixture.host("checkout")
    preferences = {
        "DOTNET_CLI_HOME": str(fixture.root.parent / "user chosen cli home"),
        "NUGET_PACKAGES": str(fixture.root.parent / "user chosen nuget"),
    }
    result = fixture.run(mode="context", environment=preferences)
    assert result["error"] is None
    assert result["cli_home"] == preferences["DOTNET_CLI_HOME"]
    assert result["nuget"] == preferences["NUGET_PACKAGES"]
    assert result["telemetry"] == "1"


@pytest.mark.parametrize(
    "caches, expected_root",
    [([], "checkout"), (["integration"], "integration"), (["checkout", "integration"], "checkout")],
)
def test_context_selects_local_then_integration_nuget_defaults(fixture, caches, expected_root):
    fixture.host("checkout")
    roots = {"checkout": fixture.root, "integration": fixture.integration}
    for name in caches:
        (roots[name] / "artifacts" / "nuget").mkdir(parents=True)
    result = fixture.run(mode="context", environment={"DOTNET_CLI_HOME": "", "NUGET_PACKAGES": ""})
    assert result["error"] is None
    assert result["cli_home"] == str(fixture.root / "artifacts" / "dotnet-home")
    assert result["nuget"] == str(roots[expected_root] / "artifacts" / "nuget")
    assert result["telemetry"] == "1"


@pytest.mark.parametrize("command", ["--info", "--list-sdks", "--list-runtimes", "--help", "-h"])
def test_runtime_only_host_keeps_informational_commands_usable(fixture, command):
    selected = fixture.host("checkout", version="", resolve_exit=42)
    result = fixture.run(arguments=[command])
    assert result["error"] is None
    assert result["exit"] == 0
    assert [(call["path"], call["arguments"]) for call in host_calls(result)] == [
        (str(selected), [command])
    ]


@pytest.mark.parametrize("exit_code", [0, 37])
def test_wrapper_forwards_arguments_output_and_exact_command_exit(fixture, exit_code):
    selected = fixture.host("integration", command_exit=exit_code, command_error="native detail")
    fixture.host("path")
    arguments = [
        "test",
        "project with spaces.csproj",
        "-p:PathMap=C:\\folder with spaces=/_/",
        "--",
        "-value",
        "literal;chars=$value",
    ]
    result = fixture.run(arguments=arguments)
    assert result["error"] is None
    assert result["exit"] == exit_code
    assert result["output"] == ["original command output"]
    assert "native detail" in result["stderr"]
    calls = host_calls(result)
    assert [(call["path"], call["arguments"]) for call in calls] == [(str(selected), arguments)]
    assert calls[0]["cwd"] == str(fixture.root / "desktop")
    assert result["cwd"] == str(ROOT)


def test_wrapper_preserves_runtime_exec_without_an_sdk_preflight(fixture):
    selected = fixture.host("checkout", version="", resolve_exit=42, command_exit=19)
    result = fixture.run(arguments=["exec", "fixture application.dll", "argument with spaces"])
    assert result["error"] is None
    assert result["exit"] == 19
    assert [(call["path"], call["arguments"]) for call in host_calls(result)] == [
        (str(selected), ["exec", "fixture application.dll", "argument with spaces"])
    ]


@pytest.mark.skipif(os.name != "nt", reason="The native fixture uses Windows command files")
def test_sdk_probe_preserves_actual_native_failure_detail(fixture):
    selected = fixture.native_host(resolve_exit=41)
    result = fixture.run(mode="context", override=str(selected), native_errors=True)
    assert result["error"]
    assert "native SDK resolution detail" in result["error"]
    assert "exit: 41" in result["error"]
    assert str(selected) in result["error"]
    assert result["cwd"] == str(ROOT)


@pytest.mark.skipif(os.name != "nt", reason="The native fixture uses Windows command files")
def test_wrapper_preserves_actual_native_streams_and_exit(fixture):
    fixture.native_host(command_exit=37)
    result = fixture.run(arguments=["test"], native_errors=True)
    assert result["error"] is None
    assert result["exit"] == 37
    assert result["output"] == ["original native command output"]
    assert "original native command stderr" in result["stderr"]


@pytest.mark.parametrize("relative", [False, True])
def test_installer_override_uses_desktop_policy_before_output_creation(fixture, relative):
    selected = fixture.host("override", version="", resolve_exit=42)
    fixture.host("checkout")
    publish, output = fixture.installer()
    override = os.path.relpath(selected, fixture.root) if relative else str(selected)
    result = fixture.run(
        "build-installer.ps1",
        parameters={
            "PublishPath": str(publish),
            "OutputPath": str(output),
            "DotnetPath": override,
        },
    )
    assert result["error"]
    assert str(selected) in result["error"]
    assert side_effects(result) == []
    assert not output.exists()
    calls = host_calls(result)
    assert [(call["path"], call["arguments"]) for call in calls] == [(str(selected), ["--version"])]
    assert calls[0]["cwd"] == str(fixture.root / "desktop")


def test_installer_success_checks_and_builds_the_same_override(fixture):
    selected = fixture.host("override", installer_output=True)
    fixture.host("checkout", version="", resolve_exit=42)
    publish, output = fixture.installer()
    result = fixture.run(
        "build-installer.ps1",
        parameters={
            "PublishPath": str(publish),
            "OutputPath": str(output),
            "DotnetPath": str(selected),
        },
    )
    assert result["error"] is None
    assert result["exit"] == 0
    calls = host_calls(result)
    assert [call["path"] for call in calls] == [str(selected), str(selected)]
    assert [call["cwd"] for call in calls] == [str(fixture.root / "desktop")] * 2
    assert calls[0]["arguments"] == ["--version"]
    assert calls[1]["arguments"][0] == "build"
    assert len(list(output.glob("AirFlash-*.msi"))) == 1


def test_build_keeps_the_preflight_host_when_checkout_host_appears_later(fixture):
    later_host = fixture.host("checkout", version="", resolve_exit=42)
    later_host.unlink()
    selected = fixture.host("integration", publish_output=True)
    fixture.host("path")
    fixture.stop_after_installer()
    result = fixture.run("build.ps1", add_checkout_host=True)
    assert result["error"] == "Fixture stopped after installer admission."
    assert [call["path"] for call in host_calls(result)] == [str(selected), str(selected)]
    calls = host_calls(result)
    assert calls[0]["arguments"] == ["--version"]
    assert calls[1]["arguments"][0] == "publish"
    assert calls[1]["cwd"] == str(fixture.root / "desktop")
    operations = [event["operation"] for event in result["events"]]
    assert operations == [
        "host",
        "reserve",
        "cleanup",
        "cleanup",
        "native",
        "host",
        "command",
        "installer",
    ]
    assert result["events"][-1]["path"] == str(selected)


def test_later_publish_failure_retains_its_build_error(fixture):
    fixture.host("checkout", command_exit=51)
    result = fixture.run("build.ps1")
    assert result["error"] == "WPF publish failed; do not use residual dist files"
    assert host_calls(result)[0]["arguments"] == ["--version"]
    assert host_calls(result)[1]["arguments"][0] == "publish"


def test_later_native_failure_prevents_publish_and_retains_its_build_error(fixture):
    fixture.host("checkout")
    result = fixture.run("build.ps1", native_exit=61)
    assert result["error"] == "Native build failed"
    assert [event["operation"] for event in result["events"]] == [
        "host",
        "reserve",
        "cleanup",
        "cleanup",
        "native",
    ]


def test_release_workflow_prepares_and_checks_sdk_before_reserving_version():
    workflow = (ROOT / ".github" / "workflows" / "release.yml").read_text(encoding="utf-8")
    setup = workflow.index("uses: actions/setup-dotnet@")
    preflight = workflow.index("scripts/check-dotnet.ps1")
    reservation = workflow.index("Reserve an increasing version")
    assert setup < preflight < reservation
