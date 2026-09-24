# Tray Radar

Click the tray icon to see live NWS weather radar, pan around, then click away and the flyout closes.

- **Radar:** NOAA/NWS base reflectivity mosaic (`conus_bref_qcd` WMS from opengeo.ncep.noaa.gov), which is the same data as radar.weather.gov. The toolbar's play button loops through roughly the last hour of scans.
- **Basemap:** Azure Maps, rendered with [WinUIEx.Maps](https://github.com/dotMorten/WinUIEx.Maps).
- **Location:** your device location or a US ZIP code. ZIP codes are looked up offline in the bundled Census ZCTA centroids.
- **Coverage:** the contiguous United States (lower 48) only.
- **Default view:** the pin button saves the current view as the one the flyout opens to. The home button returns to that view.

## Setup

1. Copy `TrayRadar/OAuth.resw.sample` to `TrayRadar/OAuth.resw`. That file is gitignored.
2. Paste your Azure Maps key into the `AzureMapsKey` value in it. Without a key, the map shows radar over a blank background.
3. Run from `TrayRadar/` with `dotnet run -c Debug -p:Platform=ARM64` (or `x64`). This registers the app with package identity through the WinApp CLI.

To regenerate the ZIP data, run `pwsh tools/Build-ZipData.ps1`.

## Licensing note

WinUIEx.Maps is free for noncommercial use. Commercial use requires an active GitHub Sponsorship of dotMorten at $10/month or more. See the package's LICENSE.md.
