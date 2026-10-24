# Third-Party Notices

CORDON includes or uses the following third-party components. Their original licenses remain in effect.

## Mod Organizer 2 USVFS

- Project: [ModOrganizer2/usvfs](https://github.com/ModOrganizer2/usvfs)
- Copyright: Sebastian Herbord and the Mod Organizer 2 Team
- License: GNU General Public License v3.0 or later
- Use: stable virtual filesystem backend for x64 and x86 game processes
- Runtime version: 0.5.7.2
- Main source revision: `c5f7ef2085bd29a3cca2cd57f3aba84dae030ff5` ([PR #99](https://github.com/ModOrganizer2/usvfs/pull/99), batched directory enumeration)
- Main patches: `scripts/patches/usvfs-pr99-tests.patch`, `scripts/patches/usvfs-pr99-runtime.patch` (test compatibility, Win32 TLS fix and early source-scan completion)
- Early source-scan completion adapted from [dlunch/usvfs](https://github.com/dlunch/usvfs)
- Optional legacy revision: `57f1ea5e6ad13f7435a7af184748e6c1312c5637`, with `scripts/patches/usvfs-msvc-pch.patch`

Standard release packages contain the main optimized runtime. Optional packages with the `-usvfs-legacy` suffix contain the previous runtime. The corresponding upstream sources are available from the linked repositories; tracked patches and build manifests (`scripts/UsvfsRuntimeManifest.psd1` and `scripts/UsvfsLegacyManifest.psd1`) are included in this launcher's source repository. The launcher itself is distributed under GNU GPLv3.

## Managed libraries

- [AngleSharp](https://github.com/AngleSharp/AngleSharp) 1.5.2 — MIT License.
- [discord-rpc-csharp](https://github.com/Lachee/discord-rpc-csharp), package `DiscordRichPresence` 1.6.1.70 — MIT License.
- [NAudio.Vorbis](https://github.com/naudio/Vorbis) 1.5.0 — MIT License.
- [NAudio](https://github.com/naudio/NAudio), package `NAudio.WinMM` 2.2.1 — MIT License.
- [SharpCompress](https://github.com/adamhathcock/sharpcompress) 0.50.1 — MIT License.
- [.NET Runtime](https://github.com/dotnet/runtime), package `System.Text.Encoding.CodePages` 8.0.0 — MIT License.

## Interface assets

- PDA interface sounds come from the ModDB package `PDA Sounds by SAM (3 in 1)`, marked Public Domain on its distribution page.
- Interface icons were collected through Iconify/Figma. Each icon remains subject to the license of its original Iconify icon set.
- The application icon and launcher-specific composition are project assets.

More information about the asset layout is available in [assets/README.md](assets/README.md).

## AP-PRO catalog content

The modification browser reads publicly available metadata and images from [AP-PRO.RU](https://ap-pro.ru/) at runtime. This content is not stored in the source repository and remains the property of AP-PRO, modification authors and their respective rights holders. Clicking a card opens its original AP-PRO page.

## Game names and trademarks

S.T.A.L.K.E.R., X-Ray and related names, artwork and trademarks belong to their respective rights holders. This launcher is an independent community project and is not affiliated with or endorsed by GSC Game World, Mod Organizer 2 or AP-PRO.
