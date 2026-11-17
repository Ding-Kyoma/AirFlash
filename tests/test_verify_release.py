"""Exercise the actual verifier's diagnostic calls with disposable fake processes."""

import json
import os
import shutil
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = Path(os.environ.get("AIRFLASH_VERIFY_RELEASE_SCRIPT", ROOT / "scripts/verify-release.ps1"))
SHELLS = [path for path in [shutil.which("pwsh"), shutil.which("powershell")] if path]

HARNESS = r"""
$ErrorActionPreference = 'Stop'
$global:diagnosticCase = $env:AIRFLASH_DIAGNOSTIC_CASE | ConvertFrom-Json
$global:diagnosticCalls = [Collections.Generic.List[object]]::new()
$global:diagnosticRemovals = [Collections.Generic.List[string]]::new()
$global:diagnosticStarted = $false
$global:diagnosticSawExisting = $null
$global:diagnosticUnboundedWait = $false
$global:diagnosticProcess = [pscustomobject]@{
    Exited = $false; ExitCode = [int]$global:diagnosticCase.exit_code
    Waits = [Collections.Generic.List[int]]::new(); Kills = 0; Disposals = 0
}
$global:diagnosticProcess | Add-Member ScriptProperty HasExited {
    if ($global:diagnosticCase.cleanup -eq 'has-exited') { throw 'Synthetic state failure.' }
    $this.Exited
}
$global:diagnosticProcess | Add-Member ScriptMethod WaitForExit {
    param([int]$Milliseconds)
    $this.Waits.Add($Milliseconds)
    if ($Milliseconds -eq 30000) {
        if ($global:diagnosticCase.wait_error) { throw 'Synthetic wait failure.' }
        if ($global:diagnosticCase.timeout) { return $false }
        $this.Exited = $true
        return $true
    }
    if ($Milliseconds -ne 5000) { throw 'Unexpected settlement deadline.' }
    if ($global:diagnosticCase.cleanup -eq 'wait') { throw 'Synthetic settlement failure.' }
    if ($global:diagnosticCase.cleanup -eq 'unsettled') { return $false }
    return $this.Exited
}
$global:diagnosticProcess | Add-Member ScriptMethod Kill {
    $this.Kills++
    if ($global:diagnosticCase.cleanup -eq 'kill') { throw 'Synthetic kill failure.' }
    $this.Exited = $true
}
$global:diagnosticProcess | Add-Member ScriptMethod Dispose {
    $this.Disposals++
    if ($global:diagnosticCase.cleanup -eq 'dispose') { throw 'Synthetic disposal failure.' }
}
function global:Start-Process {
    param([string]$FilePath, [string[]]$ArgumentList, [switch]$PassThru,
          [switch]$Wait, [string]$WindowStyle)
    if ($Wait) {
        $global:diagnosticUnboundedWait = $true
        throw 'Unbounded Start-Process -Wait observed.'
    }
    if ($FilePath -ne $global:diagnosticCase.executable -or -not $PassThru -or
        $WindowStyle -ne 'Hidden' -or $ArgumentList.Count -ne 3 -or
        $ArgumentList[1] -ne '--output' -or
        $ArgumentList[2] -ne ('"' + $global:diagnosticCase.output + '"')) {
        throw 'Unexpected owned-process arguments.'
    }
    $global:diagnosticCalls.Add(@($ArgumentList))
    $global:diagnosticSawExisting = Test-Path -LiteralPath $global:diagnosticCase.output
    if ($global:diagnosticCase.start_error) { throw 'Synthetic start failure.' }
    $global:diagnosticStarted = $true
    if ($global:diagnosticCase.report -eq 'valid') {
        [IO.File]::WriteAllText($global:diagnosticCase.output,
            '{"ok":true,"version":"0.4.0","marker":"new"}')
    } elseif ($global:diagnosticCase.report -eq 'malformed') {
        [IO.File]::WriteAllText($global:diagnosticCase.output, '{not JSON')
    }
    $global:diagnosticProcess
}
function global:Remove-Item {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$LiteralPath, [switch]$Force)
    if ($LiteralPath -ne $global:diagnosticCase.output -or
        -not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw 'Fixture removal escaped the existing report leaf.'
    }
    $global:diagnosticRemovals.Add($LiteralPath)
    Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters
}
function global:Get-Process { throw 'Process enumeration is forbidden.' }
function global:Stop-Process { throw 'Unowned process cleanup is forbidden.' }
function global:New-Object { throw 'COM and arbitrary object creation are forbidden.' }

$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $global:diagnosticCase.script, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Cannot parse the actual verifier.' }
$helpers = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Invoke-AirFlashReleaseDiagnostic'
}, $true))
if ($helpers.Count -eq 1) { . ([ScriptBlock]::Create($helpers[0].Extent.Text)) }
elseif ($helpers.Count -gt 1) { throw 'Ambiguous verifier helper.' }
# Invoke the actual entry assignment, including its timeout and exit-code policy.
# Legacy source is supported only to demonstrate its unbounded self-check regression.
$calls = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -in @('Invoke-AirFlashReleaseDiagnostic','Start-Process')
}, $true))
$selected = @($calls | Where-Object {
    $text = $_.Extent.Text
    if ($global:diagnosticCase.entry -eq 'self') { $text.Contains("'--self-check'") }
    elseif ($global:diagnosticCase.entry -eq 'compat') {
        $text.Contains("'--compat-check'") -and
            -not $text.Contains('-NoReport') -and -not $text.Contains('$invalidOutput')
    } else { $text.Contains('-NoReport') -or $text.Contains('$invalidOutput') }
})
if ($selected.Count -ne 1) { throw 'Expected exactly one diagnostic entry call.' }
$statement = $selected[0]
while ($null -ne $statement -and
       $statement -isnot [Management.Automation.Language.AssignmentStatementAst]) {
    $statement = $statement.Parent
}
if ($null -eq $statement) { throw 'Expected a diagnostic assignment.' }
$exe = $global:diagnosticCase.executable
$output = $global:diagnosticCase.output
$invalidOutput = $global:diagnosticCase.output
$result = $null; $failure = $null
if ($global:diagnosticCase.warning_stop) { $WarningPreference = 'Stop' }
try { . ([ScriptBlock]::Create($statement.Extent.Text)) }
catch { $failure = $_.Exception.Message }
[pscustomobject]@{
    error = $failure; result = $result
    starts = $global:diagnosticCalls.Count; started = $global:diagnosticStarted
    saw_existing = $global:diagnosticSawExisting
    arguments = $global:diagnosticCalls.ToArray()
    waits = $global:diagnosticProcess.Waits.ToArray()
    kills = $global:diagnosticProcess.Kills; disposals = $global:diagnosticProcess.Disposals
    removals = $global:diagnosticRemovals.ToArray()
    output_exists = Test-Path -LiteralPath $global:diagnosticCase.output
    output_is_directory = Test-Path -LiteralPath $global:diagnosticCase.output -PathType Container
    unbounded_wait = $global:diagnosticUnboundedWait
} | ConvertTo-Json -Depth 8 -Compress | ForEach-Object { "AIRFLASH_DIAGNOSTIC_RESULT=$_" }
"""


@pytest.fixture(params=SHELLS or [None], ids=lambda shell: Path(shell).stem if shell else "none")
def shell(request):
    if request.param is None:
        pytest.skip("PowerShell is required")
    return request.param


def invoke(tmp_path, shell, *, entry="self", initial="stale", **overrides):
    executable = tmp_path / "synthetic application.exe"
    executable.write_text("Synthetic text; never executed.", encoding="utf-8")
    output = tmp_path / "diagnostic report.json"
    if initial == "directory":
        output.mkdir()
        (output / "preserve.txt").write_text(
            "Keep the intentional invalid output.", encoding="utf-8"
        )
    elif initial == "stale":
        output.write_text('{"ok":true,"version":"0.4.0","marker":"stale"}', encoding="utf-8")
    case = {
        "script": str(SCRIPT), "executable": str(executable), "output": str(output),
        "entry": entry, "exit_code": 0, "report": "valid", "timeout": False,
        "wait_error": False, "start_error": False, "cleanup": "", "warning_stop": False,
        **overrides,
    }
    completed = subprocess.run(
        [shell, "-NoProfile", "-NonInteractive", "-Command", HARNESS],
        env={**os.environ, "AIRFLASH_DIAGNOSTIC_CASE": json.dumps(case)},
        capture_output=True, text=True, encoding="utf-8", timeout=20,
    )
    assert completed.returncode == 0, completed.stderr
    lines = [line for line in completed.stdout.splitlines()
             if line.startswith("AIRFLASH_DIAGNOSTIC_RESULT=")]
    assert len(lines) == 1, completed.stdout
    result = json.loads(lines[0].split("=", 1)[1])
    return result


@pytest.mark.parametrize("entry", ["self", "compat"])
def test_success_replaces_stale_report_and_disposes_exited_child(tmp_path, shell, entry):
    result = invoke(tmp_path, shell, entry=entry)
    assert result["error"] is None and result["result"]["marker"] == "new"
    assert result["saw_existing"] is False and len(result["removals"]) == 1
    assert result["waits"] == [30000, 5000]
    assert result["kills"] == 0 and result["disposals"] == 1
    assert result["arguments"][0][0] == f"--{entry if entry == 'compat' else 'self'}-check"


@pytest.mark.parametrize("entry", ["self", "compat"])
def test_missing_new_report_cannot_reuse_stale_success(tmp_path, shell, entry):
    result = invoke(tmp_path, shell, entry=entry, report="missing")
    assert "did not produce a report" in result["error"]
    assert result["result"] is None and result["output_exists"] is False
    assert result["waits"] == [30000, 5000] and result["disposals"] == 1


def test_malformed_report_preserves_parse_failure_and_disposes_child(tmp_path, shell):
    result = invoke(tmp_path, shell, report="malformed")
    assert result["error"] and "JSON" in result["error"]
    assert result["result"] is None and result["disposals"] == 1


@pytest.mark.parametrize("entry,message", [
    ("self", "Application self-check failed."),
    ("compat", "Application compatibility check failed."),
])
def test_failed_exit_is_not_accepted_even_with_valid_report(tmp_path, shell, entry, message):
    result = invoke(tmp_path, shell, entry=entry, exit_code=23)
    assert result["error"] == message and result["result"] is None
    assert result["waits"] == [30000, 5000] and result["disposals"] == 1


@pytest.mark.parametrize("entry,message", [
    ("self", "Application self-check timed out."),
    ("compat", "Compatibility check timed out."),
    ("invalid", "Early diagnostic failure opened a blocking dialog or failed to exit."),
])
def test_timeout_uses_bounded_owned_cleanup(tmp_path, shell, entry, message):
    result = invoke(tmp_path, shell, entry=entry, initial="directory" if entry == "invalid"
                    else "stale", timeout=True, report="missing")
    assert result["error"] == message
    assert result["waits"] == [30000, 5000]
    assert result["kills"] == 1 and result["disposals"] == 1
    assert result["unbounded_wait"] is False


@pytest.mark.parametrize("cleanup", ["has-exited", "kill", "wait", "unsettled", "dispose"])
def test_cleanup_failure_does_not_replace_original_timeout(tmp_path, shell, cleanup):
    result = invoke(tmp_path, shell, timeout=True, cleanup=cleanup, warning_stop=True)
    assert result["error"] == "Application self-check timed out."
    assert result["waits"] == [30000, 5000] and result["disposals"] == 1


@pytest.mark.parametrize("cleanup,message", [
    ("wait", "Synthetic settlement failure."),
    ("unsettled", "Diagnostic process did not exit after cleanup."),
    ("dispose", "Synthetic disposal failure."),
])
def test_cleanup_failure_after_success_is_reported(tmp_path, shell, cleanup, message):
    result = invoke(tmp_path, shell, cleanup=cleanup)
    assert message in result["error"]
    assert result["result"] is None and result["disposals"] == 1


def test_wait_exception_still_settles_and_disposes_owned_child(tmp_path, shell):
    result = invoke(tmp_path, shell, wait_error=True)
    assert "Synthetic wait failure." in result["error"]
    assert result["waits"] == [30000, 5000]
    assert result["kills"] == 1 and result["disposals"] == 1


def test_start_failure_does_not_clean_up_an_unowned_process(tmp_path, shell):
    result = invoke(tmp_path, shell, start_error=True)
    assert result["error"] == "Synthetic start failure."
    assert result["waits"] == [] and result["kills"] == result["disposals"] == 0


def test_report_directory_is_preserved_and_rejected_before_start(tmp_path, shell):
    result = invoke(tmp_path, shell, initial="directory")
    assert "report path is not a file" in result["error"]
    assert result["starts"] == 0 and result["removals"] == []
    assert result["output_is_directory"] is True and result["disposals"] == 0
    assert (tmp_path / "diagnostic report.json" / "preserve.txt").is_file()


def test_intentional_invalid_output_requires_exit_one_and_preserves_directory(tmp_path, shell):
    result = invoke(tmp_path, shell, entry="invalid", initial="directory",
                    exit_code=1, report="missing")
    assert result["error"] is None and result["result"] is None
    assert result["saw_existing"] is True and result["removals"] == []
    assert result["output_is_directory"] is True
    assert result["waits"] == [30000, 5000] and result["disposals"] == 1


def test_intentional_invalid_output_rejects_success_exit_with_original_detail(tmp_path, shell):
    result = invoke(tmp_path, shell, entry="invalid", initial="directory", report="missing")
    assert result["error"] == "Unexpected early diagnostic failure exit: 0"
    assert result["disposals"] == 1 and result["output_is_directory"] is True
