# Shared packaging pipeline used by the development and release entry points.
function Remove-AirFlashBuildDirectory {
    param([string]$RepoRoot, [string]$Path)

    $root = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\')
    $target = [IO.Path]::GetFullPath($Path)
    if (-not $target.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or $target -eq $root) {
        throw "Unsafe cleanup target: $target"
    }
    if (Test-Path -LiteralPath $target) {
        $item = Get-Item -LiteralPath $target
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing cleanup of junction: $target" }
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

function Assert-AirFlashNumericVersion {
    param([string]$Version)

    [Version]$parsed = $null
    if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
        -not [Version]::TryParse($Version, [ref]$parsed) -or
        $parsed.Major -gt 255 -or $parsed.Minor -gt 255 -or $parsed.Build -gt 65535) {
        throw 'Version must be canonical X.Y.Z within MSI limits (255.255.65535).'
    }
}

function New-AirFlashPackage {
    param(
        [Parameter(Mandatory)]$Context,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$InformationalVersion,
        [Parameter(Mandatory)][string]$BuildDirectory,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$CompatibilityArtifacts,
        [switch]$Console,
        [switch]$Development
    )

    $ErrorActionPreference = 'Stop'
    $repoRoot = $Context.RepoRoot
    Assert-AirFlashNumericVersion $Version
    if ([string]::IsNullOrWhiteSpace($InformationalVersion)) { throw 'InformationalVersion is required.' }

    $build = [IO.Path]::GetFullPath($BuildDirectory)
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $compatibility = [IO.Path]::GetFullPath($CompatibilityArtifacts)
    Remove-AirFlashBuildDirectory $repoRoot $build
    Remove-AirFlashBuildDirectory $repoRoot $output
    New-Item -ItemType Directory -Path (Join-Path $repoRoot 'artifacts') -Force | Out-Null
    New-Item -ItemType Directory -Path $compatibility -Force | Out-Null

    & (Join-Path $PSScriptRoot 'build-native.ps1') -CompatibilityArtifacts $compatibility
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }

    $publish = Join-Path $build 'wpf-publish'
    $project = Join-Path $repoRoot 'desktop/AirFlash.App/AirFlash.App.csproj'
    $consoleFlag = if ($Console) { 'true' } else { 'false' }
    Invoke-AirFlashDotnet -Context $Context -Arguments @(
        'publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=false',
        '-p:EnableCompressionInSingleFile=true', '-p:RestoreLockedMode=true',
        '-p:DebugType=None', '-p:DebugSymbols=false', "-p:PathMap=$repoRoot=/_/",
        "-p:AirFlashConsole=$consoleFlag", "-p:Version=$Version",
        "-p:InformationalVersion=$InformationalVersion", "-p:FileVersion=$Version.0",
        "-p:AssemblyVersion=$Version.0", "-p:CompatibilityArtifacts=$compatibility",
        '-o', $publish
    )
    if ($LASTEXITCODE -ne 0) { throw 'WPF publish failed; do not use residual dist files' }
    $builtExe = Join-Path $publish 'AirFlash.exe'
    if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) { throw 'Published executable missing' }

    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $exe = Join-Path $output 'AirFlash.exe'
    Copy-Item -LiteralPath $builtExe -Destination $exe
    $auditPath = Join-Path $compatibility 'bundle-audit.json'
    if (-not (Test-Path -LiteralPath $auditPath)) { throw 'Missing bundle compatibility audit.' }
    $audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
    if (-not $audit.ok) { throw 'Bundle compatibility audit failed.' }
    $audit | Add-Member -NotePropertyName published_exe_sha256 -NotePropertyValue (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant() -Force
    $audit | Add-Member -NotePropertyName published_version -NotePropertyValue $Version -Force
    $audit | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $auditPath -Encoding utf8

    & (Join-Path $PSScriptRoot 'build-installer.ps1') -PublishPath $publish -OutputPath $output `
        -ProductVersion $Version -ArtifactVersion $InformationalVersion -DotnetPath $Context.Path
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

    Remove-AirFlashBuildDirectory $repoRoot $build
    $msiName = "AirFlash-$InformationalVersion.msi"
    $expected = @('AirFlash.exe', $msiName)
    $actual = @(Get-ChildItem -LiteralPath $output -Force | Select-Object -ExpandProperty Name | Sort-Object)
    $expectedSorted = @($expected | Sort-Object)
    if (($actual -join '|') -cne ($expectedSorted -join '|')) {
        throw "Unexpected package outputs: $($actual -join ', ')"
    }
    if ($Development) {
        Write-Host 'Development MSI uses the current stable numeric ProductVersion; uninstall an existing same-version AirFlash package before installing it.'
    }
    Get-Item -LiteralPath (Join-Path $output 'AirFlash.exe'), (Join-Path $output $msiName) |
        Select-Object Name, Length, LastWriteTime
}
