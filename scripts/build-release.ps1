# Build a release package. Version reservation and output cleanup are intentionally
# coupled to the release lock so failed attempts still consume their version.
param([switch]$Console, [string]$ReservedVersion, [ValidateSet('stable','preview')][string]$ReleaseChannel = 'stable', [string]$NewVersion)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repoRoot
. (Join-Path $PSScriptRoot 'dotnet-common.ps1')
$dotnetContext = Get-AirFlashDotnetContext -RepoRoot $repoRoot
$null = Assert-AirFlashDotnetSdk -Context $dotnetContext
. (Join-Path $PSScriptRoot 'build-package.ps1')

New-Item -ItemType Directory -Path (Join-Path $repoRoot 'artifacts') -Force | Out-Null
$releaseLock = [IO.File]::Open((Join-Path $repoRoot 'artifacts/release.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    . (Join-Path $PSScriptRoot 'release-version.ps1')
    $releaseVersion = Reserve-ReleaseVersion $repoRoot $ReservedVersion $ReleaseChannel -NewVersion $NewVersion
    Write-Host "Building AirFlash $releaseVersion"
    $informationalVersion = if ($ReleaseChannel -eq 'preview') { "$releaseVersion-rc.1" } else { $releaseVersion }
    New-AirFlashPackage -Context $dotnetContext -Version $releaseVersion -InformationalVersion $informationalVersion `
        -BuildDirectory (Join-Path $repoRoot 'build') -OutputDirectory (Join-Path $repoRoot 'dist') `
        -CompatibilityArtifacts (Join-Path $repoRoot 'artifacts/compatibility') -Console:$Console
} finally { $releaseLock.Dispose() }
