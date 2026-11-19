# Third-party notices

Totoro.Plugins is MIT licensed (see [LICENSE](LICENSE)). Some plugins are ported from the projects below, under the
licenses given. The plugins reference no other packages besides `Totoro.Core` (and `Totoro.UI`), which the Totoro app
supplies, except where listed under "Shipped with a plugin".

| Software | License | Copyright | Source | Used in |
|---|---|---|---|---|
| anime-extensions | Apache-2.0 | yuzono and contributors | https://github.com/yuzono/anime-extensions | AnimePahe, AniPM, Gotaku, KickAssAnime, ReAnime |
| extensions-source | Apache-2.0 | keiyoushi and contributors | https://github.com/keiyoushi/extensions-source | Weeb Central |

These plugins were rewritten in C# from those extensions' Kotlin sources. The text of the Apache License 2.0 is at
https://www.apache.org/licenses/LICENSE-2.0.

## Shipped with a plugin

| Software | License | Copyright | Source | Shipped with |
|---|---|---|---|---|
| libvlc 3.0.24 (VideoLAN.LibVLC.Windows) | LGPL-2.1-or-later | the VideoLAN team and VLC authors | https://code.videolan.org/videolan/vlc | VLC |
| LibVLCSharp 3.10.2 (LibVLCSharp.WinUI) | LGPL-2.1-or-later | VideoLAN | https://code.videolan.org/videolan/LibVLCSharp | VLC |
| SharpDX (used by LibVLCSharp) | MIT | Alexandre Mutel | https://github.com/sharpdx/SharpDX | VLC |

libvlc and LibVLCSharp are separate libraries the VLC plugin loads; you may replace them with your own builds. Their
license is in the plugin's `libvlc\win-x64\COPYING.LIB`, with where their source is in `libvlc\win-x64\NOTICE.txt`.
