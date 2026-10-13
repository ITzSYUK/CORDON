# How to import a build from Mod Organizer 2

[English version](MO2_IMPORT_GUIDE_EN.md) | [Русская версия](MO2_IMPORT_GUIDE_RU.md)

The import wizard creates a new CORDON profile from an already configured MO2 profile. Game, mod and `overwrite` folders stay in place: the launcher uses them only as sources.

## What is imported

- the MO2 profile name;
- the base game path;
- mod folders;
- enabled and disabled states;
- mod order and priority;
- groups created by MO2 separators;
- `overwrite` contents, only when you enable that option manually;
- the executable, only when the launcher can detect it reliably.

Saves, `user.ltx` and MO2 launch arguments are not copied automatically. After importing, you can transfer them separately if the build needs them.

## Before you start

Close MO2 and the game. Check that the build launches from MO2 and that you do not plan to move or delete its folders: the new profile will reference them.

You only need the path to one of these locations:

- the MO2 root folder;
- the required profile folder inside `MO2\profiles`;
- that profile's `modlist.txt`.

## Step 1. Open the wizard

Click **MO2** beside the profile list.

Select the MO2 folder or a specific `modlist.txt`. The launcher will try to locate:

- available MO2 profiles;
- the `mods` folder;
- the base game;
- the `overwrite` folder.

After a successful selection, the launcher remembers this source and loads it when you next open the wizard. It does not automatically search your drives for MO2 installations.

Check the detected paths. If MO2 uses custom folder locations, select the correct paths with the **Change** buttons.

## Step 2. Select the MO2 profile

If several profiles are available, choose the required one in **MO2 profile**. You can change the name before creating the launcher profile.

Click **Preview**. The launcher reads `modlist.txt`, matches entries to physical folders and converts MO2 priorities to its own rule: `mods lower in the list have higher priority`.

## Step 3. Check the detected mods

The preview shows each mod's state, group and folder.

- **Found**: the folder is unambiguous and will be added.
- **Folder is missing**: select this mod's directory with the button in the **Folder** column, or leave the entry skipped.
- **Select one of N folders**: several similar folders were found, so the launcher requires your selection.

An ambiguous row is highlighted. Open the list in the **Folder** column and select the directory that belongs to this mod. Its state changes to **Selected manually** and the highlight disappears. Profile creation remains unavailable while any ambiguous rows remain.

If there are problems, the wizard initially shows only missing and ambiguous mods. Clear the filter checkbox to see the full list.

## Step 4. Decide whether you need MO2 overwrite

`overwrite` is MO2's service folder. It contains files created by the game or tools outside a specific mod, such as configuration files, generator output and other changes.

**Add files from MO2 overwrite as a separate highest-priority layer** is disabled by default. Enable it only when the build depends on `overwrite` contents. This layer receives the highest priority but does not appear as a normal mod in the list. You can disconnect it later in profile settings.

## Step 5. Create and check the profile

Before creating the profile, select **Workspace** or **USVFS**. USVFS is selected by default.

Click **Create profile**. The operation is transactional: if settings cannot be saved, the incomplete profile is removed and the wizard stays open.

After creation:

1. Check the base game path and selected executable.
2. Review mod order and enabled states.
3. Launch the build and check that it works; switch the backend in profile settings if it is incompatible.
4. Transfer saves and `user.ltx` only after a successful launch.

## Reading conflict icons

The radiation symbol's color shows how a mod's files interact with other mods.

- yellow: no conflicts;
- green: the mod overwrites files from mods above it;
- red: its files are overwritten by mods below it;
- multicolored: it both overwrites other files and is partially overwritten;
- gray: all useful files are covered by later mods;
- dimmed: the mod is disabled.

Double-click a mod or click **Conflicts** to see specific files and replacement chains. Source mod folders remain unchanged.

## If you only need the old order import

**Apply modlist.txt only** in profile settings does not transfer a build. It changes the order and state only of mods already added to the profile manually. Use the **MO2** wizard for a complete transfer.

## Common problems

### The base game or mods folder was not found

MO2 may use custom paths. Return to the first step and select the folders manually.

### Some mods are missing

Check that the selected `mods` folder belongs to the MO2 installation containing this profile. The wizard cannot restore deleted or moved folders.

### The imported build does not launch

Check the executable, launch arguments, selected backend and whether `overwrite` is required. Saves and `user.ltx` do not affect profile creation, but some prepared builds expect their own settings.

### Can I delete MO2 after importing?

No, if the mods remain in its `mods` folder. The launcher does not copy these files and continues to use the selected directories as sources.
