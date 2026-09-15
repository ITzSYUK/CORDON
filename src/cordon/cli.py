"""``cordon`` - the command line interface.

Everything the GUI can do is available here as well, which makes the launcher scriptable and
usable from a TTY or over SSH:

    cordon doctor "Anomaly 1.5.2"
    cordon mod scan "Anomaly" ~/Downloads/Mods
    cordon launch "Anomaly" --dry-run
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import sys

from . import __version__
from .core import audit, conflicts, diagnostics, launcherlog, layers
from .core import preflight, util, xray
from .core import engine as engine_mod
from .core import mods as mods_mod
from .core.errors import CordonError
from .core.launch import describe_launch, finish_session, open_in_file_manager, run_profile, start_session
from .core.models import BACKEND_DIRECT, BACKEND_FUSE, BACKEND_LINK, GAME_IDS
from .core.paths import AppPaths
from .core.service import CordonService


EXIT_OK = 0
EXIT_ERROR = 1
EXIT_BLOCKED = 2


# ---------------------------------------------------------------------------- helpers
def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="cordon",
        description="CORDON-LINUX — лаунчер профилей и модов S.T.A.L.K.E.R. для Linux (движок OpenXRay)",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="Примеры:\n"
               "  cordon list\n"
               "  cordon new \"Anomaly 1.5.2\" --game ~/games/anomaly\n"
               "  cordon launch \"Anomaly 1.5.2\" --dry-run\n"
               "  cordon doctor \"Anomaly 1.5.2\"\n",
    )
    parser.add_argument("--version", action="version", version=f"CORDON-LINUX {__version__}")
    parser.add_argument("--config-dir", default="", help="каталог настроек (по умолчанию XDG)")
    parser.add_argument("--data-dir", default="", help="каталог данных (профили, моды)")
    parser.add_argument("--cache-dir", default="", help="каталог кэша (журнал лаунчера)")
    parser.add_argument("--portable", default="", metavar="DIR",
                        help="портативный режим: все данные внутри указанного каталога")
    parser.add_argument("-v", "--verbose", action="store_true", help="подробный вывод в консоль")
    parser.add_argument("--json", action="store_true", help="машиночитаемый вывод там, где это поддерживается")

    sub = parser.add_subparsers(dest="command", metavar="КОМАНДА")

    sub.add_parser("gui", help="запустить графический интерфейс")
    sub.add_parser("tools", help="показать найденные внешние утилиты")

    p_list = sub.add_parser("list", help="список профилей")
    p_list.add_argument("--json", action="store_true")

    p_new = sub.add_parser("new", help="создать профиль")
    p_new.add_argument("name")
    p_new.add_argument("--game", default="", help="каталог игры или сборки")
    p_new.add_argument("--kind", default="mods", choices=("mods", "standalone"))
    p_new.add_argument("--description", default="")

    p_duplicate = sub.add_parser("duplicate", help="создать копию профиля")
    p_duplicate.add_argument("profile")
    p_duplicate.add_argument("--name", default="")

    p_show = sub.add_parser("show", help="показать профиль")
    p_show.add_argument("profile")
    p_show.add_argument("--json", action="store_true")

    p_delete = sub.add_parser("delete", help="удалить профиль")
    p_delete.add_argument("profile")
    p_delete.add_argument("--keep-files", action="store_true", help="оставить каталог профиля на диске")
    p_delete.add_argument("-y", "--yes", action="store_true", help="без подтверждения")

    p_edit = sub.add_parser("edit", help="изменить поля профиля")
    p_edit.add_argument("profile")
    p_edit.add_argument("--name")
    p_edit.add_argument("--game")
    p_edit.add_argument("--engine")
    p_edit.add_argument("--engine-data")
    p_edit.add_argument("--backend", choices=(BACKEND_LINK, BACKEND_FUSE, BACKEND_DIRECT))
    p_edit.add_argument("--game-id", choices=GAME_IDS)
    p_edit.add_argument("--executable", help="относительный путь к исполняемому файлу движка")
    p_edit.add_argument("--arguments", help="дополнительные аргументы командной строки")
    p_edit.add_argument("--appdata", choices=("profile", "shared"), help="куда писать данные профиля")
    p_edit.add_argument("--root", help="каталог профиля (по умолчанию ~/.local/share/cordon/profiles)")
    p_edit.add_argument("--no-overlay-path", action="store_true", help="не добавлять ключ -overlaypath")

    p_mods = sub.add_parser("mods", help="список модов профиля")
    p_mods.add_argument("profile")
    p_mods.add_argument("--json", action="store_true")

    p_mod_add = sub.add_parser("mod-add", help="добавить папки модов")
    p_mod_add.add_argument("profile")
    p_mod_add.add_argument("paths", nargs="+")

    p_mod_scan = sub.add_parser("mod-scan", help="найти моды в каталоге")
    p_mod_scan.add_argument("profile")
    p_mod_scan.add_argument("directory")
    p_mod_scan.add_argument("--add", action="store_true", help="сразу добавить найденные моды")

    p_mod_install = sub.add_parser("mod-install", help="установить мод из архива")
    p_mod_install.add_argument("profile")
    p_mod_install.add_argument("archives", nargs="+")

    p_mod_state = sub.add_parser("mod-state", help="включить/выключить мод")
    p_mod_state.add_argument("profile")
    p_mod_state.add_argument("mod", help="имя или id мода")
    p_mod_state.add_argument("state", choices=("on", "off", "toggle"))

    p_mod_move = sub.add_parser("mod-move", help="переместить мод в списке приоритетов")
    p_mod_move.add_argument("profile")
    p_mod_move.add_argument("mod")
    p_mod_move.add_argument("position", type=int, help="новый номер в списке (с 1)")

    p_mod_remove = sub.add_parser("mod-remove", help="убрать мод из профиля")
    p_mod_remove.add_argument("profile")
    p_mod_remove.add_argument("mod")
    p_mod_remove.add_argument("--delete-files", action="store_true")

    p_prepare = sub.add_parser("prepare", help="собрать оверлей профиля")
    p_prepare.add_argument("profile")
    p_prepare.add_argument("--force", action="store_true")

    p_launch = sub.add_parser("launch", help="запустить профиль")
    p_launch.add_argument("profile")
    p_launch.add_argument("--dry-run", action="store_true", help="только показать команду")
    p_launch.add_argument("--force", action="store_true", help="пересобрать оверлей")
    p_launch.add_argument("--detach", action="store_true", help="не ждать выхода игры")
    p_launch.add_argument("--skip-check", action="store_true", help="не выполнять предполётные проверки")
    p_launch.add_argument("--presence", action="store_true", help="Discord Rich Presence для этой сессии")

    p_doctor = sub.add_parser("doctor", help="проверки готовности профиля")
    p_doctor.add_argument("profile", nargs="?")
    p_doctor.add_argument("--json", action="store_true")

    p_conflicts = sub.add_parser("conflicts", help="анализ конфликтов модов")
    p_conflicts.add_argument("profile")
    p_conflicts.add_argument("--tree", action="store_true", help="показать итоговое дерево файлов")
    p_conflicts.add_argument("--limit", type=int, default=200)

    p_audit = sub.add_parser("audit", help="аудит чувствительности к регистру")
    p_audit.add_argument("profile")
    p_audit.add_argument("--fix", action="store_true", help="создать алиасы для несовпадающих путей")
    p_audit.add_argument("--json", action="store_true")

    p_fsgame = sub.add_parser("fsgame", help="показать подготовленный fsgame.ltx")
    p_fsgame.add_argument("profile")
    p_fsgame.add_argument("--write", action="store_true", help="записать файл в каталог профиля")

    p_mo2 = sub.add_parser("import-mo2", help="импортировать профиль Mod Organizer 2")
    p_mo2.add_argument("profile")
    p_mo2.add_argument("path", help="каталог MO2, профиль или modlist.txt")
    p_mo2.add_argument("--modlist-only", action="store_true", help="только порядок и включённость")
    p_mo2.add_argument("--apply", action="store_true", help="применить (без флага только предпросмотр)")
    p_mo2.add_argument("--no-overwrite", action="store_true", help="не подключать overwrite")

    p_export = sub.add_parser("export", help="экспортировать профиль в JSON")
    p_export.add_argument("profile")
    p_export.add_argument("target")

    p_import = sub.add_parser("import", help="импортировать профиль из JSON")
    p_import.add_argument("source")

    p_report = sub.add_parser("report", help="сохранить текстовый отчёт о профиле")
    p_report.add_argument("profile")

    p_open = sub.add_parser("open", help="открыть каталоги профиля в файловом менеджере")
    p_open.add_argument("profile")
    p_open.add_argument("what", nargs="?", default="root", choices=("root", "appdata", "mods", "logs"))

    p_unmount = sub.add_parser("unmount", help="отмонтировать оверлей fuse-overlayfs")
    p_unmount.add_argument("profile")

    return parser


def resolve_profile(service: CordonService, query: str):
    profiles = service.profiles
    for profile in profiles:
        if profile.id == query:
            return profile
    lowered = query.lower()
    exact = [profile for profile in profiles if profile.name.lower() == lowered]
    if len(exact) == 1:
        return exact[0]
    partial = [profile for profile in profiles if lowered in profile.name.lower()]
    if len(partial) == 1:
        return partial[0]
    if not partial:
        raise CordonError(f"профиль «{query}» не найден. Доступные: "
                          + ", ".join(profile.name for profile in profiles) or "нет профилей")
    raise CordonError("уточните имя профиля, подходящих вариантов несколько: "
                      + ", ".join(profile.name for profile in partial))


def _service(args) -> CordonService:
    if args.portable:
        app = AppPaths.in_directory(args.portable)
    else:
        app = AppPaths.discover(
            config_dir=args.config_dir or None,
            data_dir=args.data_dir or None,
            cache_dir=args.cache_dir or None,
        )
    logger, _memory = launcherlog.setup_logging(app, console=args.verbose)
    logger.info("CORDON-LINUX %s: команда %s", __version__, args.command)
    service = CordonService(app, logger=logger)
    service.load()
    for notice in service.notices:
        print(f"! {notice}", file=sys.stderr)
    return service


# ---------------------------------------------------------------------------- commands
def cmd_list(args, service: CordonService) -> int:
    profiles = service.profiles
    if args.json:
        print(json.dumps([profile.to_dict() for profile in profiles], ensure_ascii=False, indent=2))
        return EXIT_OK
    if not profiles:
        print("Профилей пока нет. Создайте первый: cordon new \"Имя\" --game /путь/к/игре")
        return EXIT_OK
    print(f"{'ИД':<14}{'ИМЯ':<32}{'ТИП':<12}{'ИГРА':<28}{'МОДЫ':>5}  ВРЕМЯ")
    for profile in profiles:
        kind = "сборка" if profile.is_standalone else "игра+моды"
        game = os.path.basename(profile.game_path.rstrip("/")) or "—"
        print(
            f"{profile.id:<14}{profile.name[:31]:<32}{kind:<12}{game[:27]:<28}"
            f"{len(profile.enabled_mods):>5}  {profile.playtime_display}"
        )
    return EXIT_OK


def cmd_new(args, service: CordonService) -> int:
    game = util.norm(args.game) if args.game else ""
    if game and not os.path.isdir(game):
        raise CordonError(f"каталог не найден: {game}")
    profile = service.create_profile(args.name, kind=args.kind, game_path=game, description=args.description)
    print(f"Создан профиль «{profile.name}» ({profile.id}).")
    if game:
        info = engine_mod.find_engine(profile)
        print(f"  {info.describe() if info else 'движок не найден — укажите его в настройках профиля'}")
    return EXIT_OK


def cmd_duplicate(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    clone = service.duplicate_profile(profile.id, new_name=args.name)
    print(f"Создана копия профиля: «{clone.name}» ({clone.id})")
    return EXIT_OK


def cmd_show(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    plan = service.plan_for(profile)
    info = engine_mod.find_engine(profile)
    report = preflight.run(profile, service.app, plan=plan, engine=info)
    stats = plan.stats()
    if args.json:
        print(json.dumps({
            "profile": profile.to_dict(),
            "engine": report.engine_summary,
            "root": report.root_path,
            "layers": [{"kind": layer.kind, "name": layer.name, "path": layer.path} for layer in plan.layers],
            "stats": {
                "files": stats.total_files,
                "conflicts": stats.overlapping_files,
                "bytes": stats.total_bytes,
                "per_layer": stats.per_layer,
            },
            "preflight": report.to_dict(),
        }, ensure_ascii=False, indent=2))
        return EXIT_OK
    print(f"Профиль:      {profile.name} ({profile.id})")
    print(f"Тип:          {'готовая сборка' if profile.is_standalone else 'игра + моды'}")
    print(f"Игра:         {profile.game_path or '—'}")
    print(f"Движок:       {profile.engine_path or profile.game_path or '—'}")
    print(f"Данные движка:{profile.engine_data_path or '—'}")
    print(f"Backend:      {profile.backend}")
    print(f"Каталог:      {report.root_path}")
    print(f"Моды:         включено {len(profile.enabled_mods)} из {len(profile.mods)}")
    print(f"Плейтайм:     {profile.playtime_display}, последний запуск: {profile.last_played_display}")
    print(f"Файлов:       {stats.total_files} (пересечений {stats.overlapping_files}, "
          f"объём {util.human_size(stats.total_bytes)})")
    print()
    for line in plan.describe():
        print(line)
    print()
    print(f"Готовность:   {report.headline}")
    for check in report.errors:
        print(f"  [ОШИБКА]  {check.title}: {check.detail}")
    for check in report.warnings[:6]:
        print(f"  [ВНИМАНИЕ] {check.title}: {check.detail}")
    return EXIT_OK


def cmd_delete(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    if not args.yes:
        answer = input(f"Удалить профиль «{profile.name}»? [y/N] ").strip().lower()
        if answer not in ("y", "yes", "д", "да"):
            print("Отменено.")
            return EXIT_OK
    service.delete_profile(profile.id, remove_files=not args.keep_files)
    print(f"Профиль «{profile.name}» удалён"
          + (" (файлы оставлены)" if args.keep_files else ""))
    return EXIT_OK


def cmd_edit(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    changes: list[str] = []
    if args.name:
        profile.name = args.name
        changes.append("имя")
    if args.game:
        profile.game_path = util.norm(args.game)
        changes.append("каталог игры")
    if args.engine:
        profile.engine_path = util.norm(args.engine)
        changes.append("каталог движка")
    if args.engine_data:
        profile.engine_data_path = util.norm(args.engine_data)
        changes.append("данные движка")
    if args.backend:
        profile.backend = args.backend
        changes.append("backend")
    if args.game_id:
        profile.game_id = args.game_id
        changes.append("тип игры")
    if args.executable is not None:
        profile.executable_relative = args.executable
        profile.executable_source = ""
        changes.append("исполняемый файл")
    if args.arguments is not None:
        profile.launch_arguments = args.arguments
        changes.append("аргументы")
    if args.appdata:
        profile.appdata_mode = args.appdata
        changes.append("каталог данных")
    if args.root:
        profile.root_path = util.norm(args.root)
        changes.append("каталог профиля")
    if args.no_overlay_path:
        profile.use_overlay_path = False
        changes.append("-overlaypath отключён")
    if not changes:
        print("Ничего не изменено.")
        return EXIT_OK
    service.save()
    print(f"Обновлено в профиле «{profile.name}»: {', '.join(changes)}")
    return EXIT_OK


def cmd_mods(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    if args.json:
        print(json.dumps([mod.to_dict() for mod in profile.mods], ensure_ascii=False, indent=2))
        return EXIT_OK
    if not profile.mods:
        print("У профиля нет модов.")
        return EXIT_OK
    report = service.conflict_report(profile)
    print(f"{'N':>3} {'ВКЛ':<4}{'МОД':<36}{'СТАТУС':<22}ПУТЬ")
    for index, mod in enumerate(profile.mods, start=1):
        status = report.per_mod.get(mod.id)
        label = status.label if status else ""
        mark = "да" if mod.enabled else "нет"
        print(f"{index:>3} {mark:<4}{mod.name[:35]:<36}{label:<22}{mod.path}")
    return EXIT_OK


def cmd_mod_add(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    expanded: list[str] = []
    for path in args.paths:
        target = util.norm(path)
        if os.path.isdir(target) and not xray.is_xray_tree(target) and xray.classify_mod(target).is_empty:
            found = mods_mod.scan_mod_folders(target)
            expanded.extend(found or [target])
        else:
            expanded.append(target)
    added = service.add_mods_from_folders(profile, expanded)
    print(f"Добавлено модов: {added}")
    return EXIT_OK


def cmd_mod_scan(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    result = service.scan_folder(profile, args.directory)
    for entry in result.found:
        print(f"  + {entry.name} — {entry.path}")
    for path in result.skipped:
        print(f"  ? {path} (не распознан как мод)")
    print(result.summary)
    if args.add and result.found:
        service.add_mods_from_folders(profile, [entry.path for entry in result.found])
        print(f"Добавлено: {len(result.found)}")
    elif result.found:
        print("Добавьте их командой mod-add или повторите с --add.")
    return EXIT_OK


def cmd_mod_install(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    for archive in args.archives:
        outcome = service.install_archive(profile, util.norm(archive), progress=lambda text: print(f"  {text}"))
        print(f"Установлен «{outcome.mod.name}» → {outcome.path} ({outcome.files} файлов)")
    return EXIT_OK


def cmd_mod_state(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    mod = _resolve_mod(profile, args.mod)
    if args.state == "on":
        mod.enabled = True
    elif args.state == "off":
        mod.enabled = False
    else:
        mod.enabled = not mod.enabled
    service.save()
    print(f"Мод «{mod.name}»: {'включён' if mod.enabled else 'выключен'}")
    return EXIT_OK


def cmd_mod_move(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    mod = _resolve_mod(profile, args.mod)
    position = max(1, min(args.position, len(profile.mods))) - 1
    profile.mods.remove(mod)
    profile.mods.insert(position, mod)
    service.save()
    print(f"Мод «{mod.name}» перемещён на позицию {position + 1} (чем ниже в списке, тем выше приоритет)")
    return EXIT_OK


def cmd_mod_remove(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    mod = _resolve_mod(profile, args.mod)
    name = mod.name
    service.remove_mod(profile, mod.id, delete_files=args.delete_files)
    print(f"Мод «{name}» убран из профиля" + (" вместе с файлами" if args.delete_files else ""))
    return EXIT_OK


def _resolve_mod(profile, query: str):
    for mod in profile.mods:
        if mod.id == query:
            return mod
    lowered = query.lower()
    exact = [mod for mod in profile.mods if mod.name.lower() == lowered]
    if len(exact) == 1:
        return exact[0]
    partial = [mod for mod in profile.mods if lowered in mod.name.lower()]
    if len(partial) == 1:
        return partial[0]
    if not partial:
        raise CordonError(f"мод «{query}» не найден в профиле")
    raise CordonError("уточните имя мода: " + ", ".join(mod.name for mod in partial))


def cmd_prepare(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    plan, workspace, result = service.prepare(
        profile, force=args.force, progress=lambda text: print(f"  {text}")
    )
    print(f"Каталог профиля: {workspace.root}")
    print(result.summary)
    for warning in result.warnings:
        print(f"  ! {warning}")
    print(f"fsgame.ltx: {workspace.fsgame_path}")
    return EXIT_OK


def cmd_launch(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    if not args.skip_check:
        report = preflight.run(profile, service.app)
        if not report.ok:
            print(report.to_text(), file=sys.stderr)
            print("\nЗапуск заблокирован. Исправьте ошибки или используйте --skip-check.", file=sys.stderr)
            return EXIT_BLOCKED
    if args.dry_run:
        plan = run_profile(profile, service.app, dry_run=True, force_rebuild=args.force, logger=service.logger)
        print(describe_launch(plan))
        return EXIT_OK
    if args.detach:
        session = start_session(
            profile,
            service.app,
            force_rebuild=args.force,
            progress=lambda text: print(f"  {text}"),
            logger=service.logger,
        )
        print(f"Игра запущена (pid {session.process.pid}), журнал: {session.log_path}")
        print("Лаунчер не будет ждать завершения игры.")
        return EXIT_OK

    presence = None
    if args.presence and service.settings.discord_presence:
        from .core.discord import announce_launch

        presence = announce_launch(service.settings.discord_client_id, profile.name, game_id=profile.game_id)
    try:
        outcome = run_profile(
            profile,
            service.app,
            force_rebuild=args.force,
            logger=service.logger,
            progress=lambda text: print(f"  {text}"),
        )
    finally:
        if presence:
            presence.clear()
            presence.close()
    service.save()
    print(f"Игра завершилась с кодом {outcome.returncode} за {util.human_duration(outcome.duration)}")
    if outcome.diagnostics:
        print(outcome.diagnostics.to_text())
    return EXIT_OK if outcome.returncode == 0 else EXIT_ERROR


def cmd_doctor(args, service: CordonService) -> int:
    profiles = [resolve_profile(service, args.profile)] if args.profile else service.profiles
    if not profiles:
        print("Профилей нет — проверять нечего.")
        return EXIT_OK
    payload = []
    exit_code = EXIT_OK
    for profile in profiles:
        plan = service.plan_for(profile)
        report = preflight.run(profile, service.app, plan=plan)
        payload.append(report.to_dict())
        if not args.json:
            print(report.to_text())
            print()
        if not report.ok:
            exit_code = EXIT_BLOCKED
    if args.json:
        print(json.dumps(payload, ensure_ascii=False, indent=2))
    return exit_code


def cmd_conflicts(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    plan = service.plan_for(profile)
    report = service.conflict_report(profile, plan=plan)
    print(report.to_text())
    if args.tree:
        print()
        print("Итоговое дерево (кто побеждает):")
        print(conflicts.effective_tree(plan, limit=args.limit))
    return EXIT_OK


def cmd_audit(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    result = service.audit_case(profile, fix=args.fix)
    if args.json:
        print(json.dumps({
            "scanned": result.scanned,
            "missing": result.missing,
            "issues": [
                {"referenced": issue.referenced, "actual": issue.actual, "source": issue.source_file}
                for issue in result.issues
            ],
        }, ensure_ascii=False, indent=2))
    else:
        print(result.to_text())
        if args.fix:
            print()
            print("Алиасы:")
            lines = audit.alias_report(service.workspace_for(profile).root)
            print("\n".join(f"  {line}" for line in lines) or "  —")
    return EXIT_OK


def cmd_fsgame(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    plan = service.plan_for(profile)
    workspace = service.workspace_for(profile)
    workspace.ensure_root()
    document = workspace.fsgame_document(plan)
    if args.write:
        path = workspace.write_fsgame(plan)
        print(f"Записан: {path}")
        return EXIT_OK
    print(f"; источник: {document.source or 'встроенный шаблон'}")
    print(document.render(), end="")
    return EXIT_OK


def cmd_import_mo2(args, service: CordonService) -> int:
    from .core.mo2 import apply_modlist_only, apply_preview, build_preview

    profile = resolve_profile(service, args.profile)
    preview = build_preview(args.path)
    print(preview.to_text())
    if not args.apply:
        print("\nЭто предпросмотр. Повторите с --apply, чтобы применить.")
        return EXIT_OK
    if args.modlist_only:
        count = apply_modlist_only(profile, preview)
    else:
        count = apply_preview(profile, preview, use_overwrite=not args.no_overwrite)
    service.save()
    print(f"Импортировано записей: {count}")
    return EXIT_OK


def cmd_export(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    target = service.export_profile(profile, args.target)
    print(f"Профиль сохранён: {target}")
    return EXIT_OK


def cmd_import(args, service: CordonService) -> int:
    profile = service.import_profile(util.norm(args.source))
    print(f"Импортирован профиль «{profile.name}» ({profile.id})")
    return EXIT_OK


def cmd_report(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    path = service.report(profile)
    print(f"Отчёт сохранён: {path}")
    return EXIT_OK


def cmd_open(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    workspace = service.workspace_for(profile)
    targets = {
        "root": workspace.root,
        "appdata": workspace.appdata,
        "mods": service.app.mod_storage(profile.id, profile.mod_storage_path),
        "logs": os.path.join(workspace.root, "logs"),
    }
    path = targets[args.what]
    util.ensure_dir(path)
    if open_in_file_manager(path):
        print(f"Открыто: {path}")
        return EXIT_OK
    print(f"Файловый менеджер не найден. Каталог: {path}")
    return EXIT_ERROR


def cmd_unmount(args, service: CordonService) -> int:
    profile = resolve_profile(service, args.profile)
    service.workspace_for(profile).unmount()
    print("Оверлей отмонтирован (если он был смонтирован).")
    return EXIT_OK


def cmd_tools(args, service: CordonService) -> int:
    from .core.discord import available as discord_available
    from .core.mounts import detect_tools

    status = detect_tools()
    print("Найденные утилиты:")
    for name, path in sorted(status.available.items()):
        print(f"  ✓ {name:<16}{path}")
    missing = [name for name in status.missing if name in
               ("fuse-overlayfs", "fusermount3", "7z", "bsdtar", "unrar", "xdg-open")]
    if missing:
        print("Не найдены (нужны для отдельных возможностей):")
        for name in missing:
            print(f"  ✗ {name}")
    print(f"Discord IPC: {'доступен' if discord_available() else 'не найден'}")
    return EXIT_OK


def cmd_gui(args, service: CordonService) -> int:
    try:
        from .gui.launch import main as gui_main
    except ImportError as exc:  # pragma: no cover - depends on PySide6
        print("Графический интерфейс недоступен: не установлен PySide6.\n"
              "Установите: pip install 'cordon-linux[gui]' или python -m pip install PySide6-Essentials\n"
              f"Техническая информация: {exc}", file=sys.stderr)
        return EXIT_ERROR
    argv = ["cordon-gui"]
    if args.portable:
        argv += ["--portable", args.portable]
    if args.config_dir:
        argv += ["--config-dir", args.config_dir]
    if args.data_dir:
        argv += ["--data-dir", args.data_dir]
    if args.cache_dir:
        argv += ["--cache-dir", args.cache_dir]
    return gui_main(argv)


COMMANDS = {
    "gui": cmd_gui,
    "tools": cmd_tools,
    "list": cmd_list,
    "new": cmd_new,
    "show": cmd_show,
    "duplicate": cmd_duplicate,
    "delete": cmd_delete,
    "edit": cmd_edit,
    "mods": cmd_mods,
    "mod-add": cmd_mod_add,
    "mod-scan": cmd_mod_scan,
    "mod-install": cmd_mod_install,
    "mod-state": cmd_mod_state,
    "mod-move": cmd_mod_move,
    "mod-remove": cmd_mod_remove,
    "prepare": cmd_prepare,
    "launch": cmd_launch,
    "doctor": cmd_doctor,
    "conflicts": cmd_conflicts,
    "audit": cmd_audit,
    "fsgame": cmd_fsgame,
    "import-mo2": cmd_import_mo2,
    "export": cmd_export,
    "import": cmd_import,
    "report": cmd_report,
    "open": cmd_open,
    "unmount": cmd_unmount,
}


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if not args.command:
        parser.print_help()
        return EXIT_OK
    try:
        service = _service(args)
    except CordonError as exc:
        print(f"Ошибка настроек: {exc}", file=sys.stderr)
        return EXIT_ERROR
    handler = COMMANDS.get(args.command)
    if handler is None:  # pragma: no cover - argparse guards this
        parser.print_help()
        return EXIT_ERROR
    try:
        return handler(args, service)
    except CordonError as exc:
        print(f"Ошибка: {exc}", file=sys.stderr)
        return EXIT_ERROR
    except KeyboardInterrupt:
        print("\nПрервано пользователем.", file=sys.stderr)
        return EXIT_ERROR


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
