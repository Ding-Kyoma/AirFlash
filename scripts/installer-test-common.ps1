# Pure selection shared with isolated fixtures; this file never installs a package.
function Select-AirFlashInstallerPredecessor {
    param([AllowEmptyCollection()][object[]]$Releases, [Parameter(Mandatory)][Version]$CurrentVersion)
    $candidates = foreach ($release in $Releases) {
        if ($null -eq $release) { continue }
        $tagProperty = $release.PSObject.Properties['tag_name']
        $draftProperty = $release.PSObject.Properties['draft']
        $previewProperty = $release.PSObject.Properties['prerelease']
        if ($null -eq $tagProperty -or $tagProperty.Value -isnot [string]) { continue }
        if ($null -eq $draftProperty -or $draftProperty.Value -isnot [bool] -or $draftProperty.Value) { continue }
        if ($null -eq $previewProperty -or $previewProperty.Value -isnot [bool] -or $previewProperty.Value) { continue }
        $match = [regex]::Match($tagProperty.Value, '\Av((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))\z')
        if (-not $match.Success) { continue }
        [Version]$version = $null
        if ([Version]::TryParse($match.Groups[1].Value, [ref]$version) -and $version -lt $CurrentVersion) {
            [pscustomobject]@{ Release = $release; Version = $version }
        }
    }
    $selected = $candidates | Sort-Object Version -Descending | Select-Object -First 1
    if ($null -ne $selected) { $selected.Release }
}
