"""Application service: the single entry point shared by the CLI and the GUI.

Keeping this layer free of any UI toolkit is what allows ``cordon`` (CLI) and ``cordon-gui`` to
behave identically, and it makes the whole launcher testable without a display.
"""

from __future__ import annotations

import logging
import os
import shutil
import time
from dataclasses import dataclass, field

from . import audit, conflicts, diagnostics, engine as engine_mod, launcherlog, layers, mods as mods_mod
from . import preflight, util, xray
from .errors import ProfileError
from .models import BACKEND_LINK, Profile
from .overlay import ProfileWorkspace
from .paths import AppPaths
from .settings import LoadResult, SettingsStore


@dataclass(slots=True)
class ServiceStatus:
    headline: str = ""
    engine: str = ""
    root: str = ""
    overlay: str = ""
    mods: str = ""
    problems: list[str] = field(default_factory=list)


class CordonService:
    """High level operations on profiles (create, edit, prepare, inspect)."""

    def __init__(self, app: AppPaths, *, logger: logging.Logger | None = None) -> None:
        self.app = app
        self.app.ensure_layout()
        if logger is None:
            logger, self.memory = launcherlog.setup_logging(app)
        else:
            self.memory = None
        self.logger = logger
        self.store = SettingsStore(app, logger=logger)
        self.settings = None
        self.notices: list[str] = []

    # ------------------------------------------------------------------ lifecycle
    def load(self) -> LoadResult:
        result = self.store.load()
        self.settings = result.settings
        self.notices = list(result.notices)
        for notice in result.notices:
            self.logger.warning("%s", notice)
        return result

    def save(self) -> None:
        if self.settings is not None:
            self.store.save(self.settings)

    @property
    def profiles(self) -> list[Profile]:
        return self.settings.profiles if self.settings else []

    def profile(self, profile_id: str) -> Profile:
        profile = self.settings.profile(profile_id) if self.settings else None
        if profile is None:
            raise ProfileError(f"профиль {profile_id} не найден")
        return profile

    # ------------------------------------------------------------------ CRUD
    def create_profile(
        self,
        name: str,
        *,
        kind: str = "mods",
        game_path: str = "",
        description: str = "",
    ) -> Profile:
        profile = Profile(
            id=util.new_id(),
            name=name or "Новый профиль",
            kind=kind,
            game_path=util.norm(game_path) if game_path else "",
            description=description,
        )
        profile.normalize()
        detection = engine_mod.find_engine(profile) if game_path else None
        if detection and detection.data_root and detection.data_root != util.norm(game_path):
            profile.engine_data_path = detection.data_root
        self.settings.profiles.append(profile)
        self.settings.selected_profile_id = profile.id
        self.save()
        self.logger.info("создан профиль «%s» (%s)", profile.name, profile.id)
        return profile

    def duplicate_profile(self, profile_id: str, *, new_name: str = "") -> Profile:
        import copy

        source = self.profile(profile_id)
        clone = copy.deepcopy(source)
        clone.id = util.new_id()
        clone.name = new_name or f"{source.name} (копия)"
        clone.root_path = ""
        clone.mod_storage_path = ""
        clone.total_playtime_seconds = 0.0
        clone.last_played_at = None
        for mod in clone.mods:
            mod.id = util.new_id()
        clone.normalize()
        self.settings.profiles.append(clone)
        self.settings.selected_profile_id = clone.id
        self.save()
        return clone

    def delete_profile(self, profile_id: str, *, remove_files: bool = True) -> bool:
        profile = self.profile(profile_id)
        self.settings.profiles.remove(profile)
        if self.settings.selected_profile_id == profile_id:
            self.settings.selected_profile_id = self.profiles[0].id if self.profiles else ""
        self.save()
        if not remove_files:
            return True
        workspace = ProfileWorkspace(self.app, profile, logger=self.logger)
        removed = False
        if workspace.is_initialised():
            workspace.remove_root()
            removed = True
        storage = self.app.mod_storage(profile.id, profile.mod_storage_path)
        if os.path.isdir(storage) and os.path.isfile(os.path.join(storage, ".cordon-mod-storage")):
            shutil.rmtree(storage, ignore_errors=True)
        self.logger.info("профиль «%s» удалён (файлы: %s)", profile.name, removed)
        return removed

    # ------------------------------------------------------------------ plan helpers
    def engine_for(self, profile: Profile):
        return engine_mod.find_engine(profile)

    def plan_for(self, profile: Profile, *, engine=None, progress=None) -> layers.LayerPlan:
        info = engine or engine_mod.find_engine(profile)
        data_root = info.data_root if info else profile.engine_data_path
        plan = layers.build_plan(profile, engine_data_path=data_root)
        plan.index(progress)
        return plan

    def workspace_for(self, profile: Profile) -> ProfileWorkspace:
        return ProfileWorkspace(self.app, profile, logger=self.logger)

    def prepare(self, profile: Profile, *, force: bool = False, progress=None):
        info = engine_mod.require_engine(profile)
        plan = self.plan_for(profile, engine=info, progress=progress)
        workspace = self.workspace_for(profile)
        result = workspace.prepare(plan, engine_executable=info.executable, force=force, progress=progress)
        if profile.backend == BACKEND_LINK:
            audit.apply_aliases(workspace.root, logger=self.logger)
        return plan, workspace, result

    def status(self, profile: Profile, *, plan=None, engine=None) -> ServiceStatus:
        info = engine or engine_mod.find_engine(profile)
        report = preflight.run(profile, self.app, plan=plan, engine=info, workspace=self.workspace_for(profile))
        status = ServiceStatus(
            headline=report.headline,
            engine=report.engine_summary,
            root=report.root_path,
            overlay=report.overlay_summary,
            mods=f"модов включено: {len(profile.enabled_mods)} из {len(profile.mods)}",
            problems=[f"{check.title}: {check.detail}" for check in report.errors],
        )
        return status

    def audit_case(self, profile: Profile, *, plan=None, fix: bool = False) -> audit.CaseAuditResult:
        workspace = self.workspace_for(profile)
        plan = plan or self.plan_for(profile)
        result = audit.audit_plan(plan, root=workspace.root)
        if fix:
            workspace.ensure_root()
            if not os.path.isdir(workspace.game_data):
                workspace.prepare(plan)
            # aliases live in the overlay root, next to the gamedata tree
            created = audit.fix_issues(workspace.root, result.issues, logger=self.logger)
            self.logger.info("создано алиасов регистра: %d", len(created))
            result.created = list(created)
            result.issues = [issue for issue in result.issues if issue.alias_target[0] not in set(created)]
        return result

    def conflict_report(self, profile: Profile, *, plan=None) -> conflicts.ConflictReport:
        plan = plan or self.plan_for(profile)
        return conflicts.analyze(plan)

    def add_mods_from_folders(self, profile: Profile, paths: list[str]) -> int:
        added = 0
        existing = {util.norm(mod.path) for mod in profile.mods}
        for entry in mods_mod.scan_entries(paths):
            if util.norm(entry.path) in existing:
                continue
            profile.mods.append(entry)
            existing.add(util.norm(entry.path))
            added += 1
        if added:
            self.save()
        return added

    def scan_folder(self, profile: Profile, root: str) -> mods_mod.ScanResult:
        found = mods_mod.scan_mod_folders(root)
        result = mods_mod.ScanResult()
        existing = {util.norm(mod.path) for mod in profile.mods}
        for entry in mods_mod.scan_entries(found):
            if util.norm(entry.path) in existing:
                continue
            result.found.append(entry)
        result.skipped = [path for path in found if path not in {entry.path for entry in result.found}]
        return result

    def install_archive(self, profile: Profile, archive: str, *, progress=None):
        outcome = mods_mod.install_archive(
            self.app, profile.id, archive, store_override=profile.mod_storage_path, progress=progress
        )
        profile.mods.append(outcome.mod)
        self.save()
        return outcome

    def remove_mod(self, profile: Profile, mod_id: str, *, delete_files: bool = False) -> None:
        mod = profile.mod_by_id(mod_id)
        if mod is None:
            return
        if delete_files and util.is_inside(mod.path, self.app.mod_storage_root):
            mods_mod.remove_installed_mod(self.app, mod)
        profile.mods.remove(mod)
        self.save()

    def report(self, profile: Profile, *, plan=None) -> str:
        plan = plan or self.plan_for(profile)
        info = engine_mod.find_engine(profile)
        report = preflight.run(profile, self.app, plan=plan, engine=info, workspace=self.workspace_for(profile))
        stats = plan.stats()
        text = diagnostics.build_report_text(
            profile_name=profile.name,
            engine_summary=report.engine_summary,
            executable=report.engine_executable,
            root_path=report.root_path,
            overlay_summary=(
                f"{report.overlay_summary}; файлов {stats.total_files}, пересечений {stats.overlapping_files}, "
                f"объём {util.human_size(stats.total_bytes)}"
            ),
            preflight_text=report.to_text(),
            extra_notes=[
                f"тип игры: {xray.describe_game_id(xray.detect_game_id(profile.game_path, declared=profile.game_id))}",
                f"backend: {profile.backend}",
                f"игровое время: {profile.playtime_display}",
                f"каталог модов: {self.app.mod_storage(profile.id, profile.mod_storage_path)}",
            ],
        )
        return diagnostics.write_report(self.app, util.short_hash(profile.name, 8), text)

    # ------------------------------------------------------------------ misc
    def gc_report(self) -> dict[str, int]:
        """Small helper for the Status window: what the launcher occupies on disk."""
        files = 0
        total = 0
        for root in (self.app.profiles_root, self.app.mod_storage_root):
            if not os.path.isdir(root):
                continue
            count, size = mods_mod.directory_usage(root)
            files += count
            total += size
        return {"files": files, "bytes": total}

    def touch_selected(self, profile_id: str) -> None:
        if self.settings and self.settings.selected_profile_id != profile_id:
            self.settings.selected_profile_id = profile_id
            self.save()

    def export_profile(self, profile: Profile, target: str) -> str:
        return self.store.export_profile(profile.to_dict(), target)

    def import_profile(self, source: str) -> Profile:
        assert self.settings is not None
        profile = self.store.import_profile(source, self.settings)
        self.save()
        return profile


def default_service(
    *, portable_root: str = "", config_dir: str = "", data_dir: str = "", cache_dir: str = ""
) -> CordonService:
    if portable_root:
        app = AppPaths.in_directory(portable_root)
    else:
        app = AppPaths.discover(
            config_dir=config_dir or None, data_dir=data_dir or None, cache_dir=cache_dir or None
        )
    service = CordonService(app)
    service.load()
    return service


def human_now() -> str:
    return time.strftime("%d.%m.%Y %H:%M")
