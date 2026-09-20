# Destructive installer testing is restricted to a disposable GitHub-hosted Windows runner.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Run installer lifecycle tests only in a disposable GitHub-hosted runner.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location $root
$msi = (Resolve-Path dist/AirFlash.msi).Path
$exeHash = (Get-FileHash dist/AirFlash.exe).Hash
$msiHash = (Get-FileHash $msi).Hash
$installed = Join-Path $env:ProgramFiles 'AirFlash/AirFlash.exe'
$data = Join-Path $env:APPDATA 'AirFlash'
New-Item $data -ItemType Directory -Force | Out-Null
$sentinel = Join-Path $data 'release-test-sentinel.txt'
Set-Content $sentinel 'preserve-user-data'
New-Item artifacts/installer-test -ItemType Directory -Force | Out-Null
function Run-Msi([string[]]$Arguments, [string]$Name) {
    $log = Join-Path $root "artifacts/installer-test/$Name.log"
    $process = Start-Process msiexec.exe -ArgumentList ($Arguments + @('/qn','/norestart','/L*v',"`"$log`"")) -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -notin @(0,3010)) { Get-Content $log -Tail 60; throw "MSI $Name failed: $($process.ExitCode)" }
}
function Entries {
    @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object DisplayName -EQ 'AirFlash')
}
function Check-Installed {
    if (@(Entries).Count -ne 1) { throw 'Expected exactly one uninstall entry.' }
    if ((Get-FileHash $installed).Hash -ne $exeHash) { throw 'Installed EXE hash mismatch.' }
    if (-not (Test-Path $sentinel)) { throw 'User data was deleted.' }
}
Run-Msi @('/i',"`"$msi`"") 'fresh-install'
Check-Installed
Run-Msi @('/fa',"`"$msi`"") 'repair'
Check-Installed
Run-Msi @('/x',"`"$msi`"") 'first-uninstall'
if ((Test-Path $installed) -or @(Entries).Count -ne 0) { throw 'First uninstall left application or registration.' }

# Use the preceding public release when available; first release uses a lower-version package fixture.
$releases = gh api repos/Ding-Kyoma/AirFlash/releases | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Cannot inspect preceding releases.' }
$previous = $releases | Where-Object { -not $_.draft -and -not $_.prerelease } | Select-Object -First 1
$oldDir = Join-Path $root 'artifacts/installer-test/previous'
New-Item $oldDir -ItemType Directory -Force | Out-Null
if ($previous) {
    gh release download $previous.tag_name --repo Ding-Kyoma/AirFlash --pattern AirFlash.msi --dir $oldDir
    if ($LASTEXITCODE) { throw 'Cannot download previous MSI.' }
} else {
    # WiX can hardlink its output from obj. Never reuse that intermediate tree for a different package.
    $fixtureProject = Join-Path $root 'artifacts/installer-test/fixture-project'
    New-Item $fixtureProject -ItemType Directory -Force | Out-Null
    foreach ($file in @('AirFlash.Package.wixproj','Installer.props','Package.wxs','License.rtf')) {
        Copy-Item (Join-Path $root "installer/$file") (Join-Path $fixtureProject $file)
    }
    & ./scripts/dotnet.ps1 build (Join-Path $fixtureProject 'AirFlash.Package.wixproj') -c Release '-p:ProductVersion=0.2.3' "-p:AppExe=$root/dist/AirFlash.exe" "-p:AppIcon=$root/desktop/AirFlash.App/Assets/app.ico" "-p:UiLicenseFile=$root/installer/License.rtf" "-p:OutputPath=$oldDir/" '-p:DebugType=none'
    if ($LASTEXITCODE) { throw 'Cannot build upgrade fixture.' }
}
if ((Get-FileHash $msi).Hash -ne $msiHash -or (Get-FileHash dist/AirFlash.exe).Hash -ne $exeHash) { throw 'Fixture build changed release assets.' }
$oldMsi = Join-Path $oldDir 'AirFlash.msi'
Run-Msi @('/i',"`"$oldMsi`"") 'old-install'
$oldCode = (Entries).PSChildName
Run-Msi @('/i',"`"$msi`"") 'upgrade'
Check-Installed
if ((Entries).PSChildName -eq $oldCode) { throw 'Upgrade did not replace old MSI registration.' }
Run-Msi @('/fa',"`"$msi`"") 'upgrade-repair'
Check-Installed
Run-Msi @('/x',"`"$msi`"") 'final-uninstall'
if ((Test-Path $installed) -or @(Entries).Count -ne 0) { throw 'Uninstall left application or registration.' }
foreach ($folder in @([Environment]::GetFolderPath('Desktop'),[Environment]::GetFolderPath('CommonDesktopDirectory'),[Environment]::GetFolderPath('Programs'),[Environment]::GetFolderPath('CommonPrograms'))) {
    if (Get-ChildItem $folder -Filter AirFlash.lnk -Recurse -ErrorAction SilentlyContinue) { throw 'Uninstall left a shortcut.' }
}
if ((Get-Content $sentinel -Raw).Trim() -ne 'preserve-user-data') { throw 'User data changed.' }
if ((Get-FileHash $msi).Hash -ne $msiHash) { throw 'Installer tests changed release MSI.' }
Write-Host 'MSI fresh install, repair, upgrade, uninstall, hashes and user-data preservation passed.'
