"""Tests for the low level helpers: paths, XDG layout, ELF inspection and settings."""

from __future__ import annotations

import json
import os

import pytest

from cordon.core import elf, util
from cordon.core.errors import ConfigError, SafetyError
from cordon.core.models import LauncherSettings, ModEntry, Profile
from cordon.core.paths import AppPaths, PROFILE_MARKER, guard_owned_directory, read_marker, write_marker
from cordon.core.settings import SettingsStore


def test_norm_and_inside(tmp_path):
    nested = tmp_path / "a" / "b"
    nested.mkdir(parents=True)
    assert util.is_inside(str(nested), str(tmp_path))
    assert not util.is_inside(str(tmp_path), str(nested))
    assert util.is_inside(str(tmp_path), str(tmp_path))


def test_to_posix_rejects_escape():
    assert util.to_posix("gamedata\\configs\\system.ltx") == "gamedata/configs/system.ltx"
    assert util.to_posix("./gamedata//configs/") == "gamedata/configs"
    with pytest.raises(SafetyError):
        util.to_posix("../../etc/passwd")


def test_engine_paths_use_backslashes():
    assert util.to_engine("gamedata/configs/system.ltx") == "gamedata\\configs\\system.ltx"


def test_write_text_atomic_leaves_no_temp_files(tmp_path):
    target = tmp_path / "sub" / "file.txt"
    util.write_text_atomic(str(target), "привет")
    assert target.read_text() == "привет"
    assert [p.name for p in target.parent.iterdir()] == ["file.txt"]


def test_detect_encoding_prefers_cp1251(tmp_path):
    path = tmp_path / "russian.ltx"
    path.write_bytes("Сталкер".encode("cp1251"))
    assert util.detect_encoding(str(path)) == "cp1251"


def test_detect_encoding_utf8(tmp_path):
    path = tmp_path / "utf.ltx"
    path.write_text("Сталкер", encoding="utf-8")
    assert util.detect_encoding(str(path)) == "utf-8"


def test_resolve_case_insensitive(tmp_path):
    (tmp_path / "Gamedata" / "Configs").mkdir(parents=True)
    (tmp_path / "Gamedata" / "Configs" / "System.ltx").write_text("x")
    assert util.resolve_case_insensitive(str(tmp_path), "gamedata/configs/system.LTX")
    assert not util.case_matches(str(tmp_path), "gamedata/configs/system.LTX")
    assert util.case_matches(str(tmp_path), "Gamedata/Configs/System.ltx")


def test_human_size_and_duration():
    assert util.human_size(0) == "0 Б"
    assert util.human_size(2048).startswith("2.0")
    assert util.human_duration(45) == "45 с"
    assert util.human_duration(125) == "2 мин 05 с"
    assert util.human_duration(7200) == "2 ч 00 мин"


def test_elf_inspection(fake_install, tmp_path):
    from support import make_elf  # noqa: PLC0415

    binary = make_elf(str(tmp_path / "xr_3da"), bits=64, machine=0x3E)
    info = elf.inspect(binary)
    assert info.kind == "elf" and info.bits == 64 and info.machine == "x86_64"
    assert elf.arch_matches_host(info)

    win = tmp_path / "xr_3da.exe"
    win.write_bytes(b"MZ" + b"\0" * 0x40 + b"\x0e\0")
    assert elf.inspect(str(win)).kind == "pe"
    assert not elf.arch_matches_host(elf.inspect(str(win)))


def test_elf_missing_file(tmp_path):
    info = elf.inspect(str(tmp_path / "nope"))
    assert info.kind == "unknown"
    assert "не читается" in info.description


def test_app_paths_layout(tmp_path):
    app = AppPaths.discover(
        config_dir=str(tmp_path / "cfg"), data_dir=str(tmp_path / "data"), cache_dir=str(tmp_path / "cache")
    )
    app.ensure_layout()
    assert os.path.isfile(os.path.join(app.profiles_root, ".cordon-profile-root"))
    assert os.path.isfile(os.path.join(app.mod_storage_root, ".cordon-mod-storage"))
    assert app.profile_root("abc").endswith("profiles/profile-abc")
    assert app.is_owned(app.profiles_root)
    assert not app.is_owned(str(tmp_path / "elsewhere"))


def test_guard_owned_directory(tmp_path):
    app = AppPaths(
        config_dir=str(tmp_path / "cfg"), data_dir=str(tmp_path / "data"), cache_dir=str(tmp_path / "cache")
    )
    app.ensure_layout()
    target = app.profile_root("x1")
    util.ensure_dir(target)
    with pytest.raises(SafetyError):
        guard_owned_directory(app, target, marker=PROFILE_MARKER)
    write_marker(target, PROFILE_MARKER, "profile=x1")
    assert read_marker(target, PROFILE_MARKER).startswith("profile=x1")
    guard_owned_directory(app, target, marker=PROFILE_MARKER)
    with pytest.raises(SafetyError):
        guard_owned_directory(app, str(tmp_path / "tmp"), marker=PROFILE_MARKER)


def test_settings_round_trip(tmp_path):
    app = AppPaths(
        config_dir=str(tmp_path / "cfg"), data_dir=str(tmp_path / "data"), cache_dir=str(tmp_path / "cache")
    )
    app.ensure_layout()
    store = SettingsStore(app)
    settings = LauncherSettings()
    profile = Profile(id="p1", name="Профиль", game_path="/games/cop", mods=[ModEntry(id="m1", name="M", path="/mods/m")])
    settings.profiles.append(profile)
    settings.selected_profile_id = "p1"
    store.save(settings)

    loaded = SettingsStore(app).load()
    assert loaded.settings.profiles[0].name == "Профиль"
    assert loaded.settings.profiles[0].mods[0].path == "/mods/m"
    assert loaded.settings.selected_profile_id == "p1"
    assert loaded.notices == []


def test_settings_backup_is_kept(tmp_path):
    app = AppPaths(
        config_dir=str(tmp_path / "cfg"), data_dir=str(tmp_path / "data"), cache_dir=str(tmp_path / "cache")
    )
    app.ensure_layout()
    store = SettingsStore(app)
    store.save(LauncherSettings())
    first = json.loads(open(app.settings_file, encoding="utf-8").read())
    store.save(LauncherSettings(profiles=[Profile(id="p2", name="Второй")]))
    backup = json.loads(open(app.settings_backup_file, encoding="utf-8").read())
    assert backup["version"] == first["version"]
    primary = json.loads(open(app.settings_file, encoding="utf-8").read())
    assert primary["profiles"][0]["name"] == "Второй"


def test_corrupt_settings_are_preserved_and_recovered(tmp_path):
    app = AppPaths(
        config_dir=str(tmp_path / "cfg"), data_dir=str(tmp_path / "data"), cache_dir=str(tmp_path / "cache")
    )
    app.ensure_layout()
    store = SettingsStore(app)
    store.save(LauncherSettings(profiles=[Profile(id="p1", name="Хороший")]))
    store.save(LauncherSettings(profiles=[Profile(id="p1", name="Новый")]))

    with open(app.settings_file, "w", encoding="utf-8") as handle:
        handle.write("{ это не json")
    result = SettingsStore(app).load()
    assert result.recovered_from_backup
    assert result.settings.profiles[0].name == "Хороший"
    preserved = os.listdir(app.recovery_dir)
    assert preserved and preserved[0].startswith("settings.json.")
    assert any("поврежд" in notice for notice in result.notices)


def test_both_files_corrupt_creates_new_settings(tmp_path):
    app = AppPaths(
        config_dir=str(tmp_path / "cfg"), data_dir=str(tmp_path / "data"), cache_dir=str(tmp_path / "cache")
    )
    app.ensure_layout()
    util.write_text_atomic(app.settings_file, "boom")
    util.write_text_atomic(app.settings_backup_file, "boom")
    result = SettingsStore(app).load()
    assert result.created_new and result.settings.profiles == []
    assert len(os.listdir(app.recovery_dir)) == 2


def test_settings_normalisation_repairs_ids():
    settings = LauncherSettings.from_dict(
        {
            "version": 1,
            "profiles": [
                {"id": "", "name": "A", "mods": [{"id": "", "path": "/m/1"}, {"id": "", "path": "/m/2"}]},
                {"id": "dup", "name": "B", "mods": []},
                {"id": "dup", "name": "C", "mods": []},
            ],
            "selected_profile_id": "нет такого",
        }
    )
    ids = [profile.id for profile in settings.profiles]
    assert len(set(ids)) == 3
    assert all(ids)
    mod_ids = [mod.id for mod in settings.profiles[0].mods]
    assert len(set(mod_ids)) == 2
    assert settings.selected_profile_id == ids[0]


def test_profile_export_import(tmp_path):
    app = AppPaths(
        config_dir=str(tmp_path / "cfg"), data_dir=str(tmp_path / "data"), cache_dir=str(tmp_path / "cache")
    )
    app.ensure_layout()
    store = SettingsStore(app)
    profile = Profile(id="p9", name="Экспорт", game_path="/games/cop")
    target = str(tmp_path / "profile.json")
    store.export_profile(profile.to_dict(), target)
    settings = LauncherSettings()
    imported = store.import_profile(target, settings)
    assert imported.id != "p9"
    assert imported.name == "Экспорт"
    assert settings.profiles == [imported]


def test_import_rejects_garbage(tmp_path):
    app = AppPaths(
        config_dir=str(tmp_path / "cfg"), data_dir=str(tmp_path / "data"), cache_dir=str(tmp_path / "cache")
    )
    app.ensure_layout()
    target = tmp_path / "broken.json"
    target.write_text("[]")
    with pytest.raises(ConfigError):
        SettingsStore(app).import_profile(str(target), LauncherSettings())


def test_settings_lock(tmp_path):
    app = AppPaths(
        config_dir=str(tmp_path / "cfg"), data_dir=str(tmp_path / "data"), cache_dir=str(tmp_path / "cache")
    )
    app.ensure_layout()
    first = SettingsStore(app)
    second = SettingsStore(app)
    assert first.acquire_lock()
    assert not second.acquire_lock()
    first.release_lock()
    assert second.acquire_lock()
    second.release_lock()
