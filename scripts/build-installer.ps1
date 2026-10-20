param(
    [string]$PublishPath = 'build/wpf-publish',
    [string]$OutputPath = 'dist',
    [string]$DotnetPath,
    [string]$ProductVersion,
    [string]$ArtifactVersion
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repoRoot

function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

$publishRoot = Resolve-RepoPath $PublishPath
$outputRoot = Resolve-RepoPath $OutputPath
$appExe = Join-Path $publishRoot 'AirFlash.exe'
if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) { throw "Published application missing: $appExe" }

$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appExe)
$exeVersion = '{0}.{1}.{2}' -f $fileVersion.FileMajorPart, $fileVersion.FileMinorPart, $fileVersion.FileBuildPart
if (-not $ProductVersion) { $ProductVersion = $exeVersion }
[Version]$parsedProductVersion = $null
if ($ProductVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
    -not [Version]::TryParse($ProductVersion, [ref]$parsedProductVersion) -or
    $parsedProductVersion.Major -gt 255 -or $parsedProductVersion.Minor -gt 255 -or $parsedProductVersion.Build -gt 65535) {
    throw 'ProductVersion must be canonical X.Y.Z within MSI limits (255.255.65535).'
}
if (-not $ArtifactVersion) { $ArtifactVersion = $ProductVersion }
if ($ArtifactVersion -notmatch '^[A-Za-z0-9][A-Za-z0-9.+-]*$') { throw 'ArtifactVersion contains unsupported filename characters.' }
if ($exeVersion -ne $ProductVersion) { throw "EXE version mismatch: $exeVersion (expected $ProductVersion)." }

. (Join-Path $PSScriptRoot 'dotnet-common.ps1')
# Explicit overrides and default selection both adopt the desktop SDK resolver policy.
$dotnetContext = Get-AirFlashDotnetContext -RepoRoot $repoRoot -DotnetPath $DotnetPath
$null = Assert-AirFlashDotnetSdk -Context $dotnetContext

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$outputWithSlash = $outputRoot.TrimEnd('\') + '\'
# Rebind the current EXE even when its version matches a previous publish.
$arguments = @(
    '-t:Rebuild',
    '-c', 'Release',
    "-p:ProductVersion=$ProductVersion",
    "-p:AppExe=$appExe",
    "-p:AppIcon=$(Join-Path $repoRoot 'desktop/AirFlash.App/Assets/app.ico')",
    "-p:UiLicenseFile=$(Join-Path $repoRoot 'installer/License.rtf')",
    "-p:OutputPath=$outputWithSlash",
    '-p:DebugType=none'
)

Invoke-AirFlashDotnet -Context $dotnetContext -Arguments (@('build', (Join-Path $repoRoot 'installer/AirFlash.Package.wixproj')) + $arguments)
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }

$builtMsi = Join-Path $outputRoot "AirFlash-$ProductVersion.msi"
$msi = Join-Path $outputRoot "AirFlash-$ArtifactVersion.msi"
if ($builtMsi -ne $msi) {
    if (-not (Test-Path -LiteralPath $builtMsi -PathType Leaf)) { throw "MSI output missing: $builtMsi" }
    Move-Item -LiteralPath $builtMsi -Destination $msi -Force
}
if (-not (Test-Path -LiteralPath $msi -PathType Leaf)) { throw "MSI output missing: $msi" }
Write-Output $msi
