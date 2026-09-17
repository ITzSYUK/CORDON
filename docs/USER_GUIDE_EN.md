# User guide

[English](USER_GUIDE_EN.md) | [Русский](USER_GUIDE_RU.md) | [README](../README.md) | [Technical documentation](TECHNICAL_EN.md)

## What CORDON does

CORDON starts S.T.A.L.K.E.R./X-Ray games from saved profiles. A profile combines a base game, enabled mods, their priority, a launch executable, and isolated user data. Source game and mod folders remain unchanged.

Use a regular profile for a base game plus mods. Use a standalone profile for a complete build that already contains its own game and engine.

## Installation

1. Download the appropriate archive from the latest release.
2. Extract it to a permanent folder outside your games and mods.
3. Keep the USVFS DLL and EXE files next to the launcher.
4. Run `CORDON.exe` or `CORDON-Standalone.exe`.

The compact build requires .NET 8 Desktop Runtime x64. The Standalone build includes .NET. USVFS may also require Microsoft Visual C++ 2015–2022 Redistributable x64 and x86.

## Language

Open launcher settings and select **System**, **English**, or **Русский**. Restart CORDON to apply the new language. **System** uses the Windows display language and falls back to English when no matching translation exists.

Existing settings created before localization keep Russian selected so an update does not unexpectedly change the interface language.

## Creating a regular profile

1. Select **Create profile**.
2. Choose **Game with mods**.
3. Enter a profile name.
4. Select or drop the base-game folder.
5. Add or drop mod folders.
6. Check the detected executable.
7. Create the profile and arrange the mods.
8. Select **Launch**.

Mods lower in the list have higher priority. Add a main mod and its patches as separate entries, with newer patches below the main mod.

## Creating a standalone profile

1. Select **Create profile**.
2. Choose **Standalone build**.
3. Select the complete build's root folder.
4. Check the detected executable and create the profile.

Do not use this type for a patch or a lone `gamedata` folder: it must contain a complete runnable build.

## Adding and importing mods

Use the folder button to add one prepared mod folder. Use **ZIP** to install ZIP, 7Z, or RAR archives into the profile's permanent mod directory. Scanning can find typical X-Ray structures in a folder containing many mods.

Use **MO2** to import a Mod Organizer 2 instance, an MO2 profile folder, or `modlist.txt`. Review missing and ambiguous folders before creating the profile. MO2 source folders are read-only. Its `overwrite` directory is imported as an optional highest-priority layer.

## Conflicts and priority

Open **Conflicts** to inspect winning, overwritten, and unique files. **Exclude** disables one conflicting file only in the current profile; it does not modify the source mod. The final-tree view shows the resulting file set and each replacement chain.

## Launch modes

### Workspace

Workspace creates a `current` game tree using NTFS hard links and symbolic links. It is the most predictable mode and lets you inspect the final files. Cross-drive symbolic links may require Windows Developer Mode or administrator rights.

If a safe link cannot be created, CORDON stops instead of silently copying the whole game.

### USVFS

USVFS creates a virtual overlay using Mod Organizer 2 components, so no full `current` tree is required. Only one USVFS profile can run at a time. Some unusual engines or launchers may be incompatible; switch that profile to Workspace if necessary.

## Saves and settings

Regular profiles redirect `$app_data_root$` from the effective `fsgame.ltx` into profile `userdata`. This isolates saves, settings, logs, screenshots, shader cache, and writable game files.

CORDON finds `fsgame.ltx` automatically in enabled layers or from `-fsltx`. If needed, select an `.ltx` file manually under **Profile settings → fsgame.ltx source**. The selected file must belong to the base game or an enabled mod.

The source `fsgame.ltx` is never edited.

## Profile storage

A regular profile normally uses a managed folder such as:

```text
D:\StalkerModLauncher\Workspaces\profile-12ab34cd...\
```

Important entries are:

```text
.stalker-launcher-workspace
build-manifest.json
current\
userdata\
```

`current` is disposable and can be rebuilt. Back up `userdata` and exported profiles. Do not remove the `.stalker-launcher-workspace` safety marker manually.

## Health and logs

Open **Health** to check the game, mods, executable, `fsgame.ltx`, Workspace or USVFS state, saves, the latest game log, and crash dumps. You can refresh the report, rebuild or clear Workspace, move profile data, and copy the diagnostic report.

The launcher log is stored at:

```text
%APPDATA%\StalkerModLauncher\launcher.log
```

## Import, export, and deletion

- **Copy** creates another profile with a new ID and separate managed data.
- **Export** saves profile configuration, not game or mod files.
- **Import** restores that configuration; referenced folders must exist on the new computer.
- Deleting a regular profile removes its managed workspace but not source game or mod folders.
- Deleting a standalone profile does not remove its build folder.

Back up important `userdata` before deleting a profile.

## Troubleshooting

1. Open **Health** and refresh the checks.
2. Verify the base game and every enabled mod folder.
3. Check mod order and the selected executable source.
4. Read the launcher log and latest game log.
5. If USVFS fails, switch that profile to Workspace.
6. For cross-drive link errors, enable Windows Developer Mode or run CORDON as administrator.
7. Do not place files manually in `current`; rebuilding removes them.

Game errors can also be caused by incompatible mods, missing dependencies, or incorrect priority.
