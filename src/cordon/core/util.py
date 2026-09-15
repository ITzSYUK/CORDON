"""Small helpers shared by the core modules (no third-party dependencies)."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import stat
import tempfile
from collections.abc import Iterable, Iterator
from pathlib import Path

from .errors import SafetyError

# --------------------------------------------------------------------------------------
# paths
# --------------------------------------------------------------------------------------


def norm(path: str | os.PathLike[str]) -> str:
    """Absolute, lexically normalised path as a plain string (symlinks untouched)."""
    if isinstance(path, os.PathLike):
        path = os.fspath(path)
    expanded = os.path.expanduser(str(path))
    return os.path.abspath(expanded)


def real(path: str | os.PathLike[str]) -> str:
    """Resolve symlinks; falls back to :func:`norm` for paths that do not exist yet."""
    try:
        return os.path.realpath(os.path.expanduser(str(path)))
    except OSError:  # pragma: no cover - realpath rarely fails
        return norm(path)


def to_posix(relative: str) -> str:
    """Normalise a game-relative path to the POSIX form used in manifests.

    X-Ray stores everything with backslashes (``gamedata\\configs\\system.ltx``) even on
    Linux, while :mod:`os` needs slashes.  Manifests always keep the slash form.
    """
    cleaned = relative.replace("\\", "/").strip()
    while cleaned.startswith("./"):
        cleaned = cleaned[2:]
    parts = [part for part in cleaned.split("/") if part not in ("", ".")]
    if any(part == ".." for part in parts):
        raise SafetyError(f"путь «{relative}» выходит за пределы игрового каталога")
    return "/".join(parts)


def to_engine(relative: str) -> str:
    """Convert a POSIX relative path into the backslash form X-Ray uses internally."""
    return to_posix(relative).replace("/", "\\")


def is_inside(path: str | os.PathLike[str], root: str | os.PathLike[str]) -> bool:
    """True when *path* is *root* itself or lives below it (no filesystem access)."""
    a = os.path.normpath(norm(path))
    b = os.path.normpath(norm(root))
    return a == b or a.startswith(b.rstrip(os.sep) + os.sep)


def paths_overlap(left: str | os.PathLike[str], right: str | os.PathLike[str]) -> bool:
    return is_inside(left, right) or is_inside(right, left)


def same_file(left: str, right: str) -> bool:
    """Compare two paths by inode when possible, falling back to text comparison."""
    try:
        a, b = os.stat(left), os.stat(right)
        return (a.st_dev, a.st_ino) == (b.st_dev, b.st_ino)
    except OSError:
        return os.path.normpath(norm(left)) == os.path.normpath(norm(right))


def iter_tree(root: str) -> Iterator[tuple[str, list[str], list[str]]]:
    """``os.walk`` that does not follow symlinked directories (keeps overlays finite)."""
    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        dirnames[:] = [d for d in dirnames if not os.path.islink(os.path.join(dirpath, d))]
        yield dirpath, dirnames, filenames


def entry_names(directory: str) -> list[str]:
    try:
        return sorted(os.listdir(directory))
    except OSError:
        return []


def resolve_case_insensitive(root: str, relative: str) -> str | None:
    """Find the real on-disk spelling of *relative* below *root*.

    OpenXRay on Linux uses ``xr_fs_strlwr()`` as a no-op, so the engine is fully
    case-sensitive here while mods are usually authored on Windows.  This helper answers
    "does this reference still resolve, and with which spelling".
    """
    current = root
    for part in to_posix(relative).split("/"):
        if not part:
            continue
        candidate = os.path.join(current, part)
        if os.path.exists(candidate):
            current = candidate
            continue
        match = None
        for name in entry_names(current):
            if name.lower() == part.lower():
                match = name
                break
        if match is None:
            return None
        current = os.path.join(current, match)
    return current


def case_matches(root: str, relative: str) -> bool:
    """True when *relative* exists below *root* with exactly the requested spelling."""
    return os.path.exists(os.path.join(root, *to_posix(relative).split("/")))


# --------------------------------------------------------------------------------------
# files
# --------------------------------------------------------------------------------------


def ensure_dir(path: str | os.PathLike[str], mode: int = 0o755) -> str:
    target = norm(path)
    os.makedirs(target, mode=mode, exist_ok=True)
    return target


def write_json_atomic(path: str | os.PathLike[str], payload: object, *, indent: int | None = 2) -> None:
    text = json.dumps(payload, ensure_ascii=False, indent=indent) + "\n"
    write_text_atomic(path, text)


def write_text_atomic(path: str | os.PathLike[str], text: str, *, encoding: str = "utf-8") -> None:
    """Write *text* through a temporary file and ``os.replace`` (never a half-written file)."""
    target = Path(norm(path))
    ensure_dir(target.parent)
    fd, tmp_name = tempfile.mkstemp(dir=str(target.parent), prefix=f".{target.name}.", suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding=encoding, newline="") as handle:
            handle.write(text)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(tmp_name, target)
    except BaseException:
        with suppress_os_error():
            os.unlink(tmp_name)
        raise


class suppress_os_error:  # noqa: N801 - context-manager helper, mirrors contextlib.suppress
    def __enter__(self) -> None:  # pragma: no cover - trivial
        return None

    def __exit__(self, exc_type, exc, tb) -> bool:
        return exc_type is not None and issubclass(exc_type, OSError)


def read_text(path: str | os.PathLike[str], *, encoding: str = "utf-8", errors: str = "replace") -> str:
    with open(norm(path), encoding=encoding, errors=errors, newline="") as handle:
        return handle.read()


def detect_encoding(path: str | os.PathLike[str]) -> str:
    """Return the encoding of an ``.ltx`` file (Windows-1251 is very common for S.T.A.L.K.E.R.)."""
    raw = Path(norm(path)).read_bytes()[:4096]
    for bom, encoding in ((b"\xef\xbb\xbf", "utf-8-sig"), (b"\xff\xfe", "utf-16-le"), (b"\xfe\xff", "utf-16-be")):
        if raw.startswith(bom):
            return encoding
    try:
        raw.decode("utf-8")
    except UnicodeDecodeError:
        return "cp1251"
    return "utf-8"


def file_signature(path: str) -> tuple[int, int] | None:
    """Cheap change detector: ``(mtime_ns, size)``."""
    try:
        info = os.stat(path)
    except OSError:
        return None
    return (info.st_mtime_ns, info.st_size)


def dir_signature(root: str) -> str:
    """Stable hash of a directory listing (names, sizes, mtimes) without following links."""
    digest = hashlib.sha256()
    if not os.path.isdir(root):
        return digest.hexdigest()
    for dirpath, dirnames, filenames in iter_tree(root):
        rel_dir = os.path.relpath(dirpath, root)
        digest.update(rel_dir.encode("utf-8", "surrogateescape"))
        for name in sorted(dirnames) + sorted(filenames):
            entry = os.path.join(dirpath, name)
            try:
                info = os.lstat(entry)
                kind = "d" if stat.S_ISDIR(info.st_mode) else "l" if os.path.islink(entry) else "f"
                payload = f"{name}|{kind}|{info.st_size}|{info.st_mtime_ns}"
            except OSError:
                payload = f"{name}|?"
            digest.update(payload.encode("utf-8", "surrogateescape"))
    return digest.hexdigest()


def filesystem_free_bytes(path: str) -> int:
    try:
        usage = shutil.disk_usage(norm(path if os.path.exists(path) else nearest_existing(path)))
    except OSError:  # pragma: no cover - exotic filesystems
        return -1
    return usage.free


def nearest_existing(path: str) -> str:
    current = norm(path)
    while not os.path.exists(current):
        parent = os.path.dirname(current)
        if parent == current:
            return "/"
        current = parent
    return current


def human_size(size: int | float) -> str:
    if size < 0:
        return "—"
    value = float(size)
    for unit in ("Б", "КиБ", "МиБ", "ГиБ", "ТиБ"):
        if value < 1024 or unit == "ТиБ":
            return f"{value:.0f} {unit}" if unit == "Б" else f"{value:.1f} {unit}"
        value /= 1024
    return f"{value:.1f} ТиБ"  # pragma: no cover


def human_duration(seconds: float) -> str:
    total = int(max(0.0, seconds))
    hours, remainder = divmod(total, 3600)
    minutes, secs = divmod(remainder, 60)
    if hours:
        return f"{hours} ч {minutes:02d} мин"
    if minutes:
        return f"{minutes} мин {secs:02d} с"
    return f"{secs} с"


def link_count(path: str) -> int:
    try:
        return os.lstat(path).st_nlink
    except OSError:
        return 0


def unique(items: Iterable[str]) -> list[str]:
    seen: set[str] = set()
    result: list[str] = []
    for item in items:
        if item not in seen:
            seen.add(item)
            result.append(item)
    return result


def tail_lines(path: str, count: int = 40) -> list[str]:
    if not os.path.isfile(path):
        return []
    try:
        with open(path, encoding="utf-8", errors="replace") as handle:
            lines = handle.readlines()
    except OSError:
        return []
    return [line.rstrip("\n") for line in lines[-count:]]


def short_hash(text: str, length: int = 16) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:length]


def new_id() -> str:
    """Short, filesystem-safe identifier used for profile and mod ids."""
    return os.urandom(6).hex()
