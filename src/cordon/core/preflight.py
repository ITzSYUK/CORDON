"""Pre-flight validation: exactly the same checks feed the Status window, the CLI ``doctor``
command and the quiet check performed right before a launch.

Errors block the launch, warnings describe unusual (but workable) setups.
"""

from __future__ import annotations

import os
import shutil
from dataclasses import dataclass, field

from . import audit, elf, engine as engine_mod, mounts, util, xray
from .layers import LayerPlan
from .models import BACKEND_FUSE, BACKEND_LINK, Profile
from .overlay import ProfileWorkspace
from .paths import AppPaths

LEVEL_ERROR = "error"
LEVEL_WARNING = "warning"
LEVEL_INFO = "info"
LEVEL_OK = "ok"

MIN_FREE_BYTES = 2 * 1024 * 1024 * 1024

#: Switches accepted by the engine (from ``misc/linux/bash-completion/completions/xr_3da``).
KNOWN_ENGINE_KEYS = {
    "-_g", "-auto_load_arch", "-break_on_assert", "-bug", "-build", "-cache", "-dedicated", "-demomode",
    "-depth16", "-designer", "-disasm", "-draw_borders", "-dump_bindings", "-dump_traffic", "-ebuild",
    "-force_flushlog", "-fsltx", "-game_designer", "-gl", "-gloss", "-gpu_nopure", "-gpu_ref", "-gpu_sw",
    "-ignore_save_incompatibility", "-keep_lua", "-lack_of_shaders", "-list_thm", "-load", "-ltx",
    "-lua_studio", "-luadumpstate", "-mblur", "-mt_cdb", "-nes_texture_storing", "-netsim", "-no_hom",
    "-no_occq", "-no_staging", "-noaref", "-nocolormap", "-nodf24", "-nodistort", "-nojit", "-nolog",
    "-nonvs", "-noprefetch", "-noshadows", "-nosplash", "-overlaypath", "-psp", "-savescreenshots",
    "-show_error_window", "-silent_error_mode", "-sjitter", "-skinw", "-splashnotop", "-ss_png",
    "-sunfilter", "-svcfg", "-tsh", "-tune", "-vtf", "-weather", "-xclsx",
    "-cs", "-shoc", "-soc", "-i", "-no_gamepad", "-start", "-wf", "-nointro",
}


@dataclass(slots=True)
class Check:
    level: str
    title: str
    detail: str = ""
    hint: str = ""

    @property
    def badge(self) -> str:
        return {"error": "ОШИБКА", "warning": "ВНИМАНИЕ", "info": "ИНФО", "ok": "ОК"}[self.level]


@dataclass(slots=True)
class PreflightReport:
    profile_id: str = ""
    profile_name: str = ""
    checks: list[Check] = field(default_factory=list)
    engine_summary: str = ""
    engine_executable: str = ""
    root_path: str = ""
    overlay_summary: str = ""
    case_issues: int = 0

    def add(self, level: str, title: str, detail: str = "", hint: str = "") -> None:
        self.checks.append(Check(level, title, detail, hint))

    @property
    def errors(self) -> list[Check]:
        return [check for check in self.checks if check.level == LEVEL_ERROR]

    @property
    def warnings(self) -> list[Check]:
        return [check for check in self.checks if check.level == LEVEL_WARNING]

    @property
    def ok(self) -> bool:
        return not self.errors

    @property
    def headline(self) -> str:
        if self.errors:
            return f"Найдено проблем: {len(self.errors)}"
        if self.warnings:
            return f"Готов к запуску, предупреждений: {len(self.warnings)}"
        return "Готов к запуску"

    def to_dict(self) -> dict:
        return {
            "profile": self.profile_id,
            "name": self.profile_name,
            "headline": self.headline,
            "engine": self.engine_summary,
            "executable": self.engine_executable,
            "root": self.root_path,
            "overlay": self.overlay_summary,
            "case_issues": self.case_issues,
            "checks": [
                {"level": check.level, "title": check.title, "detail": check.detail, "hint": check.hint}
                for check in self.checks
            ],
        }

    def to_text(self) -> str:
        lines = [f"Профиль: {self.profile_name or self.profile_id}", f"Итог: {self.headline}"]
        if self.engine_summary:
            lines.append(f"Движок: {self.engine_summary}")
        if self.root_path:
            lines.append(f"Каталог профиля: {self.root_path}")
        lines.append("")
        for check in self.checks:
            lines.append(f"[{check.badge}] {check.title}")
            if check.detail:
                lines.append(f"        {check.detail}")
            if check.hint:
                lines.append(f"        подсказка: {check.hint}")
        return "\n".join(lines)


def run(
    profile: Profile,
    app: AppPaths,
    *,
    plan: LayerPlan | None = None,
    engine: engine_mod.EngineInfo | None = None,
    workspace: ProfileWorkspace | None = None,
    case_audit: bool = True,
) -> PreflightReport:
    report = PreflightReport(profile_id=profile.id, profile_name=profile.name)
    report.root_path = (workspace or ProfileWorkspace(app, profile)).root

    _check_game(profile, report)
    _check_engine(profile, report, engine)
    _check_fsgame(profile, report, plan, workspace)
    _check_backend(profile, report, plan)
    _check_mods(profile, report)
    _check_paths(profile, app, report)
    _check_launch_arguments(profile, report, engine)
    _check_disk(profile, app, report)
    if case_audit and plan is not None and not profile.is_standalone:
        _check_case_sensitivity(plan, report)
    _check_standalone(profile, report)
    if plan is not None:
        report.overlay_summary = (
            f"слоёв: {len(plan.layers)}, файлов: {len(plan.entries)}, "
            f"пересечений: {sum(1 for providers in plan.entries.values() if len(providers) > 1)}"
        )
    return report


def _check_game(profile: Profile, report: PreflightReport) -> None:
    game = profile.game_path
    if not game:
        report.add(LEVEL_ERROR, "Не указан каталог игры",
                   hint="Откройте настройки профиля и выберите папку с gamedata или архивами gamedata.db*.")
        return
    if not os.path.isdir(game):
        report.add(LEVEL_ERROR, "Каталог игры недоступен", game, "Проверьте путь и права доступа.")
        return
    if profile.is_standalone:
        return
    if xray.is_xray_tree(game):
        archives = xray.find_archives(game)
        detail = f"{game}; архивов найдено: {len(archives)}"
        if not xray.has_gamedata(game) and not archives:
            detail += " (нет ни gamedata, ни архивов)"
        report.add(LEVEL_OK, "Каталог игры в порядке", detail)
    else:
        report.add(LEVEL_WARNING, "Каталог игры не похож на установку X-Ray",
                   f"{game}: нет gamedata, fsgame.ltx и архивов",
                   "Убедитесь, что выбрана папка с игрой, а не с модами.")
    detected = xray.detect_game_id(game, declared=profile.game_id)
    report.add(
        LEVEL_INFO,
        "Тип игры (переключатели движка)",
        f"{xray.describe_game_id(detected)}"
        + (" → будет добавлен ключ -cs" if detected == "cs" else ""),
    )


def _check_engine(profile: Profile, report: PreflightReport, engine: engine_mod.EngineInfo | None) -> None:
    info = engine or engine_mod.find_engine(profile)
    if info is None:
        report.add(
            LEVEL_ERROR,
            "Исполняемый файл движка не найден",
            hint="Укажите каталог OpenXRay (например /usr/games или распакованную сборку) "
                 "или выберите файл вручную в настройках профиля.",
        )
        return
    report.engine_executable = info.executable
    report.engine_summary = info.describe()
    if info.binary.kind == "pe":
        report.add(
            LEVEL_ERROR,
            "Найден Windows-исполняемый файл (.exe)",
            f"{info.executable}: {info.binary.summary()}",
            "CORDON-LINUX работает с нативными сборками OpenXRay для Linux. Windows-версию можно "
            "запустить только через Proton/Wine — такие профили здесь не поддерживаются.",
        )
        return
    if info.binary.kind != "elf":
        report.add(LEVEL_ERROR, "Неподдерживаемый формат исполняемого файла",
                   f"{info.executable}: {info.binary.summary()}")
        return
    if not elf.arch_matches_host(info.binary):
        report.add(LEVEL_WARNING, "Разрядность движка не совпадает с системой",
                   f"{info.binary.summary()} против {elf.host_machine()}",
                   "Установите совпадающую сборку движка (для 32-битных сборок нужны multilib-библиотеки).")
    else:
        report.add(LEVEL_OK, "Движок найден", f"{info.executable} ({info.binary.summary()}), источник: {info.source}")
    for library in info.missing_libraries:
        report.add(LEVEL_WARNING, "Не хватает библиотеки движка", library,
                   "Проверьте целостность установки OpenXRay (ldd покажет полный список).")
    if info.data_root:
        report.add(LEVEL_INFO, "Каталог данных движка", info.data_root)
    else:
        report.add(LEVEL_WARNING, "Не найден каталог данных OpenXRay",
                   hint="Для системной установки ожидается /usr/share/openxray с fsgame.ltx и "
                        "gamedata/shaders — можно указать вручную в настройках профиля.")


def _check_fsgame(profile: Profile, report: PreflightReport, plan: LayerPlan | None,
                  workspace: ProfileWorkspace | None) -> None:
    if profile.is_standalone:
        return
    if profile.fsgame_source:
        if os.path.isfile(profile.fsgame_source):
            report.add(LEVEL_OK, "Источник fsgame.ltx задан вручную", profile.fsgame_source)
        else:
            report.add(LEVEL_ERROR, "Указанный fsgame.ltx недоступен", profile.fsgame_source,
                       "Снимите выбор или укажите существующий файл.")
        return
    candidates: list[str] = []
    if plan is not None:
        for layer in sorted(plan.layers, key=lambda item: item.priority, reverse=True):
            candidate = os.path.join(layer.path, xray.FSGAME_NAME)
            if os.path.isfile(candidate):
                candidates.append(candidate)
    if candidates:
        report.add(LEVEL_OK, "fsgame.ltx найден", candidates[0])
        return
    report.add(
        LEVEL_WARNING,
        "fsgame.ltx не найден ни в игре, ни в модах",
        hint="Будет использован встроенный шаблон с $fs_root$-относительными путями. "
             "Для крупных сборок лучше положить оригинальный fsgame.ltx рядом с gamedata.",
    )


def _check_backend(profile: Profile, report: PreflightReport, plan: LayerPlan | None) -> None:
    if profile.backend == BACKEND_FUSE:
        if not shutil.which("fuse-overlayfs"):
            report.add(LEVEL_ERROR, "fuse-overlayfs не установлен",
                       hint=mounts.INSTALL_HINTS["fuse-overlayfs"] + " Либо переключите backend на «ссылки».")
        elif plan is not None and not mounts.layer_gamedata_dirs(plan):
            report.add(LEVEL_ERROR, "Нет слоёв gamedata для монтирования",
                       hint="Проверьте путь к игре и включённые моды, либо переключитесь на backend «ссылки».")
        else:
            overlay = mounts.mount_for_plan(plan, "/tmp/cordon-preview")
            report.add(LEVEL_INFO, "Backend fuse-overlayfs",
                       " ".join(overlay.command(executable="fuse-overlayfs")))
        if not (shutil.which("fusermount3") or shutil.which("fusermount")):
            report.add(LEVEL_WARNING, "Не найдена утилита отмонтирования fuse",
                       hint=mounts.INSTALL_HINTS["fusermount3"])
    elif profile.backend == BACKEND_LINK:
        report.add(LEVEL_OK, "Backend: символические ссылки",
                   "Сборка не требует root, монтирования и сторонних утилит.")
    else:
        report.add(LEVEL_INFO, "Backend без оверлея", "Файлы собираются напрямую в каталог профиля не будут.")


def _check_mods(profile: Profile, report: PreflightReport) -> None:
    if profile.is_standalone:
        return
    enabled = profile.enabled_mods
    if not profile.mods:
        report.add(LEVEL_INFO, "Моды не подключены", "Профиль запустит чистую игру.")
    missing_enabled = [mod for mod in enabled if not os.path.isdir(mod.path)]
    missing_disabled = [mod for mod in profile.mods if not mod.enabled and not os.path.isdir(mod.path)]
    if missing_enabled:
        report.add(LEVEL_ERROR, "Папки включённых модов недоступны",
                   ", ".join(f"{mod.name} → {mod.path}" for mod in missing_enabled[:5]),
                   "Верните папки на место или отключите моды.")
    if missing_disabled:
        report.add(LEVEL_WARNING, "Отключённые моды отсутствуют",
                   ", ".join(mod.name for mod in missing_disabled[:5]))
    empty = [mod for mod in enabled if os.path.isdir(mod.path) and xray.classify_mod(mod.path).is_empty]
    if empty:
        report.add(LEVEL_WARNING, "Пустые папки модов",
                   ", ".join(mod.name for mod in empty[:5]),
                   "В таких папках нет ни gamedata, ни игровых подкаталогов.")
    if enabled and not missing_enabled:
        report.add(LEVEL_OK, "Моды готовы", f"включено: {len(enabled)} из {len(profile.mods)}")


def _check_paths(profile: Profile, app: AppPaths, report: PreflightReport) -> None:
    workspace = ProfileWorkspace(app, profile)
    root = workspace.root
    parent = os.path.dirname(root)
    if not os.access(util.nearest_existing(parent), os.W_OK):
        report.add(LEVEL_ERROR, "Нет прав на запись в каталог профилей", parent,
                   "Измените каталог данных лаунчера или права доступа.")
        return
    report.add(LEVEL_OK, "Каталог профиля доступен для записи", root)
    for source in (profile.game_path, *(mod.path for mod in profile.enabled_mods)):
        if source and util.is_inside(source, root):
            report.add(LEVEL_ERROR, "Игра или мод находятся внутри каталога профиля", source,
                       "Это приведёт к рекурсивному вложению ссылок. Выберите другие пути.")
    if workspace.is_initialised():
        problems = workspace.verify()
        if problems:
            report.add(LEVEL_WARNING, "Предыдущая сборка оверлея неполна",
                       "; ".join(problems[:4]), "Запустите «Пересобрать» перед стартом.")
        else:
            report.add(LEVEL_OK, "Оверлей профиля цел", root)
    else:
        report.add(LEVEL_INFO, "Оверлей ещё не собран", "Он будет создан при запуске.")


def _check_launch_arguments(profile: Profile, report: PreflightReport, engine: engine_mod.EngineInfo | None) -> None:
    unknown: list[str] = []
    for key in profile.engine_flags:
        if key not in KNOWN_ENGINE_KEYS:
            unknown.append(key)
    for token in profile.launch_arguments.split():
        if token.startswith("-") and token not in KNOWN_ENGINE_KEYS:
            unknown.append(token)
    if unknown:
        report.add(LEVEL_WARNING, "Неизвестные ключи командной строки",
                   ", ".join(sorted(set(unknown))),
                   "Движок молча проигнорирует неизвестные ключи — сверьтесь со списком ключей OpenXRay.")


def _check_disk(profile: Profile, app: AppPaths, report: PreflightReport) -> None:
    free = util.filesystem_free_bytes(app.data_dir)
    if free < 0:
        return
    if free < MIN_FREE_BYTES:
        report.add(LEVEL_WARNING, "Мало свободного места", f"свободно {util.human_size(free)}",
                   "Оверлею и кэшу шейдеров нужно место для данных профиля.")
    else:
        report.add(LEVEL_INFO, "Свободное место", util.human_size(free))


def _check_case_sensitivity(plan: LayerPlan, report: PreflightReport) -> None:
    try:
        result = audit.audit_plan(plan)
    except Exception as exc:  # noqa: BLE001 - the audit must never block a launch
        report.add(LEVEL_INFO, "Аудит регистра не выполнен", str(exc))
        return
    report.case_issues = len(result.issues)
    if result.issues:
        examples = ", ".join(f"{item.referenced} → {item.actual}" for item in result.issues[:3])
        report.add(
            LEVEL_WARNING,
            "Найдено несовпадение регистра путей",
            f"проблем: {len(result.issues)} (например: {examples})",
            "Движок OpenXRay на Linux чувствителен к регистру (xr_fs_strlwr — no-op). "
            "Выполните «Аудит регистра» и примените исправление алиасами.",
        )
    elif result.scanned:
        report.add(LEVEL_OK, "Регистр путей в порядке", f"проверено файлов: {result.scanned}")


def _check_standalone(profile: Profile, report: PreflightReport) -> None:
    if not profile.is_standalone:
        return
    if not profile.game_path or not os.path.isdir(profile.game_path):
        report.add(LEVEL_ERROR, "Каталог сборки недоступен", profile.game_path)
        return
    appdata = xray.find_appdata_roots(profile.game_path)
    if appdata:
        report.add(LEVEL_INFO, "Каталоги данных сборки", ", ".join(appdata))
    else:
        report.add(LEVEL_WARNING, "Каталоги данных сборки не найдены",
                   hint="Сборка сама определяет, куда писать сохранения — проверьте её fsgame.ltx.")
