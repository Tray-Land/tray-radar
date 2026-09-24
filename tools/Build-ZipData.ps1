<#
.SYNOPSIS
    Regenerates TrayRadar/Assets/Data/zcta.csv from the US Census ZCTA Gazetteer.
.DESCRIPTION
    Downloads the national ZCTA Gazetteer file, keeps only ZIP code tabulation
    areas inside the contiguous United States, and writes a compact
    "zip,lat,lon" CSV that the app bundles for offline ZIP lookups.
#>
param(
    [string]$Year = '2025',
    [string]$OutFile = (Join-Path $PSScriptRoot '..\TrayRadar\Assets\Data\zcta.csv')
)

$ErrorActionPreference = 'Stop'
$url = "https://www2.census.gov/geo/docs/maps-data/data/gazetteer/${Year}_Gazetteer/${Year}_Gaz_zcta_national.zip"
$work = Join-Path ([IO.Path]::GetTempPath()) "trayradar-zcta"
New-Item -ItemType Directory -Force $work | Out-Null
$zip = Join-Path $work 'zcta.zip'

Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip
Expand-Archive -Path $zip -DestinationPath $work -Force
$txt = Get-ChildItem $work -Filter '*.txt' | Select-Object -First 1

$inv = [Globalization.CultureInfo]::InvariantCulture
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('zip,lat,lon')

$reader = [IO.StreamReader]::new($txt.FullName)
try {
    $first = $reader.ReadLine()
    # Older Gazetteer files are tab-delimited; 2025+ are pipe-delimited.
    $sep = if ($first.Contains("|")) { [char]"|" } else { [char]"`t" }
    $header = $first.Split($sep) | ForEach-Object { $_.Trim() }
    $iZip = [Array]::IndexOf($header, 'GEOID')
    $iLat = [Array]::IndexOf($header, 'INTPTLAT')
    $iLon = [Array]::IndexOf($header, 'INTPTLONG')
    while ($null -ne ($line = $reader.ReadLine())) {
        $cols = $line.Split($sep)
        $lat = [double]::Parse($cols[$iLat].Trim(), $inv)
        $lon = [double]::Parse($cols[$iLon].Trim(), $inv)
        # Contiguous US (lower 48) bounding box — must match LocationService.IsInConus.
        if ($lat -lt 24.4 -or $lat -gt 49.5 -or $lon -lt -125.0 -or $lon -gt -66.9) { continue }
        $lines.Add(('{0},{1},{2}' -f $cols[$iZip].Trim(), $lat.ToString('0.####', $inv), $lon.ToString('0.####', $inv)))
    }
}
finally { $reader.Dispose() }

[IO.File]::WriteAllLines([IO.Path]::GetFullPath($OutFile), $lines)
Write-Host "Wrote $($lines.Count - 1) ZIP codes to $OutFile"
