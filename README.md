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
| **KickAssAnime** (`Totoro.Plugins.KickAssAnime`) | `6da3925f-e703-4f69-9999-c17d6fc7942f` | Search, episodes, and HLS/DASH streams with subtitles via `kaa.lt`. |
| **Real-Debrid** (`Totoro.Plugins.RealDebrid`) | `8fa7a097-dba5-4177-8f06-de7caa2ac580` | Streams torrents through the Real-Debrid debrid service (needs an account). |
| **TorBox** (`Totoro.Plugins.TorBox`) | `33c86adc-7dc0-4fa4-a2bf-993acc66700b` | Streams torrents through the TorBox debrid service (needs an account). |
| **Shikimori** (`Totoro.Plugins.Shikimori`) | `4833500d-bf61-4600-9f2d-0890c63c38c4` | Anime database and list tracker via `shikimori.io` (ids are MyAnimeList's). |
| **Kitsu** (`Totoro.Plugins.Kitsu`) | `d8ae6ad9-3e24-40fe-b79a-8c5d4212dc6e` | Anime database and list tracker via `kitsu.app`. |

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

Each plugin in this repository has its own independent semantic version (defined in its `plugin.json`), and is released on its own from GitHub Actions:

1. Go to **Actions** → **[Release Plugin](https://github.com/insomniachi/Totoro.Plugins/actions/workflows/release-plugin.yml)**.
2. Click **Run workflow**.
3. Select the plugin to release (e.g. `Senshi`, `SubsPlease`, `AnimePahe`, `ReAnime`).
4. (Optional) Enter the new version number (e.g. `2.0.1`), or leave blank to increment the patch number of the latest released version.
5. (Optional) Enter release notes.
6. Click **Run workflow**.

The workflow will automatically:
- Build and package only that plugin into a `.totoro-plugin` archive, against the Totoro release its `minAppVersion` names (tag `v<minAppVersion>`). A plugin that uses anything newer fails to build, so raise `minAppVersion` first.
- Create a GitHub Release with tag `<Plugin>-v<version>` (e.g. `Senshi-v2.0.1`), with `<Plugin>.totoro-plugin` attached. A version that's already released is refused, never replaced.
- Update `plugins.json` on `main` with the new version, the download URL and the package's SHA-256, which Totoro checks before installing. If that push fails, the release is deleted, so the run can be repeated.

Releases run one at a time.

---

## Installing Plugins in Totoro

- **In-App Store**: In Totoro, open **Test → Plugins** to browse the catalog and click **Install** or **Update**.
- **Manual**: Extract any `.totoro-plugin` zip into `%LocalAppData%\Totoro\Plugins\<PluginName>\`.
