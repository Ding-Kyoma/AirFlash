param([Parameter(Mandatory)][string]$Version, [string]$Directory = 'dist',
    [ValidateSet('stable','preview')][string]$ReleaseChannel = 'stable')
$ErrorActionPreference = 'Stop'

function Invoke-AirFlashReleaseDiagnostic {
    param(
        [string]$Executable, [string]$Mode, [string]$OutputPath,
        [string]$TimeoutMessage, [string]$FailureMessage,
        [int]$ExpectedExitCode = 0, [switch]$NoReport
    )

    $output = [IO.Path]::GetFullPath($OutputPath)
    if (-not $NoReport -and (Test-Path -LiteralPath $output)) {
        if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
            throw "Diagnostic report path is not a file: $output"
        }
        Remove-Item -LiteralPath $output -Force
    }

    $process = $null; $result = $null; $failure = $null
    $cleanupErrors = [Collections.Generic.List[object]]::new()
    try {
        $process = Start-Process -FilePath $Executable -ArgumentList @($Mode, '--output', "`"$output`"") -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(30000)) { throw $TimeoutMessage }
        if ($process.ExitCode -ne $ExpectedExitCode) { throw ($FailureMessage -f $process.ExitCode) }
        if (-not $NoReport) {
            if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
                throw "Diagnostic did not produce a report: $output"
            }
            $result = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
        }
    } catch { $failure = $_ }
    finally {
        if ($null -ne $process) {
            # Settle only this invocation's child, even when waiting or reading failed.
            try { if (-not $process.HasExited) { $process.Kill() } }
            catch { $cleanupErrors.Add($_) }
            try {
                if (-not $process.WaitForExit(5000)) { throw 'Diagnostic process did not exit after cleanup.' }
            } catch { $cleanupErrors.Add($_) }
            try { $process.Dispose() } catch { $cleanupErrors.Add($_) }
        }
    }
    if ($null -ne $failure) {
        foreach ($errorRecord in $cleanupErrors) {
            Write-Warning ("Diagnostic cleanup also failed: " + $errorRecord.Exception.Message) -WarningAction Continue
        }
        throw $failure
    }
    if ($cleanupErrors.Count) { throw $cleanupErrors[0] }
    return $result
}

. (Join-Path $PSScriptRoot 'build-package.ps1')
$artifactVersion = Get-AirFlashReleaseArtifactVersion -Version $Version -ReleaseChannel $ReleaseChannel
$exe = Join-Path $Directory 'AirFlash.exe'
$msi = Join-Path $Directory "AirFlash-$artifactVersion.msi"
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $exe))
$actual = '{0}.{1}.{2}' -f $info.FileMajorPart,$info.FileMinorPart,$info.FileBuildPart
if ($actual -ne $Version) { throw "EXE version mismatch: $actual" }
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase((Resolve-Path $msi).Path, 0)
$view = $db.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'ProductVersion'")
$view.Execute(); $record = $view.Fetch()
if ($record.StringData(1) -ne $Version) { throw 'MSI version mismatch.' }
$view.Close()
$output = Join-Path $Directory 'self-check.json'
$result = Invoke-AirFlashReleaseDiagnostic -Executable (Resolve-Path $exe).Path -Mode '--self-check' -OutputPath $output `
    -TimeoutMessage 'Application self-check timed out.' -FailureMessage 'Application self-check failed.'
if (-not $result.ok -or $result.version -ne $Version) { throw 'Self-check version mismatch.' }
Remove-Item -LiteralPath $output
$output = Join-Path $Directory 'compat-check.json'
$result = Invoke-AirFlashReleaseDiagnostic -Executable (Resolve-Path $exe).Path -Mode '--compat-check' -OutputPath $output `
    -TimeoutMessage 'Compatibility check timed out.' -FailureMessage 'Application compatibility check failed.'
if (-not $result.ok -or $result.version -ne $Version) { throw 'Compatibility check version mismatch.' }
Copy-Item -LiteralPath $output -Destination 'artifacts/compatibility/host-check.json' -Force
Remove-Item -LiteralPath $output
$audit = Get-Content 'artifacts/compatibility/bundle-audit.json' -Raw | ConvertFrom-Json
if (-not $audit.ok -or $audit.published_exe_sha256 -ne (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Compatibility audit does not match release EXE.' }
# An existing directory cannot be a JSON file. Exercise the early dispatcher error
# path without changing permissions, user settings, system DLLs or WPF resources.
$invalidOutput = [IO.Path]::GetFullPath((Join-Path 'artifacts/compatibility' ('invalid-output-' + [Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $invalidOutput | Out-Null
$null = Invoke-AirFlashReleaseDiagnostic -Executable (Resolve-Path $exe).Path -Mode '--compat-check' -OutputPath $invalidOutput `
    -TimeoutMessage 'Early diagnostic failure opened a blocking dialog or failed to exit.' `
    -FailureMessage 'Unexpected early diagnostic failure exit: {0}' -ExpectedExitCode 1 -NoReport
Remove-Item -LiteralPath $invalidOutput
$hashes = foreach ($file in @($exe,$msi)) { '{0}  {1}' -f (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant(),(Split-Path $file -Leaf) }
$hashes | Set-Content (Join-Path $Directory 'SHA256SUMS.txt') -Encoding ascii
