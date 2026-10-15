# Totoro.Plugins

Official repository for external plugins for [Totoro](https://github.com/insomniachi/Totoro).

Plugins in this repository are dynamically loaded at runtime into an isolated `AssemblyLoadContext` in Totoro without compile-time coupling to the main application executable.

---

## Included Plugins

| Plugin | ID | Description |
|---|---|---|
| **Senshi** (`Totoro.Plugins.Senshi`) | `c6066fda-8e30-4545-9599-8e6c3e917312` | Stream anime and search catalogs via `senshi.to`. |
| **SubsPlease** (`Totoro.Plugins.SubsPlease`) | `9b7a71c8-7633-46e8-be5e-288c19ac1330` | Direct torrent schedule and magnet extraction via `subsplease.org`. |
| **AnimePahe** (`Totoro.Plugins.AnimePahe`) | `5c2f8e1a-7d43-4b96-a0e5-3f9c1b6d8a27` | Search, episodes, and stream link extraction via `animepahe.ru`. |

---

## Development & Debugging Workflow

### 1. Sibling Repository Auto-Discovery
If this repository is cloned alongside the Totoro repository in the same parent folder (e.g. `c:\Users\<user>\source\repos\`):
- `Directory.Build.props` automatically detects the sibling `Totoro` repository and references `Totoro.Core` directly.
- Totoro in debug mode automatically discovers plugins built in this repository.

### 2. Press F5 to Debug in Visual Studio
Each plugin project in `src/` contains a preconfigured `Properties/launchSettings.json` profile:
- Press **F5** in Visual Studio with any plugin project as the startup project.
- Visual Studio starts `Totoro.exe` with `--plugin-dir "bin\Debug\net10.0"`.
- You can set breakpoints inside the plugin's C# code, inspect DI services, and debug in real time.

### 3. Standalone Build / CI
When building in a clean CI environment or without a local Totoro clone:
- `Directory.Build.props` automatically falls back to the published `Totoro.Core` NuGet package (`>= 2.0.0`).
- Runtime host assemblies are excluded from plugin packages (`ExcludeAssets="runtime"`), keeping distribution packages lightweight.

---

## Building & Packaging

To compile and package all plugins into distributable `.totoro-plugin` archives:

```powershell
dotnet build Totoro.Plugins.slnx -c Release
```

The resulting `.totoro-plugin` zip files are automatically created in each project's `bin\Release\Package\` folder:
- `src/Totoro.Plugins.Senshi/bin/Release/Package/Senshi.totoro-plugin`
- `src/Totoro.Plugins.SubsPlease/bin/Release/Package/SubsPlease.totoro-plugin`
- `src/Totoro.Plugins.AnimePahe/bin/Release/Package/AnimePahe.totoro-plugin`

---

## Installing Plugins in Totoro

- **Manual**: Extract any `.totoro-plugin` zip into `%LocalAppData%\Totoro\Plugins\<PluginName>\`.
- **In-App**: In Totoro, open **Test → Plugins** and click **Install Plugin (.totoro-plugin)**.
