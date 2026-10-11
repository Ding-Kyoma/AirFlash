# Windows PowerShell 5.1 compatible. No elevation, SDK, Python or installed .NET needed.
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$OutputDirectory = (Join-Path $env:USERPROFILE 'Desktop'),
    [ValidateRange(5,120)][int]$TimeoutSeconds = 30
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$root = [IO.Path]::GetFullPath($OutputDirectory)
$directory = Join-Path $root ('AirFlash-diagnostics-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$started = Get-Date
function Protect-Text([string]$Text) {
    if (-not $Text) { return '' }
    # JSON encodes backslashes; redact both plain and serialized path forms.
    foreach ($path in @($env:USERPROFILE, $env:APPDATA, $env:LOCALAPPDATA, $directory)) {
        if ($path) {
            $Text = $Text -replace [regex]::Escape($path.Replace('\','\\')), '[user-path]'
            $Text = $Text -replace [regex]::Escape($path), '[user-path]'
        }
    }
    $labels = '(?:pin|password|secret|token|credentials|(?:private_|public_|session_)?key|pairing_pin)'
    $Text = $Text -replace ('(?i)("' + $labels + '"\s*:\s*)("(?:\\.|[^"\\])*"|[0-9]+)'), '$1"[redacted]"'
    $Text = $Text -replace ('(?i)\b(' + $labels + ')\s*[:=]\s*("[^"\r\n]*"|[^\s,;]+)'), '$1=[redacted]'
    return $Text
}
function Write-ProtectedJson($Value, [string]$Path) {
    $json = Protect-Text ($Value | ConvertTo-Json -Depth 15)
    [IO.File]::WriteAllText($Path, $json, (New-Object Text.UTF8Encoding($false)))
}
function Read-Bounded([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $reader = New-Object IO.StreamReader($Path)
    try { $buffer = New-Object char[] 262144; $count = $reader.ReadBlock($buffer,0,$buffer.Length); if ($count -eq 0) { return '' }; return Protect-Text (-join $buffer[0..($count-1)]) }
    finally { $reader.Dispose() }
}
function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose(); $hash.Dispose() }
}
$os = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
$report = [ordered]@{
    schema = 1; started_utc = $started.ToUniversalTime().ToString('o')
    os = @{ build = $os.CurrentBuildNumber; ubr = $os.UBR; display_version = $os.DisplayVersion; edition = $os.EditionID
        os_architecture = $(if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }); collector_architecture = $env:PROCESSOR_ARCHITECTURE }
    application = @{ version = $info.FileVersion; sha256 = (Get-Sha256 $exe) }
    requirement = 'Windows 10 1809 / build 17763 or later, x64'
    qualification = 'Win10 discovery, pairing and playback require user verification.'
    launch = @{}; events = @(); diagnostics_error = @()
}
$rawOutput = Join-Path $directory 'compat.raw'
$rawTrace = Join-Path $directory 'host.raw'
$process = New-Object Diagnostics.Process
$process.StartInfo = New-Object Diagnostics.ProcessStartInfo
$process.StartInfo.FileName = $exe
$process.StartInfo.Arguments = '--compat-check --output "' + $rawOutput + '"'
$process.StartInfo.UseShellExecute = $false
$process.StartInfo.CreateNoWindow = $true
$process.StartInfo.EnvironmentVariables['DOTNET_HOST_TRACE'] = '1'
$process.StartInfo.EnvironmentVariables['DOTNET_HOST_TRACEFILE'] = $rawTrace
$process.StartInfo.EnvironmentVariables['DOTNET_HOST_TRACE_VERBOSITY'] = '4'
$process.StartInfo.EnvironmentVariables['COREHOST_TRACE'] = '1'
$process.StartInfo.EnvironmentVariables['COREHOST_TRACEFILE'] = $rawTrace
$process.StartInfo.EnvironmentVariables['COREHOST_TRACE_VERBOSITY'] = '4'
try {
    if (-not $process.Start()) { throw 'Process.Start returned false.' }
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $report.launch = @{ timed_out = $true; timeout_seconds = $TimeoutSeconds }
        # Only this owned diagnostic process and its owned engine may be terminated.
        $report.diagnostics_error += 'The diagnostic process timed out. Collect again after closing its child engine, if any.'
        $process.Kill(); $process.WaitForExit()
    } else { $report.launch = @{ exit_code = $process.ExitCode; exit_code_hex = ('0x{0:X8}' -f $process.ExitCode) } }
} catch {
    $launchError = $_.Exception
    while ($launchError.InnerException) { $launchError = $launchError.InnerException }
    $report.launch = @{ error = (Protect-Text $launchError.Message); hresult = ('0x{0:X8}' -f $launchError.HResult) }
    if ($launchError -is [ComponentModel.Win32Exception]) { $report.launch.win32_error = $launchError.NativeErrorCode }
} finally { $process.Dispose() }
try {
    if (Test-Path -LiteralPath $rawOutput) {
        $text = Read-Bounded $rawOutput
        [IO.File]::WriteAllText((Join-Path $directory 'compatibility.json'), $text, (New-Object Text.UTF8Encoding($false)))
    }
    if (Test-Path -LiteralPath $rawTrace) {
        [IO.File]::WriteAllText((Join-Path $directory 'host-trace.txt'), (Read-Bounded $rawTrace), (New-Object Text.UTF8Encoding($false)))
    }
    $logFile = Join-Path $directory ('compat-logs\logs\wpf-' + (Get-Date -Format 'yyyy-MM-dd') + '.log')
    if (Test-Path -LiteralPath $logFile) {
        # The compat-check process has its own log directory; normal sessions cannot write here.
        [IO.File]::WriteAllText((Join-Path $directory 'startup-log.txt'), (Read-Bounded $logFile), (New-Object Text.UTF8Encoding($false)))
    }
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started; Id = @(1000,1001,1026) } -MaxEvents 30 -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'AirFlash|airflash-engine' } |
        ForEach-Object { @{ time_utc = $_.TimeCreated.ToUniversalTime().ToString('o'); id = $_.Id; provider = $_.ProviderName; message = (Protect-Text $_.Message) } })
    $report.events = $events
} catch { $report.diagnostics_error += Protect-Text $_.Exception.Message }
finally {
    # Never include raw trace/output, credentials or configuration in the archive.
    foreach ($file in @($rawOutput,$rawTrace)) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force } }
    $privateLogs = [IO.Path]::GetFullPath((Join-Path $directory 'compat-logs'))
    if ([IO.Path]::GetDirectoryName($privateLogs) -ne $directory) { throw 'Unsafe diagnostic cleanup path.' }
    if (Test-Path -LiteralPath $privateLogs) { Remove-Item -LiteralPath $privateLogs -Recurse -Force }
}
Write-ProtectedJson $report (Join-Path $directory 'report.json')
$zip = $directory + '.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($directory, $zip)
Write-Output ('Diagnostic report: ' + $zip)
