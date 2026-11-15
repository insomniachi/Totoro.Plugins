# Totoro.Plugins.WeebCentral

[Weeb Central](https://weebcentral.com) manga provider plugin for [Totoro](https://github.com/insomniachi/Totoro).

## Architecture

This plugin implements `IMangaProvider` from `Totoro.Core`. Weeb Central has no API, so search results, chapter lists and chapter pages are parsed from its HTML, fetched with a browser user agent (it turns others away).

A manga is matched by checking the series pages of the top search results for a link to its AniList or MyAnimeList entry. When none links to it, the best title match is used. Chapters are listed one per number, in reading order.

Ported from keiyoushi's Weeb Central extension (Apache-2.0); see [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md).

## Debugging with Totoro

### Option 1: Visual Studio / Rider (F5 Debug)
1. Set `Totoro.Plugins.WeebCentral` as the startup project or launch it.
2. Select the `Debug with Totoro` profile in `Properties/launchSettings.json`.
3. Press **F5**. Totoro launches and dynamically loads Weeb Central from this project's `bin/` directory via `--plugin-dir "bin\Debug\net11.0"`.

### Option 2: Command Line
```powershell
Totoro.exe --plugin-dir "C:\path\to\plugins\Totoro.Plugins.WeebCentral\bin\Debug\net11.0"
```

## Packaging & Distribution

Build in Release configuration:
```powershell
dotnet build -c Release
```
This automatically produces `bin/Release/Package/WeebCentral.totoro-plugin`.
