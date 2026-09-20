# Use a repository-local .NET 10 SDK, the integration worktree SDK, or the system SDK.
param()
$DotnetArgs = $args
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$commonGit = & git -C $repoRoot rev-parse --path-format=absolute --git-common-dir 2>$null
$integrationRoot = if ($LASTEXITCODE -eq 0) { Split-Path $commonGit -Parent } else { $repoRoot }
$sdkCandidates = @(
    (Join-Path $repoRoot 'artifacts/dotnet/dotnet.exe'),
    (Join-Path $integrationRoot 'artifacts/dotnet/dotnet.exe')
)
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = $sdkCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $dotnetPath) { $dotnetPath = if ($dotnetCommand) { $dotnetCommand.Source } else { throw 'Install the .NET 10 SDK.' } }
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $repoRoot 'artifacts/dotnet-home' }
if (-not $env:NUGET_PACKAGES) {
    $localNuget = Join-Path $repoRoot 'artifacts/nuget'
    $integrationNuget = Join-Path $integrationRoot 'artifacts/nuget'
    $env:NUGET_PACKAGES = if (Test-Path -LiteralPath $localNuget) { $localNuget } elseif (Test-Path -LiteralPath $integrationNuget) { $integrationNuget } else { $localNuget }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location -LiteralPath (Join-Path $repoRoot 'desktop')
try { & $dotnetPath @DotnetArgs; $result = $LASTEXITCODE } finally { Pop-Location }
exit $result
