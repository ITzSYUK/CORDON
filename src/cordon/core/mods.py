"""Mod folders and archive installation.

Covers the upstream features "scan a mod folder" and "install unpacked mods from ZIP/7Z/RAR";
on Linux the archive list additionally includes the tar family (``.tar.gz``, ``.tar.xz``,
``.tar.zst`` …), which is how most Linux-friendly mod packages are shipped.

Everything is extracted into the launcher's own storage (``~/.local/share/cordon/mods`` by
default), never into the game or into a user's mod folder, and every archive entry is checked
against path traversal.
"""

from __future__ import annotations

import os
import posixpath
import re
import shutil
import subprocess
import tarfile
import zipfile
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path

from . import util, xray
from .errors import SafetyError, ToolMissingError
from .models import ModEntry
from .mounts import INSTALL_HINTS
from .paths import MOD_STORAGE_MARKER, AppPaths, write_marker

ProgressCallback = Callable[[str], None] | None

ARCHIVE_SUFFIXES = (
    ".zip",
    ".7z",
    ".rar",
    ".tar",
    ".tar.gz",
    ".tgz",
    ".tar.bz2",
    ".tbz2",
    ".tar.xz",
    ".txz",
    ".tar.zst",
)

EXTERNAL_TOOLS = {
    ".7z": (("7z", "7za", "7zz"), lambda tool, archive, target: [tool, "x", "-y", f"-o{target}", archive]),
    ".rar": (("unrar",), lambda tool, archive, target: [tool, "x", "-o+", "-idq", archive, target + "/"]),
}

SLUG_RE = re.compile(r"[^0-9A-Za-zА-Яа-яЁё._-]+")


def slugify(name: str, *, fallback: str = "mod") -> str:
    cleaned = SLUG_RE.sub("_", name.strip()).strip("._-")
    return cleaned or fallback


def is_archive(path: str) -> bool:
    lowered = path.lower()
    return any(lowered.endswith(suffix) for suffix in ARCHIVE_SUFFIXES)


def looks_like_mod(directory: str) -> bool:
    """A mod folder: a ``gamedata`` tree, or loose game directories at the top level."""
    if not os.path.isdir(directory):
        return False
    if xray.has_gamedata(directory):
        return True
    for name in ("configs", "scripts", "textures", "meshes", "sounds", "spawns", "levels", "db"):
        if os.path.isdir(os.path.join(directory, name)):
            return True
    return False


def scan_mod_folders(root: str, *, recursive_depth: int = 2, include_archives: bool = True) -> list[str]:
    """Find mod folders *and* mod archives below *root*.

    ``root`` itself counts as a mod when it looks like one, which makes "add this folder" and
    "scan this downloads directory" behave the same way from the UI.
    """
    root = util.norm(root)
    if not os.path.isdir(root):
        return []
    found: list[str] = []
    if looks_like_mod(root):
        return [root]

    for depth_root, dirnames, filenames in util.iter_tree(root):
        depth = 0 if depth_root == root else os.path.relpath(depth_root, root).count(os.sep) + 1
        if depth >= recursive_depth:
            dirnames[:] = []
        for name in list(dirnames):
            candidate = os.path.join(depth_root, name)
            if looks_like_mod(candidate):
                found.append(candidate)
                dirnames.remove(name)
        if include_archives:
            for name in filenames:
                candidate = os.path.join(depth_root, name)
                if is_archive(candidate):
                    found.append(candidate)
    return sorted(found)


def archive_stem(path: str) -> str:
    """``Anomaly-1.5.2-Fix.zip`` → ``Anomaly-1.5.2-Fix`` (double suffixes like ``.tar.gz`` handled)."""
    name = os.path.basename(util.norm(path))
    lowered = name.lower()
    for suffix in sorted(ARCHIVE_SUFFIXES, key=len, reverse=True):
        if lowered.endswith(suffix):
            return name[: -len(suffix)]
    return os.path.splitext(name)[0]


def scan_entries(paths: list[str]) -> list[ModEntry]:
    """Turn paths into mod entries; archives are unpacked into our storage first."""
    entries: list[ModEntry] = []
    for path in paths:
        resolved = util.norm(path)
        if os.path.isdir(resolved):
            if xray.classify_mod(resolved).is_empty:
                continue
            entries.append(ModEntry(id=util.new_id(), name=os.path.basename(resolved.rstrip("/")), path=resolved))
        elif is_archive(resolved):
            entries.append(
                ModEntry(
                    id=util.new_id(),
                    name=archive_stem(resolved),
                    path=resolved,
                    source=f"archive:{resolved}",
                    enabled=False,  # needs unpacking before it can be used
                )
            )
    return entries


def unpack_pending(app: AppPaths, profile_id: str, entries: list[ModEntry], *,
                   progress: ProgressCallback = None) -> tuple[list[ModEntry], list[str]]:
    """Unpack archive entries (those that are not folders yet) into the managed storage."""
    ready: list[ModEntry] = []
    failed: list[str] = []
    for entry in entries:
        if os.path.isdir(entry.path):
            entry.enabled = True
            ready.append(entry)
            continue
        try:
            outcome = install_archive(app, profile_id, entry.path, name=entry.name, progress=progress)
        except (SafetyError, ToolMissingError, OSError) as exc:
            failed.append(f"{entry.name}: {exc}")
            continue
        ready.append(outcome.mod)
    return ready, failed


# ---------------------------------------------------------------------------- extraction
def extract_archive(archive: str, destination: str, *, progress: ProgressCallback = None) -> str:
    """Extract *archive* into *destination* and return the directory holding the mod root."""
    archive = util.norm(archive)
    if not os.path.isfile(archive):
        raise SafetyError(f"архив не найден: {archive}")
    destination = util.ensure_dir(destination)
    lowered = archive.lower()
    if progress:
        progress(f"Распаковка {os.path.basename(archive)}…")
    if lowered.endswith(".zip"):
        _extract_zip(archive, destination)
    elif any(lowered.endswith(suffix) for suffix in (".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz",
                                                     ".txz", ".tar.zst")):
        _extract_tar(archive, destination)
    else:
        suffix = next((item for item in EXTERNAL_TOOLS if lowered.endswith(item)), "")
        if not suffix:
            raise SafetyError(f"неизвестный формат архива: {os.path.basename(archive)}")
        _extract_external(suffix, archive, destination)
    return _collapse_single_root(destination)


def _check_entry(name: str) -> str:
    normalised = name.replace("\\", "/").lstrip("/")
    cleaned = posixpath.normpath(normalised)
    if cleaned in (".", ""):
        return ""
    if cleaned.startswith("..") or posixpath.isabs(cleaned):
        raise SafetyError(f"архив содержит небезопасный путь: {name}")
    return cleaned


def _extract_zip(archive: str, destination: str) -> None:
    with zipfile.ZipFile(archive) as handle:
        for info in handle.infolist():
            relative = _check_entry(info.filename)
            if not relative:
                continue
            target = os.path.join(destination, *relative.split("/"))
            if info.is_dir():
                util.ensure_dir(target)
                continue
            util.ensure_dir(os.path.dirname(target))
            with handle.open(info) as source, open(target, "wb") as sink:
                shutil.copyfileobj(source, sink)


def _extract_tar(archive: str, destination: str) -> None:
    with tarfile.open(archive) as handle:
        for member in handle.getmembers():
            relative = _check_entry(member.name)
            if not relative:
                continue
            target = os.path.join(destination, *relative.split("/"))
            if member.isdir():
                util.ensure_dir(target)
                continue
            if member.issym() or member.islnk():
                link_target = _check_entry(member.linkname)
                if not link_target:
                    continue
                source = os.path.join(destination, *link_target.split("/"))
                util.ensure_dir(os.path.dirname(target))
                if os.path.lexists(target):
                    os.unlink(target)
                os.symlink(source, target)
                continue
            if not member.isfile():
                continue
            util.ensure_dir(os.path.dirname(target))
            source = handle.extractfile(member)
            if source is None:  # pragma: no cover
                continue
            with source, open(target, "wb") as sink:
                shutil.copyfileobj(source, sink)
            mode = member.mode & 0o777
            if mode:
                os.chmod(target, mode)


def _extract_external(suffix: str, archive: str, destination: str) -> None:
    names, builder = EXTERNAL_TOOLS[suffix]
    tool = next((shutil.which(name) for name in names if shutil.which(name)), "")
    if not tool:
        hint = INSTALL_HINTS.get("7z", "") if suffix == ".7z" else INSTALL_HINTS.get("unrar", "")
        raise ToolMissingError(names[0], hint)
    command = builder(tool, archive, destination)
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise SafetyError(
            f"{names[0]} не смог распаковать {os.path.basename(archive)}: "
            f"{result.stderr.strip() or result.stdout.strip()}"
        )


def _collapse_single_root(destination: str) -> str:
    """Handle the usual "mod packed into one folder" layout."""
    entries = util.entry_names(destination)
    if len(entries) == 1:
        single = os.path.join(destination, entries[0])
        if os.path.isdir(single) and xray.is_xray_tree(single):
            for name in util.entry_names(single):
                shutil.move(os.path.join(single, name), os.path.join(destination, name))
            os.rmdir(single)
    return destination


# ---------------------------------------------------------------------------- install
@dataclass(slots=True)
class InstallResult:
    mod: ModEntry
    path: str
    files: int
    size: int


def install_archive(
    app: AppPaths,
    profile_id: str,
    archive: str,
    *,
    name: str = "",
    store_override: str = "",
    progress: ProgressCallback = None,
) -> InstallResult:
    """Install a mod archive into the launcher's managed storage."""
    base_name = name or Path(archive).stem
    for suffix in sorted(ARCHIVE_SUFFIXES, key=len, reverse=True):
        if base_name.lower().endswith(suffix):
            base_name = base_name[: -len(suffix)]
            break
    slug = slugify(base_name)
    storage_root = app.mod_storage(profile_id, store_override)
    util.ensure_dir(storage_root)
    write_marker(storage_root, MOD_STORAGE_MARKER, f"profile={profile_id}\n")
    target = os.path.join(storage_root, slug)
    counter = 1
    while os.path.exists(target):
        target = os.path.join(storage_root, f"{slug}-{counter}")
        counter += 1
    util.ensure_dir(target)
    try:
        extract_archive(archive, target, progress=progress)
    except BaseException:
        shutil.rmtree(target, ignore_errors=True)
        raise
    files, size = directory_usage(target)
    if progress:
        progress(f"Установлено файлов: {files}")
    signature = util.file_signature(archive)
    mod = ModEntry(
        id=util.new_id(),
        name=base_name or slug,
        path=target,
        source="archive",
        installed_at=signature[0] if signature else None,
        notes=f"источник: {util.norm(archive)}",
    )
    return InstallResult(mod=mod, path=target, files=files, size=size)


def detect_external_tools() -> dict[str, str]:
    """Which external unpackers are available (``7z`` for .7z, ``unrar`` for .rar)."""
    available: dict[str, str] = {}
    for suffix, (candidates, _builder) in EXTERNAL_TOOLS.items():
        for candidate in candidates:
            found = shutil.which(candidate)
            if found:
                available[suffix] = found
                available[candidate] = found
                break
    return available


def directory_usage(root: str) -> tuple[int, int]:
    files = 0
    total = 0
    for dirpath, _dirnames, filenames in util.iter_tree(root):
        for name in filenames:
            files += 1
            try:
                total += os.lstat(os.path.join(dirpath, name)).st_size
            except OSError:
                pass
    return files, total


def storage_root_for(app: AppPaths, path: str, *, store_root: str = "") -> str:
    """The managed ``<storage>/profile-<id>`` directory that contains *path* (``""`` if none)."""
    target = util.norm(path)
    candidates: list[str] = []
    if store_root:
        candidates.append(util.norm(store_root))
    candidates.append(util.norm(app.mod_storage_root))
    for root in candidates:
        if not root or not util.is_inside(target, root) or target == root:
            continue
        # the directory the mod lives in must be a marked storage directory of *this* launcher
        if os.path.isfile(os.path.join(root, MOD_STORAGE_MARKER)):
            return root
        for child in util.entry_names(root):
            candidate = os.path.join(root, child)
            if util.is_inside(target, candidate) and os.path.isfile(os.path.join(candidate, MOD_STORAGE_MARKER)):
                return candidate
    return ""


def remove_installed_mod(app: AppPaths, mod: ModEntry, *, store_root: str = "") -> bool:
    """Delete a mod that lives in our managed storage. Returns ``False`` for foreign folders."""
    path = util.norm(mod.path)
    if not os.path.isdir(path):
        return False
    root = storage_root_for(app, path, store_root=store_root)
    if not root or root == path:
        return False
    if not util.is_inside(path, root):
        return False
    shutil.rmtree(path)
    return True


@dataclass(slots=True)
class ScanResult:
    found: list[ModEntry] = field(default_factory=list)
    skipped: list[str] = field(default_factory=list)

    @property
    def summary(self) -> str:
        return f"найдено модов: {len(self.found)}, пропущено: {len(self.skipped)}"
