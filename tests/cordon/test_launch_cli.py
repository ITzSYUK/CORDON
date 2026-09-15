"""Launch command line, the service layer, MO2 import and the CLI."""

from __future__ import annotations

import os

import pytest

from cordon import cli
from cordon.core import engine as engine_mod
from cordon.core import launch, mo2, util
from cordon.core.models import BACKEND_FUSE, ModEntry, Profile
from cordon.core.paths import AppPaths
from cordon.core.service import CordonService


@pytest.fixture()
def service(fake_install, fake_profile) -> CordonService:
    service = CordonService(fake_install.store)
    service.load()
    service.settings.profiles = [fake_profile]
    service.settings.selected_profile_id = fake_profile.id
    service.save()
    return service


# ---------------------------------------------------------------------------- launch plan
def test_launch_plan_points_engine_at_the_profile(fake_install, fake_profile):
    plan = launch.build_launch_plan(fake_profile, fake_install.store)
    assert plan.argv[0].endswith("bin/xr_3da")
    assert "-fsltx" in plan.argv
    assert plan.argv[plan.argv.index("-fsltx") + 1] == plan.fsgame_path
    assert plan.fsgame_path.endswith(os.path.join("profile-p001", "fsgame.ltx"))
    assert "-overlaypath" in plan.argv
    assert plan.argv[plan.argv.index("-overlaypath") + 1] == plan.appdata_path
    assert plan.cwd == plan.root_path  # $fs_root$ must be the profile directory
    assert "перекл" in "".join(plan.notes) or plan.notes


def test_launch_plan_adds_clear_sky_switch(fake_install, fake_profile):
    fake_profile.game_id = "cs"
    plan = launch.build_launch_plan(fake_profile, fake_install.store)
    assert "-cs" in plan.argv


def test_launch_plan_keeps_extra_arguments_in_order(fake_install, fake_profile):
    fake_profile.engine_flags = ["-nosplash"]
    fake_profile.launch_arguments = "-nointro -ltx user.ltx"
    plan = launch.build_launch_plan(fake_profile, fake_install.store)
    assert plan.argv[-3:] == ["-nointro", "-ltx", "user.ltx"]
    assert "-nosplash" in plan.argv


def test_launch_plan_for_standalone_profile_runs_in_game_directory(fake_install, fake_profile):
    fake_profile.kind = "standalone"
    plan = launch.build_launch_plan(fake_profile, fake_install.store)
    assert plan.cwd == fake_profile.game_path
    assert "-fsltx" not in plan.argv


def test_command_line_quoting_is_posix(fake_install, fake_profile):
    fake_profile.root_path = os.path.join(fake_install.root, "profile with spaces")
    plan = launch.build_launch_plan(fake_profile, fake_install.store)
    assert "'" in plan.command_line
    assert f"'{plan.fsgame_path}'" in plan.command_line


def test_dry_run_does_not_start_a_process(fake_install, fake_profile, monkeypatch):
    def explode(*_args, **_kwargs):  # pragma: no cover - must never be called
        raise AssertionError("процесс не должен запускаться в режиме --dry-run")

    monkeypatch.setattr(launch, "spawn", explode)
    plan = launch.run_profile(fake_profile, fake_install.store, dry_run=True)
    assert plan.argv[0].endswith("bin/xr_3da")
    assert os.path.isfile(plan.fsgame_path)


def test_describe_launch_mentions_paths(fake_install, fake_profile):
    plan = launch.build_launch_plan(fake_profile, fake_install.store)
    text = launch.describe_launch(plan)
    assert "Команда" in text and plan.cwd in text


# ---------------------------------------------------------------------------- service
def test_service_create_duplicate_and_delete_profile(fake_install):
    service = CordonService(fake_install.store)
    service.load()
    created = service.create_profile("Профиль 1", game_path=fake_install.game)
    assert created.engine_data_path in ("", fake_install.game)
    clone = service.duplicate_profile(created.id, new_name="Копия")
    assert clone.id != created.id
    assert clone.name == "Копия"
    assert len(service.profiles) == 2

    service.prepare(created)
    root = service.workspace_for(created).root
    assert os.path.isdir(root)
    assert service.delete_profile(created.id)
    assert not os.path.isdir(root)


def test_service_add_mods_and_toggle(fake_install):
    service = CordonService(fake_install.store)
    service.load()
    profile = service.create_profile("Моды", game_path=fake_install.game)
    added = service.add_mods_from_folders(profile, [fake_install.mods["a"], fake_install.mods["b"]])
    assert added == 2
    assert service.add_mods_from_folders(profile, [fake_install.mods["a"]]) == 0  # no duplicates

    scan = service.scan_folder(profile, os.path.join(fake_install.root, "mods"))
    assert scan.found == []  # both are already in the profile
    assert len(scan.skipped) == 2

    service.remove_mod(profile, profile.mods[0].id)
    assert len(profile.mods) == 1


def test_service_reports_on_disk_usage(fake_install):
    service = CordonService(fake_install.store)
    service.load()
    profile = service.create_profile("Отчёт", game_path=fake_install.game)
    service.add_mods_from_folders(profile, [fake_install.mods["a"]])
    service.prepare(profile)
    usage = service.gc_report()
    assert usage["files"] > 0 and usage["bytes"] > 0
    report_path = service.report(profile)
    assert os.path.isfile(report_path)
    assert "CORDON-LINUX" in util.read_text(report_path)


def test_service_status_and_conflicts(fake_install):
    service = CordonService(fake_install.store)
    service.load()
    profile = service.create_profile("Проверки", game_path=fake_install.game)
    service.add_mods_from_folders(profile, [fake_install.mods["a"]])
    status = service.status(profile)
    assert status.headline
    assert status.mods.startswith("модов включено")
    report = service.conflict_report(profile)
    assert report.per_mod


def test_service_without_engine_raises(fake_install):
    service = CordonService(fake_install.store)
    service.load()
    profile = service.create_profile("Пусто", game_path="")
    with pytest.raises(Exception) as excinfo:
        service.prepare(profile)
    assert "движ" in str(excinfo.value).lower()


# ---------------------------------------------------------------------------- MO2
def _mo2_tree(root: str) -> str:
    base = os.path.join(root, "mo2")
    util.ensure_dir(os.path.join(base, "mods", "Unofficial Patch", "gamedata", "configs"))
    util.ensure_dir(os.path.join(base, "mods", "Arsenal", "gamedata", "scripts"))
    util.ensure_dir(os.path.join(base, "overwrite", "gamedata"))
    util.ensure_dir(os.path.join(base, "profiles", "Default"))
    util.write_text_atomic(
        os.path.join(base, "profiles", "Default", "modlist.txt"),
        "# This file was automatically generated by Mod Organizer.\n"
        "* Unofficial\n"
        "+Unofficial Patch\n"
        "-Arsenal\n",
    )
    util.write_text_atomic(os.path.join(base, "ModOrganizer.ini"), "[General]\ngamePath=/games/cop\n")
    return base


def test_mo2_preview_and_apply(fake_install, fake_profile, tmp_path):
    base = _mo2_tree(str(tmp_path))
    preview = mo2.build_preview(base)
    assert [entry.name for entry in preview.entries] == ["Unofficial", "Unofficial Patch", "Arsenal"]
    assert preview.entries[0].separator
    assert preview.entries[1].enabled and not preview.entries[2].enabled
    assert preview.layout.overwrite_dir.endswith("overwrite")
    assert preview.layout.base_game == "/games/cop"

    count = mo2.apply_preview(fake_profile, preview)
    assert count == 2
    assert fake_profile.mod_by_id(fake_profile.mods[0].id) is not None
    assert [mod.name for mod in fake_profile.mods] == ["Unofficial Patch", "Arsenal"]
    assert fake_profile.mods[0].enabled and not fake_profile.mods[1].enabled
    assert fake_profile.mods[0].group == "Unofficial"
    assert fake_profile.mo2_overwrite_path.endswith("overwrite")


def test_mo2_modlist_only_keeps_existing_mods(fake_install, fake_profile, tmp_path):
    base = _mo2_tree(str(tmp_path))
    preview = mo2.build_preview(base)
    fake_profile.mods = [
        ModEntry(id="a", name="Arsenal", path=fake_install.mods["a"]),
        ModEntry(id="b", name="Unofficial Patch", path=fake_install.mods["b"]),
    ]
    count = mo2.apply_modlist_only(fake_profile, preview)
    assert count == 2
    assert [mod.name for mod in fake_profile.mods] == ["Unofficial Patch", "Arsenal"]
    assert fake_profile.mods[0].enabled and not fake_profile.mods[1].enabled


# ---------------------------------------------------------------------------- CLI
@pytest.fixture()
def isolated_cli_env(monkeypatch, tmp_path):
    monkeypatch.setenv("CORDON_CONFIG_DIR", str(tmp_path / "cfg"))
    monkeypatch.setenv("CORDON_DATA_DIR", str(tmp_path / "data"))
    monkeypatch.setenv("CORDON_CACHE_DIR", str(tmp_path / "cache"))
    return tmp_path


def test_cli_help_and_version(capsys):
    with pytest.raises(SystemExit) as help_exit:
        cli.main(["--help"])
    assert help_exit.value.code == 0
    assert "CORDON-LINUX" in capsys.readouterr().out
    with pytest.raises(SystemExit):
        cli.main(["--version"])
    assert "CORDON-LINUX" in capsys.readouterr().out


def test_cli_full_cycle(isolated_cli_env, fake_install, capsys):
    assert cli.main(["new", "Тест CLI", "--game", fake_install.game]) == cli.EXIT_OK
    assert "Создан профиль" in capsys.readouterr().out

    assert cli.main(["list"]) == cli.EXIT_OK
    assert "Тест CLI" in capsys.readouterr().out

    assert cli.main(["mod-add", "Тест CLI", fake_install.mods["a"]]) == cli.EXIT_OK
    assert "Добавлено модов: 1" in capsys.readouterr().out

    assert cli.main(["mods", "Тест CLI"]) == cli.EXIT_OK
    assert "mod_a" in capsys.readouterr().out

    assert cli.main(["prepare", "Тест CLI"]) == cli.EXIT_OK
    assert "fsgame.ltx" in capsys.readouterr().out

    assert cli.main(["launch", "Тест CLI", "--dry-run"]) == cli.EXIT_OK
    output = capsys.readouterr().out
    assert "-fsltx" in output and "-overlaypath" in output

    assert cli.main(["doctor", "Тест CLI"]) == cli.EXIT_OK
    assert "Готов к запуску" in capsys.readouterr().out

    assert cli.main(["conflicts", "Тест CLI"]) == cli.EXIT_OK
    assert "пересечений" in capsys.readouterr().out

    assert cli.main(["show", "Тест CLI"]) == cli.EXIT_OK
    assert "Backend" in capsys.readouterr().out

    assert cli.main(["audit", "Тест CLI"]) == cli.EXIT_OK
    assert "Несовпадений регистра" in capsys.readouterr().out

    assert cli.main(["mod-state", "Тест CLI", "mod_a", "off"]) == cli.EXIT_OK
    assert "выключен" in capsys.readouterr().out

    assert cli.main(["report", "Тест CLI"]) == cli.EXIT_OK
    assert "Отчёт сохранён" in capsys.readouterr().out

    assert cli.main(["delete", "Тест CLI", "--yes"]) == cli.EXIT_OK
    assert "удалён" in capsys.readouterr().out
    assert cli.main(["list"]) == cli.EXIT_OK
    assert "Профилей пока нет" in capsys.readouterr().out


def test_cli_unknown_profile_is_reported(isolated_cli_env, capsys):
    assert cli.main(["show", "нет такого"]) == cli.EXIT_ERROR
    assert "не найден" in capsys.readouterr().err


def test_cli_import_mo2_preview_then_apply(isolated_cli_env, fake_install, tmp_path, capsys):
    base = _mo2_tree(str(tmp_path))
    cli.main(["new", "MO2", "--game", fake_install.game])
    capsys.readouterr()

    assert cli.main(["import-mo2", "MO2", base]) == cli.EXIT_OK
    assert "предпросмотр" in capsys.readouterr().out

    assert cli.main(["import-mo2", "MO2", base, "--apply"]) == cli.EXIT_OK
    assert "Импортировано записей: 2" in capsys.readouterr().out

    assert cli.main(["mods", "MO2"]) == cli.EXIT_OK
    assert "Unofficial Patch" in capsys.readouterr().out


def test_cli_export_import_roundtrip(isolated_cli_env, fake_install, tmp_path, capsys):
    cli.main(["new", "Экспорт", "--game", fake_install.game])
    capsys.readouterr()
    target = str(tmp_path / "profile.json")
    assert cli.main(["export", "Экспорт", target]) == cli.EXIT_OK
    assert cli.main(["import", target]) == cli.EXIT_OK
    assert "Импортирован профиль" in capsys.readouterr().out


def test_cli_tools_lists_unpackers(isolated_cli_env, capsys):
    assert cli.main(["tools"]) == cli.EXIT_OK
    assert "Discord IPC" in capsys.readouterr().out


def test_cli_doctor_json(isolated_cli_env, fake_install, capsys):
    import json

    cli.main(["new", "JSON", "--game", fake_install.game])
    capsys.readouterr()
    assert cli.main(["doctor", "JSON", "--json"]) == cli.EXIT_OK
    payload = json.loads(capsys.readouterr().out)
    assert payload[0]["name"] == "JSON"
    assert payload[0]["headline"]


def test_cli_engine_detection_helper(fake_install, fake_profile):
    info = launch.engine_for(fake_profile)
    assert info is not None and info.executable.endswith("xr_3da")
    assert engine_mod.find_engine(Profile(id="x", name="Пустой")) is None


def test_app_paths_marker_guard(fake_install):
    app = fake_install.store
    assert app.is_owned(app.profiles_root) is True
    assert app.is_owned("/etc") is False
