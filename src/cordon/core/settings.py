"""Settings persistence with the same safety net as upstream CORDON.

Order of operations when saving (identical in spirit to the Windows launcher):

1. serialise a full snapshot;
2. write it to a temporary file and ``fsync`` it;
3. atomically replace ``settings.json``;
4. keep the previous revision as ``settings.backup.json``.

When the primary file is unreadable it is *copied* into ``recovery/`` with a timestamp and
only then replaced by the backup.  A temporarily missing or locked file is never treated as
damage: the launcher reports the problem and keeps the user's data untouched.
"""

from __future__ import annotations

import fcntl
import json
import os
import shutil
import time
from dataclasses import dataclass

from . import util
from .errors import ConfigError
from .models import SCHEMA_VERSION, LauncherSettings
from .paths import AppPaths


@dataclass(slots=True)
class LoadResult:
    settings: LauncherSettings
    notices: list[str]
    recovered_from_backup: bool = False
    created_new: bool = False


class SettingsStore:
    """Read/write access to ``settings.json`` guarded by an advisory lock."""

    def __init__(self, app: AppPaths, logger=None) -> None:
        self.app = app
        self.logger = logger
        self._lock_handle = None

    # ------------------------------------------------------------------ locking
    def acquire_lock(self, *, blocking: bool = False) -> bool:
        """Prevent a second launcher instance from writing the same settings file."""
        util.ensure_dir(self.app.config_dir)
        path = os.path.join(self.app.config_dir, "settings.lock")
        handle = open(path, "a+", encoding="utf-8")
        flags = fcntl.LOCK_EX if blocking else fcntl.LOCK_EX | fcntl.LOCK_NB
        try:
            fcntl.flock(handle.fileno(), flags)
        except OSError:
            handle.close()
            return False
        handle.seek(0)
        handle.truncate()
        handle.write(f"pid={os.getpid()} started={time.time():.0f}\n")
        handle.flush()
        self._lock_handle = handle
        return True

    def release_lock(self) -> None:
        if self._lock_handle is not None:
            try:
                fcntl.flock(self._lock_handle.fileno(), fcntl.LOCK_UN)
                self._lock_handle.close()
            except OSError:  # pragma: no cover
                pass
            self._lock_handle = None

    # ------------------------------------------------------------------ loading
    def load(self) -> LoadResult:
        notices: list[str] = []
        primary = self.app.settings_file
        backup = self.app.settings_backup_file

        if not os.path.exists(primary):
            if os.path.exists(backup):
                notices.append("Основной файл настроек отсутствовал — загружена резервная копия.")
                return LoadResult(self._load_file(backup), notices, recovered_from_backup=True)
            return LoadResult(LauncherSettings(), notices, created_new=True)

        try:
            payload = self._read_json(primary)
        except ConfigError as exc:
            notices.append(f"{exc}")
            preserved = self._preserve(primary)
            if os.path.exists(backup):
                try:
                    settings = self._load_file(backup)
                except ConfigError as backup_exc:
                    notices.append(f"Резервная копия тоже повреждена: {backup_exc}")
                else:
                    notices.append(
                        "Настройки восстановлены из резервной копии. "
                        f"Повреждённый файл сохранён: {preserved}"
                    )
                    return LoadResult(settings, notices, recovered_from_backup=True)
            self._preserve(backup)
            notices.append(
                "Создана новая конфигурация, исходные файлы сохранены в каталоге recovery."
            )
            return LoadResult(LauncherSettings(), notices, created_new=True)
        except OSError as exc:
            raise ConfigError(
                f"Не удалось прочитать файл настроек {primary}: {exc}. "
                "CORDON-LINUX не изменял его; закройте программу, которая держит файл, и повторите."
            ) from exc

        settings = LauncherSettings.from_dict(payload)
        if settings.version > SCHEMA_VERSION:
            notices.append(
                f"Файл настроек новее этой версии лаунчера (схема {settings.version} > {SCHEMA_VERSION}). "
                "Неизвестные поля будут сохранены, но проверьте список профилей."
            )
        if settings.version < SCHEMA_VERSION:
            notices.append(f"Схема настроек обновлена: {settings.version} → {SCHEMA_VERSION}.")
        return LoadResult(settings, notices)

    def _load_file(self, path: str) -> LauncherSettings:
        return LauncherSettings.from_dict(self._read_json(path))

    @staticmethod
    def _read_json(path: str) -> dict:
        try:
            with open(path, encoding="utf-8") as handle:
                payload = json.load(handle)
        except json.JSONDecodeError as exc:
            raise ConfigError(f"Файл настроек повреждён ({path}): {exc}") from exc
        if not isinstance(payload, dict):
            raise ConfigError(f"Файл настроек имеет неверный формат ({path}): ожидался JSON-объект.")
        return payload

    def _preserve(self, path: str) -> str:
        """Copy a damaged file into ``recovery/`` and return the new location."""
        util.ensure_dir(self.app.recovery_dir)
        stamp = time.strftime("%Y%m%d-%H%M%S")
        target = os.path.join(self.app.recovery_dir, f"{os.path.basename(path)}.{stamp}")
        counter = 1
        while os.path.exists(target):
            target = os.path.join(self.app.recovery_dir, f"{os.path.basename(path)}.{stamp}-{counter}")
            counter += 1
        try:
            if os.path.exists(path):
                shutil.copy2(path, target)
        except OSError:  # pragma: no cover - best effort
            return path
        return target

    # ------------------------------------------------------------------ saving
    def save(self, settings: LauncherSettings) -> None:
        settings.normalize()
        payload = settings.to_dict()
        payload["version"] = SCHEMA_VERSION
        primary = self.app.settings_file
        backup = self.app.settings_backup_file
        util.ensure_dir(self.app.config_dir)
        if os.path.exists(primary):
            try:
                shutil.copy2(primary, backup)
            except OSError as exc:  # pragma: no cover - disk full etc.
                if self.logger:
                    self.logger.warning("не удалось обновить резервную копию настроек: %s", exc)
        util.write_json_atomic(primary, payload)
        if self.logger:
            self.logger.debug("настройки сохранены: %s", primary)

    # ------------------------------------------------------------------ helpers
    def export_profile(self, profile_payload: dict, target: str) -> str:
        payload = {"application": "CORDON-LINUX", "version": SCHEMA_VERSION, "profile": profile_payload}
        util.write_json_atomic(target, payload)
        return util.norm(target)

    def import_profile(self, source: str, settings: LauncherSettings, *, rename: bool = False):
        from .models import Profile

        try:
            payload = self._read_json(source)
        except (ConfigError, OSError) as exc:
            raise ConfigError(f"Не удалось прочитать профиль {source}: {exc}") from exc
        raw = payload.get("profile") if isinstance(payload.get("profile"), dict) else payload
        profile = Profile.from_dict(raw)
        profile.id = util.new_id()
        if rename or any(item.name == profile.name for item in settings.profiles):
            profile.name = f"{profile.name} (импорт)"
        profile.root_path = ""
        profile.normalize()
        settings.profiles.append(profile)
        settings.selected_profile_id = profile.id
        return profile
