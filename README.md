# Totoro.Plugins

Official repository for external plugins for [Totoro](https://github.com/insomniachi/Totoro).

Plugins in this repository are dynamically loaded at runtime into an isolated `AssemblyLoadContext` in Totoro without compile-time coupling to the main application executable.

---

## Included Plugins

### Anime streaming

Status comes from each provider's live test (search, episodes, streams; in WebView2, as Totoro runs them, for plugins that load pages in a browser), run weekly by [Check Providers](https://github.com/insomniachi/Totoro.Plugins/actions/workflows/check-providers.yml). Last checked: <!-- provider-check -->never<!-- /provider-check -->.

| Plugin | ID | Description | Status |
|---|---|---|---|
| **AnimePahe** (`Totoro.Plugins.AnimePahe`) | `5c2f8e1a-7d43-4b96-a0e5-3f9c1b6d8a27` | Search, episodes, and stream link extraction via `animepahe.pw`. | ⏳ Not run yet |
| **ReAnime** (`Totoro.Plugins.ReAnime`) | `1d572b0b-9185-4679-86d7-5e6dfa1bcdcd` | Search, episodes, and FlixCloud file links via `reanime.cz`. | ⏳ Not run yet |
| **KickAssAnime** (`Totoro.Plugins.KickAssAnime`) | `6da3925f-e703-4f69-9999-c17d6fc7942f` | Search, episodes, and HLS/DASH streams with subtitles via `kaa.lt`. | ⏳ Not run yet |
| **AniPM** (`Totoro.Plugins.AniPM`) | `d3180556-f773-4265-ac9d-b3f3be98ce6a` | Search (also by AniList id), episodes, and soft-sub, hard-sub and dub HLS streams with intro/outro times via `ani.pm`, played through a local proxy. | ⏳ Not run yet |
| **Gotaku** (`Totoro.Plugins.Gotaku`) | `45389725-d0a0-4426-87b0-255e86901fc8` | Search, episodes, and decrypted HLS streams (soft sub, hard sub, dub) with intro/outro times via `gotaku.to`. | ⏳ Not run yet |

### Torrents

| Plugin | ID | Description | Status |
|---|---|---|---|
| **SubsPlease** (`Totoro.Plugins.SubsPlease`) | `9b7a71c8-7633-46e8-be5e-288c19ac1330` | Direct torrent schedule and magnet extraction via `subsplease.org`. | ⏳ Not run yet |
| **TsukiHime** (`Totoro.Plugins.TsukiHime`) | `e3b5d0a4-6f21-4c8e-9a7d-2b14c9f06e58` | Episode torrents matched by AniList/MyAnimeList id via `api.tsukihime.org`. | ⏳ Not run yet |

### Debrid services

| Plugin | ID | Description |
|---|---|---|
| **Real-Debrid** (`Totoro.Plugins.RealDebrid`) | `8fa7a097-dba5-4177-8f06-de7caa2ac580` | Streams torrents through the Real-Debrid debrid service (needs an account). |
| **TorBox** (`Totoro.Plugins.TorBox`) | `33c86adc-7dc0-4fa4-a2bf-993acc66700b` | Streams torrents through the TorBox debrid service (needs an account). |

### Trackers

| Plugin | ID | Description |
|---|---|---|
| **Shikimori** (`Totoro.Plugins.Shikimori`) | `4833500d-bf61-4600-9f2d-0890c63c38c4` | Anime database and list tracker via `shikimori.io` (ids are MyAnimeList's). |
| **Kitsu** (`Totoro.Plugins.Kitsu`) | `d8ae6ad9-3e24-40fe-b79a-8c5d4212dc6e` | Anime database and list tracker via `kitsu.app`. |

### Manga

| Plugin | ID | Description |
|---|---|---|
| **Weeb Central** (`Totoro.Plugins.WeebCentral`) | `5f2ae848-fafe-41bb-a851-9e22ecfb1e21` | Manga search, chapters and pages via `weebcentral.com`, matched by AniList/MyAnimeList links. |

---

## Development & Debugging Workflow

### 1. Sibling Repository Auto-Discovery
If this repository is cloned alongside the Totoro repository in the same parent folder (e.g. `c:\Users\<user>\source\repos\`):
- `Directory.Build.props` automatically detects the sibling `Totoro` repository and references `Totoro.Core` directly.
- Totoro in debug mode automatically discovers plugins built in this repository.

### 2. Press F5 to Debug in Visual Studio
Each plugin project in `src/` contains a preconfigured `Properties/launchSettings.json` profile:
- Press **F5** in Visual Studio with any plugin project as the startup project.
- Visual Studio starts `Totoro.exe` with `--plugin-dir "bin\Debug\net11.0"`.
- You can set breakpoints inside the plugin's C# code, inspect DI services, and debug in real time.

### 3. Standalone Build / CI
When building in a clean CI environment or without a local Totoro clone:
- `Directory.Build.props` automatically falls back to the published `Totoro.Core` NuGet package (`2.1.0`).
- Runtime host assemblies are excluded from plugin packages (`ExcludeAssets="runtime"`), keeping distribution packages lightweight.

---

## Releasing Plugins Separately

Each plugin in this repository has its own independent semantic version (defined in its `plugin.json`), and is released on its own from GitHub Actions:

1. Go to **Actions** â†’ **[Release Plugin](https://github.com/insomniachi/Totoro.Plugins/actions/workflows/release-plugin.yml)**.
2. Click **Run workflow**.
3. Select the plugin to release (e.g. `SubsPlease`, `AnimePahe`, `ReAnime`).
4. (Optional) Enter the new version number (e.g. `2.0.1`), or leave blank to increment the patch number of the latest released version.
5. (Optional) Enter release notes.
6. Click **Run workflow**.

The workflow will automatically:
- Build and package only that plugin into a `.totoro-plugin` archive, against the Totoro release its `minAppVersion` names (tag `v<minAppVersion>`). A plugin that uses anything newer fails to build, so raise `minAppVersion` first.
- Create a GitHub Release with tag `<Plugin>-v<version>` (e.g. `SubsPlease-v2.0.1`), with `<Plugin>.totoro-plugin` attached. A version that's already released is refused, never replaced.
- Update `plugins.json` on `main` with the new version, the download URL and the package's SHA-256, which Totoro checks before installing. If that push fails, the release is deleted, so the run can be repeated.

Releases run one at a time.

---

## Running Your Own Plugin Repository

Totoro reads its catalog from the official [`plugins.json`](plugins.json) by default. To publish plugins from a repository of your own:

1. **Fork** this repository on GitHub (or copy it into a new one, keeping `.github/workflows`, `Directory.Build.props` and `plugins.json`).
2. **Trim the catalog**: in `plugins.json`, keep the entries you want to offer (an empty list `[]` to start fresh). Delete the projects in `src/` you don't want, and remove them from the `plugin` choices in `.github/workflows/release-plugin.yml`.
3. **Add your plugin**: copy a project in `src/` that does the same kind of thing (e.g. `Totoro.Plugins.SubsPlease` for an anime provider), rename it, and in its `plugin.json` give it:
   - a new `id` (a fresh GUID: `[guid]::NewGuid()` in PowerShell), its `name`, `author`, `description`, `version` and `minAppVersion`;
   - an `iconUrl` in your repository, e.g. `https://raw.githubusercontent.com/<you>/<repo>/main/src/<Project>/icon.png`.
   Add its name to the workflow's `plugin` choices (alphabetical).
4. **Enable Actions**: a fork starts with them off; turn them on in your repository's **Actions** tab. The workflow asks for write access itself, to create the release and update `plugins.json` on `main`.
5. **Release it**: run **Actions â†’ Release Plugin** as above. It builds the package, attaches it to a release in *your* repository, and adds it to your `plugins.json` with its download URL and SHA-256.
6. **Point Totoro at it**: in Totoro, open **Plugins**, click the **Plugin repository** button (the store icon, next to **Install from file**) and set **Plugin repository** to your catalog's raw URL:
   ```text
   https://raw.githubusercontent.com/<you>/<repo>/main/plugins.json
   ```
   The catalog reloads when the box loses focus. Clear the box to go back to the official catalog.

The catalog can be any `plugins.json` served over HTTP(S), so a repository on another host works too, as long as each entry's `downloadUrl` points at its `.totoro-plugin` package. Totoro checks a package against its `sha256` before installing it.

---

## Contributing

Keep the docs in step with the code, in the same change:
- **New plugin**: add it to [Included Plugins](#included-plugins) (name, project, ID from its `plugin.json`, what it does).
- **New anime provider**: give it a live test named `<Plugin>Provider_Live_*` and a Status cell in its table; the weekly check finds it by that name.
- **New feature**: update this README wherever it describes what changed.
- **New package**: if its license asks for a notice, or the code is ported from another project, add it to [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## Installing Plugins in Totoro

- **In-App Store**: In Totoro, open **Plugins** from the main menu to browse the catalog and click **Install** or **Update**.
- **Manual**: Extract any `.totoro-plugin` zip into `%LocalAppData%\Totoro\Plugins\<PluginName>\`.

---

## License

MIT, see [LICENSE](LICENSE). Some plugins are ported from Apache-2.0 code; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
