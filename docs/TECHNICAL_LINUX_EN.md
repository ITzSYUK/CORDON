# CORDON-LINUX - technical overview

Architecture of the Linux port, the invariants it relies on, and where to extend it.
User-facing documentation lives in `USER_GUIDE_LINUX_RU.md` and `README.LINUX.md`;
the original Windows launcher is documented in `TECHNICAL_EN.md`.

---

## 1. Layers of the port

```
src/cordon/
├── core/      pure standard library, no Qt import anywhere in this tree
│   ├── paths.py       XDG layout, portable layout, marker files, delete guards
│   ├── util.py        path/encoding/atomic-write helpers, signatures, sizes
│   ├── errors.py      CordonError hierarchy (SafetyError, ToolMissingError, ...)
│   ├── elf.py         ELF/PE/script inspection, arch comparison, ldd analysis
│   ├── models.py      settings schema, Profile/ModEntry, LayerSource, LaunchPlan
│   ├── settings.py    atomic JSON store with flock, backup and recovery
│   ├── launcherlog.py rotating launcher log + in-memory ring buffer
│   ├── xray.py        knowledge about the game tree (gamedata, db*, mod layouts)
│   ├── fsgame.py      fsgame.ltx parser/writer, alias chain resolution, template
│   ├── engine.py      engine discovery (portable, system, pinned), data roots
│   ├── layers.py      layer plan: ordering, winners, stats, conflict index
│   ├── mounts.py      fuse-overlayfs lifecycle, /proc/mounts, tool detection
│   ├── overlay.py     ProfileWorkspace: build, reuse, verify, remove
│   ├── mods.py        mod scanning, archive extraction, managed storage
│   ├── conflicts.py   per-mod conflict classification, effective tree
│   ├── preflight.py   Check/PreflightReport shared by GUI, CLI and launch
│   ├── audit.py       case-sensitivity audit and alias symlinks
│   ├── launch.py      launch plan, process session, output reader, diagnostics
│   ├── mo2.py         Mod Organizer 2 modlist import
│   ├── diagnostics.py log/dump collection and report rendering
│   ├── discord.py     Discord IPC over the UNIX socket
│   └── service.py     CordonService: the single entry point for both front ends
├── gui/       PySide6 only (theme, widgets, dialogs, worker threads, main window)
└── cli.py     argparse front end, one handler per command
```

`core` must stay importable without PySide6: `tests/cordon` runs headless and the CLI works on
machines without Qt. The GUI never touches the filesystem directly — everything goes through
`CordonService`, which is why the same behaviour can be tested without a display.

## 2. How a profile is materialised

```
ProfileWorkspace(profile)
  root       <data_dir>/profiles/profile-<id>   or profile.root_path
  game_data  <root>/gamedata
  appdata    <root>/_appdata_
  fsgame     <root>/fsgame.ltx
```

`prepare(plan)`:

1. **backend dispatch** - `direct`/standalone only prepare `_appdata_`;
2. **early GUI-independent validation** - for `fuse-overlayfs` the tool and the layer set are
   checked *before* anything is deleted, so a failed run never leaves a half-built profile;
3. **reuse check** - `build_signature(plan, engine)` (a hash of layer paths, order, mtimes and
   the engine binary) is compared with `build-manifest.json`; `_artifacts_present()` re-checks
   that the tree, the fsgame file and the links still exist, and for fuse that the mount is up;
4. **rebuild** - `_clean_generated()` (manifest driven, only paths the launcher created),
   `_link_root_entries()` (symlinks for `bin/`, `patches/`, archives …), then either
   `_build_link_overlay()` or `_mount_overlay()`;
5. **fsgame** - `write_fsgame(plan)` renders the profile copy from the best available source;
6. **appdata** - `_prepare_appdata()` creates the engine's data directories and seeds
   `user.ltx` / `shaders_cache` from layer `_appdata_` data;
7. **alias replay** - `.cordon-aliases.json` is applied again for the `link` backend.

`build-manifest.json` (schema 1) records signature, backend, layer descriptions, counts, root
links, copies and seeds; `remove_root()` refuses to delete a directory that lacks the
`.cordon-profile` marker.

## 3. Launch path and engine contract

```
xr_3da -fsltx <root>/fsgame.ltx -overlaypath <root>/_appdata_ [-cs] [profile flags] [user args]
cwd = <root>                       (standalone profiles: the game directory)
env = parent env + engine dir in LD_LIBRARY_PATH (only when .so files sit next to the binary)
```

* `-fsltx` makes the profile directory `$fs_root$` (OpenXRay resolves the *directory* of the
  given file); the installed `fsgame.ltx` is never modified.
* `-overlaypath` moves `$logs$` and `$app_data_root$` into the profile and makes the engine
  rescan the data root, so saves/logs are per profile.
* Command lines are POSIX-quoted (`shlex` semantics) and shown verbatim in `--dry-run`.
* `Session` reads the merged stdout/stderr stream on a thread, keeps the last N lines and appends
  to `<root>/logs/cordon-session.log`; `finish_session()` joins the reader so the final output is
  never lost, then collects diagnostics and unmounts fuse overlays.

## 4. Linux-specific invariants

| Invariant | Why |
|---|---|
| Paths handed to the engine are POSIX, game-relative paths use `/` in manifests and `\` only in `fsgame.ltx` values | `locator_api` translates separators before touching the real filesystem |
| Case-sensitivity audit + alias symlinks | `xr_fs_strlwr` is a no-op on Linux, the VFS is case sensitive |
| Marker files (`.cordon-root`, `.cordon-profile`, `.cordon-mod-storage`) | the launcher only ever deletes directories it created |
| `is_mounted()` checks for an exact mount point | every path is "inside" `/`; a prefix test would report the whole disk as mounted and fuse overlays would never be mounted |
| No root anywhere, no writes into the game directory | profiles are self-contained; the game stays untouched |
| XDG directories with `--portable` fallback | distro policy, AppImage/USB usage |

## 5. Conflict model

`layers.build_plan()` produces `LayerPlan`:

* ordered `layers` (engine data → base game → mods → MO2 overwrite), priority increases with order;
* `entries: {relative path: [providers in ascending priority]}` - the last provider wins;
* `excluded` - files the user pulled out of the overlay (`profile.excluded_paths`).

`conflicts.analyze()` turns that into `ModConflictStatus` per mod:

| counters | verdict |
|---|---|
| no files | `conflict_free` |
| only unique files | `conflict_free` |
| some files override others, nothing lost | `overwrites` |
| some files are shadowed, nothing overridden | `overwritten` |
| both | `mixed` |
| everything shadowed | `redundant` (safe to disable) |

## 6. Testing

```
tests/cordon/support.py     shared helpers: VANILLA_FSGAME, make_elf, FakeInstall
tests/cordon/conftest.py    fake_install / fake_profile fixtures (mini game + engine + 2 mods)
```

* Every test runs against real temporary trees; nothing is mocked away except `subprocess` when a
  test asserts on command construction.
* `test_launch_integration.py` replaces the engine with a copy of `/usr/bin/true` (and with a shell
  script for output handling), so overlay preparation, `Popen`, log capture and exit codes are
  exercised for real.
* GUI tests set `QT_QPA_PLATFORM=offscreen` and grab the widget; `LD_LIBRARY_PATH` can be pointed
  at stub libraries on machines without libGL (see the session notes), otherwise the two GUI tests
  skip.

```
/tmp/venv/bin/python -m pytest tests/cordon -q        # 154 tests
```

## 7. Extension points

* **New backend** - add the constant in `models.BACKENDS`, handle it in
  `ProfileWorkspace.prepare()`, add the tool check to `preflight._check_backend()`; the manifest
  needs no change if the artifacts can be described by the existing fields.
* **New pre-flight check** - write `_check_x(profile, report, ...)` in `preflight.py` and call it
  from `run()`. GUI, `cordon doctor` and the pre-launch gate pick it up automatically.
* **New CLI command** - add a subparser in `cli.build_parser()`, a `cmd_x(args, service)` function
  and an entry in `cli.COMMANDS`. Keep logic in `CordonService`, not in the handler.
* **New mod archive format** - extend `mods.ARCHIVE_SUFFIXES`, and for external unpackers add an
  entry to `mods.EXTERNAL_TOOLS` plus a hint in `mounts.INSTALL_HINTS`.
* **New engine flag** - add it to `ENGINE_FLAG_LABELS` (checkbox in the profile dialog) and, if the
  engine documents it, to `preflight.KNOWN_ENGINE_KEYS`.
