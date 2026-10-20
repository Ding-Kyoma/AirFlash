function Get-DevelopmentVersion {
    param([Parameter(Mandatory)][string]$Root)

    $ErrorActionPreference = 'Stop'
    $root = [IO.Path]::GetFullPath($Root)
    $tags = @(& git -C $root tag --list 'v*')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read local release tags.' }

    $stable = foreach ($tag in $tags) {
        if ($tag -match '^v((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))$') {
            [pscustomobject]@{ Tag = $tag; Version = [Version]$Matches[1] }
        }
    }
    $latest = $stable | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $latest) {
        throw 'No stable vX.Y.Z release tag found locally. Fetch release tags before building a development package.'
    }

    $commitLines = @(& git -C $root rev-parse --short=8 HEAD)
    $commitExit = $LASTEXITCODE
    if ($commitExit -ne 0 -or $commitLines.Count -eq 0 -or [string]::IsNullOrWhiteSpace($commitLines[0])) {
        throw 'Cannot read the current commit.'
    }
    $commit = $commitLines[0].Trim()
    $changes = @(& git -C $root status --porcelain --untracked-files=no | Where-Object { $_ })
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the working tree.' }
    $dirty = $changes.Count -gt 0
    $numeric = $latest.Version.ToString(3)
    $suffix = if ($dirty) { '.dirty' } else { '' }
    [pscustomobject]@{
        Numeric = $numeric
        Informational = "$numeric-dev.$commit$suffix"
        BaseTag = $latest.Tag
        Commit = $commit
        Dirty = $dirty
    }
}
