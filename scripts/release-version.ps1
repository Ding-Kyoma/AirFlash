# Caller holds the local release lock throughout the build.
function Reserve-ReleaseVersion([string]$Root, [string]$RequestedVersion = '', [string]$Channel = 'stable', [string]$NewVersion = '') {
    if ($RequestedVersion -and $NewVersion) { throw 'NewVersion and ReservedVersion are mutually exclusive.' }
    foreach ($value in @($RequestedVersion, $NewVersion)) {
        if (-not $value) { continue }
        [Version]$parsed = $null
        if ($value -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
            -not [Version]::TryParse($value, [ref]$parsed) -or
            $parsed.Major -gt 255 -or $parsed.Minor -gt 255 -or $parsed.Build -gt 65535) {
            throw 'Version must be canonical X.Y.Z within MSI limits (255.255.65535).'
        }
    }
    if ($Channel -notin @('stable', 'preview')) { throw 'Unknown release channel.' }
    $branch = & git -C $Root branch --show-current
    $expectedBranch = if ($Channel -eq 'preview') { 'codex/nic-discovery-preview' } else { 'main' }
    if ($LASTEXITCODE -ne 0 -or $branch -ne $expectedBranch) { throw "Release channel $Channel requires branch $expectedBranch." }
    $dirty = & git -C $Root status --porcelain --untracked-files=no
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Release builds require a clean tracked tree.' }
    $head = & git -C $Root rev-parse HEAD
    $remote = & git -C $Root remote get-url origin 2>$null
    if ($LASTEXITCODE -ne 0 -or $remote -notmatch '^https://github\.com/Ding-Kyoma/AirFlash(?:\.git)?$') { throw 'Configure the AirFlash origin before reserving a release.' }
    $refs = & git -C $Root ls-remote --tags origin
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read remote version reservations.' }
    $versions = @([Version]'0.2.3') # Preserve the pre-GitHub local release floor.
    $project = [xml](Get-Content -LiteralPath (Join-Path $Root 'desktop/AirFlash.App/AirFlash.App.csproj') -Raw)
    $versions += [Version]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    $statePath = Join-Path $Root 'artifacts/release-version.txt'
    if (Test-Path $statePath) { $versions += [Version](Get-Content $statePath -Raw).Trim() }
    $previousExe = Join-Path $Root 'dist/AirFlash.exe'
    if (Test-Path $previousExe) {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($previousExe)
        $versions += [Version]('{0}.{1}.{2}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart)
    }
    foreach ($line in $refs) {
        if ($line -match 'refs/tags/(?:reserved/|v)(\d+\.\d+\.\d+)$') { $versions += [Version]$Matches[1] }
    }
    $previous = $versions | Sort-Object -Descending | Select-Object -First 1
    if ($RequestedVersion) {
        if ($env:GITHUB_ACTIONS -ne 'true' -or -not (Test-Path -LiteralPath $statePath) -or
            (Get-Content -LiteralPath $statePath -Raw).Trim() -ne $RequestedVersion) {
            throw 'Pre-reserved versions require the reservation from this GitHub Actions job.'
        }
        $tag = 'refs/tags/reserved/' + $RequestedVersion
        if (-not ($refs | Where-Object { $_ -eq "$head`t$tag" })) { throw 'Reservation does not belong to this commit.' }
        if ($refs | Where-Object { $_.EndsWith("`trefs/tags/v$RequestedVersion") }) { throw 'Published versions cannot be rebuilt.' }
        if ([Version]$RequestedVersion -lt $previous) { throw 'A newer version has already been reserved.' }
        $version = $RequestedVersion
    } else {
        if ($NewVersion) {
            if ([Version]$NewVersion -le $previous) { throw 'NewVersion must exceed all reserved, published, local and source versions.' }
            $version = $NewVersion
        } else {
            $major = $previous.Major; $minor = $previous.Minor; $patch = $previous.Build + 1
            if ($patch -gt 65535) { $patch = 0; $minor++ }
            if ($minor -gt 255) { $minor = 0; $major++ }
            if ($major -gt 255) { throw 'MSI version range exhausted.' }
            $version = '{0}.{1}.{2}' -f $major, $minor, $patch
        }
        # Empty expected ref makes creation atomic even when another publisher races us.
        & git -C $Root push origin "${head}:refs/tags/reserved/$version" "--force-with-lease=refs/tags/reserved/${version}:"
        if ($LASTEXITCODE -ne 0) { throw 'Version reservation failed; fetch and retry with a new version.' }
    }
    New-Item (Join-Path $Root 'artifacts') -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText($statePath, $version)
    return $version
}
