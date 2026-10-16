# Kinetik Weather: weather forecast widget for the Windows desktop

[![License: GPL v3](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)
![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)

**Kinetik Weather** is a free, open-source **weather widget for Windows 11 and Windows 10**. It shows the current conditions, the next hours with their chance of rain, sunrise and sunset, and the week ahead, right on your desktop. Forecasts come from [Open-Meteo](https://open-meteo.com/): no account, no API key.

It's a small portable app with no installer. It's a companion to [Kinetik](https://github.com/WastedDesigner/Kinetik), the taskbar system monitor, and [Kinetik Calendar](https://github.com/WastedDesigner/KinetikCalendar), and shares their look.

<p>
  <img src="screenshots/widget.png" alt="Kinetik Weather in the Midnight theme: current conditions, the next 12 hours, chance of rain, sunrise and sunset, and five days ahead" width="370">
  <img src="screenshots/widget-light.png" alt="Kinetik Weather in the Light theme" width="370">
</p>

> **About this project:** I made this for my own desktop and decided to share it in case anyone else finds it useful. If you run into a bug or have an idea for a feature, please open an issue.

## Features

- **Now:** temperature, what it's like, feels-like, the day's high and low, wind and humidity
- **Next hours:** 6, 8 or 12 hours ahead (or 24, every other hour), each with an icon and temperature
- **Chance of rain:** a bar for each hour, and a line saying when rain is most likely
- **Sunrise and sunset**, with the sun's place on its path across the sky and how much daylight is left
- **Days ahead:** 3, 5 or 7 days, each with its chance of rain and its low-to-high range against the whole week's
- **Any place in the world:** search by name, or type coordinates
- **Works offline:** the last forecast is kept, so the widget fills in at once at startup and keeps showing it without internet
- **°C or °F, km/h, mph or m/s, 12- or 24-hour time**
- **Themes** in Kinetik's style, a choice of fonts, size and background opacity
- **Placement:** on the desktop, a normal window or always on top; drag anywhere, snap to edges, lock, click-through
- **Double-click** for a live rain radar map of your place (Windy.com)
- **Settings backup** next to the exe, offered again on a fresh Windows

## Download and use

1. Download `KinetikWeather.zip` from Releases and extract it anywhere.
2. Run `KinetikWeather.exe`. The first time, it asks where you are.
3. Right-click the widget or the tray icon for location, what to show, units, style, placement and backup.

Settings are kept in `HKCU\Software\KinetikWeather`, and the last forecast in `%LOCALAPPDATA%\KinetikWeather`. The only network requests are to Open-Meteo, for the forecast (every 30 minutes by default) and for place names when you search.

## Requirements

Windows 10 or 11 (64-bit) with .NET Framework 4.8, which ships with both.

## Building from source

No SDK needed: `build.bat` compiles `KinetikWeather.cs` with the .NET Framework compiler that ships with Windows. `build.bat package` also makes `KinetikWeather.zip` for a release.

`KinetikWeather.exe --demo` runs a separate copy with a made-up forecast (nothing is fetched) that reads and saves no settings. Add `--theme Light` (or any theme name) to pick its theme, `--place "-37.81,144.96"` to fetch a real place instead, and `--shot file.png` to draw the card into a PNG and quit, which is how the screenshots were made.

## Credits

Weather data by [Open-Meteo.com](https://open-meteo.com/), under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).

## License

[GPL-3.0-or-later](LICENSE). Copyright © 2026 Akila Sella Hennedige.

## Local maintenance build 1.0.1

Includes the approved 16 October 2026 diagnostics fixes. Cross-compilation and regression results are recorded in the Apps diagnostics report; Windows GUI and hardware validation remains pending. Run `python tests/run-tests.py` for non-GUI regressions, and `build.bat package` to compile and create a hash-verified release package. Shared support is compiled into the EXE; no additional runtime DLL is required.
