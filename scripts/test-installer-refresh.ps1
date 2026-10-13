# Packages and extracts synthetic PE copies only; never executes or installs them.
param([Parameter(Mandatory)][string]$PublishedExe, [string]$DotnetPath)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This verification requires PowerShell 7 on Windows.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$published = (Resolve-Path -LiteralPath $PublishedExe).Path
$commonGit = & git -C $repoRoot rev-parse --path-format=absolute --git-common-dir 2>$null
$integrationRoot = if ($LASTEXITCODE -eq 0) { Split-Path $commonGit -Parent } else { $repoRoot }
if (-not $DotnetPath) {
    $DotnetPath = @((Join-Path $repoRoot 'artifacts/dotnet/dotnet.exe'), (Join-Path $integrationRoot 'artifacts/dotnet/dotnet.exe')) |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $DotnetPath) { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
$dotnet = (Resolve-Path -LiteralPath $DotnetPath).Path
[xml]$project = Get-Content (Join-Path $repoRoot 'installer/AirFlash.Package.wixproj')
$wixVersion = $project.Project.Sdk.Split('/')[1]
$nuget = @($env:NUGET_PACKAGES, (Join-Path $repoRoot 'artifacts/nuget'), (Join-Path $integrationRoot 'artifacts/nuget'), (Join-Path $env:USERPROFILE '.nuget/packages')) |
    Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ "wixtoolset.sdk/$wixVersion/tools/net6.0/wix.dll")) } | Select-Object -First 1
if (-not $nuget) { throw "Restore the repository's WiX SDK $wixVersion before running this verification." }
$wix = Join-Path $nuget "wixtoolset.sdk/$wixVersion/tools/net6.0/wix.dll"
$directory = Join-Path $repoRoot ('artifacts/installer-refresh/' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $directory 'checkout with spaces'
$reportPath = Join-Path $directory 'report.json'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$report = [ordered]@{ ok = $false; input_sha256 = Hash $published; build_script_sha256 = Hash (Join-Path $repoRoot 'scripts/build-installer.ps1'); dotnet = $dotnet; wix = $wixVersion; packages = @(); error = $null }
$savedEnvironment = @{}
foreach ($name in @('DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'DOTNET_GENERATE_ASPNET_CERTIFICATE')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$payloads = @(); $first = $null
function Read-PayloadTable([string]$Msi) {
    $installer = $database = $view = $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($Msi, 0)
        $view = $database.OpenView('SELECT `FileSize`, `Version` FROM `File` WHERE `File` = ''ApplicationExe''')
        $null = $view.Execute(); $record = $view.Fetch()
        if ($null -eq $record) { throw 'ApplicationExe is missing from the MSI File table.' }
        [pscustomobject]@{ size = $record.IntegerData(1); version = $record.StringData(2) }
    }
    finally {
        if ($view) { $null = $view.Close() }
        foreach ($item in @($record, $view, $database, $installer)) {
            if ($null -ne $item) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) }
        }
    }
}
function Package([string]$Stage, [string]$Payload, [switch]$Explicit) {
    $output = Join-Path $directory $Stage
    $parameters = @('-PublishPath', (Split-Path $Payload -Parent), '-OutputPath', $output)
    if ($Explicit) { $parameters += @('-DotnetPath', $dotnet) }
    & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -NonInteractive -File (Join-Path $fixture 'scripts/build-installer.ps1') @parameters *> (Join-Path $directory "$Stage-build.log")
    if ($LASTEXITCODE -ne 0) { throw "$Stage packaging failed; see $Stage-build.log." }
    $msi = @(Get-ChildItem -LiteralPath $output -Filter 'AirFlash-*.msi' -File)
    if ($msi.Count -ne 1) { throw "$Stage did not produce exactly one package." }
    $msi = $msi[0].FullName; $msiHash = Hash $msi
    $table = Read-PayloadTable $msi
    # WiX clears existing extract directories. Give it a new, nonexistent path.
    $extract = Join-Path $directory "$Stage-extracted"
    if (Test-Path -LiteralPath $extract) { throw "Extraction directory already exists: $extract" }
    & $dotnet $wix msi decompile $msi -x $extract -o (Join-Path $directory "$Stage.wxs") -intermediateFolder (Join-Path $directory "$Stage-decompile") *> (Join-Path $directory "$Stage-decompile.log")
    if ($LASTEXITCODE -ne 0) { throw "$Stage extraction failed; see $Stage-decompile.log." }
    $embedded = Join-Path $extract 'File/ApplicationExe'
    if (-not (Test-Path -LiteralPath $embedded -PathType Leaf)) { throw "$Stage did not extract ApplicationExe." }
    $result = [pscustomobject]@{
        stage = $Stage; host = $(if ($Explicit) { 'explicit' } else { 'default' }); msi = $msi
        msi_sha256 = $msiHash; expected_sha256 = Hash $Payload; embedded_sha256 = Hash $embedded
        expected_size = (Get-Item -LiteralPath $Payload).Length; file_table_size = $table.size; file_version = $table.version
        package_unchanged_by_inspection = ($msiHash -eq (Hash $msi))
    }
    $report.packages += $result
    if ($result.embedded_sha256 -ne $result.expected_sha256 -or $result.file_table_size -ne $result.expected_size) { throw "$Stage packaged stale bytes from the shared WiX intermediate cache." }
    if (-not $result.package_unchanged_by_inspection) { throw "$Stage package changed during read-only inspection." }
    return $result
}
try {
    foreach ($path in @('scripts/build-installer.ps1', 'scripts/dotnet.ps1', 'scripts/dotnet-common.ps1', 'desktop/global.json', 'desktop/AirFlash.App/Assets/app.ico', 'installer/AirFlash.Package.wixproj', 'installer/Installer.props', 'installer/Package.wxs', 'installer/License.rtf')) {
        $source = Join-Path $repoRoot $path
        if (-not (Test-Path -LiteralPath $source)) {
            if ($path -eq 'scripts/dotnet-common.ps1') { continue }
            throw "Fixture input is missing: $source"
        }
        $target = Join-Path $fixture $path
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }
    $sdkLink = Join-Path $fixture 'artifacts/dotnet'
    New-Item -ItemType Directory -Path (Split-Path $sdkLink -Parent) -Force | Out-Null
    New-Item -ItemType Junction -Path $sdkLink -Target (Split-Path $dotnet -Parent) | Out-Null
    $env:DOTNET_CLI_HOME = Join-Path $directory 'dotnet-home'; $env:NUGET_PACKAGES = $nuget
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    foreach ($name in @('A', 'B')) {
        $path = Join-Path $directory "publish-$name/AirFlash.exe"
        New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $published -Destination $path
        $stream = [IO.File]::Open($path, [IO.FileMode]::Append, [IO.FileAccess]::Write)
        try { $bytes = [Text.Encoding]::ASCII.GetBytes("AirFlash packaging fixture $name`n" + ($name * $(if ($name -eq 'A') { 64 } else { 192 }))); $stream.Write($bytes, 0, $bytes.Length) }
        finally { $stream.Dispose() }
        [IO.File]::SetLastWriteTimeUtc($path, [DateTime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Utc))
        $payloads += [pscustomobject]@{ path = $path; hash = Hash $path; version = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion }
    }
    if (-not $payloads[0].version -or $payloads[0].version -ne $payloads[1].version -or $payloads[0].hash -eq $payloads[1].hash) { throw 'Synthetic PE payloads must have identical versions and different bytes.' }
    $first = Package 'default-A' $payloads[0].path
    # Keep the exact same project/obj and version when changing only AppExe and host selection.
    $second = Package 'explicit-B' $payloads[1].path -Explicit
    $report.first_package_preserved = ((Hash $first.msi) -eq $first.msi_sha256)
    $report.inputs_preserved = ((Hash $published) -eq $report.input_sha256 -and (Hash $payloads[0].path) -eq $payloads[0].hash -and (Hash $payloads[1].path) -eq $payloads[1].hash)
    if (-not $report.first_package_preserved -or -not $report.inputs_preserved) { throw 'Packaging changed a prior package or a source input.' }
    $report.ok = $true
}
catch { $report.error = $_.Exception.Message; throw }
finally {
    # Keep preservation evidence even when the old implementation fails on package B.
    $report.inputs_preserved = ((Hash $published) -eq $report.input_sha256 -and @($payloads | Where-Object { (Hash $_.path) -ne $_.hash }).Count -eq 0)
    if ($first) { $report.first_package_preserved = ((Test-Path -LiteralPath $first.msi) -and (Hash $first.msi) -eq $first.msi_sha256) }
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output "Installer refresh verification: $reportPath"
}
