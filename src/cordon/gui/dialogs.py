"""Dialogs: profile editor, Mod Organizer 2 import, about box and simple reports."""

from __future__ import annotations

import copy
import os

from PySide6.QtCore import Qt
from PySide6.QtWidgets import (
    QCheckBox,
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QFileDialog,
    QFormLayout,
    QGroupBox,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QMessageBox,
    QPlainTextEdit,
    QPushButton,
    QVBoxLayout,
    QWidget,
)

from ..core import engine as engine_mod
from ..core import preflight, util, xray
from ..core.models import (
    BACKEND_DIRECT,
    BACKEND_FUSE,
    BACKEND_LINK,
    ENGINE_FLAG_LABELS,
    GAME_IDS,
    PROFILE_KIND_MODS,
    PROFILE_KIND_STANDALONE,
    Profile,
)
from ..core.paths import AppPaths

BACKEND_LABELS = {
    BACKEND_LINK: "Символические ссылки (без прав root)",
    BACKEND_FUSE: "fuse-overlayfs (настоящий оверлей, только для чтения игры)",
    BACKEND_DIRECT: "Напрямую из папок модов (ничего не собирается)",
}

BACKEND_HINTS = {
    BACKEND_LINK: "Самый совместимый вариант: файлы модов объединяются ссылками внутри каталога профиля.",
    BACKEND_FUSE: "Нужен пакет fuse-overlayfs. Оверлей монтируется при запуске и отключается после выхода.",
    BACKEND_DIRECT: "Файлы модов подключаются напрямую; конфликты разрешает движок по порядку.",
}


def _path_row(edit: QLineEdit, callback, label: str = "…") -> QWidget:
    widget = QWidget()
    layout = QHBoxLayout(widget)
    layout.setContentsMargins(0, 0, 0, 0)
    layout.addWidget(edit, 1)
    button = QPushButton(label)
    button.setFixedWidth(34)
    button.clicked.connect(callback)
    layout.addWidget(button)
    return widget


class ProfileDialog(QDialog):
    """Create or edit a profile. Mutates ``profile`` only when accepted."""

    def __init__(self, profile: Profile, app: AppPaths, *, creating: bool = False, parent=None) -> None:
        super().__init__(parent)
        self._profile = profile
        self._app = app
        self._creating = creating
        self.setWindowTitle("Новый профиль" if creating else f"Профиль «{profile.name}»")
        self.setMinimumWidth(720)

        layout = QVBoxLayout(self)
        form = QFormLayout()
        form.setLabelAlignment(Qt.AlignRight)

        self.name_edit = QLineEdit(profile.name)
        form.addRow("Название", self.name_edit)

        self.kind_combo = QComboBox()
        self.kind_combo.addItem("Игра + моды (оверлей профиля)", PROFILE_KIND_MODS)
        self.kind_combo.addItem("Готовая сборка (без оверлея)", PROFILE_KIND_STANDALONE)
        self.kind_combo.setCurrentIndex(1 if profile.is_standalone else 0)
        self.kind_combo.currentIndexChanged.connect(self._sync_enabled)
        form.addRow("Тип профиля", self.kind_combo)

        self.game_edit = QLineEdit(profile.game_path)
        self.game_edit.setPlaceholderText("каталог с gamedata/ и fsgame.ltx")
        form.addRow("Каталог игры", _path_row(self.game_edit, self._pick_game))

        self.engine_edit = QLineEdit(profile.engine_path)
        self.engine_edit.setPlaceholderText("оставьте пустым — движок будет найден автоматически")
        form.addRow("Движок (OpenXRay)", _path_row(self.engine_edit, self._pick_engine))

        self.engine_data_edit = QLineEdit(profile.engine_data_path)
        self.engine_data_edit.setPlaceholderText("например /usr/share/openxray")
        form.addRow("Данные движка", _path_row(self.engine_data_edit, self._pick_engine_data))

        self.executable_edit = QLineEdit(profile.executable_relative)
        self.executable_edit.setPlaceholderText("bin/xr_3da")
        self.executable_edit.setToolTip(
            "Путь к исполняемому файлу относительно каталога движка.\n"
            "В deb-сборке OpenXRay это /usr/games/xr_3da, в portable — bin/xr_3da."
        )
        find_button = QPushButton("Найти")
        find_button.clicked.connect(self._detect)
        executable_row = QWidget()
        executable_layout = QHBoxLayout(executable_row)
        executable_layout.setContentsMargins(0, 0, 0, 0)
        executable_layout.addWidget(self.executable_edit, 1)
        executable_layout.addWidget(find_button)
        form.addRow("Исполняемый файл", executable_row)

        self.backend_combo = QComboBox()
        for backend, label in BACKEND_LABELS.items():
            self.backend_combo.addItem(label, backend)
        index = self.backend_combo.findData(profile.backend)
        self.backend_combo.setCurrentIndex(max(0, index))
        self.backend_combo.currentIndexChanged.connect(self._sync_backend_hint)
        form.addRow("Способ сборки", self.backend_combo)

        self.backend_hint = QLabel("")
        self.backend_hint.setWordWrap(True)
        self.backend_hint.setObjectName("dim")
        form.addRow("", self.backend_hint)

        self.game_id_combo = QComboBox()
        for game_id in GAME_IDS:
            self.game_id_combo.addItem(xray.describe_game_id(game_id), game_id)
        index = self.game_id_combo.findData(profile.game_id)
        self.game_id_combo.setCurrentIndex(max(0, index))
        form.addRow("Тип игры", self.game_id_combo)

        self.appdata_combo = QComboBox()
        self.appdata_combo.addItem("Внутри профиля (-overlaypath)", "profile")
        self.appdata_combo.addItem("Общая папка игры", "shared")
        self.appdata_combo.setCurrentIndex(0 if profile.appdata_mode != "shared" else 1)
        form.addRow("Данные игры", self.appdata_combo)

        self.root_edit = QLineEdit(profile.root_path)
        self.root_edit.setPlaceholderText(app.profiles_root)
        form.addRow("Каталог профиля", _path_row(self.root_edit, self._pick_root))

        self.arguments_edit = QLineEdit(profile.launch_arguments)
        self.arguments_edit.setPlaceholderText("дополнительные аргументы, например: -nointro")
        form.addRow("Аргументы запуска", self.arguments_edit)

        self.description_edit = QPlainTextEdit(profile.description)
        self.description_edit.setFixedHeight(56)
        form.addRow("Описание", self.description_edit)

        layout.addLayout(form)

        flags_box = QGroupBox("Ключи движка")
        flags_layout = QVBoxLayout(flags_box)
        self.flag_checks: dict[str, QCheckBox] = {}
        for flag, (label, tooltip) in ENGINE_FLAG_LABELS.items():
            check = QCheckBox(f"{label}  ({flag})")
            check.setToolTip(tooltip)
            check.setChecked(flag in profile.engine_flags)
            self.flag_checks[flag] = check
            flags_layout.addWidget(check)
        self.overlay_path_check = QCheckBox("Переносить данные профиля ключом -overlaypath")
        self.overlay_path_check.setChecked(profile.use_overlay_path)
        flags_layout.addWidget(self.overlay_path_check)
        self.isolate_check = QCheckBox("Свои логи, сохранения и скриншоты у каждого профиля")
        self.isolate_check.setChecked(profile.isolate_appdata)
        flags_layout.addWidget(self.isolate_check)
        layout.addWidget(flags_box)

        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        self.check_button = buttons.addButton("Проверить", QDialogButtonBox.ActionRole)
        self.check_button.clicked.connect(self._run_checks)
        buttons.accepted.connect(self._accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

        self._sync_backend_hint()
        self._sync_enabled()

    # ------------------------------------------------------------------ helpers
    def _pick_game(self) -> None:
        path = QFileDialog.getExistingDirectory(self, "Каталог игры", self.game_edit.text() or os.path.expanduser("~"))
        if path:
            self.game_edit.setText(path)
            self._autofill_from_game(path)

    def _pick_engine(self) -> None:
        path = QFileDialog.getExistingDirectory(self, "Каталог движка", self.engine_edit.text() or "/usr")
        if path:
            self.engine_edit.setText(path)

    def _pick_engine_data(self) -> None:
        path = QFileDialog.getExistingDirectory(
            self, "Каталог данных движка", self.engine_data_edit.text() or "/usr/share/openxray"
        )
        if path:
            self.engine_data_edit.setText(path)

    def _pick_root(self) -> None:
        start = self.root_edit.text() or self._app.profiles_root
        path = QFileDialog.getExistingDirectory(self, "Каталог профиля", start)
        if path:
            self.root_edit.setText(path)

    def _autofill_from_game(self, path: str) -> None:
        game_id = xray.detect_game_id(path, declared="auto")
        index = self.game_id_combo.findData(game_id)
        if index >= 0:
            self.game_id_combo.setCurrentIndex(index)
        if not self.engine_edit.text() and engine_mod.find_engine(self._scratch()) is None:
            self.engine_edit.setText(path)

    def _detect(self) -> None:
        scratch = self._scratch()
        info = engine_mod.find_engine(scratch)
        if info is None:
            QMessageBox.warning(
                self,
                "Движок не найден",
                "Не удалось найти исполняемый файл OpenXRay.\n\n"
                "Укажите каталог движка вручную или путь к файлу xr_3da.",
            )
            return
        self.engine_edit.setText(info.engine_root)
        if info.data_root and info.data_root != util.norm(self.game_edit.text()):
            self.engine_data_edit.setText(info.data_root)
        if info.executable.startswith(info.engine_root + os.sep):
            self.executable_edit.setText(os.path.relpath(info.executable, info.engine_root))
        else:
            self.executable_edit.setText(info.executable)
        QMessageBox.information(self, "Движок найден", info.describe())

    def _sync_backend_hint(self) -> None:
        backend = self.backend_combo.currentData()
        self.backend_hint.setText(BACKEND_HINTS.get(backend, ""))
        standalone = self.kind_combo.currentData() == PROFILE_KIND_STANDALONE
        self.backend_combo.setEnabled(not standalone)
        self.overlay_path_check.setEnabled(not standalone or True)

    def _sync_enabled(self) -> None:
        standalone = self.kind_combo.currentData() == PROFILE_KIND_STANDALONE
        self.backend_combo.setEnabled(not standalone)
        self._sync_backend_hint()

    def _scratch(self) -> Profile:
        profile = copy.deepcopy(self._profile)
        profile.name = self.name_edit.text().strip() or profile.name
        profile.kind = self.kind_combo.currentData()
        profile.game_path = util.norm(self.game_edit.text().strip()) if self.game_edit.text().strip() else ""
        profile.engine_path = util.norm(self.engine_edit.text().strip()) if self.engine_edit.text().strip() else ""
        profile.engine_data_path = (
            util.norm(self.engine_data_edit.text().strip()) if self.engine_data_edit.text().strip() else ""
        )
        profile.executable_relative = self.executable_edit.text().strip() or "bin/xr_3da"
        profile.backend = self.backend_combo.currentData()
        profile.game_id = self.game_id_combo.currentData()
        profile.appdata_mode = self.appdata_combo.currentData()
        profile.root_path = util.norm(self.root_edit.text().strip()) if self.root_edit.text().strip() else ""
        profile.launch_arguments = self.arguments_edit.text().strip()
        profile.engine_flags = [flag for flag, check in self.flag_checks.items() if check.isChecked()]
        profile.use_overlay_path = self.overlay_path_check.isChecked()
        profile.isolate_appdata = self.isolate_check.isChecked()
        profile.description = self.description_edit.toPlainText().strip()
        return profile

    def _run_checks(self) -> None:
        scratch = self._scratch()
        try:
            report = preflight.run(scratch, self._app)
        except Exception as exc:  # noqa: BLE001 - the dialog must stay usable
            QMessageBox.critical(self, "Проверка не удалась", str(exc))
            return
        box = QMessageBox(self)
        box.setWindowTitle("Проверка профиля")
        box.setText(report.headline)
        box.setDetailedText(report.to_text())
        box.setIcon(QMessageBox.Information if report.ok else QMessageBox.Warning)
        box.exec()

    def _accept(self) -> None:
        if not self.name_edit.text().strip():
            QMessageBox.warning(self, "Проверьте данные", "Укажите название профиля.")
            return
        game = self.game_edit.text().strip()
        if game and not os.path.isdir(game):
            QMessageBox.warning(self, "Проверьте данные", f"Каталог игры не найден:\n{game}")
            return
        engine_path = self.engine_edit.text().strip()
        if engine_path and not os.path.isdir(engine_path):
            QMessageBox.warning(self, "Проверьте данные", f"Каталог движка не найден:\n{engine_path}")
            return
        self.accept()

    def result_profile(self) -> Profile:
        return self._scratch()


class LauncherSettingsDialog(QDialog):
    """Launcher-wide preferences (theme, presence, log size)."""

    def __init__(self, settings, parent=None) -> None:
        super().__init__(parent)
        self.setWindowTitle("Настройки лаунчера")
        self.setMinimumWidth(560)
        self._settings = settings
        layout = QVBoxLayout(self)
        form = QFormLayout()

        self.theme_combo = QComboBox()
        self.theme_combo.addItem("PDA (тёмная, янтарные акценты)", "pda")
        self.theme_combo.addItem("Классическая (светлая)", "classic")
        index = self.theme_combo.findData(getattr(settings, "theme", "pda"))
        self.theme_combo.setCurrentIndex(max(0, index))
        form.addRow("Оформление", self.theme_combo)

        self.discord_check = QCheckBox("Показывать статус в Discord во время игры")
        self.discord_check.setChecked(bool(getattr(settings, "discord_presence", False)))
        form.addRow("", self.discord_check)

        self.discord_id_edit = QLineEdit(str(getattr(settings, "discord_client_id", "")))
        self.discord_id_edit.setPlaceholderText("Application ID приложения Discord")
        form.addRow("Discord Application ID", self.discord_id_edit)

        self.log_lines_edit = QLineEdit(str(getattr(settings, "keep_launcher_log_lines", 4000)))
        self.log_lines_edit.setPlaceholderText("сколько строк вывода игры хранить в окне")
        form.addRow("Строк вывода в окне", self.log_lines_edit)

        layout.addLayout(form)

        hint = QLabel(
            "Статус в Discord работает через локальный сокет Discord (нужен запущенный клиент).\n"
            "Настройки профилей находятся в диалоге «Настройки» на главной панели."
        )
        hint.setObjectName("dim")
        hint.setWordWrap(True)
        layout.addWidget(hint)

        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

    def apply_to(self, settings) -> None:
        settings.theme = self.theme_combo.currentData()
        settings.discord_presence = self.discord_check.isChecked()
        client_id = self.discord_id_edit.text().strip()
        if client_id:
            settings.discord_client_id = client_id
        try:
            lines = int(self.log_lines_edit.text().strip() or 4000)
        except ValueError:
            lines = 4000
        settings.keep_launcher_log_lines = max(200, min(200_000, lines))


class Mo2Dialog(QDialog):
    """Import ``modlist.txt`` from a Mod Organizer 2 instance."""

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        self.setWindowTitle("Импорт из Mod Organizer 2")
        self.setMinimumWidth(760)

        layout = QVBoxLayout(self)
        form = QFormLayout()

        self.path_edit = QLineEdit()
        self.path_edit.setPlaceholderText("каталог MO2, каталог профиля или путь к modlist.txt")
        form.addRow("Путь MO2", _path_row(self.path_edit, self._pick))
        layout.addLayout(form)

        self.modlist_only_check = QCheckBox("Только порядок и включённость (моды уже добавлены в профиль)")
        self.no_overwrite_check = QCheckBox("Не подключать папку overwrite")
        layout.addWidget(self.modlist_only_check)
        layout.addWidget(self.no_overwrite_check)

        self.preview = QPlainTextEdit()
        self.preview.setReadOnly(True)
        self.preview.setFont(_mono())
        layout.addWidget(self.preview, 1)

        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        self.scan_button = buttons.addButton("Предпросмотр", QDialogButtonBox.ActionRole)
        self.scan_button.clicked.connect(self.scan)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

    def _pick(self) -> None:
        path = QFileDialog.getExistingDirectory(self, "Каталог Mod Organizer 2", os.path.expanduser("~"))
        if path:
            self.path_edit.setText(path)
            self.scan()

    def scan(self) -> None:
        from ..core.mo2 import build_preview

        path = self.path_edit.text().strip()
        if not path:
            return
        try:
            self.preview.setPlainText(build_preview(path).to_text())
        except Exception as exc:  # noqa: BLE001 - show the problem in the dialog
            self.preview.setPlainText(f"Не удалось прочитать MO2: {exc}")

    def values(self) -> tuple[str, bool, bool]:
        return self.path_edit.text().strip(), self.modlist_only_check.isChecked(), self.no_overwrite_check.isChecked()


class ReportDialog(QDialog):
    """Plain text report with a copy button (used for conflicts, audits and diagnostics)."""

    def __init__(self, title: str, text: str, parent=None) -> None:
        super().__init__(parent)
        self.setWindowTitle(title)
        self.resize(900, 620)
        layout = QVBoxLayout(self)
        self.view = QPlainTextEdit(text)
        self.view.setReadOnly(True)
        self.view.setFont(_mono())
        self.view.setLineWrapMode(QPlainTextEdit.NoWrap)
        layout.addWidget(self.view, 1)
        buttons = QDialogButtonBox(QDialogButtonBox.Close)
        copy_button = buttons.addButton("Копировать", QDialogButtonBox.ActionRole)
        copy_button.clicked.connect(self._copy)
        buttons.rejected.connect(self.reject)
        buttons.accepted.connect(self.accept)
        layout.addWidget(buttons)

    def _copy(self) -> None:
        from PySide6.QtWidgets import QApplication

        QApplication.clipboard().setText(self.view.toPlainText())


def _mono():
    from .theme import monospace

    return monospace(12)


class AboutDialog(QDialog):
    def __init__(self, app: AppPaths, *, upstream: str, version: str, parent=None) -> None:
        super().__init__(parent)
        from .. import UPSTREAM_VERSION

        self.setWindowTitle("О программе")
        self.setMinimumWidth(560)
        layout = QVBoxLayout(self)

        title = QLabel("CORDON-LINUX")
        title.setObjectName("headline")
        font = title.font()
        font.setPointSize(font.pointSize() + 6)
        font.setBold(True)
        title.setFont(font)
        layout.addWidget(title)

        text = QLabel(
            f"Версия {version} · порт CORDON {UPSTREAM_VERSION} для Linux.\n"
            "Лаунчер профилей и модов S.T.A.L.K.E.R. с поддержкой движка OpenXRay.\n\n"
            f"Оригинал: {upstream}\n"
            "Лицензия: GPL-3.0 (как и у оригинального проекта).\n\n"
            f"Настройки: {app.config_dir}\n"
            f"Профили и моды: {app.data_dir}\n"
            f"Кэш: {app.cache_dir}\n"
            f"Журнал: {app.launcher_log}"
        )
        text.setWordWrap(True)
        text.setTextInteractionFlags(Qt.TextSelectableByMouse)
        layout.addWidget(text)

        buttons = QDialogButtonBox(QDialogButtonBox.Close)
        buttons.rejected.connect(self.reject)
        buttons.accepted.connect(self.accept)
        layout.addWidget(buttons)
