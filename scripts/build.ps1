# Build Rust, publish the self-contained WPF desktop application, and create the MSI.
param([switch]$Console, [string]$ReservedVersion)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repoRoot

function Remove-SafeDirectory([string]$Path) {
    $target = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($target) -ne $repoRoot) { throw "Unsafe cleanup target: $target" }
    if (Test-Path -LiteralPath $target) {
        $item = Get-Item -LiteralPath $target
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing cleanup of junction: $target" }
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

New-Item -ItemType Directory -Path (Join-Path $repoRoot 'artifacts') -Force | Out-Null
$releaseLock = [IO.File]::Open((Join-Path $repoRoot 'artifacts/release.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
. (Join-Path $PSScriptRoot 'release-version.ps1')
$releaseVersion = Reserve-ReleaseVersion $repoRoot $ReservedVersion
Write-Host "Building AirFlash $releaseVersion"
Remove-SafeDirectory (Join-Path $repoRoot 'build')
Remove-SafeDirectory (Join-Path $repoRoot 'dist')
& (Join-Path $PSScriptRoot 'build-native.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }

$publish = Join-Path $repoRoot 'build/wpf-publish'
$project = Join-Path $repoRoot 'desktop/AirFlash.App/AirFlash.App.csproj'
$consoleFlag = if ($Console) { 'true' } else { 'false' }
& (Join-Path $PSScriptRoot 'dotnet.ps1') publish $project -c Release -r win-x64 --self-contained true '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:PublishTrimmed=false' '-p:EnableCompressionInSingleFile=true' '-p:RestoreLockedMode=true' '-p:DebugType=None' '-p:DebugSymbols=false' "-p:PathMap=$repoRoot=/_/" "-p:AirFlashConsole=$consoleFlag" "-p:Version=$releaseVersion" "-p:FileVersion=$releaseVersion.0" "-p:AssemblyVersion=$releaseVersion.0" -o $publish
if ($LASTEXITCODE -ne 0) { throw 'WPF publish failed; do not use residual dist files' }
$builtExe = Join-Path $publish 'AirFlash.exe'
if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) { throw 'Published executable missing' }

$output = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $output 'AirFlash.exe'
Copy-Item -LiteralPath $builtExe -Destination $exe
$buildInstaller = Join-Path $PSScriptRoot 'build-installer.ps1'
& $buildInstaller -PublishPath $publish -OutputPath $output
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

Remove-SafeDirectory (Join-Path $repoRoot 'build')
$expected = @('AirFlash.exe', 'AirFlash.msi')
$actual = @(Get-ChildItem -LiteralPath $output -Force | Select-Object -ExpandProperty Name)
$actualSorted = @($actual | Sort-Object)
$expectedSorted = @($expected | Sort-Object)
if (($actualSorted -join '|') -cne ($expectedSorted -join '|')) {
    throw "Unexpected release outputs: $($actual -join ', ')"
}
Get-Item -LiteralPath (Join-Path $output 'AirFlash.exe'), (Join-Path $output 'AirFlash.msi') | Select-Object Name, Length, LastWriteTime

} finally { $releaseLock.Dispose() }
