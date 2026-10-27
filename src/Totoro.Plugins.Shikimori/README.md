# Totoro.Plugins.Shikimori

[Shikimori](https://shikimori.io) anime database and list tracker plugin for [Totoro](https://github.com/insomniachi/Totoro).

## Architecture

This plugin implements `IAnimeTracker`, `IAnimeMetadataProvider` and `IAccount` from `Totoro.Core`. Pick Shikimori as the anime database in Settings to browse it, or sign in to it alongside another database to keep its list in sync.

Shikimori numbers its anime with MyAnimeList's ids, so `AnimeId.Shikimori` is the MyAnimeList id: progress and settings saved for an anime carry over, and episodes come from ani.zip by that id. Anime details come from Shikimori's GraphQL API; list changes go through its REST API (`/api/v2/user_rates`), which GraphQL can't do. Shikimori has no start or finish dates on list entries, and its descriptions are in Russian.

### Signing in

Shikimori requires an OAuth application. Register one at [shikimori.io/oauth/applications](https://shikimori.io/oauth/applications) with `http://localhost:2222/callback` as its redirect URI and the `user_rates` scope, then set `AppName` in `ShikimoriSession.cs` to the application's name (Shikimori requires it in the User-Agent). The client id and secret stay out of git: releases take them from the `SHIKIMORI_CLIENT_ID` and `SHIKIMORI_CLIENT_SECRET` repository secrets, and local builds from those environment variables or a git-ignored `Totoro.Plugins.Shikimori.csproj.user` setting the same-named properties. Without them, browsing works and signing in says it can't.

## Debugging with Totoro

### Option 1: Visual Studio / Rider (F5 Debug)
1. Set `Totoro.Plugins.Shikimori` as the startup project or launch it.
2. Select the `Debug with Totoro` profile in `Properties/launchSettings.json`.
3. Press **F5**. Totoro launches and dynamically loads Shikimori from this project's `bin/` directory via `--plugin-dir "bin\Debug\net10.0"`.

### Option 2: Command Line
```powershell
Totoro.exe --plugin-dir "C:\path\to\plugins\Totoro.Plugins.Shikimori\bin\Debug\net10.0"
```

## Packaging & Distribution

Build in Release configuration:
```powershell
dotnet build -c Release
```
This automatically produces `bin/Release/Package/Shikimori.totoro-plugin`.
