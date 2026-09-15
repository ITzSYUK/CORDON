"""``cordon-gui`` - the PySide6 front end.

Kept deliberately thin: all the work lives in :mod:`cordon.core`, so the GUI can be skipped
entirely (``pip install cordon-linux`` without the ``gui`` extra) and the launcher still works
from the command line.
"""

from __future__ import annotations

import argparse
import os
import sys


def _parse_args(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="cordon-gui",
        description="CORDON-LINUX — графический лаунчер профилей S.T.A.L.K.E.R. (движок OpenXRay)",
    )
    parser.add_argument("--portable", default="", metavar="DIR",
                        help="портативный режим: настройки, профили и моды внутри каталога")
    parser.add_argument("--config-dir", default="", help="каталог настроек (по умолчанию XDG)")
    parser.add_argument("--data-dir", default="", help="каталог данных (по умолчанию XDG)")
    parser.add_argument("--cache-dir", default="", help="каталог кэша (по умолчанию XDG)")
    parser.add_argument("--profile", default="", help="открыть конкретный профиль по id или имени")
    parser.add_argument("--theme", default="", choices=("", "pda", "classic"), help="оформление окна")
    parser.add_argument("--version", action="store_true", help="показать версию и выйти")
    return parser.parse_args(argv[1:] if argv else None)


def main(argv: list[str] | None = None) -> int:
    argv = list(argv or sys.argv)
    args = _parse_args(argv)
    try:
        from PySide6.QtCore import Qt
        from PySide6.QtWidgets import QApplication, QMessageBox
    except ImportError as exc:  # pragma: no cover - depends on the environment
        print(
            "Для графического интерфейса нужен PySide6:\n"
            "  pip install 'cordon-linux[gui]'\n"
            f"({exc})",
            file=sys.stderr,
        )
        return 1

    from .. import __version__
    from ..core import launcherlog
    from ..core.errors import CordonError
    from ..core.paths import AppPaths
    from ..core.service import CordonService

    if args.version:
        print(f"CORDON-LINUX {__version__}")
        return 0

    QApplication.setAttribute(Qt.AA_ShareOpenGLContexts, True)
    app = QApplication(argv)
    app.setApplicationName("cordon-linux")
    app.setApplicationDisplayName("CORDON-LINUX")
    app.setApplicationVersion(__version__)
    app.setOrganizationName("CORDON-LINUX")

    if args.portable:
        paths = AppPaths.in_directory(args.portable)
    else:
        paths = AppPaths.discover(
            config_dir=args.config_dir or None,
            data_dir=args.data_dir or None,
            cache_dir=args.cache_dir or None,
        )
    try:
        paths.ensure_layout()
    except OSError as exc:
        QMessageBox.critical(None, "CORDON-LINUX", f"Не удалось создать каталоги лаунчера:\n{exc}")
        return 1

    logger, _memory = launcherlog.setup_logging(paths)
    logger.info(
        "CORDON-LINUX %s: запуск GUI (python %s), настройки: %s, данные: %s",
        __version__,
        sys.version.split()[0],
        paths.config_dir,
        paths.data_dir,
    )
    service = CordonService(paths, logger=logger)
    try:
        result = service.load()
    except CordonError as exc:
        QMessageBox.critical(None, "CORDON-LINUX", str(exc))
        return 1
    if args.theme:
        service.settings.theme = args.theme
        service.save()

    from .main_window import MainWindow, start_timer_update

    profile_id = args.profile
    if profile_id:
        match = next(
            (
                profile
                for profile in service.settings.profiles
                if profile.id == profile_id or profile.name.lower() == profile_id.lower()
            ),
            None,
        )
        profile_id = match.id if match else ""
        if not profile_id:
            logger.warning("профиль «%s» не найден по аргументу --profile", args.profile)

    window = MainWindow(service, profile_id=profile_id)
    start_timer_update(window)
    window.show()

    for notice in service.notices:
        logger.warning("%s", notice)

    if not service.settings.profiles:
        window.statusBar().showMessage("Начните с кнопки «Новый профиль» на панели сверху")

    # Hand the launcher over to the desktop session so tray-less sessions still work.
    return app.exec()


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
