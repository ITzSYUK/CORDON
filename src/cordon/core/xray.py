"""Knowledge about the X-Ray / OpenXRay data layout.

Verified against the engine sources (OpenXRay ``dev`` branch):

* ``CLocatorAPI::setup_fs_path`` - with ``-fsltx <file>`` the engine resolves the *directory*
  of that file through ``realpath()`` and uses it as ``$fs_root$``.  Everything else in
  ``fsgame.ltx`` is relative to it.
* ``xr_fs_strlwr()`` is ``do_nothing()`` on Linux (``src/Common/PlatformLinux.inl``), so the
  engine's virtual file system is case sensitive here, unlike on Windows.
* ``restore_path_separators()``/``convert_path_separators()`` exist because the engine keeps
  ``\\`` internally and converts only when touching the real file system - hence a stock
  ``fsgame.ltx`` with backslashes stays valid on Linux.
"""

from __future__ import annotations

import fnmatch
import os
import re
from dataclasses import dataclass, field

from . import util

FSGAME_NAME = "fsgame.ltx"
GAME_DATA_DIR = "gamedata"
APP_DATA_ALIAS = "$app_data_root$"

#: Root level entries of a typical X-Ray installation.
KNOWN_ROOT_DIRS = (
    "bin",
    "bin_x64",
    "gamedata",
    "db",
    "patches",
    "localization",
    "mp",
    "resources",
    "levels",
)

#: Directories that hold engine-side profile data and must never be layered read-only.
APPDATA_DIR_CANDIDATES = (
    "_appdata_",
    "appdata",
    "userdata",
)

#: Archives the engine loads through ``$arch_dir$`` / ``$arch_dir_patches$``.
ARCHIVE_PATTERNS = ("gamedata.db*", "*.xdb", "*.xdb0", "*.db0", "*.db1", "*.db2", "*.db3", "*.db4", "*.db5")

#: Files that only document a mod and never belong to the game tree.
DOC_PATTERNS = (
    "readme*",
    "license*",
    "licence*",
    "changelog*",
    "changes*",
    "install*",
    "*.md",
    "*.txt",
    "*.pdf",
    "*.doc",
    "*.docx",
    "*.jpg",
    "*.jpeg",
    "*.png",
    "*.gif",
    "*.bmp",
    "*.url",
)

DOC_DIRS = ("screenshots", "screens", "preview", "docs")

#: Directories whose content belongs to the engine's user data, not to the game tree.
APPDATA_ENTRY_NAMES = ("logs", "savedgames", "screenshots", "shaders_cache", "user.ltx", "overwrite", "dumps")

ANOMALY_MARKERS = (
    "gamedata/configs/axr_options.ltx",
    "gamedata/configs/plugins/axr_main.ltx",
    "gamedata/scripts/axr_main.script",
)

CLEAR_SKY_MARKERS = (
    "gamedata/configs/mp/mp_ranks.ltx",
    "gamedata/configs/creatures/actor.ltx",
)


@dataclass(slots=True)
class ModLayout:
    """How a mod folder maps onto the game tree."""

    root: str
    has_gamedata: bool = False
    game_data_dir: str = ""          # directory whose content maps to ``gamedata/``
    root_entries: dict[str, str] = field(default_factory=dict)   # relative path → absolute source
    appdata_entries: dict[str, str] = field(default_factory=dict)
    fsgame_candidates: list[str] = field(default_factory=list)
    archive_entries: dict[str, str] = field(default_factory=dict)
    ignored: list[str] = field(default_factory=list)

    @property
    def is_empty(self) -> bool:
        return not (self.game_data_dir or self.root_entries or self.archive_entries)


def is_doc_entry(name: str) -> bool:
    lowered = name.lower()
    if lowered in DOC_DIRS or lowered.startswith((".", )):
        return True
    return any(fnmatch.fnmatch(lowered, pattern) for pattern in DOC_PATTERNS)


def is_archive_name(name: str) -> bool:
    lowered = name.lower()
    if lowered.endswith(".xdb") or re.fullmatch(r".*\.db[0-9a-e]?", lowered):
        return True
    return any(fnmatch.fnmatch(lowered, pattern) for pattern in ARCHIVE_PATTERNS)


def has_gamedata(path: str) -> bool:
    return os.path.isdir(os.path.join(path, GAME_DATA_DIR))


def find_archives(root: str) -> list[str]:
    """Archives visible in a game directory (root level and the ``db`` sub directory)."""
    found: list[str] = []
    for directory in (root, os.path.join(root, "db"), os.path.join(root, "db", "mods")):
        if not os.path.isdir(directory):
            continue
        for name in util.entry_names(directory):
            entry = os.path.join(directory, name)
            if os.path.isfile(entry) and is_archive_name(name):
                found.append(entry)
    return found


def is_xray_tree(path: str) -> bool:
    if not os.path.isdir(path):
        return False
    if has_gamedata(path) or os.path.isfile(os.path.join(path, FSGAME_NAME)):
        return True
    return bool(find_archives(path))


def detect_game_id(game_path: str, *, declared: str = "auto") -> str:
    """Best effort detection of the game the profile targets.

    Only CoP, CS and CoC/Anomaly trees can be detected with reasonable confidence; anything
    else stays ``custom`` and the user picks the engine switch manually.
    """
    if declared and declared != "auto":
        return declared
    if not os.path.isdir(game_path):
        return "custom"
    gamedata = os.path.join(game_path, GAME_DATA_DIR)
    for marker in ANOMALY_MARKERS:
        if os.path.exists(os.path.join(game_path, marker)):
            return "coc"
    lowered = game_path.lower()
    if "clear sky" in lowered or "clear_sky" in lowered or "csky" in lowered:
        return "cs"
    if "anomaly" in lowered or "chernobyl" in lowered or "coc" in os.path.basename(lowered):
        return "coc"
    if os.path.isdir(gamedata) and not find_archives(game_path):
        return "coc"  # a loose-gamedata installation is a modpack rather than a retail game
    if os.path.isdir(os.path.join(game_path, "patches")) or find_archives(game_path):
        return "cop"
    return "custom"


def find_appdata_roots(build_root: str) -> list[str]:
    """Common per-build user data locations of a standalone/modpack tree."""
    candidates: list[str] = []
    for name in APPDATA_DIR_CANDIDATES:
        candidate = os.path.join(build_root, name)
        if os.path.isdir(candidate):
            candidates.append(candidate)
    for parent in ("bin", "bin_x64"):
        for name in APPDATA_DIR_CANDIDATES:
            candidate = os.path.join(build_root, parent, name)
            if os.path.isdir(candidate):
                candidates.append(candidate)
    return candidates


def classify_mod(root: str) -> ModLayout:
    """Decide which part of a mod folder is game data, root data or documentation."""
    layout = ModLayout(root=util.norm(root))
    if not os.path.isdir(layout.root):
        return layout

    for name in util.entry_names(layout.root):
        entry = os.path.join(layout.root, name)
        lowered = name.lower()
        if os.path.isdir(entry):
            if lowered == GAME_DATA_DIR:
                layout.has_gamedata = True
                layout.game_data_dir = entry
            elif lowered in APPDATA_DIR_CANDIDATES:
                layout.appdata_entries["_appdata_"] = entry
            elif lowered in KNOWN_ROOT_DIRS and lowered not in (GAME_DATA_DIR,):
                layout.root_entries[name] = entry
            elif is_doc_entry(name):
                layout.ignored.append(name)
            else:
                # Loose mod packaging: ``configs/``, ``scripts/``, ``textures/`` … live
                # directly in the archive root and belong to ``gamedata/``.
                layout.game_data_dir = layout.game_data_dir or layout.root
                layout.root_entries.setdefault(f"__gamedata__/{name}", entry)
        else:
            if lowered == FSGAME_NAME:
                layout.fsgame_candidates.append(entry)
            elif is_archive_name(lowered):
                layout.archive_entries[name] = entry
            elif is_doc_entry(name):
                layout.ignored.append(name)
            else:
                layout.root_entries.setdefault(f"__gamedata__/{name}", entry)

    if layout.game_data_dir and layout.root_entries:
        loose = {key: value for key, value in layout.root_entries.items()
                 if key.startswith("__gamedata__/") and layout.game_data_dir != layout.root}
        for key, value in loose.items():
            layout.root_entries.pop(key)
            layout.ignored.append(os.path.basename(value))
    return layout


def describe_game_id(game_id: str) -> str:
    return {
        "cop": "Call of Pripyat (OpenXRay по умолчанию)",
        "cs": "Clear Sky (движок -cs)",
        "coc": "Call of Chernobyl / Anomaly (gamedata без архивов)",
        "custom": "Другая сборка (без автоопределения)",
        "auto": "Определить автоматически",
    }.get(game_id, game_id)


def engine_switch_for(game_id: str) -> str:
    """OpenXRay switch that selects the retail game profile of the engine, if any."""
    return {"cs": "-cs"}.get(game_id, "")
