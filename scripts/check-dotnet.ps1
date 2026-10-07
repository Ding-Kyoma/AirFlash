# Validate the selected SDK without starting a build or reserving a release version.
param()
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'dotnet-common.ps1')
$context = Get-AirFlashDotnetContext -RepoRoot $repoRoot
$sdkVersion = Assert-AirFlashDotnetSdk -Context $context
Write-Host "Selected .NET SDK $sdkVersion ($($context.Path)); resolved from $($context.WorkingDirectory)"
