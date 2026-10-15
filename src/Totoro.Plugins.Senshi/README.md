# Totoro.Plugins.Senshi

Senshi anime streaming provider plugin for [Totoro](https://github.com/insomniachi/Totoro).

## Architecture

This plugin implements `IAnimeProvider` and `IModule<Settings>` from `Totoro.Core`. It is dynamically loaded at runtime into an isolated `AssemblyLoadContext` without compile-time coupling to the main Totoro application executable.

## Debugging with Totoro

### Option 1: Visual Studio / Rider (F5 Debug)
1. Set `Totoro.Plugins.Senshi` as the startup project or launch it.
2. Select the `Debug with Totoro` profile in `Properties/launchSettings.json`.
3. Press **F5**. Totoro launches and dynamically loads Senshi from this project's `bin/` directory via `--plugin-dir "$(TargetDir)"`.
4. Set breakpoints inside `SenshiProvider.cs` (e.g. `SearchAsync`, `GetEpisodesAsync`, `GetServersAsync`). Breakpoints hit immediately when Totoro queries or plays an anime.

### Option 2: Command Line / Environment Variable
Run Totoro with:
```powershell
Totoro.exe --plugin-dir "C:\path\to\plugins\Totoro.Plugins.Senshi\bin\Debug\net10.0"
```
Or set the environment variable:
```powershell
$env:TOTORO_PLUGIN_DIRS="C:\path\to\plugins\Totoro.Plugins.Senshi\bin\Debug\net10.0"
```

## Packaging & Distribution

Build in Release configuration:
```powershell
dotnet build -c Release
```
This automatically produces `bin/Release/Package/Senshi.totoro-plugin`.

In Totoro:
- Go to **Test -> Plugins** or **Settings -> Plugins**.
- Click **Install from File...** and select `Senshi.totoro-plugin`.
- Restart Totoro to activate the plugin.
