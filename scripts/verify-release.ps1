param([Parameter(Mandatory)][string]$Version, [string]$Directory = 'dist')
$ErrorActionPreference = 'Stop'
$exe = Join-Path $Directory 'AirFlash.exe'
$msi = Join-Path $Directory "AirFlash-$Version.msi"
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
$process = Start-Process -FilePath (Resolve-Path $exe) -ArgumentList @('--self-check', '--output', "`"$([IO.Path]::GetFullPath($output))`"") -PassThru -Wait -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw 'Application self-check failed.' }
$result = Get-Content $output -Raw | ConvertFrom-Json
if (-not $result.ok -or $result.version -ne $Version) { throw 'Self-check version mismatch.' }
Remove-Item -LiteralPath $output
$output = Join-Path $Directory 'compat-check.json'
$process = Start-Process -FilePath (Resolve-Path $exe) -ArgumentList @('--compat-check', '--output', "`"$([IO.Path]::GetFullPath($output))`"") -PassThru -WindowStyle Hidden
if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Compatibility check timed out.' }
if ($process.ExitCode -ne 0) { throw 'Application compatibility check failed.' }
$result = Get-Content $output -Raw | ConvertFrom-Json
if (-not $result.ok -or $result.version -ne $Version) { throw 'Compatibility check version mismatch.' }
Copy-Item -LiteralPath $output -Destination 'artifacts/compatibility/host-check.json' -Force
Remove-Item -LiteralPath $output
$audit = Get-Content 'artifacts/compatibility/bundle-audit.json' -Raw | ConvertFrom-Json
if (-not $audit.ok -or $audit.published_exe_sha256 -ne (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Compatibility audit does not match release EXE.' }
# An existing directory cannot be a JSON file. Exercise the early dispatcher error
# path without changing permissions, user settings, system DLLs or WPF resources.
$invalidOutput = [IO.Path]::GetFullPath((Join-Path 'artifacts/compatibility' ('invalid-output-' + [Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $invalidOutput | Out-Null
$process = Start-Process -FilePath (Resolve-Path $exe) -ArgumentList @('--compat-check', '--output', "`"$invalidOutput`"") -PassThru -WindowStyle Hidden
if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Early diagnostic failure opened a blocking dialog or failed to exit.' }
if ($process.ExitCode -ne 1) { throw "Unexpected early diagnostic failure exit: $($process.ExitCode)" }
Remove-Item -LiteralPath $invalidOutput
$hashes = foreach ($file in @($exe,$msi)) { '{0}  {1}' -f (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant(),(Split-Path $file -Leaf) }
$hashes | Set-Content (Join-Path $Directory 'SHA256SUMS.txt') -Encoding ascii
