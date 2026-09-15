"""Case sensitivity audit - the Linux specific extra of this port.

``xr_fs_strlwr()`` is ``do_nothing()`` on Linux (``src/Common/PlatformLinux.inl``), so while the
Windows engine resolves ``Gamedata\\Configs\\System.ltx`` case insensitively, OpenXRay on Linux
does not.  Mods authored on Windows routinely reference paths with "wrong" case, which shows up
as missing textures or scripts.

The audit reads ``.ltx``/``.script``/``.xml`` files of the layer plan, extracts every path-like
reference, and compares its spelling with the real file names in the overlay.  ``fix()`` creates
*alias symlinks* (never touch a mod folder), and the aliases are remembered in the profile so
they survive every rebuild of the overlay.
"""

from __future__ import annotations

import os
import posixpath
import re
from dataclasses import dataclass, field

from . import util
from .layers import LayerPlan

ALIASES_NAME = ".cordon-aliases.json"

SCAN_EXTENSIONS = (".ltx", ".xml", ".script", ".lua", ".ini", ".cfg")
MAX_FILE_SIZE = 1024 * 1024
MAX_FILES = 6000
MAX_ISSUES = 500

ALIAS_PREFIX_RE = re.compile(r"^\$(?P<name>[A-Za-z0-9_]+)\$(?P<sep>[\\/|])?(?P<rest>.*)$")

REFERENCE_RE = re.compile(
    r"(?P<path>(?:\$[A-Za-z0-9_]+\$[\\/|])?(?:[A-Za-z0-9_.\-]+[\\/])+[A-Za-z0-9_.\-]+\.[A-Za-z0-9]{2,6})"
)

#: ``$alias$`` prefixes are resolved against this map before comparing with the overlay root.
ALIAS_ROOTS = {
    "$game_data$": "gamedata",
    "$game_config$": "gamedata/configs",
    "$game_scripts$": "gamedata/scripts",
    "$game_textures$": "gamedata/textures",
    "$game_sounds$": "gamedata/sounds",
    "$game_meshes$": "gamedata/meshes",
    "$game_anims$": "gamedata/anims",
    "$game_levels$": "gamedata/levels",
    "$game_spawn$": "gamedata/spawns",
    "$game_shaders$": "gamedata/shaders",
    "$fs_root$": "",
}


@dataclass(slots=True)
class CaseIssue:
    referenced: str
    actual: str
    source_file: str

    @property
    def alias_target(self) -> tuple[str, str]:
        """``(path to create, relative symlink target)`` for the alias fix.

        Only a pure case difference can be fixed by a sibling symlink; when the reference points
        somewhere else entirely the audit still reports it, but nothing is created.
        """
        referenced_parts = self.referenced.split("/")
        actual_parts = self.actual.split("/")
        if len(referenced_parts) != len(actual_parts):
            return "", ""
        if any(left.lower() != right.lower() for left, right in zip(referenced_parts, actual_parts)):
            return "", ""
        for index, (reference_part, actual_part) in enumerate(zip(referenced_parts, actual_parts)):
            if reference_part != actual_part:
                return "/".join(referenced_parts[: index + 1]), actual_part
        return "", ""


@dataclass(slots=True)
class CaseAuditResult:
    issues: list[CaseIssue] = field(default_factory=list)
    missing: int = 0
    scanned: int = 0
    truncated: bool = False
    root: str = ""
    created: list[str] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return not self.issues

    def to_text(self, *, limit: int = 60) -> str:
        lines = [f"Проверено файлов: {self.scanned}", f"Несовпадений регистра: {len(self.issues)}",
                 f"Ссылок на отсутствующие файлы: {self.missing}"]
        if self.issues:
            lines.append("")
            lines.append("Несовпадения:")
            for issue in self.issues[:limit]:
                lines.append(f"  {issue.referenced}  →  {issue.actual}")
            if len(self.issues) > limit:
                lines.append(f"  … ещё {len(self.issues) - limit}")
        if self.truncated:
            lines.append("")
            lines.append("Часть файлов не проверена (превышен лимит обхода).")
        return "\n".join(lines)


def audit_plan(plan: LayerPlan, *, root: str = "") -> CaseAuditResult:
    """Audit the *effective* file set of a plan (nothing needs to be materialised yet).

    ``root`` is the overlay root the aliases will be created in - in practice the profile
    directory, so that ``gamedata/...`` paths line up with the plan's relative paths.
    """
    references: dict[str, str] = {}
    scanned = 0
    for relative in sorted(plan.entries):
        if not relative.lower().endswith(SCAN_EXTENSIONS):
            continue
        if scanned >= MAX_FILES:
            result = CaseAuditResult(scanned=scanned, root=util.norm(root))
            result.truncated = True
            _compare_plan(plan, references, result)
            return result
        path = plan.winner_path(relative)
        if not path:
            continue
        try:
            if os.path.getsize(path) > MAX_FILE_SIZE:
                continue
        except OSError:
            continue
        references.update(_references_in_file(path))
        scanned += 1
    result = CaseAuditResult(scanned=scanned, root=util.norm(root))
    _compare_plan(plan, references, result)
    return result


def _compare_plan(plan: LayerPlan, references: dict[str, str], result: CaseAuditResult) -> None:
    """Find references that only resolve after lowercasing inside the effective tree."""
    known = plan.entries
    by_lower: dict[str, str] = {}
    for key in known:
        by_lower.setdefault(key.lower(), key)
    tops = {key.split("/", 1)[0] for key in known}
    for referenced, source_file in sorted(references.items()):
        if len(result.issues) >= MAX_ISSUES:
            result.truncated = True
            return
        if referenced in known:
            continue
        actual = by_lower.get(referenced.lower())
        if actual is None:
            if referenced.split("/", 1)[0] in tops:
                result.missing += 1
            continue
        result.issues.append(CaseIssue(referenced=referenced, actual=actual, source_file=source_file))
    result.issues.sort(key=lambda issue: issue.referenced)


def audit_overlay(root: str, *, limit_files: int = MAX_FILES) -> CaseAuditResult:
    """Audit an already materialised overlay directory (used by the CLI on a profile root)."""
    references: dict[str, str] = {}
    scanned = 0
    for dirpath, _dirnames, filenames in util.iter_tree(root):
        for name in filenames:
            if not name.lower().endswith(SCAN_EXTENSIONS):
                continue
            if scanned >= limit_files:
                break
            path = os.path.join(dirpath, name)
            try:
                if os.path.getsize(path) > MAX_FILE_SIZE:
                    continue
            except OSError:
                continue
            references.update(_references_in_file(path))
            scanned += 1
    result = CaseAuditResult(scanned=scanned, root=util.norm(root))
    _compare(result.root, references, result)
    return result


def _references_in_file(path: str) -> dict[str, str]:
    try:
        encoding = util.detect_encoding(path)
        text = util.read_text(path, encoding=encoding, errors="replace")
    except OSError:  # pragma: no cover
        return {}
    found: dict[str, str] = {}
    for match in REFERENCE_RE.finditer(text):
        raw = match.group("path")
        normalised = _normalise_reference(raw)
        if normalised:
            found.setdefault(normalised, path)
    return found


def _normalise_reference(raw: str) -> str:
    value = raw.replace("\\", "/").strip()
    if value.startswith("/") or ":" in value.split("/", 1)[0]:
        return ""
    match = ALIAS_PREFIX_RE.match(value)
    if match:
        # $game_data$\gamedata\x.ltx and the fsgame style $game_data$|x.ltx are both common
        alias = f"${match.group('name').lower()}$"
        mapped = ALIAS_ROOTS.get(alias)
        if mapped is None:
            return ""
        rest = match.group("rest").lstrip("/|")
        value = f"{mapped}/{rest}" if mapped else rest
    elif "$" in value:
        return ""
    parts = [part for part in value.split("/") if part not in ("", ".")]
    if len(parts) < 2 or len(value) > 220:
        return ""
    if any(part == ".." for part in parts):
        return ""
    return "/".join(parts)


def _compare(root: str, references: dict[str, str], result: CaseAuditResult) -> None:
    for referenced, source_file in references.items():
        if len(result.issues) >= MAX_ISSUES:
            result.truncated = True
            return
        top = referenced.split("/", 1)[0]
        if not os.path.isdir(os.path.join(root, top)):
            continue  # outside the paths we manage (other fsgame roots, engine-relative names)
        direct = os.path.join(root, *referenced.split("/"))
        if os.path.exists(direct):
            continue
        resolved = util.resolve_case_insensitive(root, referenced)
        if resolved is None:
            result.missing += 1
            continue
        actual = os.path.relpath(resolved, root).replace(os.sep, "/")
        if actual == referenced:
            continue
        result.issues.append(CaseIssue(referenced=referenced, actual=actual, source_file=source_file))
    result.issues.sort(key=lambda issue: issue.referenced)


# ---------------------------------------------------------------------------- fixing
def aliases_path(root: str) -> str:
    return os.path.join(util.norm(root), ALIASES_NAME)


def read_aliases(root: str) -> list[list[str]]:
    import json

    try:
        with open(aliases_path(root), encoding="utf-8") as handle:
            payload = json.load(handle)
    except (OSError, ValueError):
        return []
    entries = payload.get("aliases") if isinstance(payload, dict) else payload
    if not isinstance(entries, list):
        return []
    return [[str(item[0]), str(item[1])] for item in entries if isinstance(item, list | tuple) and len(item) == 2]


def write_aliases(root: str, aliases: list[list[str]]) -> None:
    payload = {"schema": 1, "aliases": sorted({(alias, target) for alias, target in aliases})}
    util.write_json_atomic(aliases_path(root), payload)


def apply_aliases(root: str, *, logger=None) -> int:
    """(Re)create every remembered alias inside an overlay root."""
    created = 0
    for alias, target in read_aliases(root):
        link = os.path.join(root, *alias.split("/"))
        if os.path.lexists(link):
            continue
        if not os.path.lexists(os.path.join(os.path.dirname(link), target)):
            continue
        util.ensure_dir(os.path.dirname(link))
        try:
            os.symlink(target, link)
            created += 1
        except OSError as exc:  # pragma: no cover
            if logger:
                logger.warning("не удалось создать алиас %s: %s", link, exc)
    return created


def fix_issues(root: str, issues: list[CaseIssue], *, logger=None) -> list[str]:
    """Create the alias symlinks that make the wrong-case references resolve."""
    root = util.norm(root)
    created: list[str] = []
    aliases = {(alias, target) for alias, target in read_aliases(root)}
    for issue in issues:
        alias, target = issue.alias_target
        if not alias or not target:
            continue  # reported, but not repairable with a symlink
        link = os.path.join(root, *alias.split("/"))
        parent = os.path.dirname(link)
        if not os.path.isdir(parent):
            continue
        if not os.path.lexists(os.path.join(parent, target)):
            continue
        if os.path.lexists(link):
            aliases.add((alias, target))
            continue
        try:
            os.symlink(target, link)
        except OSError as exc:  # pragma: no cover
            if logger:
                logger.warning("не удалось создать алиас %s: %s", link, exc)
            continue
        aliases.add((alias, target))
        created.append(alias)
    write_aliases(root, [[alias, target] for alias, target in aliases])
    return created


def alias_report(root: str) -> list[str]:
    return [f"{alias} → {target}" for alias, target in read_aliases(root)]


def case_insensitive_duplicates(root: str, *, limit: int = 50) -> list[tuple[str, str]]:
    """Two files in one directory whose names differ only by case (a classic Linux trap)."""
    duplicates: list[tuple[str, str]] = []
    for dirpath, _dirnames, filenames in util.iter_tree(root):
        seen: dict[str, str] = {}
        for name in filenames:
            lowered = name.lower()
            if lowered in seen and seen[lowered] != name:
                duplicates.append((posixpath.join(dirpath, seen[lowered]), posixpath.join(dirpath, name)))
                if len(duplicates) >= limit:
                    return duplicates
            seen[lowered] = name
    return duplicates
