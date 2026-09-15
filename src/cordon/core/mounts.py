"""Optional FUSE overlay backend (``fuse-overlayfs``).

The default backend materialises a symlink tree.  Users who prefer a *real* merged view (for
example because they want to browse it in a file manager, or because an engine build is
unhappy with symlinked assets) can switch the profile to ``fuse-overlayfs``: the merged
``gamedata`` directory becomes a mount point whose lower layers are the enabled mods, so the
engine sees ordinary files and writes land in the profile's ``upper`` directory.

overlayfs semantics used here: the **first** entry of ``lowerdir`` has the highest priority,
which is why the list is built from the highest-priority layer downwards.
"""

from __future__ import annotations

import os
import shutil
import subprocess
from dataclasses import dataclass, field

from . import util, xray
from .errors import OverlayError, ToolMissingError
from .layers import LayerPlan

MOUNT_TOOLS = ("fuse-overlayfs",)
UNMOUNT_TOOLS = ("fusermount3", "fusermount")

#: Suggested package names per distribution, shown when the tool is missing.
INSTALL_HINTS = {
    "fuse-overlayfs": "Установите пакет fuse-overlayfs (Debian/Ubuntu: apt install fuse-overlayfs; "
                      "Arch: pacman -S fuse-overlayfs; Fedora: dnf install fuse-overlayfs).",
    "fusermount3": "Установите пакет fuse3 (Debian/Ubuntu: apt install fuse3).",
    "7z": "Установите p7zip-full (Debian/Ubuntu) или p7zip (Arch), либо unrar для RAR-архивов.",
    "bsdtar": "Установите libarchive-tools (Debian/Ubuntu) или libarchive (Arch).",
    "unrar": "Установите unrar (несвободный пакет) для распаковки RAR.",
}


@dataclass(slots=True)
class ToolStatus:
    available: dict[str, str] = field(default_factory=dict)
    missing: list[str] = field(default_factory=list)

    def find(self, *names: str) -> str:
        for name in names:
            if name in self.available:
                return self.available[name]
        return ""

    def summary(self) -> str:
        if not self.available:
            return "внешние утилиты не найдены"
        return ", ".join(sorted(self.available))


def detect_tools() -> ToolStatus:
    status = ToolStatus()
    for tool in ("fuse-overlayfs", "fusermount3", "fusermount", "7z", "7za", "7zz", "bsdtar", "tar", "unrar", "unzip",
                 "xdg-open", "steam", "ldd"):
        path = shutil.which(tool)
        if path:
            status.available[tool] = path
        else:
            status.missing.append(tool)
    return status


def require_tool(name: str) -> str:
    path = shutil.which(name)
    if not path:
        raise ToolMissingError(name, INSTALL_HINTS.get(name, ""))
    return path


def mounts() -> dict[str, str]:
    """Parse ``/proc/mounts`` into ``{mount point: source}``."""
    table: dict[str, str] = {}
    try:
        with open("/proc/mounts", encoding="utf-8", errors="replace") as handle:
            for line in handle:
                parts = line.split()
                if len(parts) >= 2:
                    table[parts[1].replace("\\040", " ")] = parts[0]
    except OSError:  # pragma: no cover - non Linux or restricted /proc
        pass
    return table


def is_mounted(path: str) -> bool:
    target = os.path.realpath(util.norm(path))
    table = mounts()
    if target in table:
        return True
    return any(util.is_inside(target, mount_point) for mount_point in table)


def layer_gamedata_dirs(plan: LayerPlan) -> list[tuple[int, str]]:
    """``(priority, gamedata directory)`` for every layer that contributes game data."""
    found: list[tuple[int, str]] = []
    for layer in plan.layers:
        if layer.kind == "mod":
            layout = xray.classify_mod(layer.path)
            candidate = layout.game_data_dir
        else:
            candidate = os.path.join(layer.path, xray.GAME_DATA_DIR)
        if candidate and os.path.isdir(candidate):
            found.append((layer.priority, util.norm(candidate)))
    found.sort(key=lambda item: item[0], reverse=True)  # highest priority first
    return found


@dataclass(slots=True)
class OverlayMount:
    """Lifecycle of the ``fuse-overlayfs`` mount handling one profile."""

    mount_point: str
    upper_dir: str
    work_dir: str
    lower_dirs: list[str]

    @property
    def helper_dir(self) -> str:
        return os.path.dirname(self.upper_dir)

    def command(self, *, executable: str | None = None) -> list[str]:
        tool = executable or shutil.which("fuse-overlayfs") or "fuse-overlayfs"
        options = ",".join(
            [
                "lowerdir=" + ":".join(self.lower_dirs),
                f"upperdir={self.upper_dir}",
                f"workdir={self.work_dir}",
            ]
        )
        return [tool, "-f", "-o", options, self.mount_point]

    def describe(self) -> str:
        lines = ["fuse-overlayfs:", f"  точка: {self.mount_point}"]
        for index, directory in enumerate(self.lower_dirs):
            lines.append(f"  нижний слой #{index + 1} (высший приоритет выше): {directory}")
        lines.append(f"  верхний слой: {self.upper_dir}")
        return "\n".join(lines)

    def prepare_dirs(self) -> None:
        util.ensure_dir(self.mount_point)
        util.ensure_dir(self.upper_dir)
        util.ensure_dir(self.work_dir)

    def mount(self, *, logger=None) -> None:
        require_tool("fuse-overlayfs")
        if not self.lower_dirs:
            raise OverlayError("нечего накладывать: ни один слой не содержит каталог gamedata")
        if is_mounted(self.mount_point):
            if logger:
                logger.info("точка %s уже смонтирована", self.mount_point)
            return
        self.prepare_dirs()
        command = self.command()
        if logger:
            logger.info("монтирование оверлея: %s", " ".join(command))
        result = subprocess.run(command, capture_output=True, text=True, check=False)
        if result.returncode != 0:
            raise OverlayError(
                "fuse-overlayfs не смонтировал оверлей "
                f"({result.returncode}): {result.stderr.strip() or result.stdout.strip()}"
            )

    def unmount(self, *, logger=None) -> None:
        if not is_mounted(self.mount_point):
            return
        candidates = [shutil.which(name) for name in UNMOUNT_TOOLS]
        tool = next((path for path in candidates if path), "")
        last_error = ""
        for attempt in ([tool, "-uz", self.mount_point] if tool else []) + [["umount", "-l", self.mount_point]]:
            if not attempt[0]:
                continue
            result = subprocess.run(attempt, capture_output=True, text=True, check=False)
            if result.returncode == 0 and not is_mounted(self.mount_point):
                if logger:
                    logger.info("оверлей отмонтирован: %s", self.mount_point)
                return
            last_error = result.stderr.strip() or result.stdout.strip()
        raise OverlayError(f"не удалось отмонтировать {self.mount_point}: {last_error}")


def mount_for_plan(plan: LayerPlan, root: str) -> OverlayMount:
    root = util.norm(root)
    helper = os.path.join(root, ".cordon-overlay")
    lower = [directory for _priority, directory in layer_gamedata_dirs(plan)]
    return OverlayMount(
        mount_point=os.path.join(root, xray.GAME_DATA_DIR),
        upper_dir=os.path.join(helper, "upper"),
        work_dir=os.path.join(helper, "work"),
        lower_dirs=lower,
    )
