# Opt-in verification on a Windows desktop, using only isolated GUIDs and mock services.
param([string]$Executable = 'dist/AirFlash.exe', [string]$Directory = 'artifacts/tray-test', [switch]$RequirePathRejection)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $root $Path))
}
$source = (Resolve-Path -LiteralPath (Resolve-RepoPath $Executable)).Path
$session = (Get-Process -Id $PID).SessionId
if (-not (Get-Process explorer -ErrorAction SilentlyContinue | Where-Object SessionId -EQ $session)) {
    throw 'Tray verification requires an interactive Windows desktop with Explorer.'
}
$runDirectory = Join-Path (Resolve-RepoPath $Directory) ([Guid]::NewGuid().ToString('N'))
$guid = [Guid]::NewGuid().ToString()
$sourceDirectory = Split-Path $source -Parent
foreach ($name in @('a', 'b')) {
    $destination = Join-Path $runDirectory $name
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    if (Test-Path -LiteralPath (Join-Path $sourceDirectory 'AirFlash.dll')) {
        # Development builds need their runtime files; published single-file builds need only the EXE.
        Get-ChildItem -LiteralPath $sourceDirectory | Copy-Item -Destination $destination -Recurse
    } else { Copy-Item -LiteralPath $source -Destination (Join-Path $destination 'AirFlash.exe') }
}
$results = foreach ($case in @(
    @{ Name = 'a'; Language = 'en-US'; WindowIdentity = $false },
    @{ Name = 'b'; Language = 'en-US'; WindowIdentity = $false },
    @{ Name = 'b'; Language = 'zh-CN'; WindowIdentity = $false },
    @{ Name = 'b'; Language = 'en-US'; WindowIdentity = $true }
)) {
    $exe = Join-Path $runDirectory ($case.Name + '/AirFlash.exe')
    $suffix = if ($case.WindowIdentity) { '-window-id' } else { '' }
    $report = Join-Path $runDirectory ($case.Name + '-' + $case.Language + $suffix + '/report.json')
    $arguments = @('--tray-smoke', '--tray-guid', $guid, '--ui-language', $case.Language, '--output', ('"' + $report + '"'))
    if ($case.WindowIdentity) { $arguments += '--tray-window-id' }
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw "Tray verification timed out: $exe" }
    if ($process.ExitCode -ne 0) { if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }; throw "Tray verification failed: $exe" }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.ok -or ($case.Name -eq 'a' -and $result.identity -ne 'Guid') -or ($case.WindowIdentity -and $result.identity -ne 'WindowIconId')) { throw "Unexpected tray identity: $($result.identity). Report: $report" }
    if ($RequirePathRejection -and $case.Name -eq 'b' -and -not $case.WindowIdentity -and $result.identity -ne 'WindowIconId') { throw "This Windows environment did not reject the moved GUID. Report: $report" }
    [pscustomobject]@{ Path = $result.path; Language = $case.Language; Identity = $result.identity; ForcedWindowIdentity = $case.WindowIdentity; PathRejectionObserved = (-not $case.WindowIdentity -and $result.identity -eq 'WindowIconId'); Checks = $result.checks.Count; Report = $report }
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'summary.json') -Encoding utf8
$results | Format-List
