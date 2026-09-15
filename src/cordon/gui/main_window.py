"""The main window: profiles on the left, the mod/priority list and reports on the right."""

from __future__ import annotations

import os

from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import QAction, QKeySequence
from PySide6.QtWidgets import (
    QCheckBox,
    QFileDialog,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QListWidget,
    QMainWindow,
    QMessageBox,
    QPlainTextEdit,
    QProgressBar,
    QSplitter,
    QStatusBar,
    QTabWidget,
    QVBoxLayout,
    QWidget,
)

from .. import __version__
from ..core import audit, conflicts, diagnostics, engine as engine_mod, launcherlog, layers, mods as mods_mod
from ..core import preflight, util
from ..core.errors import CordonError
from ..core.models import BACKEND_FUSE, ENGINE_FLAG_LABELS, Profile
from ..core.service import CordonService
from . import theme as theme_mod
from .dialogs import AboutDialog, LauncherSettingsDialog, Mo2Dialog, ProfileDialog, ReportDialog
from .widgets import ModTable, ProfileList, ReportPane, StatusStrip, make_button, summary_line
from .workers import SessionThread, Task

UPSTREAM = "https://github.com/ITzSYUK/CORDON"


class MainWindow(QMainWindow):
    def __init__(self, service: CordonService, *, profile_id: str = "") -> None:
        super().__init__()
        self.service = service
        self.settings = service.settings
        self._task: Task | None = None
        self._session_thread: SessionThread | None = None
        self._presence = None
        self._status = None
        self.actions_map: dict[str, QAction] = {}

        self.setWindowTitle(f"CORDON-LINUX {__version__}")
        self.resize(1280, 820)

        self._header = self._build_header()
        self._build_central()
        self.setStatusBar(QStatusBar())
        self._apply_theme()

        if profile_id:
            profile = self.settings.profile(profile_id)
            if profile:
                self.settings.selected_profile_id = profile.id
        self.refresh_profiles(select=self.settings.selected_profile_id)
        self._restore_geometry()

    # ------------------------------------------------------------------ ui setup
    def _build_header(self) -> QWidget:
        """Compact top bar: the few actions used often, the rest behind menus.

        A plain ``QToolBar`` was too wide for 1280 px windows, so the launch buttons ended up in
        the overflow menu; here the buttons are placed explicitly instead.
        """
        header = QWidget()
        header.setObjectName("header")
        layout = QHBoxLayout(header)
        layout.setContentsMargins(8, 6, 8, 6)
        layout.setSpacing(6)

        def action(text: str, slot, shortcut: str = "", tip: str = "") -> QAction:
            item = QAction(text, self)
            item.triggered.connect(slot)
            if shortcut:
                item.setShortcut(QKeySequence(shortcut))
            if tip:
                item.setToolTip(tip)
            self.addAction(item)
            self.actions_map[text] = item
            return item

        def button(text: str, slot=None, tip: str = "", primary: bool = False):
            widget = make_button(text, primary=primary)
            if slot is not None:
                widget.clicked.connect(slot)
            if tip:
                widget.setToolTip(tip)
            layout.addWidget(widget)
            return widget

        def menu_button(text: str, entries: list[QAction], tip: str = ""):
            from PySide6.QtWidgets import QMenu, QToolButton

            tool = QToolButton()
            tool.setText(text + "  ▾")
            tool.setObjectName("menuButton")
            tool.setPopupMode(QToolButton.InstantPopup)
            menu = QMenu(tool)
            for entry in entries:
                menu.addAction(entry)
            tool.setMenu(menu)
            if tip:
                tool.setToolTip(tip)
            layout.addWidget(tool)
            return tool

        # --- profile / mods
        action("Новый профиль", self.new_profile, "Ctrl+N", "Создать профиль")
        action("Настройки профиля", self.edit_profile, "Ctrl+E", "Изменить выбранный профиль")
        action("Дублировать", self.duplicate_profile, "", "Копия профиля вместе с модами")
        action("Удалить", self.delete_profile, "Ctrl+Delete", "Удалить профиль")
        button("Новый профиль", self.new_profile, "Ctrl+N")
        button("Настройки", self.edit_profile, "Ctrl+E — изменить профиль")
        menu_button(
            "Профиль",
            [self.actions_map["Дублировать"], self.actions_map["Удалить"]],
            "Дублировать или удалить профиль",
        )

        action("Добавить моды", self.add_mods, "Ctrl+O", "Добавить папки модов")
        action("Найти моды", self.scan_mods, "Ctrl+F", "Найти моды в каталоге")
        action("Установить архив", self.install_archive, "Ctrl+I", "Установить мод из архива")
        action("Импорт MO2", self.import_mo2, "", "Импортировать порядок модов из Mod Organizer 2")
        menu_button(
            "Моды",
            [
                self.actions_map["Добавить моды"],
                self.actions_map["Найти моды"],
                self.actions_map["Установить архив"],
                self.actions_map["Импорт MO2"],
            ],
            "Добавление, поиск и установка модов",
        )

        # --- maintenance
        action("Собрать", self.prepare_profile, "F5", "Собрать оверлей профиля")
        action("Проверить", self.run_checks, "F6", "Предполётные проверки")
        action("Конфликты", self.show_conflicts, "Ctrl+K", "Показать конфликты файлов")
        action("Аудит регистра", self.run_audit, "Ctrl+R", "Проверить регистр путей (Linux-специфика)")
        action("Отчёт", self.save_report, "Ctrl+P", "Сохранить отчёт о профиле")
        action("Настройки лаунчера", self.launcher_settings, "Ctrl+,", "Тема, Discord-статус, размер журнала")
        action("О программе", self.show_about, "F1", "Версия и каталоги лаунчера")
        button("Собрать", self.prepare_profile, "F5 — собрать оверлей профиля")
        button("Проверить", self.run_checks, "F6 — предполётные проверки")
        menu_button(
            "Инструменты",
            [
                self.actions_map["Конфликты"],
                self.actions_map["Аудит регистра"],
                self.actions_map["Отчёт"],
                self.actions_map["Настройки лаунчера"],
                self.actions_map["О программе"],
            ],
            "Конфликты, аудит регистра, отчёт",
        )

        layout.addStretch(1)

        self.launch_button = make_button("Запустить", primary=True)
        self.launch_button.setToolTip("Запустить игру с выбранным профилем (F9)")
        self.launch_button.clicked.connect(self.launch_profile)
        self.launch_action = QAction("Запустить", self)
        self.launch_action.setShortcut(QKeySequence("F9"))
        self.launch_action.triggered.connect(self.launch_profile)
        self.addAction(self.launch_action)
        layout.addWidget(self.launch_button)

        self.stop_button = make_button("Остановить")
        self.stop_button.setEnabled(False)
        self.stop_button.setToolTip("Завершить запущенную игру")
        self.stop_button.clicked.connect(self.stop_game)
        layout.addWidget(self.stop_button)

        return header

    def _build_central(self) -> None:
        splitter = QSplitter(Qt.Horizontal)

        left = QWidget()
        left.setMinimumWidth(300)
        left_layout = QVBoxLayout(left)
        left_layout.setContentsMargins(8, 8, 4, 8)
        header = QLabel("Профили")
        header.setObjectName("headline")
        left_layout.addWidget(header)
        self.filter_edit = QLineEdit()
        self.filter_edit.setPlaceholderText("Фильтр по названию…")
        self.filter_edit.textChanged.connect(self._apply_filter)
        left_layout.addWidget(self.filter_edit)
        self.profile_list = ProfileList()
        self.profile_list.currentItemChanged.connect(self._profile_changed)
        self.profile_list.profile_activated.connect(self._activate_profile)
        left_layout.addWidget(self.profile_list, 1)
        splitter.addWidget(left)

        right = QWidget()
        right_layout = QVBoxLayout(right)
        right_layout.setContentsMargins(4, 8, 8, 8)
        self.status_strip = StatusStrip()
        right_layout.addWidget(self.status_strip)

        self.tabs = QTabWidget()
        right_layout.addWidget(self.tabs, 1)

        # --- mods tab
        mods_tab = QWidget()
        mods_layout = QVBoxLayout(mods_tab)
        mods_layout.setContentsMargins(6, 6, 6, 6)
        self.mod_table = ModTable()
        self.mod_table.toggled.connect(self._mod_toggled)
        self.mod_table.order_changed.connect(self._mods_reordered)
        self.mod_table.selection_changed.connect(lambda _mod_id: self._sync_mod_buttons())
        mods_layout.addWidget(self.mod_table, 1)

        mod_buttons = QHBoxLayout()
        for text, slot, tip in (
            ("Вверх", lambda: self._move_selected(-1), "Выше в списке — ниже приоритет"),
            ("Вниз", lambda: self._move_selected(1), "Ниже в списке — выше приоритет"),
            ("Включить все", lambda: self._set_all(True), ""),
            ("Отключить все", lambda: self._set_all(False), ""),
            ("Убрать из профиля", self.remove_mod, "Убрать мод из профиля"),
            ("Удалить файлы", self.delete_mod_files, "Убрать и удалить распакованные файлы мода"),
        ):
            button = make_button(text)
            button.clicked.connect(slot)
            if tip:
                button.setToolTip(tip)
            mod_buttons.addWidget(button)
        mod_buttons.addStretch(1)
        self.hint_label = QLabel("Чем ниже мод в списке, тем выше его приоритет (его файлы побеждают).")
        self.hint_label.setObjectName("dim")
        mod_buttons.addWidget(self.hint_label)
        mods_layout.addLayout(mod_buttons)
        self.tabs.addTab(mods_tab, "Моды и приоритет")

        # --- reports tab
        self.report_pane = ReportPane()
        self.tabs.addTab(self.report_pane, "Проверки и конфликты")

        # --- diagnostics tab
        diag_tab = QWidget()
        diag_layout = QVBoxLayout(diag_tab)
        diag_buttons = QHBoxLayout()
        for text, slot in (
            ("Игровой лог", self.open_game_log),
            ("Дампы", self.open_dumps),
            ("Каталог профиля", lambda: self.open_path("root")),
            ("Данные профиля", lambda: self.open_path("appdata")),
            ("Папка модов", lambda: self.open_path("mods")),
            ("Журнал лаунчера", self.open_launcher_log),
        ):
            button = make_button(text)
            button.clicked.connect(slot)
            diag_buttons.addWidget(button)
        diag_buttons.addStretch(1)
        diag_layout.addLayout(diag_buttons)
        self.diag_view = QPlainTextEdit()
        self.diag_view.setReadOnly(True)
        self.diag_view.setLineWrapMode(QPlainTextEdit.NoWrap)
        diag_layout.addWidget(self.diag_view, 1)
        self.tabs.addTab(diag_tab, "Диагностика")

        # --- log tab
        log_tab = QWidget()
        log_layout = QVBoxLayout(log_tab)
        log_buttons = QHBoxLayout()
        clear_button = make_button("Очистить")
        clear_button.clicked.connect(lambda: self.log_view.clear())
        follow_check = QCheckBox("Прокручивать за выводом")
        follow_check.setChecked(True)
        self.follow_check = follow_check
        log_buttons.addWidget(clear_button)
        log_buttons.addWidget(follow_check)
        log_buttons.addStretch(1)
        log_layout.addLayout(log_buttons)
        self.log_view = QPlainTextEdit()
        self.log_view.setReadOnly(True)
        self.log_view.setLineWrapMode(QPlainTextEdit.NoWrap)
        self.log_view.setFont(theme_mod.monospace(11))
        log_layout.addWidget(self.log_view, 1)
        self.tabs.addTab(log_tab, "Вывод игры")

        splitter.addWidget(right)
        splitter.setStretchFactor(0, 0)
        splitter.setStretchFactor(1, 1)
        splitter.setSizes([360, 920])
        self.splitter = splitter

        central = QWidget()
        central_layout = QVBoxLayout(central)
        central_layout.setContentsMargins(0, 0, 0, 0)
        central_layout.setSpacing(0)
        central_layout.addWidget(self._header)
        central_layout.addWidget(splitter, 1)
        self.setCentralWidget(central)

        self.progress = QProgressBar()
        self.progress.setRange(0, 0)
        self.progress.setVisible(False)
        self.progress.setMaximumWidth(180)
        self.statusBar().addPermanentWidget(self.progress)

    def _apply_theme(self) -> None:
        palette = theme_mod.palettes().get(self.settings.theme, theme_mod.PDA)
        self.setPalette(theme_mod.qpalette(palette))
        self.setStyleSheet(theme_mod.stylesheet(palette))
        self._palette = palette

    # ------------------------------------------------------------------ profiles
    def refresh_profiles(self, *, select: str = "") -> None:
        profiles = self.settings.profiles
        target = select or self.settings.selected_profile_id
        self.profile_list.set_profiles(profiles, target)
        self._apply_filter(self.filter_edit.text())
        if not profiles:
            self.status_strip.headline.setText("Профилей нет")
            self.status_strip.detail.setText("Нажмите «Новый профиль», чтобы начать")
            self.mod_table.setRowCount(0)
            self.launch_button.setEnabled(False)
            return
        if not self.profile_list.current_profile_id():
            self.profile_list.setCurrentRow(0)
        self._profile_changed()

    def _apply_filter(self, text: str) -> None:
        needle = text.strip().lower()
        for row in range(self.profile_list.count()):
            item = self.profile_list.item(row)
            item.setHidden(bool(needle) and needle not in item.text().lower())

    def current_profile(self) -> Profile | None:
        profile_id = self.profile_list.current_profile_id()
        if not profile_id and self.settings.profiles:
            profile_id = self.settings.profiles[0].id
        return self.settings.profile(profile_id) if profile_id else None

    def _profile_changed(self, *_args) -> None:
        profile = self.current_profile()
        if profile is None:
            self.launch_button.setEnabled(False)
            return
        self.settings.selected_profile_id = profile.id
        self.service.save()
        self.launch_button.setEnabled(self._session_thread is None)
        self.mod_table.set_mods(profile)
        self.hint_label.setText(
            "Чем ниже мод в списке, тем выше его приоритет (его файлы побеждают)."
            if not profile.is_standalone
            else "Профиль-сборка: файлы модов не подключаются, запускается только игра."
        )
        self.status_strip.headline.setText(profile.name)
        self.status_strip.detail.setText(summary_line(profile))
        self._sync_mod_buttons()
        self._refresh_status_light()

    def _activate_profile(self, profile_id: str) -> None:
        self.launch_profile()

    def new_profile(self) -> None:
        profile = Profile(id=util.new_id(), name="Новый профиль")
        dialog = ProfileDialog(profile, self.service.app, creating=True, parent=self)
        if dialog.exec() == ProfileDialog.Accepted:
            created = dialog.result_profile()
            self.settings.profiles.append(created)
            self.settings.selected_profile_id = created.id
            self.service.save()
            self.refresh_profiles(select=created.id)
            self._log(f"Создан профиль «{created.name}»")

    def edit_profile(self) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        dialog = ProfileDialog(profile, self.service.app, parent=self)
        if dialog.exec() == ProfileDialog.Accepted:
            updated = dialog.result_profile()
            index = self.settings.profiles.index(profile)
            self.settings.profiles[index] = updated
            self.service.save()
            self.refresh_profiles(select=updated.id)
            self._log(f"Профиль «{updated.name}» обновлён")

    def duplicate_profile(self) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        try:
            clone = self.service.duplicate_profile(profile.id)
        except CordonError as exc:
            self._error(str(exc))
            return
        self.refresh_profiles(select=clone.id)
        self._log(f"Создана копия: «{clone.name}»")

    def delete_profile(self) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        answer = QMessageBox.question(
            self,
            "Удаление профиля",
            f"Удалить профиль «{profile.name}»?\n\n"
            "Каталог профиля и распакованные моды будут удалены, папки самих модов — нет.",
            QMessageBox.Yes | QMessageBox.No,
            QMessageBox.No,
        )
        if answer != QMessageBox.Yes:
            return
        name = profile.name
        self.service.delete_profile(profile.id)
        self.refresh_profiles()
        self._log(f"Профиль «{name}» удалён")

    # ------------------------------------------------------------------ mods
    def _sync_mod_buttons(self) -> None:
        has_selection = bool(self.mod_table.selected_mod_id())
        for button in self.findChildren(type(self.stop_button)):
            if button.text() in ("Вверх", "Вниз", "Убрать из профиля", "Удалить файлы"):
                button.setEnabled(has_selection)

    def _mod_toggled(self, mod_id: str, enabled: bool) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        mod = profile.mod_by_id(mod_id)
        if mod is None:
            return
        mod.enabled = enabled
        self.service.save()
        self.status_strip.detail.setText(summary_line(profile))
        self._log(("Включён" if enabled else "Отключён") + f" мод «{mod.name}»")

    def _mods_reordered(self, order: list[str]) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        layers.reorder_mods(profile, order)
        self.service.save()
        self._log("Порядок модов изменён: " + ", ".join(mod.name for mod in profile.mods[:5]) + "…")
        self._refresh_status_light()

    def _move_selected(self, delta: int) -> None:
        profile = self.current_profile()
        mod_id = self.mod_table.selected_mod_id()
        if profile is None or not mod_id:
            return
        index = next((i for i, mod in enumerate(profile.mods) if mod.id == mod_id), None)
        if index is None:
            return
        target = max(0, min(len(profile.mods) - 1, index + delta))
        if target == index:
            return
        mod = profile.mods.pop(index)
        profile.mods.insert(target, mod)
        self.service.save()
        self.mod_table.set_mods(profile)
        self.mod_table.selectRow(target)
        self._refresh_status_light()

    def _set_all(self, enabled: bool) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        for mod in profile.mods:
            mod.enabled = enabled
        self.service.save()
        self.mod_table.set_mods(profile)
        self.status_strip.detail.setText(summary_line(profile))

    def add_mods(self) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        directories = QFileDialog.getExistingDirectory(
            self, "Каталог с модами", os.path.expanduser("~"), QFileDialog.ShowDirsOnly
        )
        if not directories:
            return
        self._run_task(
            "Поиск модов…",
            lambda progress: self.service.add_mods_from_folders(profile, [directories]),
            self._mods_added,
        )

    def scan_mods(self) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        directory = QFileDialog.getExistingDirectory(self, "Каталог для поиска модов", os.path.expanduser("~"))
        if not directory:
            return

        def work(progress):
            result = self.service.scan_folder(profile, directory)
            return result

        def done(result):
            if not result.found:
                self._info("Моды не найдены", "В выбранном каталоге не найдено папок, похожих на моды S.T.A.L.K.E.R.")
                return
            answer = QMessageBox.question(
                self,
                "Найдены моды",
                f"Найдено модов: {len(result.found)}.\n\nДобавить их в профиль «{profile.name}»?",
                QMessageBox.Yes | QMessageBox.No,
                QMessageBox.Yes,
            )
            if answer != QMessageBox.Yes:
                return
            added = self.service.add_mods_from_folders(profile, [entry.path for entry in result.found])
            self._mods_added(added)

        self._run_task("Сканирование…", work, done)

    def _mods_added(self, added: int) -> None:
        profile = self.current_profile()
        self.service.save()
        if profile is not None:
            self.mod_table.set_mods(profile)
            self.status_strip.detail.setText(summary_line(profile))
        self._log(f"Добавлено модов: {added}")

    def install_archive(self) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        pattern = "Архивы модов (" + " ".join(f"*{suffix}" for suffix in mods_mod.ARCHIVE_SUFFIXES) + ");;Все файлы (*)"
        archives, _filter = QFileDialog.getOpenFileNames(self, "Установить моды из архивов", os.path.expanduser("~"), pattern)
        if not archives:
            return

        def work(progress):
            installed = []
            for archive in archives:
                outcome = self.service.install_archive(profile, archive, progress=progress)
                installed.append(outcome.mod.name)
            return installed

        def done(names):
            self.service.save()
            self.mod_table.set_mods(profile)
            self._log("Установлены моды: " + ", ".join(names))
            QMessageBox.information(self, "Готово", "Установлено модов: " + str(len(names)))

        self._run_task("Распаковка архивов…", work, done)

    def remove_mod(self) -> None:
        profile = self.current_profile()
        mod_id = self.mod_table.selected_mod_id()
        if profile is None or not mod_id:
            return
        mod = profile.mod_by_id(mod_id)
        if mod is None:
            return
        self.service.remove_mod(profile, mod_id, delete_files=False)
        self.mod_table.set_mods(profile)
        self._log(f"Мод «{mod.name}» убран из профиля (файлы на диске сохранены)")

    def delete_mod_files(self) -> None:
        profile = self.current_profile()
        mod_id = self.mod_table.selected_mod_id()
        if profile is None or not mod_id:
            return
        mod = profile.mod_by_id(mod_id)
        if mod is None:
            return
        answer = QMessageBox.question(
            self,
            "Удаление мода",
            f"Убрать «{mod.name}» и удалить его файлы?\n\n{mod.path}",
            QMessageBox.Yes | QMessageBox.No,
            QMessageBox.No,
        )
        if answer != QMessageBox.Yes:
            return
        self.service.remove_mod(profile, mod_id, delete_files=True)
        self.mod_table.set_mods(profile)
        self._log(f"Мод «{mod.name}» удалён")

    def import_mo2(self) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        dialog = Mo2Dialog(self)
        if dialog.exec() != Mo2Dialog.Accepted:
            return
        path, modlist_only, no_overwrite = dialog.values()
        if not path:
            return
        from ..core.mo2 import apply_modlist_only, apply_preview, build_preview

        try:
            preview = build_preview(path)
        except Exception as exc:  # noqa: BLE001
            self._error(f"Не удалось прочитать MO2: {exc}")
            return
        if modlist_only:
            count = apply_modlist_only(profile, preview)
        else:
            count = apply_preview(profile, preview, use_overwrite=not no_overwrite)
        self.service.save()
        self.mod_table.set_mods(profile)
        self._log(f"Импорт MO2: применено записей — {count}")

    # ------------------------------------------------------------------ checks
    def prepare_profile(self, *, force: bool = True) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        if profile.is_standalone:
            self.service.workspace_for(profile).ensure_root()
            self._log("Профиль-сборка: оверлей не нужен")
            return

        def work(progress):
            return self.service.prepare(profile, force=force, progress=progress)

        def done(result):
            plan, workspace, overlay = result
            stats = plan.stats()
            self.report_pane.set_text(
                f"{overlay.summary}\n"
                f"Файлов: {stats.total_files}, пересечений: {stats.overlapping_files}, "
                f"объём: {util.human_size(stats.total_bytes)}\n"
                f"fsgame.ltx: {workspace.fsgame_path}",
                "ok",
            )
            self.tabs.setCurrentWidget(self.report_pane)
            self._log(overlay.summary)
            self._refresh_status_light()

        self._run_task("Сборка оверлея…", work, done)

    def run_checks(self) -> None:
        profile = self.current_profile()
        if profile is None:
            return

        def work(progress):
            plan = self.service.plan_for(profile, progress=progress)
            report = preflight.run(profile, self.service.app, plan=plan, workspace=self.service.workspace_for(profile))
            return report

        def done(report):
            lines = [(check.level, self._format_check(check)) for check in report.checks]
            self.report_pane.set_lines([("plain", report.headline), ("plain", "")] + lines)
            self.tabs.setCurrentWidget(self.report_pane)
            self._status = report
            self._refresh_status_light()

        self._run_task("Проверка профиля…", work, done)

    @staticmethod
    def _format_check(check) -> str:
        hint = f"\n     → {check.hint}" if getattr(check, "hint", "") else ""
        return f"[{check.badge}] {check.title}\n     {check.detail}{hint}"

    def show_conflicts(self) -> None:
        profile = self.current_profile()
        if profile is None:
            return

        def work(progress):
            plan = self.service.plan_for(profile, progress=progress)
            return self.service.conflict_report(profile, plan=plan)

        def done(report):
            text = report.to_text()
            if report.redundant:
                text += "\n\nПолностью перекрытые моды:\n" + "\n".join(
                    f"  • {item.name}" for item in report.redundant
                )
            dialog = ReportDialog(f"Конфликты — {profile.name}", text, self)
            dialog.exec()

        self._run_task("Анализ конфликтов…", work, done)

    def run_audit(self) -> None:
        profile = self.current_profile()
        if profile is None:
            return

        def work(progress):
            plan = self.service.plan_for(profile, progress=progress)
            return self.service.audit_case(profile, plan=plan, fix=False)

        def done(result):
            text = result.to_text()
            if result.issues:
                answer = QMessageBox.question(
                    self,
                    "Аудит регистра",
                    f"Найдено проблем с регистром: {len(result.issues)}.\n\n"
                    "Создать ссылки-алиасы, чтобы движок нашёл файлы?\n"
                    "Это безопасно: файлы модов не изменяются.",
                    QMessageBox.Yes | QMessageBox.No,
                    QMessageBox.Yes,
                )
                if answer == QMessageBox.Yes:
                    self._audit_fix(profile)
                    return
            dialog = ReportDialog(f"Аудит регистра — {profile.name}", text, self)
            dialog.exec()

        self._run_task("Аудит чувствительности к регистру…", work, done)

    def _audit_fix(self, profile) -> None:
        def work(progress):
            return self.service.audit_case(profile, fix=True)

        def done(result):
            self._log(f"Создано алиасов: {len(result.created)}")
            QMessageBox.information(
                self,
                "Аудит регистра",
                "Создано ссылок-алиасов: "
                + str(len(result.created))
                + "\nОни будут восстанавливаться при каждой пересборке профиля.",
            )

        self._run_task("Создание алиасов…", work, done)

    def save_report(self) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        try:
            path = self.service.report(profile)
        except CordonError as exc:
            self._error(str(exc))
            return
        self._log(f"Отчёт сохранён: {path}")
        QMessageBox.information(self, "Отчёт", f"Отчёт сохранён:\n{path}")

    # ------------------------------------------------------------------ launch
    def launch_profile(self) -> None:
        profile = self.current_profile()
        if profile is None or self._session_thread is not None:
            return
        if profile.is_standalone:
            self._start_session(profile, force=False)
            return

        def work(progress):
            plan = self.service.plan_for(profile, progress=progress)
            report = preflight.run(profile, self.service.app, plan=plan, workspace=self.service.workspace_for(profile))
            return report

        def done(report):
            self._status = report
            if not report.ok:
                lines = [(check.level, self._format_check(check)) for check in report.checks]
                self.report_pane.set_lines([("plain", report.headline), ("plain", "")] + lines)
                self.tabs.setCurrentWidget(self.report_pane)
                answer = QMessageBox.question(
                    self,
                    "Есть проблемы",
                    f"{report.headline}\n\nВсё равно запустить игру?",
                    QMessageBox.Yes | QMessageBox.No,
                    QMessageBox.No,
                )
                if answer != QMessageBox.Yes:
                    return
            self._start_session(profile, force=False)

        self._run_task("Подготовка запуска…", work, done)

    def _start_session(self, profile: Profile, *, force: bool) -> None:
        self.log_view.clear()
        self.tabs.setCurrentWidget(self.log_view.parentWidget().parentWidget())
        self.progress.setVisible(True)
        self.launch_button.setEnabled(False)
        self.stop_button.setEnabled(True)

        thread = SessionThread(profile, self.service.app, service=self.service, force_rebuild=force, parent=self)
        thread.line.connect(self._append_game_line)
        thread.started.connect(self._on_game_started)
        thread.finished.connect(self._on_game_finished)
        thread.failed.connect(self._on_game_failed)
        self._session_thread = thread
        thread.start()

    def _on_game_started(self, command_line: str) -> None:
        self._log(f"Запуск: {command_line}")
        self.statusBar().showMessage("Игра запущена")
        self._start_presence()

    # ------------------------------------------------------------------ discord
    def _start_presence(self) -> None:
        profile = self.current_profile()
        if profile is None or not self.settings.discord_presence:
            return
        from ..core.discord import announce_launch

        self._presence = announce_launch(self.settings.discord_client_id, profile.name, game_id=profile.game_id)
        if self._presence is None:
            self._log("Discord не найден — статус не публикуется")
        else:
            self._log("Статус Discord включён")

    def _stop_presence(self) -> None:
        if self._presence is None:
            return
        try:
            self._presence.clear()
            self._presence.close()
        except Exception:  # noqa: BLE001 - presence must never break the UI
            pass
        self._presence = None

    def _append_game_line(self, line: str) -> None:
        self.log_view.appendPlainText(line)
        if self.follow_check.isChecked():
            self.log_view.verticalScrollBar().setValue(self.log_view.verticalScrollBar().maximum())

    def _on_game_finished(self, code: int, duration: float) -> None:
        self._session_thread = None
        self._stop_presence()
        self.progress.setVisible(False)
        self.launch_button.setEnabled(True)
        self.stop_button.setEnabled(False)
        self.statusBar().showMessage(f"Игра завершилась с кодом {code} за {util.human_duration(duration)}")
        self._log(f"Процесс завершён: код {code}, время сессии {util.human_duration(duration)}")
        profile = self.current_profile()
        if profile is not None:
            self.status_strip.detail.setText(summary_line(profile))
            self.service.save()
        self._load_diagnostics(profile)

    def _on_game_failed(self, message: str) -> None:
        self._session_thread = None
        self._stop_presence()
        self.progress.setVisible(False)
        self.launch_button.setEnabled(True)
        self.stop_button.setEnabled(False)
        self._error(f"Не удалось запустить игру: {message}")

    def stop_game(self) -> None:
        if self._session_thread is None:
            return
        answer = QMessageBox.question(
            self,
            "Остановить игру",
            "Завершить запущенную игру? Несохранённый прогресс будет потерян.",
            QMessageBox.Yes | QMessageBox.No,
            QMessageBox.No,
        )
        if answer == QMessageBox.Yes:
            self._log("Запрошено завершение игры…")
            self._session_thread.stop()

    # ------------------------------------------------------------------ diagnostics
    def _load_diagnostics(self, profile: Profile | None) -> None:
        if profile is None:
            return
        workspace = self.service.workspace_for(profile)
        diag = diagnostics.collect(workspace.appdata)
        lines = [diag.to_text(), ""]
        overlay_result = workspace.verify()
        lines.append("Проверка оверлея:")
        lines.extend(f"  {line}" for line in overlay_result)
        self.diag_view.setPlainText("\n".join(lines))

    def open_game_log(self) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        workspace = self.service.workspace_for(profile)
        diag = diagnostics.collect(workspace.appdata)
        if diag.log_path:
            self._open_file(diag.log_path)
        else:
            self._info("Лог не найден", "Игровой лог ещё не создан — сначала запустите игру.")

    def open_dumps(self) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        dumps = os.path.join(self.service.workspace_for(profile).appdata, "dumps")
        if os.path.isdir(dumps):
            self._open_file(dumps)
        else:
            self._info("Дампов нет", "Каталог дампов пока не создан.")

    def open_path(self, what: str) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        workspace = self.service.workspace_for(profile)
        mapping = {
            "root": workspace.root,
            "appdata": workspace.appdata,
            "mods": self.service.app.mod_storage(profile.id, profile.mod_storage_path),
        }
        path = mapping[what]
        util.ensure_dir(path)
        self._open_file(path)

    def open_launcher_log(self) -> None:
        path = self.service.app.launcher_log
        if os.path.isfile(path):
            self._open_file(path)
        else:
            self._info("Журнал пуст", "Журнал лаунчера ещё не создан.")

    def _open_file(self, path: str) -> None:
        from PySide6.QtCore import QUrl
        from PySide6.QtGui import QDesktopServices

        if not os.path.exists(path):
            self._info("Не найдено", path)
            return
        QDesktopServices.openUrl(QUrl.fromLocalFile(path))

    def _refresh_status_light(self) -> None:
        profile = self.current_profile()
        if profile is None:
            return
        try:
            plan = self.service.plan_for(profile)
            stats = plan.stats()
            detail = f"{stats.total_files} файлов · {util.human_size(stats.total_bytes)}"
            if stats.overlapping_files:
                detail += f" · пересечений {stats.overlapping_files}"
            self.statusBar().showMessage(detail)
        except Exception:  # noqa: BLE001 - the status bar is cosmetic
            self.statusBar().showMessage("готово")

    # ------------------------------------------------------------------ misc ui
    def _run_task(self, message: str, work, done) -> None:
        if self._task is not None:
            return
        self.progress.setVisible(True)
        self.statusBar().showMessage(message)
        task = Task(work, self)

        def finished(result) -> None:
            self._finish_task()
            try:
                done(result)
            except Exception as exc:  # noqa: BLE001
                self._error(str(exc))

        def failed(text: str) -> None:
            self._finish_task()
            self._error(text)

        task.finished_ok.connect(finished)
        task.failed.connect(failed)
        task.progressed.connect(lambda text: self.statusBar().showMessage(text))
        self._task = task
        task.start()

    def _finish_task(self) -> None:
        self._task = None
        self.progress.setVisible(False)
        self.statusBar().clearMessage()

    def _log(self, text: str) -> None:
        self.log_view.appendPlainText(text)
        self.service.logger.info("%s", text)

    def _info(self, title: str, text: str) -> None:
        QMessageBox.information(self, title, text)

    def _error(self, text: str) -> None:
        self._log("Ошибка: " + text)
        QMessageBox.critical(self, "Ошибка", text)

    def launcher_settings(self) -> None:
        dialog = LauncherSettingsDialog(self.settings, self)
        if dialog.exec() == LauncherSettingsDialog.Accepted:
            dialog.apply_to(self.settings)
            self.service.save()
            self._apply_theme()
            self._log("Настройки лаунчера обновлены")

    def show_about(self) -> None:
        AboutDialog(self.service.app, upstream=UPSTREAM, version=__version__, parent=self).exec()

    # ------------------------------------------------------------------ persistence
    def showEvent(self, event) -> None:  # noqa: N802 - Qt API
        super().showEvent(event)
        if not getattr(self, "_sizes_applied", False):
            self.splitter.setSizes([360, max(320, self.width() - 380)])
            self._sizes_applied = True

    def _restore_geometry(self) -> None:
        geometry = getattr(self.settings, "window_geometry", "")
        if geometry:
            try:
                self.restoreGeometry(bytes.fromhex(geometry))
            except ValueError:  # pragma: no cover - corrupt value in settings
                pass

    def closeEvent(self, event) -> None:  # noqa: N802 - Qt API
        if self._session_thread is not None:
            answer = QMessageBox.question(
                self,
                "Игра запущена",
                "Игра всё ещё работает. Закрыть лаунчер? Игра продолжит работу.",
                QMessageBox.Yes | QMessageBox.No,
                QMessageBox.No,
            )
            if answer != QMessageBox.Yes:
                event.ignore()
                return
        self._stop_presence()
        try:
            self.settings.window_geometry = bytes(self.saveGeometry().toHex()).decode("ascii")
            self.service.save()
        except Exception:  # noqa: BLE001 - never block closing
            pass
        event.accept()


def start_timer_update(window: MainWindow) -> None:
    """Periodically refresh the played-time of an active profile (cheap, once a minute)."""
    timer = QTimer(window)
    timer.setInterval(60_000)
    timer.timeout.connect(lambda: window.refresh_profiles(select=window.settings.selected_profile_id))
    timer.start()
