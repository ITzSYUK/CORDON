"""Locating the OpenXRay engine and its data directories.

Two installation shapes are supported, both found in the wild:

* **portable** - ``xr_3da`` and ``libxr*.so`` next to the game data (``~/GOG/CoP``, AUR
  "portable" builds, CI artifacts);
* **system** - the distribution package installs ``/usr/games/xr_3da`` plus libraries in
  ``/usr/lib/<triplet>/`` and engine data in ``/usr/share/openxray`` (see the ``debian/``
  directory of xray-16).
"""

from __future__ import annotations

import os
import shutil
import subprocess
from dataclasses import dataclass, field

from . import elf, util
from .errors import EngineNotFoundError
from .models import Profile
from .xray import FSGAME_NAME, engine_switch_for

EXECUTABLE_NAMES = (
    "xr_3da",
    "xr_3da_x64",
    "xrengine",
    "xr_engine",
    "openxray",
    "OpenXRay",
    "anomaly",
    "Anomaly",
)

EXECUTABLE_PREFIXES = ("xr_", "xray", "openxray", "anomaly")

BINARY_SUBDIRS = ("", "bin", "bin_x64", "engine")

SYSTEM_DATA_DIRS = (
    "/usr/share/openxray",
    "/usr/local/share/openxray",
    "/usr/share/games/openxray",
    "/app/share/openxray",  # Flatpak-ish
)

SYSTEM_BINARY_DIRS = ("/usr/games", "/usr/bin", "/usr/local/bin", "/usr/lib/openxray")

ENGINE_LIBRARY_NAMES = ("xrCore.so", "xrEngine.so", "xrGame.so", "xrAPI.so")


@dataclass(slots=True)
class EngineInfo:
    executable: str
    binary: elf.BinaryInfo
    engine_root: str
    game_root: str
    data_root: str = ""
    fsgame_source: str = ""
    source: str = "profile"
    libraries: list[str] = field(default_factory=list)
    missing_libraries: list[str] = field(default_factory=list)
    game_id: str = "cop"

    @property
    def linux_native(self) -> bool:
        return self.binary.kind == "elf"

    @property
    def game_switch(self) -> str:
        return engine_switch_for(self.game_id)

    def describe(self) -> str:
        parts = [f"движок: {self.executable} ({self.binary.summary()})"]
        if self.data_root:
            parts.append(f"данные движка: {self.data_root}")
        if self.missing_libraries:
            parts.append("не найдены библиотеки: " + ", ".join(self.missing_libraries))
        return "; ".join(parts)


def candidate_executables(*roots: str) -> list[str]:
    found: list[str] = []
    for root in roots:
        if not root or not os.path.isdir(root):
            continue
        for sub in BINARY_SUBDIRS:
            directory = os.path.join(root, sub) if sub else root
            if not os.path.isdir(directory):
                continue
            for name in EXECUTABLE_NAMES:
                candidate = os.path.join(directory, name)
                if os.path.isfile(candidate) and os.access(candidate, os.X_OK):
                    found.append(util.norm(candidate))
            for name in util.entry_names(directory):
                candidate = os.path.join(directory, name)
                lowered = name.lower()
                if (
                    os.path.isfile(candidate)
                    and os.access(candidate, os.X_OK)
                    and lowered.startswith(EXECUTABLE_PREFIXES)
                    and elf.inspect(candidate).kind == "elf"
                ):
                    found.append(util.norm(candidate))
    unique = util.unique(found)
    unique.sort(key=lambda path: (not os.path.basename(path).startswith("xr_3da"), path))
    return unique


def detect_system_data_root() -> str:
    for directory in SYSTEM_DATA_DIRS:
        if os.path.isfile(os.path.join(directory, FSGAME_NAME)):
            return directory
    for directory in SYSTEM_DATA_DIRS:
        if os.path.isdir(directory):
            return directory
    return ""


def detect_system_binary() -> str:
    for directory in SYSTEM_BINARY_DIRS:
        for name in EXECUTABLE_NAMES:
            candidate = os.path.join(directory, name)
            if os.path.isfile(candidate) and os.access(candidate, os.X_OK):
                return candidate
    return ""


def find_libraries(engine_root: str) -> list[str]:
    libraries: list[str] = []
    for sub in ("", "bin", "bin_x64", "lib", "lib64"):
        directory = os.path.join(engine_root, sub) if sub else engine_root
        if not os.path.isdir(directory):
            continue
        for name in util.entry_names(directory):
            if name.endswith(".so") or ".so." in name:
                libraries.append(os.path.join(directory, name))
    return libraries


def ldd_missing(executable: str) -> list[str]:
    """Return the sonames reported as missing by ``ldd`` (best effort)."""
    if not shutil.which("ldd"):
        return []
    try:
        result = subprocess.run(
            ["ldd", executable], capture_output=True, text=True, timeout=20, check=False
        )
    except (OSError, subprocess.SubprocessError):  # pragma: no cover - ldd oddities
        return []
    missing: list[str] = []
    for line in result.stdout.splitlines():
        if "not found" in line:
            missing.append(line.split("=>")[0].strip() or line.strip())
    return missing


def find_engine(profile: Profile, *, extra_roots: tuple[str, ...] = ()) -> EngineInfo | None:
    """Locate the engine for *profile*, honouring a manually pinned executable first."""
    game_root = util.norm(profile.game_path) if profile.game_path else ""
    engine_root = util.norm(profile.engine_path) if profile.engine_path else game_root

    executable = ""
    source = "auto"
    if profile.executable_source:
        pinned = util.norm(profile.executable_source)
        if os.path.isfile(pinned) and os.access(pinned, os.X_OK):
            executable = pinned
            source = "вручную"
    if not executable and profile.executable_relative:
        for base in (engine_root, game_root):
            candidate = os.path.join(base, profile.executable_relative)
            if os.path.isfile(candidate) and os.access(candidate, os.X_OK):
                executable = util.norm(candidate)
                source = "профиль"
                break
    if not executable:
        candidates = candidate_executables(engine_root, game_root, *extra_roots)
        if not candidates:
            detected = detect_system_binary()
            candidates = [detected] if detected else []
            if candidates:
                source = "системная установка"
        if candidates:
            executable = candidates[0]
    if not executable:
        return None

    binary = elf.inspect(executable)
    data_root = util.norm(profile.engine_data_path) if profile.engine_data_path else ""
    if not data_root:
        if os.path.isfile(os.path.join(game_root, FSGAME_NAME)) or os.path.isdir(os.path.join(game_root, "gamedata")):
            data_root = game_root
        else:
            data_root = detect_system_data_root()

    fsgame_source = util.norm(profile.fsgame_source) if profile.fsgame_source else ""
    if not fsgame_source:
        for base in (data_root, game_root, engine_root):
            candidate = os.path.join(base, FSGAME_NAME) if base else ""
            if candidate and os.path.isfile(candidate):
                fsgame_source = util.norm(candidate)
                break

    libraries = find_libraries(engine_root or os.path.dirname(executable))
    missing = [name for name in ENGINE_LIBRARY_NAMES if not any(os.path.basename(lib) == name for lib in libraries)]
    if missing and binary.kind == "elf":
        # A distribution package keeps the modules in a multiarch directory; a missing entry
        # there is normal, only ``ldd`` complaints are authoritative.
        ldd_missing_libs = ldd_missing(executable)
        missing = [name for name in missing if name in " ".join(ldd_missing_libs)] or []
        if not missing and not libraries:
            missing = []

    return EngineInfo(
        executable=executable,
        binary=binary,
        engine_root=util.norm(os.path.dirname(executable)),
        game_root=game_root,
        data_root=data_root,
        fsgame_source=fsgame_source,
        source=source,
        libraries=libraries,
        missing_libraries=missing,
        game_id=profile.game_id,
    )


def require_engine(profile: Profile) -> EngineInfo:
    info = find_engine(profile)
    if info is None:
        raise EngineNotFoundError(
            "не найден исполняемый файл движка X-Ray/OpenXRay. Укажите каталог игры (или каталог "
            "установки OpenXRay) в настройках профиля, либо выберите исполняемый файл вручную."
        )
    return info
