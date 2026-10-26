# Totoro.Plugins.Kitsu

[Kitsu](https://kitsu.app) anime database and list tracker plugin for [Totoro](https://github.com/insomniachi/Totoro).

## Architecture

This plugin implements `IAnimeTracker`, `IAnimeMetadataProvider` and `IAccount` from `Totoro.Core`. Pick Kitsu as the anime database in Settings to browse it, or sign in to it alongside another database to keep its list in sync.

The API is called at `kitsu.io`, its old address: at `kitsu.app`, Cloudflare answers the app's sign-in with a bot check it can't pass. Links to anime still go to `kitsu.app`.

Kitsu has its own ids. Every anime is fetched with Kitsu's mappings, which fill in its MyAnimeList, AniList and AniDB ids, so progress saved for an anime (keyed by its MyAnimeList id) carries over. Episodes come from ani.zip by Kitsu id; characters come from AniList by the mapped AniList id, since Kitsu's API no longer returns any. Kitsu's genres are its most used categories.

### Signing in

Kitsu's API only signs in with a password (OAuth's password grant), so Totoro asks for the Kitsu email and password in a dialog and keeps only the token Kitsu returns, which lasts a month and is refreshed.

## Debugging with Totoro

### Option 1: Visual Studio / Rider (F5 Debug)
1. Set `Totoro.Plugins.Kitsu` as the startup project or launch it.
2. Select the `Debug with Totoro` profile in `Properties/launchSettings.json`.
3. Press **F5**. Totoro launches and dynamically loads Kitsu from this project's `bin/` directory via `--plugin-dir "bin\Debug\net10.0"`.

### Option 2: Command Line
```powershell
Totoro.exe --plugin-dir "C:\path\to\plugins\Totoro.Plugins.Kitsu\bin\Debug\net10.0"
```

## Packaging & Distribution

Build in Release configuration:
```powershell
dotnet build -c Release
```
This automatically produces `bin/Release/Package/Kitsu.totoro-plugin`.
