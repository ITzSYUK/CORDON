"""Helpers shared by the test modules (imported from pytest's rootdir sys.path entry)."""

from __future__ import annotations

import os
import struct
from dataclasses import dataclass

from cordon.core import util
from cordon.core.models import LauncherSettings, ModEntry, Profile
from cordon.core.paths import AppPaths

VANILLA_FSGAME = """; vanilla style fsgame.ltx
$app_data_root$         = true|  false| $fs_root$|            _appdata_\\
$arch_dir$              = false| false| $fs_root$
$arch_dir_patches$      = false| true|  $fs_root$|            patches\\
$game_data$             = false| true|  $fs_root$|            gamedata\\
$game_config$           = true|  false| $game_data$|          configs\\
$game_scripts$          = true|  false| $game_data$|          scripts\\
$logs$                  = true|  false| $app_data_root$|      logs\\
$screenshots$           = true|  false| $app_data_root$|      screenshots\\
$game_saves$            = true|  false| $app_data_root$|      savedgames\\
"""


def make_elf(path: str, *, bits: int = 64, machine: int = 0x3E, elf_type: int = 3) -> str:
    """A syntactically valid ELF header (no program headers) for architecture checks."""
    ident = bytearray(16)
    ident[0:4] = b"\x7fELF"
    ident[4] = 2 if bits == 64 else 1
    ident[5] = 1  # little endian
    ident[6] = 1  # EV_CURRENT
    header = bytes(ident) + struct.pack("<HHI", elf_type, machine, 1)
    header += struct.pack("<QQQ", 0, 0, 0)  # entry, phoff, shoff
    header += struct.pack("<IHHHHHH", 0, 64 if bits == 64 else 52, 0, 0, 0, 0, 0)
    util.ensure_dir(os.path.dirname(path))
    with open(path, "wb") as handle:
        handle.write(header)
    os.chmod(path, 0o755)
    return path


@dataclass
class FakeInstall:
    root: str
    game: str
    engine_data: str
    mods: dict[str, str]
    store: AppPaths
    settings: LauncherSettings

    def profile(self, **overrides) -> Profile:
        profile = Profile(
            id="p001",
            name="Тестовый профиль",
            game_path=self.game,
            engine_data_path=self.engine_data,
            engine_path=self.game,
            mods=[
                ModEntry(id="m1", name="Мод A", path=self.mods["a"]),
                ModEntry(id="m2", name="Мод B", path=self.mods["b"]),
            ],
        )
        for key, value in overrides.items():
            setattr(profile, key, value)
        profile.normalize()
        return profile
