# Totoro.Plugins.TorBox

[TorBox](https://torbox.app) torrent streamer plugin for [Totoro](https://github.com/insomniachi/Totoro).

## Architecture

This plugin implements `ITorrentStreamer` and `IModule<Settings>` from `Totoro.Core`. TorBox downloads the torrent on its servers; the player then streams TorBox's copy over HTTPS. Set your API key (from torbox.app/settings) in the module's settings, and pick TorBox under Settings → Playback → Torrent streamer.

Torrents added for a stream are removed from TorBox when it ends, unless "Keep torrents in TorBox" is on; torrents already in the account are never removed. "Only play what TorBox has cached" sends `add_only_if_cached`, so playback starts at once and stays clear of TorBox's limit of 60 uncached torrents an hour.

## Debugging with Totoro

### Option 1: Visual Studio / Rider (F5 Debug)
1. Set `Totoro.Plugins.TorBox` as the startup project or launch it.
2. Select the `Debug with Totoro` profile in `Properties/launchSettings.json`.
3. Press **F5**. Totoro launches and dynamically loads TorBox from this project's `bin/` directory via `--plugin-dir "bin\Debug\net10.0"`.

### Option 2: Command Line
```powershell
Totoro.exe --plugin-dir "C:\path\to\plugins\Totoro.Plugins.TorBox\bin\Debug\net10.0"
```

## Packaging & Distribution

Build in Release configuration:
```powershell
dotnet build -c Release
```
This automatically produces `bin/Release/Package/TorBox.totoro-plugin`.
