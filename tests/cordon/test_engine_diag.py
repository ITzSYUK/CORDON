"""Engine discovery, diagnostics collection and pending-archive handling."""

from __future__ import annotations

import os
import zipfile

import pytest

from cordon.core import diagnostics, engine as engine_mod, mods, preflight, util, xray
from cordon.core.models import ModEntry, Profile


def _pe_stub(path: str) -> str:
    """A minimal Windows executable (MZ + PE header) for the "wrong platform" checks."""
    import struct

    dos = bytearray(64)
    dos[0:2] = b"MZ"
    struct.pack_into("<I", dos, 60, 64)  # e_lfanew
    data = bytes(dos) + b"PE\x00\x00" + struct.pack("<HH", 0x8664, 0) + struct.pack("<HHH", 2, 0, 0)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(data)
    os.chmod(path, 0o755)
    return path


# ---------------------------------------------------------------------------- engine
def test_find_engine_prefers_the_pinned_executable(fake_install, fake_profile):
    alt = os.path.join(fake_install.root, "system", "bin")
    os.makedirs(alt, exist_ok=True)
    binary = os.path.join(alt, "xr_3da")
    with open(os.path.join(fake_install.game, "bin", "xr_3da"), "rb") as handle:
        head = handle.read()
    with open(binary, "wb") as handle:
        handle.write(head)
    os.chmod(binary, 0o755)

    fake_profile.executable_source = binary
    info = engine_mod.find_engine(fake_profile)
    assert info is not None
    assert info.executable == util.norm(binary)
    assert info.source == "вручную"
    assert info.engine_root == util.norm(alt)


def test_find_engine_uses_extra_roots_for_portable_installs(fake_install):
    portable = os.path.join(fake_install.root, "portable-engine")
    os.makedirs(os.path.join(portable, "bin_x64"), exist_ok=True)
    binary = os.path.join(portable, "bin_x64", "xr_3da_x64")
    with open(os.path.join(fake_install.game, "bin", "xr_3da"), "rb") as handle:
        head = handle.read()
    with open(binary, "wb") as handle:
        handle.write(head)
    os.chmod(binary, 0o755)

    profile = Profile(id="p2", name="Только движок")
    info = engine_mod.find_engine(profile, extra_roots=(portable,))
    assert info is not None
    assert os.path.basename(info.executable) == "xr_3da_x64"
    assert info.source == "auto"
    assert info.linux_native


def test_find_engine_detects_system_data_root(fake_install, fake_profile, monkeypatch, tmp_path):
    share = tmp_path / "share" / "openxray"
    (share / "gamedata" / "shaders").mkdir(parents=True)
    (share / "fsgame.ltx").write_text("[root]\n")
    monkeypatch.setattr(engine_mod, "SYSTEM_DATA_DIRS", (str(share),))

    fake_profile.engine_data_path = ""
    fake_profile.game_path = ""  # nothing to point at → the system directory is used
    fake_profile.engine_path = ""
    fake_profile.executable_relative = ""
    fake_profile.executable_source = os.path.join(fake_install.game, "bin", "xr_3da")
    info = engine_mod.find_engine(fake_profile)
    assert info is not None
    assert info.data_root == util.norm(str(share))
    assert info.fsgame_source == util.norm(str(share / "fsgame.ltx"))


def test_engine_with_windows_binary_is_flagged(fake_install, fake_profile):
    _pe_stub(os.path.join(fake_install.game, "bin", "xr_3da"))
    fake_profile.executable_source = os.path.join(fake_install.game, "bin", "xr_3da")
    info = engine_mod.find_engine(fake_profile)
    assert info is not None
    assert info.binary.kind == "pe"
    assert info.binary.is_windows_binary and not info.linux_native

    report = preflight.run(fake_profile, fake_install.store)
    assert not report.ok
    assert any("Windows" in check.title for check in report.errors)


def test_engine_not_found_raises_clear_error(fake_install, fake_profile):
    from cordon.core.errors import EngineNotFoundError

    empty = os.path.join(fake_install.root, "empty")
    os.makedirs(empty, exist_ok=True)
    fake_profile.game_path = empty
    fake_profile.engine_path = ""
    fake_profile.executable_relative = ""
    fake_profile.executable_source = ""
    if engine_mod.detect_system_binary():  # pragma: no cover - depends on the host
        pytest.skip("в системе установлен настоящий движок OpenXRay")
    with pytest.raises(EngineNotFoundError) as excinfo:
        engine_mod.require_engine(fake_profile)
    assert "движк" in str(excinfo.value).lower()


def test_ldd_missing_on_a_stub_binary_is_empty(fake_install):
    # the 64-byte test ELF has no interpreter, ldd reports "not a dynamic executable"
    assert engine_mod.ldd_missing(os.path.join(fake_install.game, "bin", "xr_3da")) == []


def test_candidate_executables_ignores_unrelated_files(fake_install):
    junk = os.path.join(fake_install.game, "bin", "xr_not-an-executable")
    util.write_text_atomic(junk, "not an elf")
    found = engine_mod.candidate_executables(fake_install.game)
    assert os.path.join(fake_install.game, "bin", "xr_3da") in found
    assert junk not in found
    assert found[0].endswith("xr_3da")  # xr_3da sorts first


# ---------------------------------------------------------------------------- diagnostics
def _write_log(appdata: str, name: str, lines: list[str], *, mtime: float | None = None) -> str:
    path = os.path.join(appdata, "logs", name)
    util.ensure_dir(os.path.dirname(path))
    util.write_text_atomic(path, "\n".join(lines) + "\n")
    if mtime is not None:
        os.utime(path, (mtime, mtime))
    return path


def test_diagnostics_finds_the_newest_log(fake_install, fake_profile, tmp_path):
    appdata = str(tmp_path / "appdata")
    _write_log(appdata, "xray_old.log", ["old"], mtime=1)
    _write_log(appdata, "xray_new.log", ["X-Ray 1.6", "! cannot find texture", "* done"])

    diag = diagnostics.collect(appdata)
    assert diag.log_path.endswith("xray_new.log")
    assert diag.log_tail[-1] == "* done"
    assert diag.has_problems  # the "!" line marks a problem
    assert "xray_new.log" in diag.to_text()


def test_diagnostics_respects_the_since_timestamp(fake_install, tmp_path):
    appdata = str(tmp_path / "appdata")
    _write_log(appdata, "xray_old.log", ["old"], mtime=1)
    diag = diagnostics.collect(appdata, since=0.0)
    assert diag.log_path == "" or diag.log_path.endswith("xray_old.log")
    fresh = diagnostics.collect(appdata, since=10_000_000_000.0)  # far in the future
    assert fresh.log_path == ""
    assert any("не найден" in note for note in fresh.notes)


def test_diagnostics_collects_crash_dumps(tmp_path):
    appdata = str(tmp_path / "appdata")
    dumps = os.path.join(appdata, "dumps")
    util.ensure_dir(dumps)
    util.write_text_atomic(os.path.join(dumps, "xray_2026.dmp"), "dump")
    util.write_text_atomic(os.path.join(dumps, "crash_report.txt"), "crash")
    util.write_text_atomic(os.path.join(dumps, "readme.txt"), "not a dump")

    diag = diagnostics.collect(appdata)
    names = {os.path.basename(path) for path in diag.dumps}
    assert names == {"xray_2026.dmp", "crash_report.txt"}
    assert diag.has_problems
    assert "crash_report.txt" in diag.to_text()


def test_diagnostics_without_appdata_directory(tmp_path):
    diag = diagnostics.collect(str(tmp_path / "nope"))
    assert diag.log_path == "" and diag.dumps == []
    assert "не найден" in diag.to_text()


def test_report_text_and_file(fake_install, fake_profile, tmp_path):
    diag = diagnostics.collect(str(tmp_path / "missing"))
    text = diagnostics.build_report_text(
        profile_name=fake_profile.name,
        engine_summary="движок: xr_3da",
        executable="/usr/games/xr_3da",
        root_path="/tmp/profile",
        overlay_summary="слоёв: 3",
        preflight_text="[ОК] всё хорошо",
        diag=diag,
        extra_notes=["тип игры: Call of Pripyat"],
    )
    assert fake_profile.name in text and "тип игры" in text
    path = diagnostics.write_report(fake_install.store, "тест", text)
    assert os.path.isfile(path)
    assert util.read_text(path) == text


def test_standalone_data_dirs_uses_the_build_root(fake_install, tmp_path):
    build = str(tmp_path / "anomaly")
    util.ensure_dir(os.path.join(build, "appdata", "logs"))
    roots = diagnostics.standalone_data_dirs(build)
    assert any(root.endswith("appdata") for root in roots)


# ---------------------------------------------------------------------------- pending archives
def test_unpack_pending_installs_archives_and_keeps_folders(fake_install, fake_profile, tmp_path):
    folder = str(tmp_path / "already-unpacked")
    util.ensure_dir(os.path.join(folder, "gamedata", "configs"))
    util.write_text_atomic(os.path.join(folder, "gamedata", "configs", "system.ltx"), "[system]\n")

    archive = str(tmp_path / "Pack.zip")
    with zipfile.ZipFile(archive, "w") as zf:
        zf.writestr("gamedata/scripts/actor.script", "-- packed\n")

    entries = mods.scan_entries([folder, archive])
    assert entries[0].enabled is True
    assert entries[1].enabled is False  # archives wait for unpacking
    assert entries[1].name == "Pack"

    ready, failed = mods.unpack_pending(fake_install.store, fake_profile.id, entries)
    assert failed == []
    assert len(ready) == 2
    assert all(entry.enabled for entry in ready)
    unpacked = ready[1]
    assert os.path.isdir(unpacked.path)
    assert os.path.isfile(os.path.join(unpacked.path, "gamedata", "scripts", "actor.script"))
    assert unpacked.source == "archive"
    assert archive in unpacked.notes


def test_unpack_pending_reports_broken_archives(fake_install, fake_profile, tmp_path):
    broken = tmp_path / "Broken.7z"
    broken.write_bytes(b"7z\xbc\xaf\x27\x1c")
    if mods.detect_external_tools().get(".7z"):  # pragma: no cover - host dependant
        pytest.skip("7z установлен — проверка пропущена")

    entries = mods.scan_entries([str(broken)])
    ready, failed = mods.unpack_pending(fake_install.store, fake_profile.id, entries)
    assert ready == []
    assert failed and "Broken" in failed[0]


def test_archive_stem_handles_double_suffixes(tmp_path):
    assert mods.archive_stem("/tmp/Anomaly-1.5.2-Fix.zip") == "Anomaly-1.5.2-Fix"
    assert mods.archive_stem("/tmp/mod.tar.gz") == "mod"
    assert mods.archive_stem("/tmp/плайн.7z") == "плайн"


# ---------------------------------------------------------------------------- xray helpers
def test_xray_finds_archives_and_appdata(fake_install):
    archives = xray.find_archives(fake_install.game)
    assert any(path.endswith("gamedata.db0") for path in archives)
    # the "patches" root carries its own archives, which the overlay links as a whole
    assert any(path.endswith("gamedata.dba") for path in xray.find_archives(os.path.join(fake_install.game, "patches")))
    assert xray.is_xray_tree(fake_install.game)
    assert xray.has_gamedata(fake_install.game)
    assert xray.is_archive_name("gamedata.db0") and xray.is_archive_name("my_mod.db")
    assert not xray.is_archive_name("readme.txt")


def test_engine_switch_only_for_clear_sky():
    assert xray.engine_switch_for("cs") == "-cs"
    assert xray.engine_switch_for("cop") == ""
    assert xray.engine_switch_for("custom") == ""
    assert "Clear Sky" in xray.describe_game_id("cs")


def test_classify_mod_recognises_archives_inside_a_mod(fake_install):
    layout = xray.classify_mod(fake_install.mods["b"])
    assert layout.game_data_dir.endswith("gamedata")
    assert not layout.is_empty
    assert "db" in layout.root_entries  # db/mods/extra.db is merged into the game's db tree
    assert layout.appdata_entries  # appdata/user.ltx + shaders_cache → _appdata_
    assert util.is_inside(layout.game_data_dir, layout.root)
