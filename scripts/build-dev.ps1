# Build a local development package from the latest stable release tag. This path
# never reserves a version, pushes a tag, or writes the release version record.
param([switch]$Console)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repoRoot
. (Join-Path $PSScriptRoot 'dotnet-common.ps1')
$dotnetContext = Get-AirFlashDotnetContext -RepoRoot $repoRoot
$null = Assert-AirFlashDotnetSdk -Context $dotnetContext
. (Join-Path $PSScriptRoot 'build-package.ps1')
. (Join-Path $PSScriptRoot 'dev-version.ps1')

$development = Get-DevelopmentVersion -Root $repoRoot
Write-Host "Building AirFlash $($development.Informational) from $($development.BaseTag)"
New-AirFlashPackage -Context $dotnetContext -Version $development.Numeric -InformationalVersion $development.Informational `
    -BuildDirectory (Join-Path $repoRoot 'build/dev') -OutputDirectory (Join-Path $repoRoot 'dist/dev') `
    -CompatibilityArtifacts (Join-Path $repoRoot 'artifacts/dev/compatibility') -Console:$Console -Development
