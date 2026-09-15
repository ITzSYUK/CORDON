"""Shared fixtures: a miniature S.T.A.L.K.E.R. / OpenXRay installation."""

from __future__ import annotations

import os

import pytest

from cordon.core import util
from cordon.core.models import LauncherSettings
from cordon.core.paths import AppPaths
from support import VANILLA_FSGAME, FakeInstall, make_elf  # noqa: F401  (re-exported for tests)

__all__ = ["make_elf", "VANILLA_FSGAME"]


@pytest.fixture()
def fake_install(tmp_path) -> FakeInstall:
    root = str(tmp_path / "install")
    game = os.path.join(root, "cop")
    util.ensure_dir(os.path.join(game, "gamedata", "configs"))
    util.ensure_dir(os.path.join(game, "gamedata", "scripts"))
    util.ensure_dir(os.path.join(game, "gamedata", "textures"))
    util.ensure_dir(os.path.join(game, "patches"))
    util.ensure_dir(os.path.join(game, "bin"))
    util.write_text_atomic(os.path.join(game, "gamedata", "configs", "system.ltx"), "[system]\nversion = 1.0\n")
    util.write_text_atomic(os.path.join(game, "gamedata", "scripts", "actor.script"), "-- base actor\n")
    util.write_text_atomic(os.path.join(game, "gamedata", "textures", "wall.dds"), "base texture")
    util.write_text_atomic(os.path.join(game, "gamedata.db0"), "archive placeholder")
    util.write_text_atomic(os.path.join(game, "fsgame.ltx"), VANILLA_FSGAME)
    util.write_text_atomic(os.path.join(game, "patches", "gamedata.dba"), "patch archive")
    make_elf(os.path.join(game, "bin", "xr_3da"))

    engine_data = os.path.join(root, "usr-share-openxray")
    util.ensure_dir(os.path.join(engine_data, "gamedata", "shaders", "gl"))
    util.write_text_atomic(os.path.join(engine_data, "gamedata", "shaders", "gl", "common.h"), "// engine shader")
    util.write_text_atomic(os.path.join(engine_data, "fsgame.ltx"), VANILLA_FSGAME)

    mod_a = os.path.join(root, "mods", "mod_a")
    util.ensure_dir(os.path.join(mod_a, "gamedata", "configs"))
    util.write_text_atomic(os.path.join(mod_a, "gamedata", "configs", "system.ltx"), "[system]\nversion = 2.0\n")
    util.write_text_atomic(os.path.join(mod_a, "readme.txt"), "docs only")

    mod_b = os.path.join(root, "mods", "mod_b")
    util.ensure_dir(os.path.join(mod_b, "gamedata", "scripts"))
    util.ensure_dir(os.path.join(mod_b, "db", "mods"))
    util.ensure_dir(os.path.join(mod_b, "appdata", "shaders_cache"))
    util.write_text_atomic(os.path.join(mod_b, "gamedata", "scripts", "actor.script"), "-- modded actor\n")
    util.write_text_atomic(os.path.join(mod_b, "db", "mods", "extra.db"), "mod archive")
    util.write_text_atomic(os.path.join(mod_b, "appdata", "user.ltx"), "rs_stats on\n")
    util.write_text_atomic(os.path.join(mod_b, "appdata", "shaders_cache", "cache.bin"), "prepared cache")

    store = AppPaths(
        config_dir=str(tmp_path / "config"),
        data_dir=str(tmp_path / "data"),
        cache_dir=str(tmp_path / "cache"),
    )
    store.ensure_layout()

    return FakeInstall(
        root=root,
        game=game,
        engine_data=engine_data,
        mods={"a": mod_a, "b": mod_b},
        store=store,
        settings=LauncherSettings(),
    )


@pytest.fixture()
def fake_profile(fake_install) -> "object":
    return fake_install.profile()
