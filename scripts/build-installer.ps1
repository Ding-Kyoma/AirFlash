param(
    [string]$PublishPath = 'build/wpf-publish',
    [string]$OutputPath = 'dist',
    [string]$DotnetPath
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
$productVersion = '{0}.{1}.{2}' -f $fileVersion.FileMajorPart, $fileVersion.FileMinorPart, $fileVersion.FileBuildPart
if ($fileVersion.FileMajorPart -gt 255 -or $fileVersion.FileMinorPart -gt 255 -or $fileVersion.FileBuildPart -gt 65535) {
    throw 'Application file version exceeds MSI version limits.'
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$outputWithSlash = $outputRoot.TrimEnd('\') + '\'
$arguments = @(
    '-c', 'Release',
    "-p:ProductVersion=$productVersion",
    "-p:AppExe=$appExe",
    "-p:AppIcon=$(Join-Path $repoRoot 'desktop/AirFlash.App/Assets/app.ico')",
    "-p:UiLicenseFile=$(Join-Path $repoRoot 'installer/License.rtf')",
    "-p:OutputPath=$outputWithSlash",
    '-p:DebugType=none'
)

if ($DotnetPath) {
    & (Resolve-RepoPath $DotnetPath) build (Join-Path $repoRoot 'installer/AirFlash.Package.wixproj') @arguments
}
else {
    & (Join-Path $PSScriptRoot 'dotnet.ps1') build (Join-Path $repoRoot 'installer/AirFlash.Package.wixproj') @arguments
}
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }

$msi = Join-Path $outputRoot 'AirFlash.msi'
if (-not (Test-Path -LiteralPath $msi -PathType Leaf)) { throw "MSI output missing: $msi" }
Write-Output $msi
