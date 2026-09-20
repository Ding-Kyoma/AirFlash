param([Parameter(Mandatory)][string]$Version, [string]$Directory = 'dist')
$ErrorActionPreference = 'Stop'
$exe = Join-Path $Directory 'AirFlash.exe'
$msi = Join-Path $Directory 'AirFlash.msi'
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
$hashes = foreach ($file in @($exe,$msi)) { '{0}  {1}' -f (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant(),(Split-Path $file -Leaf) }
$hashes | Set-Content (Join-Path $Directory 'SHA256SUMS.txt') -Encoding ascii
