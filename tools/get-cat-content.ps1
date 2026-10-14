<#
.SYNOPSIS
    Download free cat pictures, GIFs and silhouettes from Wikimedia Commons into a folder,
    ready for the Playlist tab's "Auto from folder…".

.DESCRIPTION
    Reads hand-picked Commons categories (silhouettes, icons, animations, drawings) through the
    Commons API and keeps only files whose licence needs no attribution (CC0 / public domain),
    so they are safe for a public LAN-party show. -IncludeAttribution also takes CC BY / CC BY-SA
    files; their authors are then listed in credits.csv and should be credited somewhere.

    SVGs (most silhouettes and icons) arrive as transparent PNGs, the best kind of sprite.
    Everything is scaled down to about -Width px wide; animated GIFs stay animated.
    Files already in the folder are skipped, so the script can be re-run to top up.

.EXAMPLE
    ./tools/get-cat-content.ps1
    ./tools/get-cat-content.ps1 -OutDir D:\lan\cats -Max 300 -IncludeAttribution
#>
param(
    [string]$OutDir = (Join-Path ([Environment]::GetFolderPath('MyPictures')) 'WaBiBaBuSy cats'),
    [int]$Max = 200,
    [int]$Width = 500,   # Commons serves fixed thumbnail widths (… 330, 500, 960 …) and rounds up to the next one
    [switch]$IncludeAttribution,
    [string[]]$Categories = @(
        'Plain black SVG cat icons', 'SVG cat icons', 'Silhouettes of cats', 'Cat icons',
        'Animations of cats', 'Animated GIF files of cats', 'Cat emoji', 'Drawings of cats')
)

$ErrorActionPreference = 'Stop'
$api = 'https://commons.wikimedia.org/w/api.php'
# Wikimedia asks every script for a descriptive User-Agent.
$agent = 'WaBiBaBuSy-cat-content/1.0 (https://github.com/Wubbl/WaBiBaBuSy) PowerShell'

# Commons answers 429 when it wants a pause: wait and retry instead of giving up.
function Invoke-Polite([scriptblock]$call) {
    for ($try = 1; ; $try++) {
        try { return & $call }
        catch {
            $status = [int]$_.Exception.Response.StatusCode
            if ($status -ne 429 -or $try -ge 5) { throw }
            Write-Host "Commons asks for a pause, waiting $(10 * $try) s..."
            Start-Sleep -Seconds (10 * $try)
        }
    }
}
$free = '^(CC0|Public domain|PD)'
$attribution = '^CC BY(-SA)? '

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$credits = Join-Path $OutDir 'credits.csv'
$rows = @(if (Test-Path $credits) { Import-Csv $credits })
$seen = @{}; foreach ($r in $rows) { $seen[$r.Title] = $true }
$added = 0

foreach ($category in $Categories) {
    $continue = $null
    do {
        $query = @{
            action = 'query'; format = 'json'; generator = 'categorymembers'; gcmtype = 'file'; gcmlimit = '50'   # the API makes thumbnail URLs for 50 files per request
            gcmtitle = "Category:$category"; prop = 'imageinfo'; iiprop = 'url|size|mime|extmetadata'
            iiextmetadatafilter = 'LicenseShortName|Artist'; iiurlwidth = "$Width"
        }
        if ($continue) { $query.gcmcontinue = $continue }
        $page = Invoke-Polite { Invoke-RestMethod -Uri $api -Body $query -UserAgent $agent }
        $continue = $page.continue.gcmcontinue
        if (-not $page.query) { break }

        foreach ($p in $page.query.pages.PSObject.Properties.Value) {
            if ($added -ge $Max) { break }
            if (-not $p.imageinfo) { continue }   # missing or hidden file (or past the API's 50-thumbnail limit)
            $info = $p.imageinfo[0]
            $license = "$($info.extmetadata.LicenseShortName.value)"
            $ok = $license -match $free -or ($IncludeAttribution -and $license -match $attribution)
            if (-not $ok -or $seen[$p.title]) { continue }
            if ($info.mime -notin 'image/gif', 'image/png', 'image/jpeg', 'image/svg+xml') { continue }

            # The thumbnail is a PNG for SVGs and a smaller (still animated) copy for big GIFs/photos.
            $url = if ($info.thumburl) { $info.thumburl } else { $info.url }
            $ext = [IO.Path]::GetExtension(([Uri]$url).AbsolutePath).ToLowerInvariant()
            if ($ext -notin '.gif', '.png', '.jpg', '.jpeg') { continue }
            $name = ($p.title -replace '^File:', '' -replace '\.[^.]+$', '' -replace '[^\w\- ]', '_').Trim()
            $file = Join-Path $OutDir ($name.Substring(0, [Math]::Min(80, $name.Length)) + $ext)

            try { Invoke-Polite { Invoke-WebRequest -Uri $url -OutFile $file -UserAgent $agent } }
            catch { Write-Warning "skipped $($p.title): $($_.Exception.Message)"; continue }
            $seen[$p.title] = $true
            $added++
            $artist = "$($info.extmetadata.Artist.value)" -replace '<[^>]+>', '' -replace '\s+', ' '
            $rows += [pscustomobject]@{
                File = Split-Path $file -Leaf; Title = $p.title; License = $license
                Artist = $artist.Trim(); Source = $info.descriptionurl
            }
            Write-Host ("{0,3}  {1}  [{2}]" -f $added, (Split-Path $file -Leaf), $license)
            Start-Sleep -Milliseconds 200   # be polite to Commons
        }
    } while ($continue -and $added -lt $Max)
    if ($added -ge $Max) { break }
}

$rows | Export-Csv $credits -NoTypeInformation -Encoding utf8
Write-Host "`n$added new files in $OutDir (licences and authors: credits.csv)."
Write-Host 'Next: Playlist tab -> + Add -> Auto from folder... and pick that folder.'
