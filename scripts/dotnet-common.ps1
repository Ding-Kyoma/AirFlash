# Share host selection, SDK resolution and invocation across the build entry points.
function Get-AirFlashDotnetContext {
    param([string]$RepoRoot, [string]$DotnetPath)

    $PSNativeCommandUseErrorActionPreference = $false
    $repoRoot = [IO.Path]::GetFullPath($RepoRoot)
    $commonGit = & git -C $repoRoot rev-parse --path-format=absolute --git-common-dir 2>$null
    $integrationRoot = if ($LASTEXITCODE -eq 0) { Split-Path $commonGit -Parent } else { $repoRoot }
    if ($DotnetPath) {
        $hostPath = if ([IO.Path]::IsPathRooted($DotnetPath)) {
            $DotnetPath
        } else { Join-Path $repoRoot $DotnetPath }
        $hostPath = [IO.Path]::GetFullPath($hostPath)
        if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) {
            throw "Selected .NET host does not exist: $hostPath. Install the SDK required by desktop/global.json: https://dotnet.microsoft.com/download/dotnet/10.0"
        }
    } else {
        $sdkCandidates = @(
            (Join-Path $repoRoot 'artifacts/dotnet/dotnet.exe'),
            (Join-Path $integrationRoot 'artifacts/dotnet/dotnet.exe')
        )
        $hostPath = $sdkCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if (-not $hostPath) {
            $dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
            if (-not $dotnetCommand) {
                throw 'No .NET host found in checkout/integration artifacts/dotnet or on PATH. Install the SDK required by desktop/global.json: https://dotnet.microsoft.com/download/dotnet/10.0'
            }
            $hostPath = $dotnetCommand.Source
        }
    }

    if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $repoRoot 'artifacts/dotnet-home' }
    if (-not $env:NUGET_PACKAGES) {
        $localNuget = Join-Path $repoRoot 'artifacts/nuget'
        $integrationNuget = Join-Path $integrationRoot 'artifacts/nuget'
        $env:NUGET_PACKAGES = if (Test-Path -LiteralPath $localNuget) { $localNuget } elseif (Test-Path -LiteralPath $integrationNuget) { $integrationNuget } else { $localNuget }
    }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    [pscustomobject]@{
        Path = $hostPath
        RepoRoot = $repoRoot
        IntegrationRoot = $integrationRoot
        WorkingDirectory = Join-Path $repoRoot 'desktop'
    }
}

function Assert-AirFlashDotnetSdk {
    param($Context)

    # Let the selected host implement global.json's resolver policy, including roll-forward.
    $PSNativeCommandUseErrorActionPreference = $false
    $result = $null
    $exitCode = $null
    Push-Location -LiteralPath $Context.WorkingDirectory
    try {
        try {
            $result = (& $Context.Path --version 2>&1 | Out-String).Trim()
            $exitCode = $LASTEXITCODE
        } catch { $result = $_.Exception.Message }
    } finally { Pop-Location }
    if ($exitCode -ne 0 -or [string]::IsNullOrWhiteSpace($result)) {
        $globalJson = Join-Path $Context.WorkingDirectory 'global.json'
        $policy = (Get-Content -LiteralPath $globalJson -Raw).Trim()
        throw ("Selected .NET host '$($Context.Path)' cannot resolve the SDK from '$($Context.WorkingDirectory)' (exit: $exitCode). " +
            "Required policy in ${globalJson}: $policy. Install a compatible SDK into the selected installation; " +
            "replace or remove a runtime-only/incompatible artifacts/dotnet host if it shadows PATH. " +
            "Download: https://dotnet.microsoft.com/download/dotnet/10.0`n$result")
    }
    return $result
}

function Invoke-AirFlashDotnet {
    param($Context, [string[]]$Arguments)

    # Callers keep this context after preflight so later selection cannot change the host.
    $PSNativeCommandUseErrorActionPreference = $false
    Push-Location -LiteralPath $Context.WorkingDirectory
    try { & $Context.Path @Arguments; $commandExitCode = $LASTEXITCODE } finally { Pop-Location }
    $global:LASTEXITCODE = $commandExitCode
}
