# Totoro.Plugins

Official repository for external plugins for [Totoro](https://github.com/insomniachi/Totoro).

Plugins in this repository are dynamically loaded at runtime into an isolated `AssemblyLoadContext` in Totoro without compile-time coupling to the main application executable.

---

## Included Plugins

| Plugin | ID | Description |
|---|---|---|
| **Senshi** (`Totoro.Plugins.Senshi`) | `c6066fda-8e30-4545-9599-8e6c3e917312` | Stream anime and search catalogs via `senshi.to`. |
| **SubsPlease** (`Totoro.Plugins.SubsPlease`) | `9b7a71c8-7633-46e8-be5e-288c19ac1330` | Direct torrent schedule and magnet extraction via `subsplease.org`. |
| **AnimePahe** (`Totoro.Plugins.AnimePahe`) | `5c2f8e1a-7d43-4b96-a0e5-3f9c1b6d8a27` | Search, episodes, and stream link extraction via `animepahe.pw`. |
| **ReAnime** (`Totoro.Plugins.ReAnime`) | `1d572b0b-9185-4679-86d7-5e6dfa1bcdcd` | Search, episodes, and FlixCloud file links via `reanime.to`. |
| **TsukiHime** (`Totoro.Plugins.TsukiHime`) | `e3b5d0a4-6f21-4c8e-9a7d-2b14c9f06e58` | Episode torrents matched by AniList/MyAnimeList id via `api.tsukihime.org`. |
| **Real-Debrid** (`Totoro.Plugins.RealDebrid`) | `8fa7a097-dba5-4177-8f06-de7caa2ac580` | Streams torrents through the Real-Debrid debrid service (needs an account). |
| **TorBox** (`Totoro.Plugins.TorBox`) | `33c86adc-7dc0-4fa4-a2bf-993acc66700b` | Streams torrents through the TorBox debrid service (needs an account). |

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

## Releasing Plugins Separately

Each plugin in this repository has its own independent semantic version (defined in its `plugin.json`). You can release any plugin individually using either of two methods:

### Method 1: Using the GitHub Actions UI (Recommended)
1. Go to **Actions** → **[Release Plugin](https://github.com/insomniachi/Totoro.Plugins/actions/workflows/release-plugin.yml)**.
2. Click **Run workflow**.
3. Select the plugin to release (e.g. `Senshi`, `SubsPlease`, `AnimePahe`, `ReAnime`).
4. (Optional) Enter the new version number (e.g. `2.0.1`), or leave blank to use the version in `plugin.json`.
5. (Optional) Enter release notes.
6. Click **Run workflow**.

The workflow will automatically:
- Build and package only that plugin into a `.totoro-plugin` archive.
- Create a GitHub Release with tag `<Plugin>-v<version>` (e.g. `Senshi-v2.0.1`).
- Attach `<Plugin>.totoro-plugin` to the release.
- Update `plugins.json` on `main` with the new version and release download URL.

### Method 2: Using Scoped Git Tags
Push a tag following the pattern `<PluginName>-v<Version>`:

```powershell
# Example: release version 2.0.1 of Senshi
git tag Senshi-v2.0.1
git push origin Senshi-v2.0.1

# Example: release version 2.1.0 of SubsPlease
git tag SubsPlease-v2.1.0
git push origin SubsPlease-v2.1.0
```

The `Release Plugin` workflow triggers automatically on matching tags and performs the complete release and catalog update process.

---

## Installing Plugins in Totoro

- **In-App Store**: In Totoro, open **Test → Plugins** to browse the catalog and click **Install** or **Update**.
- **Manual**: Extract any `.totoro-plugin` zip into `%LocalAppData%\Totoro\Plugins\<PluginName>\`.
