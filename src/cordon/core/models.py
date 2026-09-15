"""Persisted and computed data models.

Field names intentionally mirror the upstream CORDON ``ModProfile`` (``Mods``, ``WorkspacePath``,
``LaunchBackendKind``, ``ExecutableRelativePath``, ``FsgameSourcePath``, ``TotalPlaytimeSeconds``,
``LastPlayedAt``, ``Mo2OverwritePath``) so a fork of the Windows launcher stays recognisable.
The Linux port adds explicit backend names (``link``, ``fuse-overlayfs``, ``direct``) and the OpenXRay-specific knobs (``game_id``, ``engine_flags``, ``overlay_path``).
"""

from __future__ import annotations

import time
from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from typing import Any

from . import util

SCHEMA_VERSION = 1

PROFILE_KIND_MODS = "mods"
PROFILE_KIND_STANDALONE = "standalone"

BACKEND_LINK = "link"
BACKEND_FUSE = "fuse-overlayfs"
BACKEND_DIRECT = "direct"

BACKENDS = (BACKEND_LINK, BACKEND_FUSE, BACKEND_DIRECT)

GAME_IDS = ("auto", "cop", "cs", "coc", "custom")

#: OpenXRay command line switches that are safe to expose as checkboxes in the GUI.
ENGINE_FLAG_LABELS: dict[str, tuple[str, str]] = {
    "-nosplash": ("Без заставки", "Не показывать сплэш при старте (OpenXRay -nosplash)."),
    "-nolog": ("Не писать лог", "Отключить запись xray_*.log в каталог данных профиля."),
    "-i": ("Не захватывать ввод", "Не захватывать клавиатуру и мышь (удобно для отладки)."),
    "-no_gamepad": ("Без геймпада", "Не инициализировать SDL game controller API."),
    "-dedicated": ("Выделенный сервер", "Запуск в режиме dedicated server (без графики)."),
    "-gl": ("OpenGL", "Принудительно использовать OpenGL-рендерер."),
    "-savescreenshots": ("Скриншоты в файл", "Сохранять скриншоты в каталог профиля."),
    "-silent_error_mode": ("Тихий режим ошибок", "Не показывать окно с ошибкой, писать только в лог."),
}


@dataclass(slots=True)
class ModEntry:
    """One mod folder inside a profile; later entries (lower in the UI) win conflicts."""

    id: str
    name: str
    path: str
    enabled: bool = True
    group: str = ""
    source: str = "manual"
    installed_at: float | None = None
    notes: str = ""

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, payload: dict[str, Any]) -> ModEntry:
        return cls(
            id=str(payload.get("id") or util.new_id()),
            name=str(payload.get("name") or "Мод"),
            path=str(payload.get("path") or ""),
            enabled=bool(payload.get("enabled", True)),
            group=str(payload.get("group") or ""),
            source=str(payload.get("source") or "manual"),
            installed_at=payload.get("installed_at"),
            notes=str(payload.get("notes") or ""),
        )


@dataclass(slots=True)
class Profile:
    id: str
    name: str
    description: str = ""
    kind: str = PROFILE_KIND_MODS
    game_path: str = ""
    engine_path: str = ""
    engine_data_path: str = ""
    game_id: str = "auto"
    mods: list[ModEntry] = field(default_factory=list)
    backend: str = BACKEND_LINK
    executable_relative: str = "bin/xr_3da"
    executable_source: str = ""
    fsgame_source: str = ""
    launch_arguments: str = ""
    engine_flags: list[str] = field(default_factory=list)
    appdata_mode: str = "profile"  # profile | shared
    root_path: str = ""
    mod_storage_path: str = ""
    excluded_paths: list[str] = field(default_factory=list)
    mo2_overwrite_path: str = ""
    total_playtime_seconds: float = 0.0
    last_played_at: str | None = None
    created_at: str | None = None
    use_overlay_path: bool = True
    isolate_appdata: bool = True

    # ------------------------------------------------------------------ helpers
    @property
    def is_standalone(self) -> bool:
        return self.kind == PROFILE_KIND_STANDALONE

    @property
    def enabled_mods(self) -> list[ModEntry]:
        return [mod for mod in self.mods if mod.enabled]

    def mod_by_id(self, mod_id: str) -> ModEntry | None:
        return next((mod for mod in self.mods if mod.id == mod_id), None)

    def mark_played(self, seconds: float) -> None:
        self.total_playtime_seconds = max(0.0, self.total_playtime_seconds + seconds)
        self.last_played_at = datetime.now(timezone.utc).isoformat(timespec="seconds")

    @property
    def playtime_display(self) -> str:
        return util.human_duration(self.total_playtime_seconds) if self.total_playtime_seconds else "—"

    @property
    def last_played_display(self) -> str:
        if not self.last_played_at:
            return "—"
        try:
            stamp = datetime.fromisoformat(self.last_played_at)
        except ValueError:  # pragma: no cover
            return "—"
        return stamp.astimezone().strftime("%d.%m.%Y %H:%M")

    def to_dict(self) -> dict[str, Any]:
        payload = asdict(self)
        payload["mods"] = [mod.to_dict() for mod in self.mods]
        return payload

    @classmethod
    def from_dict(cls, payload: dict[str, Any]) -> Profile:
        raw_mods = payload.get("mods") or []
        profile = cls(
            id=str(payload.get("id") or util.new_id()),
            name=str(payload.get("name") or "Новый профиль"),
            description=str(payload.get("description") or ""),
            kind=str(payload.get("kind") or PROFILE_KIND_MODS),
            game_path=str(payload.get("game_path") or ""),
            engine_path=str(payload.get("engine_path") or ""),
            engine_data_path=str(payload.get("engine_data_path") or ""),
            game_id=str(payload.get("game_id") or "auto"),
            mods=[ModEntry.from_dict(item) for item in raw_mods if isinstance(item, dict)],
            backend=str(payload.get("backend") or BACKEND_LINK),
            executable_relative=str(payload.get("executable_relative") or "bin/xr_3da"),
            executable_source=str(payload.get("executable_source") or ""),
            fsgame_source=str(payload.get("fsgame_source") or ""),
            launch_arguments=str(payload.get("launch_arguments") or ""),
            engine_flags=[str(flag) for flag in (payload.get("engine_flags") or [])],
            appdata_mode=str(payload.get("appdata_mode") or "profile"),
            root_path=str(payload.get("root_path") or ""),
            mod_storage_path=str(payload.get("mod_storage_path") or ""),
            excluded_paths=[util.to_posix(p) for p in (payload.get("excluded_paths") or [])],
            mo2_overwrite_path=str(payload.get("mo2_overwrite_path") or ""),
            total_playtime_seconds=float(payload.get("total_playtime_seconds") or 0.0),
            last_played_at=payload.get("last_played_at"),
            created_at=payload.get("created_at"),
            use_overlay_path=bool(payload.get("use_overlay_path", True)),
            isolate_appdata=bool(payload.get("isolate_appdata", True)),
        )
        profile.normalize()
        return profile

    def normalize(self) -> None:
        """Repair values that would break the layer plan or the file system."""
        self.mods = [mod for mod in self.mods if mod.path]
        seen: set[str] = set()
        for mod in self.mods:
            if not mod.id or mod.id in seen:
                mod.id = util.new_id()
            seen.add(mod.id)
            mod.path = util.norm(mod.path)
            if mod.installed_at is None:
                mod.installed_at = time.time()
        if self.backend not in BACKENDS:
            self.backend = BACKEND_LINK
        if self.kind not in (PROFILE_KIND_MODS, PROFILE_KIND_STANDALONE):
            self.kind = PROFILE_KIND_MODS
        if self.game_id not in GAME_IDS:
            self.game_id = "auto"
        self.excluded_paths = util.unique(self.excluded_paths)
        if not self.created_at:
            self.created_at = datetime.now(timezone.utc).isoformat(timespec="seconds")


@dataclass(slots=True)
class LauncherSettings:
    version: int = SCHEMA_VERSION
    profiles: list[Profile] = field(default_factory=list)
    selected_profile_id: str = ""
    theme: str = "pda"
    language: str = "ru"
    workspace_root: str = ""
    mod_storage_root: str = ""
    discord_presence: bool = False
    discord_client_id: str = "1394540039864053760"
    check_updates: bool = True
    keep_launcher_log_lines: int = 4000
    last_fullscreen: bool = False
    window_geometry: str = ""

    def to_dict(self) -> dict[str, Any]:
        payload = asdict(self)
        payload["profiles"] = [profile.to_dict() for profile in self.profiles]
        return payload

    @classmethod
    def from_dict(cls, payload: dict[str, Any]) -> LauncherSettings:
        settings = cls(
            version=int(payload.get("version") or SCHEMA_VERSION),
            profiles=[Profile.from_dict(item) for item in (payload.get("profiles") or []) if isinstance(item, dict)],
            selected_profile_id=str(payload.get("selected_profile_id") or ""),
            theme=str(payload.get("theme") or "pda"),
            language=str(payload.get("language") or "ru"),
            workspace_root=str(payload.get("workspace_root") or ""),
            mod_storage_root=str(payload.get("mod_storage_root") or ""),
            discord_presence=bool(payload.get("discord_presence", False)),
            discord_client_id=str(payload.get("discord_client_id") or cls.discord_client_id),
            check_updates=bool(payload.get("check_updates", True)),
            keep_launcher_log_lines=int(payload.get("keep_launcher_log_lines") or 4000),
            last_fullscreen=bool(payload.get("last_fullscreen", False)),
            window_geometry=str(payload.get("window_geometry") or ""),
        )
        settings.normalize()
        return settings

    def normalize(self) -> None:
        by_id: dict[str, Profile] = {}
        for profile in self.profiles:
            profile.normalize()
            if profile.id in by_id:
                profile.id = util.new_id()
            by_id[profile.id] = profile
        self.profiles = list(by_id.values())
        if self.selected_profile_id not in by_id:
            self.selected_profile_id = self.profiles[0].id if self.profiles else ""
        if self.theme not in ("pda", "classic"):
            self.theme = "pda"

    def profile(self, profile_id: str) -> Profile | None:
        return next((item for item in self.profiles if item.id == profile_id), None)

    @property
    def selected_profile(self) -> Profile | None:
        return self.profile(self.selected_profile_id)


@dataclass(slots=True)
class LayerSource:
    """One layer of the overlay: the engine data, the base game, a mod or profile data."""

    kind: str  # engine | game | mod | appdata | overwrite
    name: str
    path: str
    priority: int
    mod_id: str = ""

    def describe(self) -> str:
        return f"[{self.priority:>3}] {self.kind:<8} {self.name} → {self.path}"


@dataclass(slots=True)
class ConflictingFile:
    relative: str
    winner: LayerSource
    losers: list[LayerSource]
    excluded: bool = False


@dataclass(slots=True)
class LayeredStats:
    total_files: int = 0
    overlapping_files: int = 0
    total_bytes: int = 0
    per_layer: dict[str, int] = field(default_factory=dict)
    extensions: dict[str, int] = field(default_factory=dict)
    conflicts: list[ConflictingFile] = field(default_factory=list)
    shader_textures: int = 0


@dataclass(slots=True)
class LaunchPlan:
    """Everything needed to start the game; also the payload of ``cordon launch --dry-run``."""

    profile_id: str
    executable: str
    argv: list[str]
    cwd: str
    env: dict[str, str]
    fsgame_path: str = ""
    root_path: str = ""
    appdata_path: str = ""
    backend: str = BACKEND_LINK
    notes: list[str] = field(default_factory=list)

    @property
    def command_line(self) -> str:
        return " ".join(_shlex_quote(part) for part in self.argv)


def _shlex_quote(part: str) -> str:
    if part and all(ch.isalnum() or ch in "-_./=:+,@%" for ch in part):
        return part
    return "'" + part.replace("'", "'\\''") + "'"
