# The text whose signature makes a feed entry Balsoft's (design part 15b): the same lines, joined by "\n", that
# UpdateManifest.Statement() in Hosting/UpdateFeed.cs builds. Dot-sourced by sign-azure-package.ps1.
function Get-UpdateStatement($Manifest) {
    # ConvertFrom-Json may turn a date into a DateTime; the entry holds it as yyyy-MM-dd text.
    $date = if ($null -eq $Manifest.releaseDate) { '' } elseif ($Manifest.releaseDate -is [datetime]) { $Manifest.releaseDate.ToString('yyyy-MM-dd') } else { [string]$Manifest.releaseDate }
    $lines = @('hive-bdc-azure-update', $Manifest.version, ([long]$Manifest.size).ToString([Globalization.CultureInfo]::InvariantCulture),
        $Manifest.sha256.ToLowerInvariant(), $Manifest.runtime, $date)
    $lines += @($Manifest.notes | Where-Object { $null -ne $_ })
    return ($lines -join "`n")
}