"""Tests for fsgame.ltx handling, layer resolution and the symlink overlay."""

from __future__ import annotations

import os

import pytest

from cordon.core import fsgame as fsgame_mod
from cordon.core import layers, util, xray
from cordon.core.errors import ConfigError
from cordon.core.models import BACKEND_FUSE, BACKEND_LINK, ModEntry, Profile
from cordon.core.overlay import MANIFEST_NAME, ProfileWorkspace

from support import VANILLA_FSGAME


# ---------------------------------------------------------------------------- fsgame
def test_fsgame_parse_and_render_round_trip(tmp_path):
    path = tmp_path / "fsgame.ltx"
    path.write_text(VANILLA_FSGAME)
    doc = fsgame_mod.FsGameDocument.load(str(path))
    assert "$app_data_root$" in doc
    assert doc.entry("$game_data$").root == "$fs_root$"
    assert doc.entry("$game_data$").add.startswith("gamedata")
    rendered = doc.render()
    assert "$app_data_root$" in rendered and rendered.endswith("\n")


def test_fsgame_resolves_alias_chain(tmp_path):
    doc = fsgame_mod.FsGameDocument.parse(VANILLA_FSGAME)
    resolved = doc.resolve_alias("$game_config$", str(tmp_path))
    assert resolved == os.path.join(str(tmp_path), "gamedata", "configs")
    assert doc.app_data_root(str(tmp_path)) == os.path.join(str(tmp_path), "_appdata_")


def test_prepare_profile_fsgame_profile_local(tmp_path):
    doc = fsgame_mod.FsGameDocument.parse(VANILLA_FSGAME, source="game")
    prepared = fsgame_mod.prepare_profile_fsgame(doc, appdata_root=None)
    entry = prepared.entry("$app_data_root$")
    assert entry.root == "$fs_root$"
    assert entry.add.startswith("_appdata_")
    target = os.path.join(str(tmp_path), "fsgame.ltx")
    prepared.write(target)
    assert "_appdata_" in open(target, encoding="utf-8").read()


def test_prepare_profile_fsgame_shared(tmp_path):
    doc = fsgame_mod.FsGameDocument.parse(VANILLA_FSGAME)
    shared = str(tmp_path / "shared-appdata")
    prepared = fsgame_mod.prepare_profile_fsgame(doc, appdata_root=shared)
    assert prepared.entry("$app_data_root$").root == shared
    assert prepared.entry("$app_data_root$").add == ""


def test_prepare_profile_fsgame_adds_missing_aliases():
    doc = fsgame_mod.FsGameDocument.parse("$app_data_root$ = true| false| $fs_root$| _appdata_\\\n")
    prepared = fsgame_mod.prepare_profile_fsgame(doc, appdata_root=None)
    assert "$game_data$" in prepared
    assert "$game_config$" in prepared


def test_fsgame_load_missing_file(tmp_path):
    with pytest.raises(ConfigError):
        fsgame_mod.FsGameDocument.load(str(tmp_path / "nope.ltx"))


def test_fsgame_keeps_comments_and_encoding(tmp_path):
    text = "; комментарий\n$app_data_root$ = true| false| $fs_root$| _appdata_\\  ; хвост\n"
    doc = fsgame_mod.FsGameDocument.parse(text)
    prepared = fsgame_mod.prepare_profile_fsgame(doc, appdata_root=None)
    assert "; хвост" in prepared.render()
    assert prepared.render().startswith("; комментарий")


# ---------------------------------------------------------------------------- xray layout
def test_classify_mod_gamedata(fake_install):
    layout = xray.classify_mod(fake_install.mods["a"])
    assert layout.has_gamedata
    assert layout.game_data_dir.endswith("gamedata")
    assert "readme.txt" in layout.ignored


def test_classify_mod_root_entries_and_archives(fake_install):
    layout = xray.classify_mod(fake_install.mods["b"])
    assert "db" in layout.root_entries
    assert layout.appdata_entries.get("_appdata_", "").endswith("appdata")


def test_classify_loose_mod(tmp_path):
    loose = tmp_path / "loose"
    (loose / "configs").mkdir(parents=True)
    (loose / "configs" / "system.ltx").write_text("x")
    (loose / "readme.md").write_text("docs")
    layout = xray.classify_mod(str(loose))
    assert not layout.has_gamedata
    assert layout.game_data_dir == str(loose)
    assert "readme.md" in layout.ignored


def test_detect_game_id(fake_install):
    assert xray.detect_game_id(fake_install.game) == "cop"
    assert xray.detect_game_id(fake_install.game, declared="cs") == "cs"
    anomaly = os.path.join(fake_install.root, "anomaly-build")
    util.ensure_dir(os.path.join(anomaly, "gamedata", "configs"))
    util.write_text_atomic(os.path.join(anomaly, "gamedata", "configs", "axr_options.ltx"), "[x]\n")
    assert xray.detect_game_id(anomaly) == "coc"


# ---------------------------------------------------------------------------- layers
def test_layer_priority_and_winners(fake_install, fake_profile):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    kinds = [layer.kind for layer in plan.layers]
    assert kinds == ["engine", "game", "mod", "mod"]
    winner = plan.winner("gamedata/configs/system.ltx")
    assert winner is not None and winner.mod_id == "m1"
    assert not plan.entries.get("readme.txt"), "документация мода не должна попадать в оверлей"
    assert "gamedata/shaders/gl/common.h" in plan.entries
    assert "gamedata/scripts/actor.script" in plan.entries
    assert plan.winner("gamedata/scripts/actor.script").mod_id == "m2"


def test_mod_order_controls_priority(fake_install):
    profile = fake_install.profile()
    profile.mods.reverse()  # B becomes the winner for its own files only
    plan = layers.build_plan(profile, engine_data_path=fake_install.engine_data)
    plan.index()
    assert plan.winner("gamedata/scripts/actor.script").name == "Мод B"


def test_loose_archive_mapping(fake_install, fake_profile):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    assert plan.winner("db/mods/extra.db") is not None


def test_excluded_paths(fake_install, fake_profile):
    fake_profile.excluded_paths = ["gamedata/configs/system.ltx"]
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    assert "gamedata/configs/system.ltx" not in plan.entries


def test_disabled_mod_is_not_layered(fake_install, fake_profile):
    fake_profile.mods[1].enabled = False
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    assert plan.winner("gamedata/scripts/actor.script").kind == "game"


def test_conflicts_and_stats(fake_install, fake_profile):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    conflicts = {item.relative: item for item in plan.conflicts()}
    assert "gamedata/configs/system.ltx" in conflicts
    conflict = conflicts["gamedata/configs/system.ltx"]
    assert conflict.winner.name == "Мод A"
    assert [loser.kind for loser in conflict.losers] == ["game"]
    stats = plan.stats()
    assert stats.total_files == len(plan.entries)
    assert stats.overlapping_files >= 2
    assert stats.per_layer["Базовая игра"] > 0
    assert stats.total_bytes > 0


def test_reorder_mods(fake_profile):
    layers.reorder_mods(fake_profile, ["m2", "m1"])
    assert [mod.id for mod in fake_profile.mods] == ["m2", "m1"]
    layers.reorder_mods(fake_profile, ["m1"])
    assert [mod.id for mod in fake_profile.mods] == ["m1", "m2"]


def test_standalone_plan(fake_install):
    profile = Profile(id="s1", name="Сборка", kind="standalone", game_path=fake_install.game)
    plan = layers.build_plan(profile)
    plan.index()
    assert len(plan.layers) == 1
    assert plan.winner("gamedata/configs/system.ltx").kind == "game"


# ---------------------------------------------------------------------------- overlay
def test_link_overlay_build(fake_install, fake_profile):
    from cordon.core.engine import find_engine

    engine = find_engine(fake_profile)
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    result = workspace.prepare(plan, engine_executable=engine.executable if engine else "")
    assert result.rebuilt
    root = workspace.root
    assert os.path.isfile(os.path.join(root, "fsgame.ltx"))
    assert os.path.islink(os.path.join(root, "gamedata.db0"))
    assert os.path.islink(os.path.join(root, "patches"))
    system_ltx = os.path.join(root, "gamedata", "configs", "system.ltx")
    assert os.path.islink(system_ltx)
    assert os.path.realpath(system_ltx).startswith(fake_install.mods["a"])
    actor = os.path.join(root, "gamedata", "scripts", "actor.script")
    assert os.path.realpath(actor).startswith(fake_install.mods["b"])
    assert os.path.isfile(os.path.join(root, "gamedata", "shaders", "gl", "common.h"))
    assert os.path.isdir(os.path.join(root, "_appdata_", "savedgames"))
    assert os.path.isfile(os.path.join(root, "_appdata_", "user.ltx"))
    assert os.path.isfile(os.path.join(root, "_appdata_", "shaders_cache", "cache.bin"))
    assert os.path.isfile(os.path.join(root, MANIFEST_NAME))
    assert workspace.verify() == []


def test_overlay_reuses_build_when_nothing_changed(fake_install, fake_profile):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    first = workspace.prepare(plan)
    assert first.rebuilt
    second = workspace.prepare(plan)
    assert not second.rebuilt
    assert second.links == first.links


def test_overlay_rebuilds_after_mod_change(fake_install, fake_profile):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    workspace.prepare(plan)
    util.write_text_atomic(os.path.join(fake_install.mods["a"], "gamedata", "configs", "extra.ltx"), "[extra]\n")
    plan2 = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan2.index()
    again = workspace.prepare(plan2)
    assert again.rebuilt
    assert os.path.isfile(os.path.join(workspace.root, "gamedata", "configs", "extra.ltx"))


def test_overlay_removes_stale_entries_after_mod_disable(fake_install, fake_profile):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    workspace.prepare(plan)
    fake_profile.mods[0].enabled = False
    plan2 = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan2.index()
    workspace.prepare(plan2)
    system_ltx = os.path.join(workspace.root, "gamedata", "configs", "system.ltx")
    assert os.path.realpath(system_ltx).startswith(fake_install.game)


def test_overlay_skips_writable_files_as_copies(fake_install, fake_profile):
    util.ensure_dir(os.path.join(fake_install.game, "gamedata", "configs"))
    util.write_text_atomic(os.path.join(fake_install.game, "gamedata", "configs", "localization.ltx"), "en")
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    workspace.prepare(plan)
    target = os.path.join(workspace.root, "gamedata", "configs", "localization.ltx")
    assert os.path.isfile(target) and not os.path.islink(target)
    util.write_text_atomic(target, "ru")
    assert open(os.path.join(fake_install.game, "gamedata", "configs", "localization.ltx")).read() == "en"


def test_overlay_manifest_records_layers(fake_install, fake_profile):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    workspace.prepare(plan)
    manifest = workspace.load_manifest()
    assert manifest["profile_id"] == fake_profile.id
    assert [layer["kind"] for layer in manifest["layers"]] == ["engine", "game", "mod", "mod"]
    assert manifest["counts"]["links"] > 0


def test_workspace_refuses_to_delete_foreign_directory(fake_install, fake_profile, tmp_path):
    from cordon.core.errors import SafetyError

    fake_profile.root_path = str(tmp_path / "not-ours")
    util.ensure_dir(fake_profile.root_path)
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    with pytest.raises(SafetyError):
        workspace.remove_root()


def test_workspace_removes_own_directory(fake_install, fake_profile):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    workspace.prepare(plan)
    workspace.remove_root()
    assert not os.path.exists(workspace.root)


def test_fuse_backend_plan_builds_mount_command(fake_install, fake_profile):
    from cordon.core import mounts

    fake_profile.backend = BACKEND_FUSE
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    overlay = mounts.mount_for_plan(plan, fake_install.store.profile_root(fake_profile.id))
    command = overlay.command(executable="/usr/bin/fuse-overlayfs")
    assert command[0] == "/usr/bin/fuse-overlayfs"
    joined = " ".join(command)
    assert "lowerdir=" in joined and "upperdir=" in joined and "workdir=" in joined
    lowers = overlay.lower_dirs
    assert lowers[-1].endswith("gamedata")
    assert lowers[0].startswith(fake_install.mods["b"])  # highest priority comes first


def test_fuse_backend_without_lower_layers(fake_install, fake_profile):
    from cordon.core import mounts

    plan = layers.LayerPlan()
    overlay = mounts.mount_for_plan(plan, fake_install.store.profile_root(fake_profile.id))
    assert overlay.lower_dirs == []


def test_backend_direct_creates_appdata_only(fake_install, fake_profile):
    from cordon.core.models import BACKEND_DIRECT

    fake_profile.backend = BACKEND_DIRECT
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    result = workspace.prepare(plan)
    assert not result.rebuilt
    assert os.path.isdir(workspace.appdata)
    assert not os.path.exists(os.path.join(workspace.root, "gamedata"))


def test_fsgame_written_into_root(fake_install, fake_profile):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    workspace.ensure_root()
    path = workspace.write_fsgame(plan)
    content = open(path, encoding="utf-8").read()
    assert path == os.path.join(workspace.root, "fsgame.ltx")
    assert "$app_data_root$" in content
    assert "_appdata_" in content


def test_fsgame_shared_mode_points_to_game_directory(fake_install, fake_profile):
    fake_profile.appdata_mode = "shared"
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    workspace.ensure_root()
    workspace.write_fsgame(plan)
    content = open(workspace.fsgame_path, encoding="utf-8").read()
    assert os.path.join(fake_install.game, "_appdata_") in content


def test_engine_candidates_from_mods(fake_install, fake_profile, tmp_path):
    from support import make_elf

    make_elf(os.path.join(fake_install.mods["b"], "bin", "xr_3da"))
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    candidates = plan.executable_candidates()
    assert any(path.endswith("bin/xr_3da") and layer.mod_id == "m2" for path, layer in candidates)


def test_mod_backend_name_is_validated(fake_profile):
    fake_profile.backend = "bindfs"
    fake_profile.normalize()
    assert fake_profile.backend == BACKEND_LINK


def test_mod_entry_defaults():
    mod = ModEntry.from_dict({"path": "/tmp/mod"})
    assert mod.enabled and mod.id and mod.name == "Мод"
