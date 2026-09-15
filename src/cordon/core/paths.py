"""Launcher paths (XDG layout) and the marker files that guard generated directories.

Upstream CORDON puts everything under ``%APPDATA%\\StalkerModLauncher`` and protects its
workspaces with ``.stalker-launcher-workspace`` markers.  On Linux we follow the XDG base
directory specification and keep the same marker idea, so a stray ``rm -rf`` can never be
performed by the launcher on a directory it does not own.
"""

from __future__ import annotations

import os
import time
from dataclasses import dataclass, field

from . import util
from .errors import SafetyError

CONFIG_ENV = "CORDON_CONFIG_DIR"
DATA_ENV = "CORDON_DATA_DIR"
CACHE_ENV = "CORDON_CACHE_DIR"

PROFILE_ROOT_MARKER = ".cordon-profile-root"
PROFILE_MARKER = ".cordon-profile"
MOD_STORAGE_MARKER = ".cordon-mod-storage"
LAUNCHER_ROOT_MARKER = ".cordon-root"


def _xdg(env: str, default: str) -> str:
    value = os.environ.get(env, "").strip()
    if value:
        return util.norm(value)
    return util.norm(os.path.join(os.path.expanduser("~"), default))


@dataclass(slots=True)
class AppPaths:
    """Resolved directory layout for one launcher installation."""

    config_dir: str
    data_dir: str
    cache_dir: str
    portable: bool = False
    extra: dict[str, str] = field(default_factory=dict)

    # ---------------------------------------------------------------- constructors
    @classmethod
    def discover(cls, *, config_dir: str | None = None, data_dir: str | None = None,
                 cache_dir: str | None = None) -> AppPaths:
        config = util.norm(config_dir) if config_dir else _xdg(CONFIG_ENV, ".config/cordon")
        data = util.norm(data_dir) if data_dir else _xdg(DATA_ENV, ".local/share/cordon")
        cache = util.norm(cache_dir) if cache_dir else _xdg(CACHE_ENV, ".cache/cordon")
        return cls(config_dir=config, data_dir=data, cache_dir=cache)

    @classmethod
    def in_directory(cls, root: str) -> AppPaths:
        """Portable layout: everything inside one directory (``cordon --portable``).

        The name avoids clashing with the ``portable`` dataclass field.
        """
        base = util.ensure_dir(root)
        layout = cls(
            config_dir=util.ensure_dir(os.path.join(base, "config")),
            data_dir=util.ensure_dir(os.path.join(base, "data")),
            cache_dir=util.ensure_dir(os.path.join(base, "cache")),
            portable=True,
        )
        util.write_text_atomic(os.path.join(base, LAUNCHER_ROOT_MARKER), f"portable\n{int(time.time())}\n")
        return layout

    # ---------------------------------------------------------------- properties
    @property
    def settings_file(self) -> str:
        return os.path.join(self.config_dir, "settings.json")

    @property
    def settings_backup_file(self) -> str:
        return os.path.join(self.config_dir, "settings.backup.json")

    @property
    def recovery_dir(self) -> str:
        return os.path.join(self.config_dir, "recovery")

    @property
    def launcher_log(self) -> str:
        return os.path.join(self.cache_dir, "launcher.log")

    @property
    def launcher_log_old(self) -> str:
        return os.path.join(self.cache_dir, "launcher.old.log")

    @property
    def profiles_root(self) -> str:
        """Default root for generated per-profile game roots (the Linux "Workspace")."""
        return os.path.join(self.data_dir, "profiles")

    @property
    def mod_storage_root(self) -> str:
        """Default root for mod sources installed from archives."""
        return os.path.join(self.data_dir, "mods")

    @property
    def reports_dir(self) -> str:
        return os.path.join(self.data_dir, "reports")

    @property
    def runtime_dir(self) -> str:
        runtime = os.environ.get("XDG_RUNTIME_DIR", "").strip()
        base = util.norm(runtime) if runtime and os.path.isdir(runtime) else self.cache_dir
        return os.path.join(base, "cordon")

    # ---------------------------------------------------------------- helpers
    def ensure_layout(self) -> None:
        for directory in (
            self.config_dir,
            self.data_dir,
            self.cache_dir,
            self.profiles_root,
            self.mod_storage_root,
        ):
            util.ensure_dir(directory)
        util.write_text_atomic(os.path.join(self.profiles_root, PROFILE_ROOT_MARKER), "cordon profiles root\n")
        util.write_text_atomic(os.path.join(self.mod_storage_root, MOD_STORAGE_MARKER), "cordon mod storage\n")

    def profile_root(self, profile_id: str, override: str = "") -> str:
        if override:
            return util.norm(override)
        return os.path.join(self.profiles_root, f"profile-{profile_id}")

    def mod_storage(self, profile_id: str, override: str = "") -> str:
        if override:
            return util.norm(override)
        return os.path.join(self.mod_storage_root, f"profile-{profile_id}")

    def is_owned(self, path: str, *, marker: str = "") -> bool:
        """True when *path* is inside our data/cache/config tree (and carries *marker*)."""
        target = util.norm(path)
        roots = (self.profiles_root, self.mod_storage_root, self.cache_dir, self.data_dir, self.config_dir)
        inside = any(util.is_inside(target, root) for root in roots)
        if not inside:
            return False
        if marker:
            return os.path.exists(os.path.join(target, marker))
        return True


def write_marker(directory: str, marker: str, payload: str = "") -> None:
    util.ensure_dir(directory)
    util.write_text_atomic(os.path.join(directory, marker), payload or f"cordon {int(time.time())}\n")


def read_marker(directory: str, marker: str) -> str | None:
    path = os.path.join(directory, marker)
    if not os.path.isfile(path):
        return None
    try:
        return util.read_text(path).strip()
    except OSError:  # pragma: no cover
        return None


def guard_owned_directory(app: AppPaths, path: str, *, marker: str) -> str:
    """Refuse to delete anything that is not a marked directory owned by the launcher."""
    target = util.norm(path)
    if not app.is_owned(target, marker=marker):
        raise SafetyError(
            "операция отменена: каталог не помечен как созданный CORDON-LINUX "
            f"({target}). Удалите его вручную, если он больше не нужен."
        )
    return target
