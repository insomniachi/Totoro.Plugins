# Totoro.Plugins.AniPM

AniPM streaming provider plugin for [Totoro](https://github.com/insomniachi/Totoro).

## Architecture

This plugin implements `IAnimeProvider` and `IModule<Settings>` from `Totoro.Core`. It is dynamically loaded at runtime into an isolated `AssemblyLoadContext` without compile-time coupling to the main Totoro application executable.

The video comes from Settlar (`embed.settlar.io`, `media.settlar.io`), whose Cloudflare turns away .NET's TLS handshake and any request whose `Host` header isn't first (as ffmpeg, so mpv, sends it). So the embed session is loaded through Totoro's browser (`IWebPageLoader`), and the stream and subtitles are served to the player from a localhost proxy (`AniPMProxy`) that fetches them with Windows' own `curl.exe`.

## Debugging with Totoro

### Option 1: Visual Studio / Rider (F5 Debug)
1. Set `Totoro.Plugins.AniPM` as the startup project or launch it.
2. Select the `Debug with Totoro` profile in `Properties/launchSettings.json`.
3. Press **F5**. Totoro launches and dynamically loads AniPM from this project's `bin/` directory via `--plugin-dir "bin\Debug\net10.0"`.

### Option 2: Command Line
```powershell
Totoro.exe --plugin-dir "C:\path\to\plugins\Totoro.Plugins.AniPM\bin\Debug\net10.0"
```

## Packaging & Distribution

Build in Release configuration:
```powershell
dotnet build -c Release
```
This automatically produces `bin/Release/Package/AniPM.totoro-plugin`.
