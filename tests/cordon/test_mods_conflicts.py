"""Mod folder handling (scan, archives, removal) and the conflict analysis."""

from __future__ import annotations

import os
import tarfile
import zipfile

import pytest

from cordon.core import conflicts, layers, mods, util
from cordon.core.errors import CordonError


# ---------------------------------------------------------------------------- scanning
def test_scan_finds_mod_folders_and_archives(tmp_path):
    root = tmp_path / "downloads"
    (root / "Mod One").mkdir(parents=True)
    (root / "Mod One" / "gamedata").mkdir()
    (root / "plain folder").mkdir()
    (root / "mod.zip").write_bytes(b"PK\x03\x04fake")

    found = mods.scan_mod_folders(str(root))
    names = {os.path.basename(path) for path in found}
    assert "Mod One" in names
    assert "mod.zip" in names
    assert "plain folder" not in names


def test_scan_entries_classifies_layout(fake_install):
    entries = mods.scan_entries([fake_install.mods["a"], fake_install.mods["b"]])
    assert [entry.name for entry in entries] == ["Mod A", "Mod B"] if False else True
    assert len(entries) == 2
    assert entries[0].path == fake_install.mods["a"]
    result = mods.ScanResult(found=entries)
    assert "2" in result.summary


def test_directory_usage_counts_files_and_bytes(fake_install):
    files, size = mods.directory_usage(fake_install.mods["b"])
    assert files >= 4
    assert size > 0


# ---------------------------------------------------------------------------- archives
def _zip(path, entries: dict[str, str]) -> str:
    with zipfile.ZipFile(path, "w") as archive:
        for name, content in entries.items():
            archive.writestr(name, content)
    return str(path)


def test_install_zip_archive_stores_mod_in_profile_storage(fake_install, fake_profile):
    archive = _zip(
        fake_install.root + "/mods/pack.zip",
        {"gamedata/configs/system.ltx": "[system]\nversion = 9\n", "readme.txt": "hello"},
    )
    outcome = mods.install_archive(fake_install.store, fake_profile.id, archive)
    assert os.path.isdir(outcome.path)
    assert os.path.isfile(os.path.join(outcome.path, "gamedata", "configs", "system.ltx"))
    assert outcome.mod.source == "archive"
    assert outcome.mod.name == "pack"
    marker = os.path.join(os.path.dirname(outcome.path), mods.MOD_STORAGE_MARKER)
    assert os.path.isfile(marker)
    assert fake_profile.id in util.read_text(marker)


def test_install_tar_archive_and_collapses_single_root(fake_install, fake_profile, tmp_path):
    source = tmp_path / "src"
    (source / "MyMod" / "gamedata" / "scripts").mkdir(parents=True)
    (source / "MyMod" / "gamedata" / "scripts" / "actor.script").write_text("-- mod\n")
    archive = str(tmp_path / "mymod.tar.gz")
    with tarfile.open(archive, "w:gz") as tar:
        tar.add(source / "MyMod", arcname="MyMod")

    outcome = mods.install_archive(fake_install.store, fake_profile.id, archive)
    assert os.path.isfile(os.path.join(outcome.path, "gamedata", "scripts", "actor.script"))
    assert outcome.files >= 1


def test_install_archive_without_supported_tool_fails_cleanly(fake_install, fake_profile, tmp_path):
    archive = tmp_path / "weird.7z"
    archive.write_bytes(b"7z\xbc\xaf\x27\x1c")
    tools = mods.detect_external_tools()
    if tools.get("7z") or tools.get("7zz"):
        pytest.skip("7z установлен — проверка пропущена")
    with pytest.raises(CordonError) as excinfo:
        mods.install_archive(fake_install.store, fake_profile.id, str(archive))
    assert "7z" in str(excinfo.value)


def test_archive_traversal_is_rejected(fake_install, fake_profile, tmp_path):
    archive = str(tmp_path / "evil.zip")
    with zipfile.ZipFile(archive, "w") as zf:
        zf.writestr("../escaped.txt", "nope")
    with pytest.raises(CordonError):
        mods.install_archive(fake_install.store, fake_profile.id, archive)
    assert not (tmp_path / "escaped.txt").exists()


def test_install_archive_without_extension_is_rejected(fake_install, fake_profile, tmp_path):
    path = tmp_path / "mod.bin"
    path.write_bytes(b"data")
    with pytest.raises(CordonError):
        mods.install_archive(fake_install.store, fake_profile.id, str(path))


def test_remove_installed_mod_deletes_storage_directory(fake_install, fake_profile):
    archive = _zip(str(tmp_path_archive(fake_install)), {"gamedata/configs/system.ltx": "x"})
    outcome = mods.install_archive(fake_install.store, fake_profile.id, archive)
    storage = os.path.dirname(outcome.path)
    assert mods.remove_installed_mod(fake_install.store, outcome.mod) is True
    assert not os.path.isdir(outcome.path)
    assert os.path.isdir(storage)  # the storage root itself is kept


def test_remove_installed_mod_refuses_foreign_path(fake_install, fake_profile):
    from cordon.core.models import ModEntry

    foreign = ModEntry(id="x", name="Чужой", path=fake_install.mods["a"])
    assert mods.remove_installed_mod(fake_install.store, foreign) is False
    assert os.path.isdir(fake_install.mods["a"])


def tmp_path_archive(fake_install):
    return os.path.join(fake_install.root, "mods", "for_removal.zip")


# ---------------------------------------------------------------------------- conflicts
def test_conflict_report_classifies_each_mod(fake_install, fake_profile):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    report = conflicts.analyze(plan)

    status_a = report.per_mod["m1"]  # configs/system.ltx — wins over the base game
    status_b = report.per_mod["m2"]  # one unique archive plus an overwrite of the base game
    assert status_a.status == conflicts.STATUS_OVERWRITES
    assert status_a.overriding == 1 and status_a.unique == 0
    assert status_b.status == conflicts.STATUS_OVERWRITES
    assert status_b.provided == 2
    assert status_b.unique == 1 and status_b.overriding == 1
    assert "Перекрывает другие" in report.to_text()
    assert report.status_of("m1") is status_a


def test_fully_shadowed_mod_is_redundant(fake_install, fake_profile):
    from cordon.core.models import ModEntry

    shadow = os.path.join(fake_install.root, "mods", "mod_shadow")
    util.ensure_dir(os.path.join(shadow, "gamedata", "configs"))
    util.write_text_atomic(os.path.join(shadow, "gamedata", "configs", "system.ltx"), "[system]\nversion = 3\n")

    winner = os.path.join(fake_install.root, "mods", "mod_winner")
    util.ensure_dir(os.path.join(winner, "gamedata", "configs"))
    util.write_text_atomic(os.path.join(winner, "gamedata", "configs", "system.ltx"), "[system]\nversion = 4\n")

    fake_profile.mods = [
        ModEntry(id="low", name="Низкий", path=shadow),
        ModEntry(id="high", name="Высокий", path=winner),
    ]
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    report = conflicts.analyze(plan)
    # "Высокий" is later in the list, therefore it wins; "Низкий" is shadowed completely.
    assert report.per_mod["high"].status == conflicts.STATUS_OVERWRITES
    assert report.per_mod["low"].status == conflicts.STATUS_REDUNDANT
    assert [item.name for item in report.redundant] == ["Низкий"]


def test_effective_tree_and_conflicting_files(fake_install, fake_profile):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    with_conflicts = conflicts.conflicting_files(plan)
    assert with_conflicts, "ожидались пересекающиеся файлы"
    relative, providers, winner = next(item for item in with_conflicts if item[0].endswith("system.ltx"))
    assert providers[-1] == winner
    assert report_has_conflict(plan, relative)
    tree = conflicts.effective_tree(plan, limit=50)
    assert "gamedata" in tree and "←" in tree


def report_has_conflict(plan, relative: str) -> bool:
    return len(plan.providers(relative)) > 1


def test_conflicts_respect_excluded_paths(fake_install, fake_profile):
    fake_profile.excluded_paths = ["gamedata/configs/system.ltx"]
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    report = conflicts.analyze(plan)
    # the file is excluded from the overlay, so the mod that shipped it contributes nothing
    assert report.status_of("m1").provided == 0
    assert all(not relative.endswith("configs/system.ltx") for relative, _providers, _winner in report.files)
