# Totoro.Plugins.TsukiHime

TsukiHime anime streaming provider plugin for [Totoro](https://github.com/insomniachi/Totoro).

## Architecture

This plugin implements `IAnimeProvider` and `IModule<Settings>` from `Totoro.Core`. It is dynamically loaded at runtime into an isolated `AssemblyLoadContext` without compile-time coupling to the main Totoro application executable.

## Debugging with Totoro

### Option 1: Visual Studio / Rider (F5 Debug)
1. Set `Totoro.Plugins.TsukiHime` as the startup project or launch it.
2. Select the `Debug with Totoro` profile in `Properties/launchSettings.json`.
3. Press **F5**. Totoro launches and dynamically loads TsukiHime from this project's `bin/` directory via `--plugin-dir "bin\Debug\net10.0"`.

### Option 2: Command Line
```powershell
Totoro.exe --plugin-dir "C:\path\to\plugins\Totoro.Plugins.TsukiHime\bin\Debug\net10.0"
```

## Packaging & Distribution

Build in Release configuration:
```powershell
dotnet build -c Release
```
This automatically produces `bin/Release/Package/TsukiHime.totoro-plugin`.
