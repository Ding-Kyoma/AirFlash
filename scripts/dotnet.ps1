# Use a repository-local .NET 10 SDK, the integration worktree SDK, or the system SDK.
param()
$DotnetArgs = $args
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'dotnet-common.ps1')
$context = Get-AirFlashDotnetContext -RepoRoot $repoRoot
# Pass through runtime diagnostics as well as SDK commands, with their native exit status.
Invoke-AirFlashDotnet -Context $context -Arguments $DotnetArgs
exit $LASTEXITCODE
