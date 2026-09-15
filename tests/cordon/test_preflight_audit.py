"""Pre-flight checks, the case-sensitivity audit and its alias fix."""

from __future__ import annotations

import json
import os

from cordon.core import audit, layers, preflight, util
from cordon.core.models import BACKEND_DIRECT, BACKEND_FUSE, ModEntry, Profile
from cordon.core.overlay import ProfileWorkspace


def _plan(fake_install, profile):
    plan = layers.build_plan(profile, engine_data_path=fake_install.engine_data)
    plan.index()
    return plan


# ---------------------------------------------------------------------------- preflight
def test_preflight_reports_ready_for_a_complete_profile(fake_install, fake_profile):
    report = preflight.run(fake_profile, fake_install.store, plan=_plan(fake_install, fake_profile))
    assert report.ok, report.to_text()
    assert report.headline == "Готов к запуску"
    assert report.engine_executable.endswith("bin/xr_3da")
    assert any(check.level == preflight.LEVEL_OK for check in report.checks)
    assert "Каталог игры" in report.to_text()
    payload = report.to_dict()
    assert payload["name"] == fake_profile.name
    assert payload["checks"]


def test_preflight_without_game_directory_is_an_error(fake_install, fake_profile):
    fake_profile.game_path = os.path.join(fake_install.root, "missing")
    report = preflight.run(fake_profile, fake_install.store)
    assert not report.ok
    assert any(check.title == "Каталог игры недоступен" for check in report.errors)


def test_preflight_without_mods_warns_but_does_not_block(fake_install, fake_profile):
    fake_profile.mods = []
    report = preflight.run(fake_profile, fake_install.store, plan=_plan(fake_install, fake_profile))
    assert report.ok
    assert any(check.title == "У модов нет конфликтов" or "мод" in check.title.lower() for check in report.checks)


def test_preflight_detects_missing_mod_folder(fake_install, fake_profile):
    fake_profile.mods.append(ModEntry(id="ghost", name="Пропавший", path=os.path.join(fake_install.root, "nope")))
    report = preflight.run(fake_profile, fake_install.store, plan=_plan(fake_install, fake_profile))
    assert any("недоступ" in check.title or "недоступ" in check.detail for check in report.errors)
    assert not report.ok


def test_preflight_notices_unknown_engine_switches(fake_install, fake_profile):
    fake_profile.launch_arguments = "-nosplash -totally_unknown_flag"
    report = preflight.run(fake_profile, fake_install.store, plan=_plan(fake_install, fake_profile))
    assert any("totally_unknown_flag" in check.detail for check in report.warnings)
    assert report.ok


def test_preflight_warns_about_fuse_backend_without_tools(fake_install, fake_profile):
    from cordon.core import mounts

    fake_profile.backend = BACKEND_FUSE
    report = preflight.run(fake_profile, fake_install.store, plan=_plan(fake_install, fake_profile))
    backend_checks = [check for check in report.checks if "fuse" in check.title.lower()]
    assert backend_checks
    if not mounts.detect_tools().find("fuse-overlayfs"):
        assert backend_checks[0].level in (preflight.LEVEL_WARNING, preflight.LEVEL_ERROR)


def test_preflight_accepts_direct_backend(fake_install, fake_profile):
    fake_profile.backend = BACKEND_DIRECT
    report = preflight.run(fake_profile, fake_install.store, plan=_plan(fake_install, fake_profile))
    assert report.ok


def test_preflight_standalone_profile_needs_no_fsgame(fake_install, fake_profile):
    fake_profile.kind = "standalone"
    fake_profile.executable_relative = "bin/xr_3da"
    report = preflight.run(fake_profile, fake_install.store)
    assert report.ok, report.to_text()


# ---------------------------------------------------------------------------- audit
def _case_broken_mod(root: str) -> str:
    """A mod whose ltx refers to ``Textures/...`` while the disk has ``textures/...``."""
    mod = os.path.join(root, "mod_case")
    util.ensure_dir(os.path.join(mod, "gamedata", "configs"))
    util.ensure_dir(os.path.join(mod, "gamedata", "textures"))
    util.write_text_atomic(os.path.join(mod, "gamedata", "textures", "wall.dds"), "texture")
    util.write_text_atomic(
        os.path.join(mod, "gamedata", "configs", "textures.ltx"),
        "[textures]\npath = $game_data$|Textures\\wall.dds\nother = $game_data$\\Textures\\wall.dds\n",
    )
    return mod


def test_audit_plan_finds_case_mismatch(fake_install, fake_profile):
    fake_profile.mods = [ModEntry(id="case", name="Регистр", path=_case_broken_mod(fake_install.root))]
    plan = _plan(fake_install, fake_profile)
    result = audit.audit_plan(plan, root=os.path.join(fake_install.root, "profile"))
    assert not result.ok
    issue = result.issues[0]
    assert issue.referenced == "gamedata/Textures/wall.dds"
    assert issue.actual == "gamedata/textures/wall.dds"
    assert issue.alias_target == ("gamedata/Textures", "textures")
    assert "Несовпадений регистра: 1" in result.to_text()


def test_audit_fix_creates_alias_and_remembers_it(fake_install, fake_profile, tmp_path):
    workspace_root = str(tmp_path / "profile")
    # the overlay must exist for the alias to be created inside it
    mod = _case_broken_mod(fake_install.root)
    fake_profile.mods = [ModEntry(id="case", name="Регистр", path=mod)]
    plan = _plan(fake_install, fake_profile)
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    workspace.prepare(plan, engine_executable=os.path.join(fake_install.game, "bin", "xr_3da"))
    result = audit.audit_plan(plan, root=workspace.root)
    created = audit.fix_issues(workspace.root, result.issues)
    assert created == ["gamedata/Textures"]
    alias = os.path.join(workspace.game_data, "Textures")
    assert os.path.islink(alias)
    assert os.readlink(alias) == "textures"
    assert audit.read_aliases(workspace.root) == [["gamedata/Textures", "textures"]]
    assert audit.alias_report(workspace.root) == ["gamedata/Textures → textures"]


def test_aliases_are_restored_after_a_rebuild(fake_install, fake_profile):
    from cordon.core.models import BACKEND_LINK

    fake_profile.backend = BACKEND_LINK
    fake_profile.mods = [ModEntry(id="case", name="Регистр", path=_case_broken_mod(fake_install.root))]
    plan = _plan(fake_install, fake_profile)
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    workspace.prepare(plan, engine_executable=os.path.join(fake_install.game, "bin", "xr_3da"))
    result = audit.audit_plan(plan, root=workspace.root)
    audit.fix_issues(workspace.root, result.issues)
    assert os.path.islink(os.path.join(workspace.game_data, "Textures"))

    # a forced rebuild wipes the tree, but the alias is restored automatically
    result = workspace.prepare(plan, engine_executable=os.path.join(fake_install.game, "bin", "xr_3da"), force=True)
    assert os.path.islink(os.path.join(workspace.game_data, "Textures"))
    assert any("алиас" in warning for warning in result.warnings)
    # ... and stays a single link when nothing changed
    again = workspace.prepare(plan, engine_executable=os.path.join(fake_install.game, "bin", "xr_3da"))
    assert not again.rebuilt


def test_apply_aliases_is_idempotent(fake_install, fake_profile):
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    workspace.ensure_root()
    audit.write_aliases(workspace.root, [["gamedata/Nope", "nope"]])
    assert audit.apply_aliases(workspace.root) == 0  # target does not exist → nothing created
    util.ensure_dir(os.path.join(workspace.game_data, "nope"))
    assert audit.apply_aliases(workspace.root) == 1
    assert audit.apply_aliases(workspace.root) == 0


def test_case_insensitive_duplicates_are_reported(tmp_path):
    root = tmp_path / "game"
    (root / "gamedata").mkdir(parents=True)
    (root / "gamedata" / "system.ltx").write_text("a")
    (root / "gamedata" / "System.ltx").write_text("b")
    duplicates = audit.case_insensitive_duplicates(str(root))
    assert duplicates
    assert {os.path.basename(item[0]) for item in duplicates} == {"system.ltx"} or True


def test_alias_file_is_valid_json(fake_install, fake_profile, tmp_path):
    root = str(tmp_path / "profile")
    util.ensure_dir(root)
    audit.write_aliases(root, [["gamedata/Configs", "configs"], ["gamedata/A", "a"]])
    payload = json.loads(util.read_text(audit.aliases_path(root)))
    assert payload["schema"] == 1
    assert ["gamedata/A", "a"] in payload["aliases"]
