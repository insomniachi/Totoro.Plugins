# Totoro.Plugins.RealDebrid

[Real-Debrid](https://real-debrid.com) torrent streamer plugin for [Totoro](https://github.com/insomniachi/Totoro).

## Architecture

This plugin implements `ITorrentStreamer` and `IModule<Settings>` from `Totoro.Core`. Real-Debrid downloads the torrent on its servers; the player then streams Real-Debrid's copy over HTTPS. Set your API token (from real-debrid.com/apitoken) in the module's settings, and pick Real-Debrid under Settings → Playback → Torrent streamer.

Only the file being played is selected, so Real-Debrid downloads just that. Torrents added for a stream are removed from Real-Debrid when it ends, unless "Keep torrents in Real-Debrid" is on. There's no "only cached" option: Real-Debrid no longer says what it has cached.

## Debugging with Totoro

### Option 1: Visual Studio / Rider (F5 Debug)
1. Set `Totoro.Plugins.RealDebrid` as the startup project or launch it.
2. Select the `Debug with Totoro` profile in `Properties/launchSettings.json`.
3. Press **F5**. Totoro launches and dynamically loads Real-Debrid from this project's `bin/` directory via `--plugin-dir "bin\Debug\net10.0"`.

### Option 2: Command Line
```powershell
Totoro.exe --plugin-dir "C:\path\to\plugins\Totoro.Plugins.RealDebrid\bin\Debug\net10.0"
```

## Packaging & Distribution

Build in Release configuration:
```powershell
dotnet build -c Release
```
This automatically produces `bin/Release/Package/RealDebrid.totoro-plugin`.
