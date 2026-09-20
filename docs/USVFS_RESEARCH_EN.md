# USVFS research branch

[English version](USVFS_RESEARCH_EN.md) | [Русская версия](USVFS_RESEARCH_RU.md)

This branch is intentionally separate from `main`. The stable launcher mode is still
`LinkedWorkspace`; this branch is for researching integration with
[ModOrganizer2/usvfs](https://github.com/ModOrganizer2/usvfs) without risking the
current workspace-based launch path.

## What was verified

`ModOrganizer2/usvfs` is a native C++ library that creates process-local virtual
file links by using API hooking. Its public README describes the goal as links
visible only to selected processes, including overlaying multiple directories
over one destination. The same README says the project is GPLv3 licensed and
is built with CMake, Python 3+ and vcpkg:

```powershell
cmake --preset vs2022-windows-x64
cmake --build --preset vs2022-windows-x64 --config Release
```

The public header exposes the important controller/runtime functions:

- `usvfsCreateVFS` / `usvfsConnectVFS`
- `usvfsVirtualLinkFile`
- `usvfsVirtualLinkDirectoryStatic`
- `usvfsCreateProcessHooked`
- `usvfsCreateVFSDump`

That shape fits our launcher better than the removed custom hook experiment:
our managed code should prepare a mapping plan, while the native library owns
process injection and filesystem interception.

## Local build and PoC status

`ModOrganizer2/usvfs` was built locally as x64 and x86 with Visual Studio Build Tools,
CMake, Ninja and vcpkg. The build artifacts used for research were kept outside
the repository under `.external/` and a temporary source/build folder.

The branch also contains a tiny external proof of concept in
`research/usvfs-poc`. It links against the locally built `usvfs_x64.lib`, copies
`usvfs_x64.dll` and `usvfs_proxy_x64.exe` to the PoC output directory, then:

1. creates a `base` folder and a `mod` folder;
2. maps both folders over one virtual root with `usvfsVirtualLinkDirectoryStatic`;
3. starts a child process through `usvfsCreateProcessHooked`;
4. verifies that files unique to each layer are visible and that `mod` wins over
   `base` for matching paths.

Verified result:

```text
shared=mod
base-only=base
mod-only=mod
nested=mod-system
USVFS overlay PoC passed.
```

The next bridge is also verified: `research/usvfs-managed-poc` uses the
launcher-side `UsvfsRuntime` managed adapter, loads the same official
`usvfs_x64.dll` via P/Invoke and starts a hooked child process from C#.

Verified result:

```text
shared=mod
base-only=base
mod-only=mod
nested=mod-system
Managed USVFS PoC passed.
```

The branch now contains `UsvfsLaunchBackend`. The backend is
available when the complete validated x64/x86 USVFS bundle and x86 host are
placed next to the launcher executable. The environment variable
`STALKER_MOD_LAUNCHER_ENABLE_OFFICIAL_USVFS=1` remains available for isolated
test runs, but is not required by the prepared USVFS build.
Each non-standalone profile can select `LinkedWorkspace` or `VirtualFileSystem`
in the profile settings UI.

The integration supports both executable architectures. x64 targets use the
managed adapter and `usvfs_x64.dll` directly. x86 targets are started by the
small same-bitness `StalkerModLauncher.UsvfsX86Host.exe`, which loads
`usvfs_x86.dll`, applies the same mapping plan and waits for the game process.
This deliberately avoids the unstable cross-bitness proxy path observed during
research. The launcher permits one active USVFS profile at a time for both
architectures; an x86 session itself runs in an isolated helper process.

The helper remains alive while hooked descendants are running, so a short-lived
launcher such as Gunslinger `Play.exe` cannot tear down USVFS before its
`xrEngine.exe` child exits.

For Anomaly, `Auto` starts the 32-bit `AnomalyLauncher.exe` through the x86
helper, and the 64-bit engine it creates inherits USVFS. A manual selection of
DX8, DX9, DX10, DX11, or AVX bypasses the launcher and starts the selected
`AnomalyDX*.exe` directly. This selection is stored as a relative
path, so `FileLayerPlan` still resolves the executable from
the enabled mod with the highest priority.

## Current integration

`UsvfsMappingPlanBuilder` converts the existing launcher model into operations
applied by the official USVFS runtime:

```text
mod 1 root     -> virtual game root
mod 2 root     -> virtual game root
...
known writable files -> exact virtual file paths
userdata\overwrite -> virtual game root, create target
```

The runtime backend creates a small profile-local bootstrap root. The selected
EXE, its loader-time dependencies and the generated `fsgame.ltx` physically
exist there. OGSR/X-Ray profiles use it as their virtual game root because some
engines derive the root from the module path. Anomaly keeps its physical game
root as the USVFS destination because its loose script loading is not reliable
through a separate root. Mapping a base directory onto itself is deliberately
avoided in both strategies because it can hide physical `gamedata.db*` files.

The profile workspace path is reserved before either backend prepares a launch.
It is persisted by profile ID and contains the standard launcher marker, so
renaming a profile does not create a second directory and deletion follows the
same safety rules in `LinkedWorkspace` and `VirtualFileSystem` modes.

This keeps one source of truth:

- `FileLayerPlan` defines layer order.
- `OverlayManifest` defines executable, writable files and overwrite storage.
- `UsvfsMappingPlanBuilder` translates those layers into USVFS operations.

## Why this direction

The removed custom VFS tried to solve too many native-hooking problems inside
this launcher. It crashed too often and did not reliably apply mod layers.

The new direction is narrower:

1. Keep `LinkedWorkspace` as the stable launch backend.
2. Keep `FileLayerPlan` and `OverlayManifest` as shared logic.
3. Keep the official USVFS adapter isolated behind `IProfileLaunchBackend`.
4. Expose it as an explicit per-profile setting without changing
   the default backend.

## Manual test workflow

1. Put the USVFS runtime files next to the launcher: `usvfs_x64.dll`,
   `usvfs_proxy_x64.exe`, `usvfs_x86.dll`, `usvfs_proxy_x86.exe` and
   `StalkerModLauncher.UsvfsX86Host.exe`.
2. Open profile settings and select `USVFS - stable`.
3. For Anomaly, leave `Auto` selected or choose the required renderer.
4. Start the profile and verify mod files, saves, settings and the application log.
5. Switch the profile back to `Workspace - stable` if the game build is not
   compatible with the current USVFS runtime.

## Important constraints

- Do not replace `LinkedWorkspace` automatically; select the backend per profile.
- Do not reintroduce custom hook DLLs.
- Do not let USVFS write into game or mod folders.
- All new/changed files must still target profile-controlled writable storage.
