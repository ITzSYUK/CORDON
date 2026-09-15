"""Profile root preparation: the Linux equivalent of the upstream "Workspace".

A profile root looks like this::

    ~/.local/share/cordon/profiles/profile-<id>/
      .cordon-profile            # marker: this directory belongs to profile <id>
      fsgame.ltx                 # generated, makes this directory the engine's $fs_root$
      gamedata/                  # merged view: engine data → base game → mods (symlinks)
      db/  patches/  *.db*       # root level entries linked from the base game
      _appdata_/                 # per-profile user data root ($app_data_root$)
      build-manifest.json        # layer list, signature, generated links
      .cordon-overlay/           # upper/work dirs when the FUSE backend is used

Because the engine is started with ``-fsltx <root>/fsgame.ltx``, the *directory of that file*
becomes ``$fs_root$`` (``CLocatorAPI::setup_fs_path``), and the game keeps reading ``gamedata``,
``patches`` and the archives exactly where it expects them - without a single byte being
written into the game installation.
"""

from __future__ import annotations

import json
import os
import shutil
import time
from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import datetime, timezone

from . import fsgame as fsgame_mod
from . import mounts, util, xray
from .errors import OverlayError, SafetyError
from .layers import WRITABLE_GAME_PATHS, LayerPlan
from .models import BACKEND_DIRECT, BACKEND_FUSE, BACKEND_LINK, Profile
from .paths import (
    AppPaths,
    PROFILE_MARKER,
    PROFILE_ROOT_MARKER,
    guard_owned_directory,
    read_marker,
    write_marker,
)

MANIFEST_NAME = "build-manifest.json"
MANIFEST_SCHEMA = 1
APPDATA_DIR_NAME = "_appdata_"
HELPER_DIR_NAME = ".cordon-overlay"

APPDATA_SUBDIRS = ("logs", "savedgames", "screenshots", "shaders_cache", "dumps")

ROOT_LINK_SKIP = {
    xray.GAME_DATA_DIR,
    xray.FSGAME_NAME,
    MANIFEST_NAME,
    APPDATA_DIR_NAME,
    "appdata",
    "userdata",
    PROFILE_MARKER,
    PROFILE_ROOT_MARKER,
}


@dataclass(slots=True)
class OverlayResult:
    root: str
    rebuilt: bool
    links: int = 0
    root_links: int = 0
    copies: list[str] = field(default_factory=list)
    seeds: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    duration: float = 0.0
    mount_command: list[str] = field(default_factory=list)

    @property
    def summary(self) -> str:
        state = "пересобран" if self.rebuilt else "актуален"
        return (
            f"{state}: ссылок {self.links}, корневых ссылок {self.root_links}, "
            f"локальных копий {len(self.copies)}"
        )


class ProfileWorkspace:
    """Owns one profile root and everything the launcher generates inside it."""

    def __init__(self, app: AppPaths, profile: Profile, logger=None) -> None:
        self.app = app
        self.profile = profile
        self.logger = logger

    # ------------------------------------------------------------------ paths
    @property
    def root(self) -> str:
        return self.app.profile_root(self.profile.id, self.profile.root_path)

    @property
    def appdata(self) -> str:
        return os.path.join(self.root, APPDATA_DIR_NAME)

    @property
    def game_data(self) -> str:
        return os.path.join(self.root, xray.GAME_DATA_DIR)

    @property
    def fsgame_path(self) -> str:
        return fsgame_mod.profile_fsgame_path(self.root)

    @property
    def manifest_path(self) -> str:
        return os.path.join(self.root, MANIFEST_NAME)

    @property
    def helper_dir(self) -> str:
        return os.path.join(self.root, HELPER_DIR_NAME)

    def log(self, level: str, message: str, *args) -> None:
        if self.logger:
            getattr(self.logger, level, self.logger.info)(message, *args)

    # ------------------------------------------------------------------ lifecycle
    def ensure_root(self) -> str:
        root = util.ensure_dir(self.root)
        write_marker(root, PROFILE_MARKER, f"profile={self.profile.id}\nname={self.profile.name}\n")
        util.ensure_dir(self.appdata)
        for name in APPDATA_SUBDIRS:
            util.ensure_dir(os.path.join(self.appdata, name))
        return root

    def is_initialised(self) -> bool:
        return read_marker(self.root, PROFILE_MARKER) is not None

    def remove_root(self) -> None:
        """Delete this profile root, but only after the marker check."""
        if os.path.exists(self.root):
            guard_owned_directory(self.app, self.root, marker=PROFILE_MARKER)
            shutil.rmtree(self.root)

    # ------------------------------------------------------------------ signature
    def build_signature(self, plan: LayerPlan, engine_executable: str = "") -> str:
        parts = [
            f"schema={MANIFEST_SCHEMA}",
            f"profile={self.profile.id}",
            f"kind={self.profile.kind}",
            f"backend={self.profile.backend}",
            f"game={util.norm(self.profile.game_path)}",
            f"engine={util.norm(engine_executable)}",
            f"fsgame={util.norm(self.profile.fsgame_source) if self.profile.fsgame_source else ''}",
            f"appdata={self.profile.appdata_mode}",
            f"overlay_path={int(self.profile.use_overlay_path)}",
            "excluded=" + ",".join(sorted(plan.excluded)),
        ]
        for layer in plan.layers:
            parts.append(f"layer[{layer.priority}]={layer.kind}:{layer.path}:{util.dir_signature(layer.path)}")
        if self.profile.is_standalone:
            parts.append(f"standalone={util.dir_signature(self.profile.game_path)}")
        return util.short_hash("\n".join(parts), 32)

    def load_manifest(self) -> dict:
        try:
            with open(self.manifest_path, encoding="utf-8") as handle:
                payload = json.load(handle)
        except (OSError, json.JSONDecodeError):
            return {}
        return payload if isinstance(payload, dict) else {}

    # ------------------------------------------------------------------ building
    def prepare(
        self,
        plan: LayerPlan,
        *,
        engine_executable: str = "",
        force: bool = False,
        progress: Callable[[str], None] | None = None,
    ) -> OverlayResult:
        started = time.monotonic()
        result = OverlayResult(root=self.ensure_root(), rebuilt=False)

        if self.profile.backend == BACKEND_DIRECT or self.profile.is_standalone:
            if not self.profile.is_standalone:
                self.write_fsgame(plan)
            self._prepare_appdata(plan, result)
            result.duration = time.monotonic() - started
            return result

        if self.profile.backend == BACKEND_FUSE:
            # fail before touching anything: otherwise a missing package would leave the
            # previous build half-deleted
            mounts.require_tool("fuse-overlayfs")
            if not mounts.layer_gamedata_dirs(plan):
                raise OverlayError(
                    "fuse-overlayfs: ни один слой не содержит каталог gamedata — "
                    "проверьте путь к игре и включённые моды"
                )

        signature = self.build_signature(plan, engine_executable)
        manifest = self.load_manifest()
        reuse = (
            not force
            and manifest.get("signature") == signature
            and manifest.get("backend") == self.profile.backend
            and self._artifacts_present(manifest)
        )

        if reuse:
            self.log("info", "оверлей не изменился (подпись %s), сборка пропущена", signature[:12])
            if progress:
                progress("Слои не изменились — сборка не требуется")
            if self.profile.backend == BACKEND_FUSE:
                self._mount_overlay(plan, result)
            result.links = int(manifest.get("counts", {}).get("links", 0))
            result.root_links = int(manifest.get("counts", {}).get("root_links", 0))
            result.copies = list(manifest.get("copies", []))
        else:
            if progress:
                progress("Очистка предыдущей сборки…")
            self._clean_generated(manifest)
            if progress:
                progress("Подготовка корневых ссылок…")
            result.root_links = self._link_root_entries(plan, manifest_root_links=[])
            if self.profile.backend == BACKEND_LINK:
                if progress:
                    progress("Сборка объединённого gamedata…")
                result.links, copies = self._build_link_overlay(plan, progress)
                result.copies = sorted(copies)
            elif self.profile.backend == BACKEND_FUSE:
                result.links = 0
                self._mount_overlay(plan, result)
            else:  # pragma: no cover - defensive
                raise OverlayError(f"неизвестный backend «{self.profile.backend}»")
            self._write_manifest(plan, signature, result, engine_executable)
            result.rebuilt = True

        self.write_fsgame(plan)
        self._prepare_appdata(plan, result)
        if self.profile.backend == BACKEND_LINK:
            self._reapply_aliases(result)
        result.duration = time.monotonic() - started
        return result

    def _reapply_aliases(self, result: OverlayResult) -> None:
        """Case-sensitivity aliases created by the audit survive every rebuild."""
        try:
            from . import audit as audit_mod

            created = audit_mod.apply_aliases(self.root, logger=self.logger)
        except Exception as exc:  # noqa: BLE001 - aliases are a convenience feature
            result.warnings.append(f"не удалось восстановить алиасы регистра: {exc}")
            return
        if created:
            result.warnings.append(f"восстановлено алиасов регистра: {created}")

    def _artifacts_present(self, manifest: dict) -> bool:
        if not os.path.isdir(self.root):
            return False
        if self.profile.backend == BACKEND_LINK and not os.path.isdir(self.game_data):
            return False
        if self.profile.backend == BACKEND_FUSE and not mounts.is_mounted(self.game_data):
            # a mount can legitimately be gone after a reboot: rebuild it lazily
            return False
        for entry in manifest.get("root_links", []):
            if not isinstance(entry, list | tuple) or not entry:
                continue
            link = os.path.join(self.root, entry[0])
            if not os.path.lexists(link):
                return False
        return os.path.exists(self.fsgame_path)

    # ------------------------------------------------------------------ root links
    def _root_link_targets(self, plan: LayerPlan) -> dict[str, str]:
        """Root level entries taken from the game/engine installations (archives, patches…)."""
        targets: dict[str, str] = {}
        for layer in plan.layers:
            if layer.kind not in ("game", "engine"):
                continue
            if not os.path.isdir(layer.path):
                continue
            for name in util.entry_names(layer.path):
                if name in ROOT_LINK_SKIP or name.startswith(".cordon"):
                    continue
                source = os.path.join(layer.path, name)
                if name == xray.GAME_DATA_DIR:
                    continue
                if os.path.isfile(os.path.join(source, "placeholder")):  # pragma: no cover
                    continue
                targets[name] = source
        return targets

    def _link_root_entries(self, plan: LayerPlan, *, manifest_root_links: list) -> int:
        created = 0
        for name, source in self._root_link_targets(plan).items():
            link = os.path.join(self.root, name)
            if os.path.lexists(link):
                continue
            self._symlink(source, link)
            created += 1
        # Mod-provided root entries (db/mods/*.db, patches/…, custom folders) win over the game.
        for relative, providers in plan.entries.items():
            if relative.startswith(f"{xray.GAME_DATA_DIR}/") or "/" not in relative:
                if not relative.startswith("db/") and "/" in relative:
                    continue
                if relative.startswith(f"{xray.GAME_DATA_DIR}/"):
                    continue
            if relative.split("/", 1)[0] in ROOT_LINK_SKIP:
                continue
            top = relative.split("/", 1)[0]
            if top in self._root_link_targets(plan):
                continue
            source = plan.winner_path(relative)
            if not source:
                continue
            link = os.path.join(self.root, relative)
            if os.path.lexists(link) and plan.winner(relative) is not None:
                provider = plan.winner(relative)
                if provider and provider.kind in ("game", "engine"):
                    continue
            if os.path.lexists(link) and os.path.isdir(link) and not os.path.islink(link):
                continue
            self._symlink(source, link)
            created += 1
        return created

    def _build_link_overlay(
        self, plan: LayerPlan, progress: Callable[[str], None] | None
    ) -> tuple[int, set[str]]:
        prefix = f"{xray.GAME_DATA_DIR}/"
        links = 0
        copies: set[str] = set()
        total = sum(1 for relative in plan.entries if relative.startswith(prefix))
        for index, (relative, providers) in enumerate(plan.entries.items()):
            if not relative.startswith(prefix):
                continue
            if progress and index % 5000 == 0:
                progress(f"Ссылки gamedata: {index}/{total}")
            source = plan.winner_path(relative)
            if not source:
                continue
            destination = os.path.join(self.root, *relative.split("/"))
            util.ensure_dir(os.path.dirname(destination))
            if relative in WRITABLE_GAME_PATHS:
                if os.path.lexists(destination):
                    os.unlink(destination)
                shutil.copy2(source, destination)
                copies.add(relative)
                continue
            self._symlink(source, destination)
            links += 1
        return links, copies

    def _mount_overlay(self, plan: LayerPlan, result: OverlayResult) -> None:
        overlay = mounts.mount_for_plan(plan, self.root)
        result.mount_command = overlay.command()
        if not overlay.lower_dirs:
            raise OverlayError(
                "режим fuse-overlayfs выбран, но ни один слой не содержит каталог gamedata "
                "(проверьте путь к игре и включённые моды)"
            )
        overlay.unmount(logger=self.logger)
        overlay.mount(logger=self.logger)

    def unmount(self) -> None:
        mounts.OverlayMount(
            mount_point=self.game_data,
            upper_dir=os.path.join(self.helper_dir, "upper"),
            work_dir=os.path.join(self.helper_dir, "work"),
            lower_dirs=[],
        ).unmount(logger=self.logger)

    # ------------------------------------------------------------------ appdata
    def _prepare_appdata(self, plan: LayerPlan, result: OverlayResult) -> None:
        util.ensure_dir(self.appdata)
        for name in APPDATA_SUBDIRS:
            util.ensure_dir(os.path.join(self.appdata, name))
        if self.profile.appdata_mode != "profile" or not self.profile.isolate_appdata:
            return
        seeds = self._appdata_seed_sources(plan)
        user_ltx_target = os.path.join(self.appdata, "user.ltx")
        if not os.path.exists(user_ltx_target):
            for _priority, source in seeds:
                candidate = os.path.join(source, "user.ltx")
                if os.path.isfile(candidate):
                    shutil.copy2(candidate, user_ltx_target)
                    result.seeds.append("user.ltx")
                    self.log("info", "начальный user.ltx взят из %s", candidate)
                    break
            else:
                self._write_default_user_ltx(user_ltx_target)
                result.seeds.append("user.ltx (значения по умолчанию)")
        cache_target = os.path.join(self.appdata, "shaders_cache")
        for _priority, source in seeds:
            cache_source = os.path.join(source, "shaders_cache")
            if not os.path.isdir(cache_source):
                continue
            for dirpath, _dirnames, filenames in util.iter_tree(cache_source):
                rel = os.path.relpath(dirpath, cache_source)
                for name in filenames:
                    destination = os.path.join(cache_target, rel, name) if rel != "." else os.path.join(cache_target, name)
                    if os.path.exists(destination):
                        continue
                    util.ensure_dir(os.path.dirname(destination))
                    try:
                        shutil.copy2(os.path.join(dirpath, name), destination)
                    except OSError as exc:  # pragma: no cover - disk full
                        result.warnings.append(f"не удалось скопировать кэш шейдеров: {exc}")
                        break
                    result.seeds.append(os.path.join("shaders_cache", rel, name) if rel != "." else f"shaders_cache/{name}")

    def _appdata_seed_sources(self, plan: LayerPlan) -> list[tuple[int, str]]:
        sources: list[tuple[int, str]] = []
        for layer in sorted(plan.layers, key=lambda item: item.priority, reverse=True):
            for name in ("appdata", APPDATA_DIR_NAME, "userdata"):
                candidate = os.path.join(layer.path, name)
                if os.path.isdir(candidate):
                    sources.append((layer.priority, candidate))
        return sources

    @staticmethod
    def _write_default_user_ltx(path: str) -> None:
        util.write_text_atomic(
            path,
            "; CORDON-LINUX: файл создан лаунчером при первом запуске профиля.\n"
            "; Настройки графики, звука и управления записываются сюда движком.\n",
        )

    # ------------------------------------------------------------------ fsgame
    def fsgame_document(self, plan: LayerPlan) -> fsgame_mod.FsGameDocument:
        """Source document for the generated configuration (highest priority first)."""
        candidates: list[str] = []
        if self.profile.fsgame_source:
            candidates.append(util.norm(self.profile.fsgame_source))
        for layer in sorted(plan.layers, key=lambda item: item.priority, reverse=True):
            path = os.path.join(layer.path, xray.FSGAME_NAME)
            if os.path.isfile(path):
                candidates.append(path)
        for candidate in candidates:
            try:
                return fsgame_mod.FsGameDocument.load(candidate)
            except Exception as exc:  # noqa: BLE001 - keep looking on damaged files
                self.log("warning", "fsgame.ltx %s не прочитан: %s", candidate, exc)
        self.log("info", "fsgame.ltx не найден в слоях — используется встроенный шаблон")
        return fsgame_mod.FsGameDocument.default()

    def write_fsgame(self, plan: LayerPlan) -> str:
        """Write the prepared ``-fsltx`` file of this profile and return its path."""
        source = self.fsgame_document(plan)
        shared_root = ""
        if self.profile.appdata_mode == "shared" or not self.profile.isolate_appdata:
            # The reference point is the directory the chosen fsgame.ltx belongs to, exactly like
            # the upstream launcher resolves ``$app_data_root$`` from the base game's own config.
            if source.source and os.path.isfile(source.source):
                reference = os.path.dirname(util.norm(source.source))
            else:
                reference = util.norm(self.profile.engine_data_path or self.profile.game_path)
            shared_root = source.app_data_root(reference) or os.path.join(reference, APPDATA_DIR_NAME)
            util.ensure_dir(shared_root)
        document = fsgame_mod.prepare_profile_fsgame(
            source,
            appdata_root=shared_root or None,
            appdata_add=f"{APPDATA_DIR_NAME}\\",
        )
        return document.write(self.fsgame_path)

    # ------------------------------------------------------------------ manifest
    def _write_manifest(self, plan: LayerPlan, signature: str, result: OverlayResult, engine_executable: str) -> None:
        root_links = [
            [name, os.path.realpath(os.path.join(self.root, name))]
            for name in util.entry_names(self.root)
            if os.path.islink(os.path.join(self.root, name))
        ]
        payload = {
            "schema": MANIFEST_SCHEMA,
            "profile_id": self.profile.id,
            "profile_name": self.profile.name,
            "built_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
            "signature": signature,
            "backend": self.profile.backend,
            "engine_executable": engine_executable,
            "layers": [
                {
                    "kind": layer.kind,
                    "name": layer.name,
                    "path": layer.path,
                    "priority": layer.priority,
                    "files": plan.layer_counts.get(layer.priority, 0),
                }
                for layer in plan.layers
            ],
            "counts": {
                "links": result.links,
                "root_links": len(root_links),
                "conflicts": sum(1 for providers in plan.entries.values() if len(providers) > 1),
                "entries": len(plan.entries),
            },
            "root_links": root_links,
            "copies": result.copies,
            "seeds": result.seeds,
            "excluded": sorted(plan.excluded),
            "game_data": self.game_data,
            "appdata": self.appdata,
        }
        util.write_json_atomic(self.manifest_path, payload)
        self.log("info", "манифест сборки записан: %s", self.manifest_path)

    # ------------------------------------------------------------------ verifying
    def verify(self) -> list[str]:
        """Structural checks of an existing overlay (used by the Status window)."""
        problems: list[str] = []
        if not os.path.isdir(self.root):
            return ["каталог профиля ещё не создан"]
        if not self.is_initialised():
            problems.append(f"отсутствует маркер {PROFILE_MARKER} — каталог не создан лаунчером")
        if not os.path.isfile(self.fsgame_path):
            problems.append("fsgame.ltx профиля отсутствует — запустите подготовку заново")
        if not os.path.isdir(self.appdata):
            problems.append("каталог _appdata_ отсутствует")
        manifest = self.load_manifest()
        if not manifest:
            problems.append("build-manifest.json отсутствует — оверлей ещё не собирался")
        if self.profile.backend == BACKEND_LINK:
            gamedata = self.game_data
            if not os.path.isdir(gamedata):
                problems.append("каталог gamedata отсутствует")
            else:
                broken = 0
                checked = 0
                for dirpath, _dirnames, filenames in util.iter_tree(gamedata):
                    for name in filenames:
                        entry = os.path.join(dirpath, name)
                        checked += 1
                        if not os.path.exists(entry):
                            broken += 1
                            if broken <= 5:
                                problems.append(f"битая ссылка: {os.path.relpath(entry, self.root)}")
                if broken > 5:
                    problems.append(f"…всего битых ссылок: {broken}")
                if checked == 0:
                    problems.append("gamedata пуст — проверьте путь к базовой игре")
        if self.profile.backend == BACKEND_FUSE and not mounts.is_mounted(self.game_data):
            problems.append("fuse-overlayfs не смонтирован — запустите подготовку профиля")
        for name, target in ((entry[0], entry[1]) for entry in manifest.get("root_links", []) if len(entry) >= 2):
            link = os.path.join(self.root, name)
            if not os.path.lexists(link):
                problems.append(f"корневая ссылка пропала: {name}")
            elif not os.path.exists(target):
                problems.append(f"источник корневой ссылки недоступен: {target}")
        return problems

    # ------------------------------------------------------------------ cleanup
    def _clean_generated(self, manifest: dict) -> None:
        if self.profile.backend == BACKEND_FUSE or mounts.is_mounted(self.game_data):
            try:
                self.unmount()
            except Exception as exc:  # noqa: BLE001 - cleanup must not abort the rebuild
                self.log("warning", "не удалось отмонтировать оверлей: %s", exc)

        generated: list[str] = []
        for entry in manifest.get("root_links", []):
            if isinstance(entry, list | tuple) and entry:
                generated.append(str(entry[0]))
        generated.extend(entry for entry in util.entry_names(self.root) if os.path.islink(os.path.join(self.root, entry)))

        for name in dict.fromkeys(generated):
            path = os.path.join(self.root, name)
            if os.path.islink(path):
                try:
                    os.unlink(path)
                except OSError as exc:  # pragma: no cover
                    self.log("warning", "не удалось удалить ссылку %s: %s", path, exc)
            elif os.path.isdir(path) and name in ("db", "patches"):
                self._remove_empty_dirs(path)

        for directory in (self.game_data, os.path.join(self.root, "db", "mods")):
            if os.path.islink(directory):
                os.unlink(directory)
            elif os.path.isdir(directory):
                shutil.rmtree(directory, ignore_errors=False)
        db_root = os.path.join(self.root, "db")
        if os.path.isdir(db_root) and not os.listdir(db_root):
            os.rmdir(db_root)

    @staticmethod
    def _remove_empty_dirs(root: str) -> None:
        for dirpath, dirnames, filenames in os.walk(root, topdown=False):
            if dirnames or filenames:
                continue
            if os.path.islink(dirpath):
                continue
            try:
                os.rmdir(dirpath)
            except OSError:
                pass

    # ------------------------------------------------------------------ helpers
    @staticmethod
    def _symlink(source: str, destination: str) -> None:
        if os.path.lexists(destination):
            if os.path.islink(destination) and os.readlink(destination) == source:
                return
            if os.path.isdir(destination) and not os.path.islink(destination):
                raise SafetyError(f"каталог {destination} не является ссылкой — сборка остановлена")
            os.unlink(destination)
        util.ensure_dir(os.path.dirname(destination))
        os.symlink(source, destination)


def build_plan_for_profile(
    profile: Profile,
    app: AppPaths,
    *,
    engine_data_path: str = "",
    progress: Callable[[str], None] | None = None,
) -> LayerPlan:
    from .layers import build_plan

    plan = build_plan(profile, engine_data_path=engine_data_path)
    plan.index(progress)
    return plan
