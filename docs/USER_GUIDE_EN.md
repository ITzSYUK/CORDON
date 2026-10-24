# User guide

[English](USER_GUIDE_EN.md) | [Русский](USER_GUIDE_RU.md) | [README](../README.md) | [Technical documentation](TECHNICAL_EN.md)

## What the launcher does

When you start a game from a launcher profile, the base game and selected mods are combined into a single file set. The original base-game and mod folders stay where they are.

Each configured setup is called a **profile**. A profile remembers:

- the base-game folder;
- the list of mods and their order;
- the file used to start the game;
- the file mounting mode;
- the location of saves, settings, logs, and screenshots: separate profile data by default, or an explicitly selected shared directory;
- play time and additional launch arguments.

For example, you can create one profile for Anomaly with gameplay add-ons, another for Anomaly with graphics add-ons, and a third for a different custom build. The launcher means you do not have to install THREE copies of the base Anomaly game at the same time: the same base game can be connected to three different profiles and used as the foundation on which add-ons, modified engine files, and other content are overlaid.

## What you need beforehand

A **Game with mods** profile requires:

1. An installed base game or ready-to-use base installation, such as Shadow of Chernobyl, Clear Sky, Call of Pripyat, or Anomaly.
2. Mod folders or ZIP, 7Z, RAR, and `.fomod` archives.
3. Free space for the profile's small internal files and writable data.

The launcher does not download mods. You can add a prepared folder directly, or install a ZIP, 7Z, RAR, or `.fomod` archive with the built-in `ZIP` button. FOMOD packages open a component-selection wizard in an existing Game with mods profile.

If a mod is distributed as a complete ready-to-run build with its own game and engine, you can create a **Standalone build** profile for it. A base game is not required in this case.

## Installing the launcher

1. Download the appropriate archive from the latest release page.
2. Extract the archive to a separate permanent folder.
3. Do not remove the USVFS DLL and EXE files next to the launcher.
4. Run `CORDON.exe` or `CORDON-Standalone.exe`.

The compact version requires .NET 8 Desktop Runtime x64. The Standalone version already includes .NET. USVFS may also require Microsoft Visual C++ 2015–2022 Redistributable x64 and x86.

`CORDON.exe` stores launcher settings in AppData. `CORDON-Standalone.exe` stores them beside the executable in `Data\StalkerModLauncher`; this folder must be writable. On the first launch without local settings, the Standalone launcher imports the existing primary AppData settings file, then keeps its own independent settings. Game and mod folders outside the launcher directory are not made portable automatically.

The Standalone launcher package and the Standalone build profile type are different: both launcher packages support both profile types.

### Main and legacy USVFS

For regular use, choose a standard archive: it includes the validated optimized USVFS. Optional `-usvfs-legacy` archives contain the previous runtime for manual rollback if compatibility problems arise. They are not included in every release; .NET requirements and settings locations match the compact and standalone variants.

The main runtime includes batched directory enumeration from PR #99, early source-directory scan completion adapted from dlunch, and our TLS fix for the observed OGSR CoP crash. Gains depend on the build; faster file enumeration does not imply an equivalent increase in FPS or game loading speed. Known limitation: asynchronous directory enumeration sees only physical entries.

The downloaded archive selects the USVFS version; the profile settings still use **USVFS** mode. There is no separate optimization switch. Extract the complete package and do not mix USVFS DLLs and EXEs from main and legacy sets. See [Discord and update checks](#discord-and-update-checks) for update behavior.

## What a profile is

You can think of a profile as a saved launch flow:

```text
which game to use + which mods to mount + in what order + which EXE to run
```

A profile is not a copy of the game. It stores settings and references to the selected folders. The same base-game files can be used by multiple profiles.

You can rename, copy, export, or delete a profile. Its working folder is tied to an internal ID, so simply renaming the profile does not create a new folder.

## Two profile types

### Game with mods

Choose this option to add mods to a separate game or ready-made build. The source folders are not modified.

Examples:

- Shadow of Chernobyl + a main mod + a patch;
- Call of Pripyat + the iX-Ray engine + gameplay add-ons;
- Anomaly 1.5.3 + MCM + graphics and gameplay mods + Anomaly-modded-exes.

The launcher combines the base folder and enabled mods in the specified order. Both Workspace and USVFS modes are available for this profile type.

A new **Game with mods** profile is created with USVFS selected. You can switch it to Workspace in the profile settings when necessary. Existing profiles are not switched automatically.

### Standalone build

Choose this option for a ready-made build whose folder already contains everything required to run and does not need additional mods.

A **Standalone build** profile runs directly from the selected folder. It does not create a separate working folder or overlay separate mod layers.

Important: a standalone build stores its saves and settings wherever its own `fsgame.ltx` points. The launcher detects common `appdata`, `userdata`, and `_appdata_` folders, but it does not forcibly move the build's data into a separate environment.

## Creating a Game with mods profile

1. Select **Create profile**.
2. Choose **Game with mods**.
3. Enter a clear name, such as `Anomaly`.
4. Select the base-game folder (`D:\Games\Anomaly`). You can also drag it into the corresponding field.
5. Add mod folders with the button or drag them into the list.
6. Check the detected launch file.
7. Finish creating the profile.
8. Arrange the mods in the required order.
9. Select **Launch**.

If a mod consists of a main part and patches, add them as separate entries. The main mod should be higher in the list and the newer patch lower.

## Creating a Standalone build profile

1. Select **Create profile**.
2. Choose **Standalone build**.
3. Enter a name.
4. Select or drag in the root folder of the complete build.
5. Check the detected EXE.
6. Finish creating the profile.

Do not choose **Standalone build** for a regular patch or an individual `gamedata` folder: it does not contain a complete game and cannot run on its own.

## Mod order and priority

Mods lower in the list have higher priority.

```text
base game
mod 1
mod 2
patch
```

If `mod 1` and `patch` contain a file at the same relative path, the game sees the version from the patch. This replacement is normal: fixes and add-ons are usually installed this way.

You can:

- enable and disable mods;
- drag one mod or a selected group;
- quickly move mods to the top or bottom of the list;
- remove a mod from the profile without deleting its source folder from disk.

Changing the order changes the resulting file set. On the next launch, Workspace checks the order and rebuilds `current` if necessary; USVFS applies the new order when creating the virtual overlay.

## Adding and finding mods

The add button lets you select one folder manually. In an existing profile, selecting or dropping an unpacked folder containing `fomod/ModuleConfig.xml` opens the FOMOD wizard. Select the required options: their files are copied to the profile's permanent mods folder, while the original package stays unchanged. No archive is needed. Plain folders are connected directly. Scanning is useful when one common directory contains many mods: the launcher searches for typical X-Ray structures, including `gamedata`, `bin`, `bin_x64`, game archives, and `db` or `patches` folders.

The `ZIP` button installs a mod from a ZIP, 7Z, RAR, or `.fomod` archive. Plain archives are extracted to the profile's permanent mods folder and then added to the list like a regular mod; FOMOD archives install only the resolved components. You can change this folder's path in the profile settings. Workspace is not used for the installed sources and can be freely recreated.

Check the detected root folders before adding scan results. Automatic detection helps, but an unusually packaged mod may need to be added manually.

### Installing a FOMOD package

First create a Game with mods profile with a base game. Then use `ZIP` for an archive or **Add mod folder** for an unpacked package containing `fomod/ModuleConfig.xml`. You can also drop the unpacked package into the existing profile's mod list. Adding folders during profile creation or scanning does not run the FOMOD wizard; use those flows for already prepared mods.

Classic UI opens a separate installer window. PDA UI and PDA UI 2 show the same wizard inside the shell. Choose the components, read their descriptions and images, then use **Next**, **Back**, and **Install**. A group may require one choice, allow several choices, or require all available components. Required components cannot be unchecked, recommended components are preselected, and unavailable components cannot be selected. Canceling does not add a mod or change the source package.

Only selected, required, and conditionally included files are installed. The original archive or folder is preserved. To change the component set, run the installer again from that original package; when the destination name already exists, the launcher offers a separate folder instead of replacing the previous installation.

Declarative XML installers are supported; C# FOMOD scripts and Bethesda-specific game/manager version dependencies are not. Successful installation does not provide missing engine or addon dependencies. For example, addons requiring Anomaly Modded Exes still need that engine installed in the profile.

### Importing from Mod Organizer 2

For a ready-made MO2 setup, select **MO2** next to the profile creation and import controls. In the wizard, you can select a Mod Organizer 2 root folder, an MO2 profile folder, or its `modlist.txt`. The last successfully selected source is loaded the next time the wizard opens; MO2 installations are not scanned automatically. The launcher searches for `profiles`, `mods`, the base game, and `overwrite`, then asks you to select an MO2 profile.

Before the profile is created, the wizard shows:

- detected mod folders, their status, and final order;
- groups restored from MO2 separators;
- missing and ambiguously matched folders;
- the contents of `overwrite` as a separate optional highest-priority layer that does not clutter the mod list;
- the automatically detected EXE, or a warning that you must select it manually.

When there are problems, the preview initially shows only missing and ambiguous entries; you can disable the filter. You can assign an individual folder to a missing mod, otherwise it is not included in the profile. If one entry matches multiple folders, select the correct one in the **Folder** column—the profile cannot be created until every such match is resolved. Choose Workspace or USVFS before creating the profile; USVFS is selected by default. MO2 source folders are used only as sources and are not modified. A new profile is saved transactionally: if saving fails, it is removed from the list and the settings remain unchanged.

`overwrite` is an MO2 service folder for files created by the game or tools outside a specific mod, such as generated configs, patches, and other outputs. Mount it only if the setup actually depends on these files. The launcher keeps it as a separate highest-priority layer; you can disable the layer in the imported profile's settings.

The MO2 wizard does not currently copy saves or `user.ltx`. Move them separately after checking the created profile. The **Apply modlist.txt only** button in profile settings changes only the order and enabled state of already added mods; it does not add folders. See the [MO2 import guide](MO2_IMPORT_GUIDE_EN.md) for the complete procedure.

## Mod conflicts

The radiation symbol's color shows the status of an enabled mod: yellow means no conflicts, green means the mod overwrites earlier mods, red means its files are overwritten, multicolored means a mixed conflict, and gray means the mod is completely overwritten. A disabled mod has a dimmed icon. The tooltip shows the exact status and file count.

Double-click a mod or select **Conflicts** to see winning, losing, and unique files. **Exclude** disables only the selected conflicting file in the current profile; the source mod folder is not modified. The **Final tree** tab in the conflicts window shows the resulting build with search, filters, and the replacement chain for every file.

## Choosing the launch file

The launcher searches for suitable EXE files in the base game and enabled mods. With automatic selection, a file from a higher-priority mod can replace a same-named file from the base game or an earlier mod.

Manual selection is required if:

- the build contains multiple engines;
- a specific renderer is required;
- the mod author requires a separate launcher;
- the automatic option is incorrect.

The **Select automatically** button removes the manual pin and lets the launcher determine the EXE source from the layers again.

For Anomaly in USVFS mode, you can keep automatic launch through Anomaly Launcher or select DX8, DX9, DX10, DX11, and AVX in the profile settings. A manually selected renderer runs the corresponding `AnomalyDX*.exe` directly.

## File mounting modes

Select the mode in the settings of a regular profile.

### Workspace: stable mode

Use this mode when a build is incompatible with USVFS or you need to inspect the assembled file tree. The launcher creates a `current` folder in which the game sees the final structure after mods are overlaid.

Most large files are not copied again. Hard links are used on the same drive, and symbolic links are used across drives. Only files that must be writable or physically present in the working environment are created separately.

Advantages:

- the most predictable compatibility with different engines and helper launchers;
- the resulting files can be inspected in `current`;
- after the first build, later launches are usually faster thanks to caching.

Things to know:

- the first build of a large profile may take some time;
- File Explorer shows the logical file size and may make it look as though the entire game was copied;
- cross-drive symbolic links may require Windows Developer Mode or running as administrator.

If a safe link cannot be created, the launcher stops and reports the problem. It does not silently begin copying the whole game.

### USVFS: stable virtual mode

USVFS uses the Mod Organizer 2 virtual file system. The base game and mods become a single file set only for the launched game, so no `current` folder is created.

Advantages:

- the final game tree does not need to be physically assembled;
- the profile usually takes less space;
- changes to mod order are applied without rebuilding `current`.

Things to know:

- some unusual engines, launchers, and ready-made builds may be incompatible;
- only one USVFS profile can run at a time;
- all x64 and x86 USVFS files must remain next to the launcher;
- if there is a problem, you can always switch the profile back to Workspace without modifying the source folders.

If launching requires physical EXE and DLL files before USVFS is mounted, the launcher creates a bootstrap cache at `<workspace>\.usvfs-bootstrap`. It is inside the profile's ASCII-only workspace and is not a copy of the whole game. This cache is not created when an unmodified base-game EXE can launch directly.

## What a workspace is

A workspace is a launcher-managed folder for a regular profile. By default, it is created on the base game's drive, for example:

```text
D:\StalkerModLauncher\Workspaces\profile-12ab34cd...\
```

The folder name is based only on the internal ID and always uses ASCII characters. The profile's display name, such as “Ихрей,” is stored in the launcher settings and does not affect the path. Old folders named like `Ихрей-12ab34cd` are renamed once the next time the profile is prepared.

Typical Workspace structure:

```text
profile-12ab34cd...\
  .stalker-launcher-workspace
  build-manifest.json
  current\
  userdata\
```

In USVFS mode, the profile folder and `userdata` remain, but `current` is not used.

### current

`current` is the rebuildable final game structure used in stable Workspace mode. It contains links to base-game and mod files, plus a small number of local service files.

You can clear or rebuild `current`. Saves and settings must not be stored there, so it is not considered valuable user data.

### userdata

`userdata` contains persistent data for a specific profile. It usually includes:

```text
userdata\savedgames
userdata\logs
userdata\screenshots
userdata\user.ltx
userdata\shaders_cache
userdata\writable-game-files
userdata\overwrite
```

Not every folder is created immediately. The exact set depends on the game, engine, and selected mode.

- `savedgames` contains saves;
- `logs` contains game logs and crash dumps;
- `screenshots` contains screenshots;
- `user.ltx` contains X-Ray user settings;
- `shaders_cache` stores the profile's shader cache if the build needs it;
- `writable-game-files` stores configuration files that the engine expects inside the game tree but must not be allowed to modify in the source folder;
- `overwrite` receives new or modified USVFS files;
- `.usvfs-bootstrap` inside the workspace contains only the physical files required to start that specific USVFS profile.

This rebuildable cache is not preserved when a profile is deleted or moved.

When backing up a profile, save `userdata` and a profile export first.

### build-manifest.json

This is an internal record of the last Workspace build: which sources and settings were used and how many links were created. The launcher uses it to determine whether the prepared `current` can be reused.

### Safety marker

The `.stalker-launcher-workspace` file confirms that the folder was created and is managed by the launcher. Before clearing or deleting the folder, the launcher checks this marker to avoid accidentally deleting an arbitrary user folder.

Do not remove the marker manually. If it is missing, clearing may be blocked for safety.

## How saves and settings are isolated

For a Game with mods profile, **Profile settings → User data** selects where saves, `user.ltx`, logs, screenshots, and shader caches are stored:

- **In the profile folder** is the default. Data is separate in `<workspace>\userdata`.
- **In the base game's data folder** uses the directory configured through `$app_data_root$`. The settings show the resolved path; a manually selected configuration or `-fsltx` can change its source. Profiles using the same directory share these data, and the game is explicitly allowed to write there. Deleting a profile does not delete shared data.

Switching the setting does not move files. **Copy missing data to the selected folder** copies from the other location, skips existing files, and excludes service stores. This action requires confirmation and is not undone by canceling the settings window. The selected location is used by log, save, screenshot, and folder-opening commands. Standalone build profiles retain their own data routing.

The following isolation and initial `user.ltx` rules describe In the profile folder mode.

For a regular profile, the launcher takes the resulting file configuration and changes only the `$app_data_root$` path, redirecting it to `userdata`. By default, the source is detected automatically in the enabled layers: either the regular `fsgame.ltx` or the file specified by the `-fsltx` launch argument.

If automatic detection is unsuitable, open **Profile settings → fsgame.ltx source → Select file...** and choose an `.ltx` file from the base game or an enabled mod. The manually selected source determines the configuration contents, while `-fsltx` determines the relative path the engine opens. If the paths differ, a prepared copy of the manually selected file is also created at the `-fsltx` path.

When saving the settings, the launcher verifies that the selected file is accessible and contains the required `$app_data_root$` line. Invalid settings are not saved, and the window stays open with an explanation of the error.

A manually selected file keeps its name and relative path. If its name differs from `fsgame.ltx`, an additional compatible copy named `fsgame.ltx` is created beside it with the same contents. For example, `_bin_olr_\fsolr.ltx` remains in place, while `_bin_olr_\fsgame.ltx` serves as an engine fallback. All other lines, including extra parameters specific to the mod, are preserved.

Because the managed profile folder has the ASCII-only name `profile-<ID>`, iXray and other older engines receive a path to `user.ltx` with no Cyrillic characters. No additional folder is needed to avoid Cyrillic characters.

As a result, two profiles using the `same base game` get different saves, settings, logs, and screenshots. The source `fsgame.ltx` in the game or mod folder is not edited.

On the first launch, `user.ltx` is taken from the highest-priority layer (the mod in the profile list) that provides it. After the game or the user changes the profile file, the launcher tries to preserve that version instead of overwriting it at every launch.

When launching through AnomalyLauncher, the launcher and game engine use one active `user.ltx` in the selected user-data directory. This works in Workspace and USVFS: settings changed through AnomalyLauncher or the game affect the same file. With shared game data selected, that file is also shared by profiles using the same directory.

After upgrading from an older version, the first Workspace launch of an AnomalyLauncher profile may rebuild `current` once. Differing legacy internal copies of `user.ltx` are saved as `<workspace>\diagnostics\user.ltx.migrated-<ID>.bak`; they do not replace the active selected settings. There is no need to move them back manually.

## Status window

The Status window shows whether the profile is ready to launch, which mode it uses, and what should be checked.

For Workspace, it shows the logical size of the resulting game, the actual additional disk space, and the file count. A large logical size does not mean that the same amount of data was copied.

For USVFS, it shows the number of mounted layers and the state of profile data. This mode does not require `current`.

The **fsgame.ltx source** entry shows the selection method and full path: detected automatically, detected from `-fsltx`, or selected manually. A missing automatic source is shown as a warning; an inaccessible or invalid selected file is shown as an error.

Main actions:

- **Refresh** repeats the Status check;
- **Rebuild** prepares Workspace again by rebuilding the `current` folder;
- **Clear cache** removes rebuildable data while preserving `userdata`;
- **Move...** copies `userdata` to a new managed workspace, switches the profile to it, and removes the old folder. `current` and `.usvfs-bootstrap` are recreated. If the old folder cannot be removed immediately, the new workspace remains active and the launcher offers to retry only the cleanup;
- **Latest log** opens the latest game log;
- **Crash dump** opens the latest crash dump if one exists;
- **Copy report** copies a diagnostic summary to the clipboard.

The save counter includes the main `.sav` and `.scop` file formats. Auxiliary `.dds` previews and `.scop_data` metadata are not counted as separate saves.

## Launcher log

The log at the bottom of the main window shows check progress, the selected mode, the EXE source, Workspace or USVFS preparation, and the launch result.

The full log location depends on the launcher package:

| Package | Path |
| --- | --- |
| `CORDON.exe` | `%APPDATA%\StalkerModLauncher\launcher.log` |
| `CORDON-Standalone.exe` | `<EXE directory>\Data\StalkerModLauncher\launcher.log` |

When the file reaches approximately 1 MB, it is moved to `launcher.old.log` and logging continues in a new file. The log does not grow indefinitely.

A USVFS session also writes `<workspace>\diagnostics\usvfs.log`. Its size is limited to approximately 2 MiB, with the previous part stored as `usvfs.old.log`. This is the virtual file system diagnostic log; the X-Ray game log is stored separately in the selected user-data directory. **Copy report** in the **Status** window includes the final lines of the USVFS log.

## Launcher settings

Open the settings with the gear button. Here you can:

- select Classic UI, PDA UI, or PDA UI 2, and System, Russian, or English language;
- configure startup with Windows and tray-icon behavior;
- select the `launcher.log` level;
- configure update checks;
- reset only the launcher settings without deleting profiles, mods, or game files.

In PDA UI and PDA UI 2, errors appear inside the current interface and are accompanied by the standard system error sound. Classic UI uses standard system dialog boxes.

## Where launcher settings are stored

```text
%APPDATA%\StalkerModLauncher\settings.json
%APPDATA%\StalkerModLauncher\settings.backup.json
%APPDATA%\StalkerModLauncher\profiles\profile-<ID>.json
%APPDATA%\StalkerModLauncher\profiles\profile-<ID>.backup.json
```

These paths apply to `CORDON.exe`. For `CORDON-Standalone.exe`, the same settings and backup filenames are stored in `<EXE directory>\Data\StalkerModLauncher`; launcher logs, cache, and temporary files are local too. Use **Open settings folder** in launcher settings to open the active location.

`settings.json` contains general launcher settings and profile order. Each JSON in `profiles` contains its profile's Workspace/USVFS and standalone modes, launch arguments, paths, statistics, and mod list. Legacy settings migrate automatically, preserving the original as `settings.legacy.json`. Importing a new-format `settings.json` requires its sibling `profiles` directory. The Standalone launcher stores paths inside its own directory relatively; external paths stay absolute. Game files are not copied into settings. Mods and profile workspaces keep their separate storage locations.

If a game or mod has been moved to another drive, correct the path in the profile. Do not edit the JSON while the launcher is running: the application saves all settings at once and may overwrite manual changes.

## What to back up

For reliable recovery, save:

1. `settings.json`, `settings.backup.json`, and the entire `profiles` directory; include `settings.pending.json` and `settings.legacy.json` if present;
2. the `userdata` folders of the required regular profiles;
3. exported profile files.

For profiles using shared game data and for standalone builds, also back up the actual data directory shown in profile settings or Status. Keep game/mod source folders and original archives separately: profile exports contain no game or mod files. In the Standalone launcher, include `Data\StalkerModLauncher` in the backup.

You do not need to back up `current`: the launcher can rebuild it from the base game and mod folders.

## Screenshots

The **Screenshots** button opens the images found for the profile. You can copy an image from its context menu; the full-screen viewer also supports double-clicking.

The launcher scans typical screenshot folders for the profile and standalone build. Copying does not modify the source file.

## Copying, exporting, and deleting a profile

- **Copy** creates a new profile with the same sources and mod order, but with a new ID and a separate working folder.
- **Export** saves the profile description for transferring the list settings. Games and mods themselves are not included in the export.
- **Import** restores a profile, but its paths must exist on the new computer.
- **Delete profile** removes the entry and the managed workspace of a regular profile. The source game and mod folders are preserved.
- Deleting a standalone profile also preserves the ready-made build's folder.

Before deleting a profile with important saves, back up its actual game-data directory. Shared data is preserved when the profile is deleted.

## Mod browser

The browser shows the public AP-PRO catalog for Shadow of Chernobyl, Clear Sky, and Call of Pripyat. You can select a category, search by name, and open the project's original page.

The launcher does not download or install mods from the browser. Covers and pages load gradually with request throttling; information is cached temporarily in memory only.

## Discord and update checks

For each profile, you can enable **Show profile status in Discord**. Disable this setting if the build already provides its own Rich Presence.

The launcher compares the installed version with the latest GitHub release. Automatic checking on startup and system notifications can be configured in launcher settings; a manual check is available in settings and the **About** window.

When an update is available, settings offer the release page and package download. Choose the compact or Standalone package and confirm installation. The archive is saved beside the launcher, its checksum and contents are verified, and the launcher closes, replaces its application files, and starts again. Existing `Data`, game/mod sources, workspaces, and saves are preserved. Updates cannot be installed while a game or profile preparation/mod installation is active. Choosing a different package type shows a warning because it changes the .NET requirement and launcher-settings location.

The built-in update installer downloads the main package with optimized USVFS. Updating a legacy package also replaces its previous runtime with the main one. To retain legacy, download the matching `-usvfs-legacy.zip` archive from the release page and update manually, preserving `Data` and user data. If the new version has no such archive, its legacy variant has not been published.

## Usage examples

### Anomaly with an engine and add-ons

Base: clean Anomaly 1.5.3.

Mods in order:

```text
Anomaly-modded-exes
MCM
gameplay mods
graphics mods
compatibility patches
```

The engine from `Anomaly-modded-exes` replaces Anomaly's engine files in the `bin` directory, and the patches at the bottom of the list receive the highest priority. Saves and settings remain in this profile's `userdata`.

### Shadow of Chernobyl with a main mod and patch

Base: installed Shadow of Chernobyl.

```text
main mod (for example, the “Likvidatsiya” mod)
latest patch (Patch 1.1 released for “Likvidatsiya”)
```

If the patch contains a new `xrEngine.exe` or configuration, the resulting file comes from the patch. The original game folder remains unchanged.

### Two different builds based on one game

You can create two profiles based on the same Call of Pripyat installation:

- one profile with graphics mods;
- another with a story mod and its own engine.

Both use the same base folder, but each gets its own mod list and its own uniquely identified Workspace directory.

### Standalone build

If a downloaded mod already contains all game files and runs from its own folder, create a **Standalone build** profile. The launcher remembers its EXE and provides quick access to logs and screenshots without creating a separate Workspace.

## If the game does not start

1. Open **Status** and refresh the check.
2. Make sure the base-game folder and all enabled mod folders exist.
3. Check mod order and the build author's requirements.
4. Check the selected EXE and its source.
5. Read the launcher log and latest game log.
6. If you use USVFS, temporarily switch the profile to Workspace. If Workspace works, the specific engine or its launcher is probably incompatible with USVFS.
7. For cross-drive link problems, enable Windows Developer Mode or run the launcher as administrator.
8. Do not place files manually in `current`: they are removed when it is rebuilt.

A game error does not always mean a launcher error. Incompatible mods, incorrect order, and missing dependencies produce the same errors as they do with a manual installation.
