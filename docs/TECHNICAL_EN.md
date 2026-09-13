# Technical documentation

[English version](TECHNICAL_EN.md) | [Русская версия](TECHNICAL_RU.md) | [Russian user guide](USER_GUIDE_RU.md)

This document describes the current architecture of CORDON `v1.4.2`: how profiles are stored, how the winning file is selected, how Workspace differs from USVFS, where profile data is kept, and which checks protect original game and mod folders.

The detailed USVFS research history and early prototypes are available in [USVFS_RESEARCH_EN.md](USVFS_RESEARCH_EN.md).

## 1. Scope and compatibility

The launcher works with local game and mod folders. Its main use case is a base game plus an ordered list of folders whose contents must be layered over that game.

Support is based on file structure rather than a hard-coded game title. The file model understands common X-Ray elements:

- `gamedata`;
- `fsgame.ltx`;
- `bin` and `bin_x64`;
- `gamedata.db*` archives, including archives stored in `db` and `patches`;
- `appdata`, `userdata`, and `_appdata_`.

In practice this covers Shadow of Chernobyl, Clear Sky, Call of Pripyat, Anomaly, OGSR, iX-Ray, and many derived projects. Compatibility with every custom launcher or modified engine cannot be guaranteed.

The launcher can install mods from ZIP, 7Z, and RAR archives into separate managed storage. It does not execute EXE installers, process FOMOD scripts, or repair incompatible game content.

## 2. Core concepts

### Profile

Each profile is stored as a separate launch recipe. It contains:

- profile ID and name;
- profile type;
- base game path;
- the mod list, order, and enabled state;
- selected launch mode;
- relative EXE path and, for a manual choice, its pinned source folder;
- command-line arguments and relative working directory;
- workspace path;
- playtime, last launch time, Discord setting, and Anomaly renderer setting.

Calculated values such as the current running state and formatted dates are not written to JSON.

### “Game with mods” (standard profile)

A “Game with mods” profile creates this layer order:

```text
base game -> mod 1 -> mod 2 -> ... -> profile writable data
```

A mod lower in the UI has higher priority. When several enabled layers contain the same relative path, the last layer wins.

### “Standalone build” (standalone profile)

A “Standalone build” profile starts an already assembled game or mod from its own folder without additional mods. It does not create a layer plan, `current`, or a separate mod overlay. Save and log locations are controlled by that build through `fsgame.ltx` and its usual data directories.

## 3. Launch pipeline

Both launch modes use the same high-level sequence:

1. Game and mod folders, EXE, working directory, and launch-mode availability are validated.
2. A managed workspace is assigned to the ID of a standard profile.
3. One shared layer plan is created and the winning file for every overlapping path is resolved.
4. Workspace or USVFS prepares the final launch parameters.
5. The game starts with the selected EXE, arguments, and working directory.
6. The launcher tracks process exit, playtime, and Discord Rich Presence.
7. After exit, it looks for a new game log and crash dump.

Preparation produces a small instruction set: which mode to use, which absolute EXE to start, which arguments to pass, and which working directory to assign. For USVFS it also keeps a temporary virtual-file-system session alive.

This split keeps the Status window, preflight checks, and actual launch from independently choosing different executables or interpreting mod order differently.

## 4. Shared layer model

### File layer plan

Before validation or launch, the launcher builds one overlay plan for a standard profile. It contains:

- the base game at order `0`;
- enabled mods in user-defined order;
- `userdata` as the final profile-data layer.

The launcher uses this plan to answer four main questions:

- which file at a relative path the game will finally see;
- which layers provide that file;
- which EXE files are available and where they come from;
- which earlier files are overridden by a mod.

Workspace and USVFS consume the same plan instead of implementing separate priority rules.

### Final overlay snapshot

After analysis, the launcher keeps a compact snapshot of the important results:

- layers and their order;
- selected EXE and its source;
- important files such as `fsgame.ltx`, `user.ltx`, and `localization.ltx`;
- writable files;
- the `userdata\overwrite` root;
- overlap information when requested.

This in-memory snapshot is not the same as Workspace `build-manifest.json`. The first describes what should be visible. The second records the latest physical `current` build.

## 5. Executable resolution

Automatic selection examines candidates from the base game and every enabled mod. If the same relative path exists in several layers, the candidate from the highest-priority layer wins.

A manual selection stores two values:

- the EXE path inside the virtual game tree;
- the pinned folder from which that exact EXE must be taken.

This allows a user to intentionally select an executable from a lower layer even when a higher-priority mod contains an EXE with the same name. Returning to automatic selection clears the pinned source.

For Anomaly under USVFS there are two supported paths:

- automatic mode starts the Anomaly Launcher found in the base-game root through a small 32-bit helper, and the game process it creates inherits USVFS;
- a manually selected renderer starts the chosen `AnomalyDX*.exe` directly, including AVX variants.

If a mod provides the selected renderer at the same relative path, the file from the highest-priority layer is selected.

Automatic mode always prefers the base-game Anomaly Launcher over engine executables from mods. The standard `AnomalyLauncher.exe` name is recognized directly; after a rename, EXE metadata (`OriginalFilename`, `InternalName`, description) is used, or `AnomalyLauncher.cfg` when there is a single root EXE. Ambiguous cases are not guessed and require manual selection.

## 6. Workspace: stable mode

The stable mode creates a managed working folder for the profile. By default, its root is placed on the base game's drive:

```text
<game drive>\StalkerModLauncher\Workspaces\profile-<ID>\
```

The display name is not part of the filesystem path, so Russian and English profiles use the same ASCII format. When an older managed directory named `<name>-<short ID>` is found, the launcher renames the whole directory before launch while preserving `userdata`, `current`, and service files.

If a drive cannot be resolved, the fallback root is:

```text
%LOCALAPPDATA%\StalkerModLauncher\Workspaces
```

Workspace layout:

```text
<workspace>\
  .stalker-launcher-workspace
  build-manifest.json
  current\
  userdata\
```

### Building current

Before building, the launcher snapshots the base game and mods. It then creates a signature from source paths, file state, mod order, selected EXE, and relevant profile options. `build-manifest.json` also stores the detailed inputs of that signature, so a mismatch can name the exact changed file, EXE, EXE source, layer set, or layer order in the log.

When the signature matches `build-manifest.json` and the selected EXE still exists, `current` is reused. Profile writable files are still restored and validated.

When sources have changed, the workspace is rebuilt:

1. known writable files are collected from the old `current`;
2. the old `current` contents are removed through guarded deletion;
3. the base game is materialized;
4. enabled mods are applied in order;
5. profile `fsgame.ltx`, `user.ltx`, and writable files are prepared;
6. a manually pinned EXE source is enforced when required;
7. a new `build-manifest.json` is written.

### File strategy

The launcher chooses a safe representation for each file:

- hard link when the source and workspace are on the same NTFS volume;
- symbolic link when a cross-volume link is needed;
- local file for configuration that must change independently;
- a dedicated read-only copy only where linking would be unsafe for the source.

There is no silent fallback that copies the whole game. If a safe link cannot be created, preparation stops with an actionable error.

A hard link shares physical data with its source, so writing through it would also change the source. Files that may be modified are therefore never left as ordinary writable hard links. Attributes and local copies protect the original folders.

Explorer counts hard-linked files toward the visible logical size. Actual additional disk usage is calculated from local files and shown separately.

## 7. USVFS: stable virtual mode

USVFS mode uses the official [ModOrganizer2/usvfs](https://github.com/ModOrganizer2/usvfs) runtime. Instead of creating `current`, it presents one merged virtual tree to the launched game.

New standard profiles select USVFS by default. Existing profiles, standalone builds, and imported profiles retain their stored or explicitly selected backend.

The shared layer plan is converted into this mapping order:

```text
base game -> mods in order -> known writable files -> profile overwrite
```

`userdata\overwrite` is mapped last, monitors changes, and has the highest priority.

### x64 and x86

x64 targets are started through the managed adapter and `usvfs_x64.dll`. x86 targets use `StalkerModLauncher.UsvfsX86Host.exe`, which loads `usvfs_x86.dll` in a process with matching architecture.

The x86 host remains alive while injected child processes are active and publishes their PIDs to the managed readiness monitor. The managed x64 path likewise waits for the USVFS process list to remain empty after the initial EXE exits. This is required for launcher applications that exit immediately after starting the actual engine.

Only one USVFS session may run at a time because the official runtime uses shared process state and a shared namespace.

### Virtual-root strategy

The launcher chooses a virtual-root strategy according to the launch layout:

- the physical base-game root is used when its own EXE starts and mods do not provide loader-time files;
- the physical Anomaly root is preferred when an `AnomalyDX*.exe` is selected directly;
- Anomaly automatic mode uses an isolated launcher bootstrap so the base-game Anomaly Launcher can start physically prepared final engine files;
- a physical X-Ray 1.6 root is used when `$arch_dir_*` entries are present so archives in `patches` and similar directories remain visible;
- an isolated bootstrap root is used when a mod provides the engine or the selected executable needs its own neighboring DLL set.

The physical game directory is never mapped over itself. Doing so can hide real `gamedata.db*` archives from the engine.

### usvfs-bootstrap

The bootstrap is created inside the profile's ASCII workspace:

```text
<workspace>\.usvfs-bootstrap
```

The directory is created only for launch layouts where Windows and the engine must see physical files before full virtual lookup is available:

- selected EXE;
- loader-time DLL files from its directory;
- profile `fsgame.ltx`;
- the smallest required set of neighboring files.

For Anomaly automatic mode, the bootstrap also contains the final top-level `bin` files from the active layers. That physical `bin` level is not mapped over again, so a child engine and its loader-time DLLs resolve from one consistent directory; nested folders and the remaining game/mod data stay virtual. `AnomalyLauncher.cfg` and `commandline.txt` are profile-owned files stored in `userdata\overwrite`, so launcher changes persist without modifying the base game.

This directory is a service cache, not a full game copy. It is not created when an unmodified physical base-game EXE can be launched directly. The cache is regenerated instead of being preserved during a workspace move and is removed with the workspace when the profile is deleted.

USVFS runtime files distributed beside the launcher are:

```text
usvfs_x64.dll
usvfs_proxy_x64.exe
usvfs_x86.dll
usvfs_proxy_x86.exe
StalkerModLauncher.UsvfsX86Host.exe
```

## 8. Profile data isolation

Standard profiles now have a **Game data** setting: profile-local data (default), or the base game's configured directory. Shared mode resolves `$app_data_root$` and referenced aliases from the base game's `fsgame.ltx`; missing, unknown, cyclic, and unsafe paths block launch. Only the prepared configuration is rewritten. Standalone profiles keep their direct-launch behavior.

Workspace and USVFS write the resolved absolute path into the prepared configuration. A manually selected file keeps its original name and relative path. If its name differs from `fsgame.ltx`, the prepared working layer also gets a sibling `fsgame.ltx` compatibility copy with identical content; the source file in the game or mod is neither renamed nor changed. `$fs_root$` resolves from the game or virtual root rather than the configuration directory. With USVFS, the shared directory stays outside the virtual bootstrap root and is not mapped through `CreateTarget`, so X-Ray accesses the base game's directory directly. This is an explicit write exception for that directory only. Service stores, diagnostics, writable game-tree files, and overwrite remain inside the profile. Save/log/screenshot discovery and folder opening follow the selected location; deleting a profile never deletes shared data.

The `-fsltx` launch argument selects its relative `.ltx` path through normal layer priority. A manual source controls the contents; when `-fsltx` names another path, a prepared copy of that manual file is also written there. Readiness checks and profile-data discovery use the same source as the actual launch.

Switching does not copy or overwrite game data. The separate **Copy missing data** action copies from the other location to the selected one, skips existing entries, rejects directory links, and excludes service stores. Canceling profile settings does not undo an explicitly requested copy. The following isolation/seeding rules apply to profile-local mode only.

Persistent data for a standard profile is kept in:

```text
<workspace>\userdata
```

The launcher takes the selected file-system configuration, preserves its name, relative path, and encoding, including Windows-1251, and changes `$app_data_root$` to the profile's absolute `userdata` path. Other aliases and mod-specific lines remain intact. Data previously misrouted into the corresponding `userdata\overwrite` subtree is copied without replacing existing files, and the source copy is retained. Managed workspaces always use the ASCII name `profile-<ID>`, so iXray and other engines with limited Unicode support receive a normal path without an extra junction or second data directory.

For a non-standalone profile, launch is blocked when `fsgame.ltx` is missing or does not contain `$app_data_root$`. Workspace and USVFS therefore never report profile-data isolation as successful when the engine would still use a shared data directory.

Common contents include:

```text
userdata\savedgames
userdata\logs
userdata\screenshots
userdata\user.ltx
userdata\shaders_cache
userdata\writable-game-files
userdata\overwrite
```

### user.ltx

On first preparation, the source is selected from the highest-priority layer down to the base game. If the profile copy has already been changed and no longer matches a source file, it is treated as user-owned and preserved.

If the profile copy still equals the previous lower-layer source, a new `user.ltx` from a higher-priority patch may safely replace it.

### Shader cache

Prepared shader caches from layer appdata folders are merged according to priority. Existing user cache files are not overwritten without a reason. This supports Anomaly builds that ship a prepared cache with their fixes or presets.

### Writable files inside the game tree

Some engines write configuration inside the game tree. Known paths including `gamedata\configs\localization.ltx` and Anomaly `gamedata\configs\axr_options.ltx` receive a separate profile copy under `userdata\writable-game-files`. Workspace materializes these as independent local copies from the first build instead of hard-linking them to a source.

Workspace places that copy into `current` and collects changes before the next preparation or profile cleanup. When switching between Workspace and USVFS, the newer of the two copies wins, so a stale `current` cannot overwrite settings changed through USVFS. On its first launch, USVFS seeds the profile copy from the highest-priority layer and maps it to the expected virtual path. Legacy copies of these files under `userdata\overwrite` are migrated into the profile store.

### Standalone profiles

For a standalone profile, the launcher inspects `fsgame.ltx` and common locations such as `appdata`, `userdata`, `_appdata_`, `bin\_appdata_`, and `bin_x64\_appdata_`. It does not rewrite data routing for that build.

## 9. File safety and workspace lifecycle

Deleting a profile never deletes source game or mod folders.

A managed workspace is protected at two levels:

- `.stalker-launcher-workspace-root` identifies an allowed Workspaces root;
- `.stalker-launcher-workspace` binds a profile folder to its profile ID.

Before recursive cleanup, move, or deletion, the launcher verifies:

- the target is inside an allowed root;
- the marker file exists;
- the short ID matches the profile;
- source and destination do not form an unsafe nested path.

The operation is blocked when validation fails. A missing marker may be restored only when an automatically generated folder unambiguously matches the profile ID.

The workspace path becomes bound to the profile ID after its first assignment. Renaming the profile does not create a new workspace. A copied profile receives a new ID and its own folder.

Deleting a standard profile removes only its validated managed workspace. Deleting a standalone profile removes its settings entry but leaves the standalone build folder untouched.

## 10. Settings and recovery

### Standalone local settings

`CORDON-Standalone.exe` automatically uses `<EXE directory>\Data\StalkerModLauncher` for settings, backup, launcher logs, `Cache`, and `Temp`. Regular `CORDON.exe` always uses AppData and ignores `portable.flag`. `Mods` and `Workspaces` keep their normal `<game drive>\StalkerModLauncher` location; the `%LOCALAPPDATA%` fallback is used when a drive cannot be resolved. Write access is checked before startup; failure shows an error without falling back to AppData. Windows startup registration is supported and points to the current `CORDON-Standalone.exe`.

The portable launcher must be outside game/mod source folders: overlapping source roots are rejected before building layers to avoid including its own Data tree. USVFS still requires an ASCII-only workspace path, so place the portable launcher under a path without non-ASCII characters.

Paths inside the EXE directory are stored relative to it; external game, mod, and workspace paths stay absolute. On the first launch without a local `settings.json` or backup, the primary AppData settings file is imported automatically. The standalone store is independent after that import; manual `settings.json` import is also available while the profile list is empty. Profile `userdata`, `Mods`, and `Workspaces` remain under `<game drive>\StalkerModLauncher`.

The paths below describe ordinary mode.

Settings files are stored at:

```text
%APPDATA%\StalkerModLauncher\settings.json
%APPDATA%\StalkerModLauncher\settings.backup.json
```

The JSON file contains a settings-structure version number. Its current value is `8`. While loading the file, the launcher:

- upgrades the schema version;
- creates missing collections;
- repairs empty or duplicate IDs;
- normalizes mod order;
- resets the temporary running-state marker;
- migrates supported legacy fields.

Saving is atomic as far as the file system permits:

1. a complete snapshot is serialized;
2. it is written to `.tmp`;
3. the main file is replaced;
4. the previous version becomes the backup.

If the primary JSON is damaged, it is first copied with a timestamp to `%APPDATA%\StalkerModLauncher\recovery` and is replaced with a readable backup only after the copy succeeds. If neither file can be read, both originals are preserved in `recovery`, a new configuration is created, and the user receives an explicit notification. A temporarily locked or inaccessible file is not treated as damaged: the launcher leaves it unchanged and blocks further writes until a successful reload. The complete failure reason is written to the launcher log.

Settings reads and writes are performed one at a time, so two operations cannot edit the file concurrently. A second launcher instance is also blocked.

An explicit save from the profile settings UI propagates persistence failures back to the window. The window remains open, shows the error, and restores the in-memory profile values instead of presenting unsaved changes as successful.

In ordinary mode, game and mod paths are absolute. When a source folder is moved, the user must select it again. The workspace move operation first copies `userdata`, changes the stored path only after success, and then removes only the explicitly remembered old path without performing a general profile-folder search. The path is changed on the UI thread because WPF observes the profile. If final cleanup fails, the move remains successful: the launcher shows both paths, writes the full exception to the log, and offers to retry only the old-folder cleanup.

## 11. Validation, status, and diagnostics

Preflight validates:

- base game and enabled mod folders;
- final EXE and architecture;
- working directory and arguments;
- the complete x64/x86 USVFS bundle, PE architecture of every file, and matching versions of the upstream runtime components;
- readiness of `fsgame.ltx` and profile data;
- workspace safety markers;
- common loader-time DLL files near the selected engine.

Errors block launch. Warnings describe unusual layouts that may still be valid.

The Status window uses the same models and displays a compact summary. Workspace statistics are read from `build-manifest.json` without rescanning the full tree. In USVFS mode it shows layers and profile-data readiness because `current` does not exist.

Application logs are stored at:

```text
%APPDATA%\StalkerModLauncher\launcher.log
%APPDATA%\StalkerModLauncher\launcher.old.log
```

The current log rotates at 1 MB, replacing `launcher.old.log` with the previous log.

Game logs and dumps are searched in profile `userdata` or the common data folders of a standalone build. Diagnostics use the launch timestamp so an old crash dump is not reported as the result of the current session.

After process creation the launcher watches actual readiness for up to 30 seconds: a main window, normal process memory growth, or a fresh game log. When none of these signals appears, the profile remains running, but the user receives a warning and may terminate the related processes or keep waiting.

The internal USVFS message queue is drained continuously outside the game logs into the bounded `<workspace>\diagnostics\usvfs.log` file, rotating to `usvfs.old.log`. This prevents queue saturation from blocking the engine and prevents third-party addons from mistaking the service file for an X-Ray log. The final 30 lines are included in the Status-window report. Legacy `userdata\logs\usvfs*.log` files are moved automatically on the next launch.

## 12. Mod management

Scanning searches recursively for mod roots, but stops treating nested folders as separate mods after a valid root is found. It recognizes unpacked files and X-Ray archives, including `db` and `patches` directories.

Archive installation is transactional through a temporary directory. The launcher validates entry paths, prevents extraction outside the destination, finds one X-Ray content root, and renames the temporary directory only after successful extraction. The storage path is profile-specific and defaults to `StalkerModLauncher\Mods\profile-<ID>` beside the workspace root. Source mods are never stored in the workspace because it is a rebuildable overlay result. Like MO2's Anomaly checker, loose `.db*` files are moved into `db\mods`.

Grouped movement preserves the relative order of selected mods. The UI supports drag and drop plus move-to-start and move-to-end commands.

The MO2 transfer wizard accepts a Mod Organizer 2 root, an MO2 profile directory, or `modlist.txt`. It reads `ModOrganizer.ini`, supports standard and relative paths, and discovers `profiles`, `mods`, the base game, and `overwrite` without writing to any source directory.

The preview matches `modlist.txt` entries to physical directories, reports missing matches, and requires the user to select a source when several directories match. After all ambiguities are resolved, it transfers enabled state and converts MO2 order to the launcher's lower-is-higher priority rule. MO2 separators are persisted in `ModEntry.GroupName` and do not participate in the layer plan. A non-empty `overwrite` directory can be connected through `ModProfile.Mo2OverwritePath` as a separate layer after regular mods; it is hidden from the user-facing mod list and can be disconnected in profile settings. The executable is selected only through the existing detection over actual layers; MO2 launch arguments are not copied automatically.

Profile creation is transactional with respect to settings: the profile is first added in memory and then explicitly saved through the atomic settings store. On failure it is removed, the previous selection is restored, and the wizard remains open. Saves and `user.ltx` are not copied in the current version.

The additional **Apply only modlist.txt** command preserves the previous behavior: it matches entries only to mods already present in a profile and changes their state and order.

Conflict analysis indexes `relative path -> providing mods` and classifies every enabled mod as conflict-free, overwriting earlier mods, overwritten by later mods, mixed, or fully redundant. The detail view shows winning, losing, and unique files; the final tree shows the effective provider of every path.

An individual conflicting file can be excluded only in the current profile without changing its source directory. Workspace skips it during materialization, while USVFS maps the previous effective provider back onto that path after directory mappings. Unique files cannot be excluded because a normal USVFS mapping cannot hide them correctly.

## 13. AP-PRO modification browser

The browser reads the public Shadow of Chernobyl, Clear Sky, and Call of Pripyat categories. It does not download or install modifications.

Network behavior is deliberately conservative:

- honest `User-Agent: StalkerModLauncher/<version>` with a repository link;
- sequential page loading with a short delay;
- no more than four simultaneous cover-image downloads;
- one retry after `429 Too Many Requests`, honoring `Retry-After` up to 30 seconds;
- cancellation of an old category load when the user switches categories;
- an in-memory cache lasting about 10 minutes;
- lazy image decoding.
- a 4 MiB limit for catalog HTML and an 8 MiB limit for each cover image, enforced while streaming even when the server omits `Content-Length`.

Search filters loaded titles and continues to include later pages. The empty-result message appears only after the category load has finished.

Catalog pages are parsed as an HTML5 DOM rather than with regular expressions. The browser remains dependent on AP-PRO availability and on the semantic classes and attributes used by the site.

## 14. Additional features

- Screenshot discovery finds PNG, JPG, and BMP files in profile and standalone data locations.
- Clipboard copying releases the full-size image after transfer instead of keeping it in memory unnecessarily.
- Discord Rich Presence publishes profile and launch state only when the user enables the option.
- Update checking is manual and compares the installed version with the latest GitHub release tag.
- UI sounds reuse embedded OGG decoders instead of creating a new decoder on every click.
- Child-window navigation is kept outside the main window so its code can focus on display and user actions.

## 15. Project structure

```text
src/StalkerModLauncher/
  Models/          persisted data and final launch parameters
  ViewModels/      WPF state and commands
  Views/           windows and reusable controls
  Themes/          palette and styles
  Services/        launch, workspace, USVFS, diagnostics, AP-PRO
  Infrastructure/  MVVM base types and commands

native/
  StalkerModLauncher.UsvfsX86Host/

research/
  usvfs-poc/
  usvfs-managed-poc/

tests/StalkerModLauncher.Tests/
```

At application startup, one shared module creates the required components. Workspace is always available. USVFS is enabled when its runtime files are present; a development override is retained for isolated tests.

The UI follows MVVM without an external dependency-injection container. Main-screen logic is divided by user scenario, and major areas of the window are separate controls.

The classic and PDA interfaces share the same data model and commands. In PDA mode, settings, profile status, the catalog, log, screenshots, and the profile creation wizard are hosted inside one shell; only the true fullscreen screenshot viewer opens a separate monitor-sized window.

## 16. Build, tests, and release packaging

.NET 8 SDK is required for development:

```powershell
dotnet build .\StalkerModLauncher.sln -c Release
dotnet test .\StalkerModLauncher.sln -c Release
dotnet run --project .\src\StalkerModLauncher\StalkerModLauncher.csproj
```

NuGet dependencies are pinned by per-project lock files. Built-in .NET analyzers and warnings-as-errors run during every build; the explicit baseline in `Directory.Build.props` records pre-existing analyzer debt without hiding new warning IDs. GitHub Actions restores in locked mode, verifies formatting, builds Release, runs all tests, and uploads Cobertura coverage on every push to `main` and every pull request.

Complete release packaging:

```powershell
.\scripts\Build-Release.ps1
```

The script reads the version from the project file and verifies formatting, the Release build, all unit tests, and the native x64/x86 USVFS overlay before packaging. Both smoke scenarios verify a short-lived launcher, its child process, and a changed mod file across five consecutive sessions.

The full local stress test is started with `.\scripts\Test-UsvfsStress.ps1`. By default it performs 50 consecutive x64 and x86 sessions while changing overlay contents between launches.

The script creates two ZIP archives:

- a compact framework-dependent package that requires .NET 8 Desktop Runtime x64;
- a self-contained package with the .NET runtime included.

Both packages contain the official x64/x86 USVFS runtime, x86 host, `LICENSE.txt`, and `THIRD-PARTY-NOTICES.txt`. PDB, JSON, Markdown, and intermediate files are excluded from user ZIP files.

Before packaging, the USVFS source is checked against its pinned revision and tracked patch; `scripts\Prepare-UsvfsSource.ps1` prepares that state. The names, paths, and version of the four upstream components are checked against `scripts\UsvfsRuntimeManifest.psd1`. Source builds and CI do not compare binary hashes: output bytes may differ between Windows SDK and vcpkg versions. Every package contains a `checksums.txt`, and the release directory contains another checksum file for the ZIP archives. After packaging, both ZIP files are extracted to a temporary directory and compared completely with their prepared packages, including EXE version, source commit, and absence of unexpected files.

USVFS publish:

```powershell
.\scripts\Build-UsvfsDependencies.ps1
.\scripts\Build-Usvfs.ps1
```

`Build-UsvfsDependencies.ps1` reproducibly prepares the pinned USVFS source, builds the x64/x86 runtime, x86 host, and both x86 smoke-process executables. For a clean clone, pass `-BootstrapUsvfs`; Visual Studio 2022 Build Tools with MSVC v143 for x64/x86 C++, CMake, Git, and `VCPKG_ROOT` are required. The script forces vcpkg to use VS 2022 and temporarily maps the repository root to a free drive letter so MSVC does not depend on non-ASCII characters in the original path. This is a local test build. After publishing, the script automatically runs the same x64/x86 USVFS smoke tests.

After changing Visual Studio or vcpkg, use `-CleanUsvfsBuild`: it removes only `.external\usvfs\vsbuild64` and `vsbuild32` before configuring again.

Without `-CleanPublishRoot`, the script recreates only `publish\usvfs`; the switch deletes all of `publish`. Compiled third-party binaries are not stored in Git.

The `.github\workflows\usvfs.yml` workflow repeats the complete build and smoke test on Windows 2022 when USVFS, the native helper, or related scripts change.

## 17. Known limitations

- USVFS and Workspace are both considered stable. Use the other backend for builds that are incompatible with one of them.
- USVFS requires the Microsoft Visual C++ 2015-2022 Redistributable matching the target game's architecture.
- Cross-drive symbolic links depend on Windows configuration.
- Absolute game and mod paths are not repaired automatically after folders are moved.
- A “Standalone build” profile cannot guarantee separate saves if the build itself writes to a shared external folder.
- Automatic EXE and mod-root detection cannot replace the instructions supplied by a specific mod author.
- The modification browser depends on AP-PRO availability and HTML structure.
