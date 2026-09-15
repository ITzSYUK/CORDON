"""Import of a Mod Organizer 2 setup (``modlist.txt``).

Only the *list* is imported - enabled state and order - which is what the upstream launcher
offers too.  Nothing is written into the MO2 instance.
"""

from __future__ import annotations

import configparser
import os
from dataclasses import dataclass, field

from . import util
from .models import ModEntry, Profile


@dataclass(slots=True)
class Mo2Layout:
    root: str
    mods_dir: str = ""
    profiles_dir: str = ""
    overwrite_dir: str = ""
    base_game: str = ""

    @property
    def profiles(self) -> list[str]:
        if not os.path.isdir(self.profiles_dir):
            return []
        return [name for name in util.entry_names(self.profiles_dir)
                if os.path.isdir(os.path.join(self.profiles_dir, name))]


@dataclass(slots=True)
class Mo2ImportEntry:
    name: str
    enabled: bool
    separator: bool = False
    matched_path: str = ""
    ambiguous: list[str] = field(default_factory=list)

    @property
    def missing(self) -> bool:
        return not self.separator and not self.matched_path


@dataclass(slots=True)
class Mo2Preview:
    layout: Mo2Layout
    profile_dir: str = ""
    entries: list[Mo2ImportEntry] = field(default_factory=list)

    @property
    def matched(self) -> list[Mo2ImportEntry]:
        return [entry for entry in self.entries if entry.matched_path]

    @property
    def missing(self) -> list[Mo2ImportEntry]:
        return [entry for entry in self.entries if entry.missing]

    @property
    def ambiguous(self) -> list[Mo2ImportEntry]:
        return [entry for entry in self.entries if entry.ambiguous]

    def to_text(self) -> str:
        lines = [
            f"MO2: {self.layout.root}",
            f"Моды: {self.layout.mods_dir or '—'}",
            f"Профиль: {self.profile_dir or '—'}",
            f"Записей в modlist.txt: {len(self.entries)} "
            f"(найдено {len(self.matched)}, отсутствует {len(self.missing)}, неоднозначно {len(self.ambiguous)})",
        ]
        for entry in self.entries[:60]:
            mark = "+" if entry.enabled else "-"
            state = entry.matched_path or ("разделитель" if entry.separator else "НЕ НАЙДЕН")
            lines.append(f"  {mark} {entry.name} → {state}")
        if len(self.entries) > 60:
            lines.append(f"  … ещё {len(self.entries) - 60}")
        return "\n".join(lines)


def find_layout(path: str) -> Mo2Layout:
    """Accept an MO2 root, a profile directory or the ``modlist.txt`` itself."""
    target = util.norm(path)
    if os.path.isfile(target):
        target = os.path.dirname(target)
    layout = Mo2Layout(root=target)

    candidates = [target]
    parent = os.path.dirname(target)
    if parent:
        candidates.append(parent)
    for candidate in candidates:
        mods_dir = os.path.join(candidate, "mods")
        profiles_dir = os.path.join(candidate, "profiles")
        overwrite = os.path.join(candidate, "overwrite")
        if os.path.isdir(mods_dir) or os.path.isdir(profiles_dir):
            layout.root = candidate
            layout.mods_dir = mods_dir if os.path.isdir(mods_dir) else ""
            layout.profiles_dir = profiles_dir if os.path.isdir(profiles_dir) else ""
            layout.overwrite_dir = overwrite if os.path.isdir(overwrite) else ""
            break
    else:
        # the caller pointed straight at a profile directory
        if os.path.basename(target) and os.path.isfile(os.path.join(target, "modlist.txt")):
            layout.profiles_dir = os.path.dirname(target)
            parent = os.path.dirname(layout.profiles_dir)
            layout.root = parent
            layout.mods_dir = os.path.join(parent, "mods")

    layout.base_game = _read_base_game(layout.root)
    return layout


def _read_base_game(root: str) -> str:
    ini_path = os.path.join(root, "ModOrganizer.ini")
    if not os.path.isfile(ini_path):
        return ""
    parser = configparser.ConfigParser(strict=False)
    parser.optionxform = str  # type: ignore[assignment]
    try:
        parser.read(ini_path, encoding="utf-8")
    except (OSError, configparser.Error):  # pragma: no cover
        return ""
    for section in ("General", "Settings", "game"):
        for option in ("gamePath", "game_path", "GamePath"):
            if parser.has_option(section, option):
                value = parser.get(section, option).strip().strip('"')
                if value:
                    return os.path.expanduser(value)
    return ""


def parse_modlist(path: str) -> list[Mo2ImportEntry]:
    entries: list[Mo2ImportEntry] = []
    target = util.norm(path)
    if os.path.isdir(target):
        target = os.path.join(target, "modlist.txt")
    if not os.path.isfile(target):
        return entries
    for line in util.read_text(target, errors="replace").splitlines():
        if not line.strip():
            continue
        marker, name = line[0], line[1:].strip()
        if marker == "#":
            continue
        if marker == "*":
            entries.append(Mo2ImportEntry(name=name, enabled=False, separator=True))
            continue
        if marker not in "+-":
            continue
        entries.append(Mo2ImportEntry(name=name, enabled=marker == "+"))
    return entries


def build_preview(path: str, *, profile_name: str = "") -> Mo2Preview:
    layout = find_layout(path)
    profile_dir = ""
    if profile_name:
        candidate = os.path.join(layout.profiles_dir, profile_name)
        if os.path.isdir(candidate):
            profile_dir = candidate
    if not profile_dir:
        if os.path.isfile(os.path.join(util.norm(path), "modlist.txt")):
            profile_dir = util.norm(path)
        elif os.path.isfile(path) and os.path.basename(path) == "modlist.txt":
            profile_dir = os.path.dirname(util.norm(path))
        elif layout.profiles:
            profile_dir = os.path.join(layout.profiles_dir, sorted(layout.profiles)[0])

    preview = Mo2Preview(layout=layout, profile_dir=profile_dir)
    entries = parse_modlist(profile_dir or layout.profiles_dir)
    by_lower: dict[str, list[str]] = {}
    if layout.mods_dir and os.path.isdir(layout.mods_dir):
        for name in util.entry_names(layout.mods_dir):
            by_lower.setdefault(name.lower(), []).append(os.path.join(layout.mods_dir, name))
    for entry in entries:
        if entry.separator:
            continue
        matches = by_lower.get(entry.name.lower(), [])
        if len(matches) == 1:
            entry.matched_path = matches[0]
        elif len(matches) > 1:
            entry.ambiguous = matches
    preview.entries = entries
    return preview


def apply_preview(profile: Profile, preview: Mo2Preview, *, use_overwrite: bool = True) -> int:
    """Transfer enabled state and order into a profile (lower in list = higher priority)."""
    imported: list[ModEntry] = []
    group = ""
    for entry in preview.entries:
        if entry.separator:
            group = entry.name
            continue
        if not entry.matched_path:
            continue
        imported.append(
            ModEntry(
                id=util.new_id(),
                name=entry.name,
                path=entry.matched_path,
                enabled=entry.enabled,
                group=group,
                source="mo2",
            )
        )
    profile.mods = imported
    if use_overwrite and preview.layout.overwrite_dir:
        profile.mo2_overwrite_path = preview.layout.overwrite_dir
    return len(imported)


def apply_modlist_only(profile: Profile, preview: Mo2Preview) -> int:
    """Apply only the enabled state and order to mods already present in a profile."""
    by_lower: dict[str, ModEntry] = {}
    for mod in profile.mods:
        for key in (mod.name.lower(), os.path.basename(mod.path.rstrip("/")).lower()):
            by_lower.setdefault(key, mod)
    ordered: list[ModEntry] = []
    for entry in preview.entries:
        if entry.separator:
            continue
        mod = by_lower.get(entry.name.lower())
        if mod is None:
            continue
        mod.enabled = entry.enabled
        ordered.append(mod)
    ordered_ids = {mod.id for mod in ordered}
    ordered.extend(mod for mod in profile.mods if mod.id not in ordered_ids)
    profile.mods = ordered
    return len(ordered)
