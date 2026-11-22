# Hidamari for Windows

Hidamari is a live wallpaper: sunlight and shadows on a wall that follow the real sun in your city.
This repository is the Windows app. The Mac app and its website are at https://hidamari.tsarionov.com.

The code is published to be read, not reused: see [LICENSE](LICENSE).

## What is where

| | |
|---|---|
| `Hidamari/` | the app (.NET 10, WebView2): the wall under the desktop icons, the tray icon and its card |
| `Check/` | the check that runs the app on GitHub's Windows machines and looks at what it does |
| `HidamariProbe/` | the first experiment, kept unchanged as the yardstick for memory |
| `ci/` | what the GitHub machines need besides .NET (the WebView2 runtime) |
| `shared/` | files the Windows app shares with the Mac app: the wall's picture (`wall.html`), the texts (`Strings.swift`), scene pictures, grain, fonts. Copied from the Mac app's repository; never edited here |
| `tools/check-strings.py` | stops the build when a text differs from the Mac app's |
| `tools/make-cities.py` | builds `Hidamari/Resources/cities.gz`, the list of places for the city search and for naming the place Windows finds, from GeoNames (CC BY 4.0) |

## Build

On Windows with the .NET 10 SDK:

```
dotnet publish Hidamari -c Release -r win-x64 -o out/app
```

The result is one file, `out/app/Hidamari.exe`. It needs the WebView2 runtime (part of Windows 11; on Windows 10 it comes with Edge).

## Checks

Every push runs `.github/workflows/windows.yml`: a build on Linux, then the app on `windows-2025` and `windows-2022`. What is checked is listed at the top of `Check/Program.cs`; pictures, the report and the app's journals are kept as the run's artifacts for a few days.
