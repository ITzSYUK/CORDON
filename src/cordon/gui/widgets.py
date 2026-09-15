"""Reusable widgets: the profile list, the priority table for mods and the report/log panes."""

from __future__ import annotations

import os

from PySide6.QtCore import Qt, Signal
from PySide6.QtGui import QColor, QFont
from PySide6.QtWidgets import (
    QAbstractItemView,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QListWidget,
    QListWidgetItem,
    QPushButton,
    QTableWidget,
    QTableWidgetItem,
    QTextEdit,
    QVBoxLayout,
    QWidget,
)

from ..core.conflicts import STATUS_REDUNDANT
from ..core.models import BACKEND_FUSE, BACKEND_LINK, Profile
from .theme import monospace

MOD_ID_ROLE = Qt.UserRole + 1


class ProfileList(QListWidget):
    """Profiles with a compact summary line; double click launches."""

    profile_activated = Signal(str)

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        self.setAlternatingRowColors(True)
        self.setUniformItemSizes(False)
        self.itemDoubleClicked.connect(self._emit)

    def _emit(self, item: QListWidgetItem) -> None:
        self.profile_activated.emit(str(item.data(MOD_ID_ROLE)))

    def set_profiles(self, profiles: list[Profile], selected_id: str = "") -> None:
        self.clear()
        for profile in profiles:
            kind = "сборка" if profile.is_standalone else "игра+моды"
            backend = {"link": "ссылки", BACKEND_FUSE: "fuse", "direct": "напрямую"}.get(
                profile.backend, profile.backend
            )
            badge = "●" if not profile.is_standalone else "◆"
            item = QListWidgetItem(
                f"{badge} {profile.name}\n"
                f"    {kind} · модов {len(profile.enabled_mods)}/{len(profile.mods)} · "
                f"{backend} · {profile.playtime_display}"
            )
            item.setData(MOD_ID_ROLE, profile.id)
            item.setToolTip(
                f"{profile.game_path or 'каталог игры не задан'}\n"
                f"последний запуск: {profile.last_played_display}"
            )
            self.addItem(item)
        for row in range(self.count()):
            if self.item(row).data(MOD_ID_ROLE) == selected_id:
                self.setCurrentRow(row)
                self.item(row).setSelected(True)
                break

    def current_profile_id(self) -> str:
        item = self.currentItem()
        return str(item.data(MOD_ID_ROLE)) if item else ""


class ModTable(QTableWidget):
    """Priority table: the checkbox enables a mod, the row order is the priority order.

    A mod *lower* in the table wins file conflicts, exactly like in the Windows launcher.
    """

    order_changed = Signal(list)
    toggled = Signal(str, bool)
    selection_changed = Signal(str)

    HEADERS = ("Вкл", "№", "Мод", "Статус", "Путь")

    def __init__(self, parent=None) -> None:
        super().__init__(0, len(self.HEADERS), parent)
        self.setHorizontalHeaderLabels(self.HEADERS)
        self.verticalHeader().setVisible(False)
        self.setSelectionBehavior(QAbstractItemView.SelectRows)
        self.setSelectionMode(QAbstractItemView.ExtendedSelection)
        self.setEditTriggers(QAbstractItemView.NoEditTriggers)
        self.setAlternatingRowColors(True)
        self.setDragDropMode(QAbstractItemView.InternalMove)
        self.setDragEnabled(True)
        self.setAcceptDrops(True)
        self.setDropIndicatorShown(True)
        header = self.horizontalHeader()
        header.setSectionResizeMode(0, QHeaderView.ResizeToContents)
        header.setSectionResizeMode(1, QHeaderView.ResizeToContents)
        header.setSectionResizeMode(2, QHeaderView.Stretch)
        header.setSectionResizeMode(3, QHeaderView.ResizeToContents)
        header.setSectionResizeMode(4, QHeaderView.Stretch)
        self.itemChanged.connect(self._on_item_changed)
        self.itemSelectionChanged.connect(self._on_selection)

    # ------------------------------------------------------------------ population
    def set_mods(self, profile: Profile, statuses: dict | None = None) -> None:
        statuses = statuses or {}
        self.blockSignals(True)
        self.setRowCount(0)
        for index, mod in enumerate(profile.mods, start=1):
            self.insertRow(self.rowCount())
            row = self.rowCount() - 1
            check = QTableWidgetItem()
            check.setFlags(Qt.ItemIsUserCheckable | Qt.ItemIsEnabled | Qt.ItemIsSelectable)
            check.setCheckState(Qt.Checked if mod.enabled else Qt.Unchecked)
            check.setData(MOD_ID_ROLE, mod.id)
            self.setItem(row, 0, check)

            number = QTableWidgetItem(str(index))
            number.setData(MOD_ID_ROLE, mod.id)
            self.setItem(row, 1, number)

            missing = not os.path.isdir(mod.path)
            name = QTableWidgetItem(("⚠ " if missing else "") + mod.name)
            if missing:
                name.setForeground(QColor("#e2564b"))
            name.setToolTip(mod.path)
            name.setData(MOD_ID_ROLE, mod.id)
            self.setItem(row, 2, name)

            status = statuses.get(mod.id) or statuses.get(mod.name)
            label = status.label if status else "—"
            status_item = QTableWidgetItem(label)
            if status and status.status == STATUS_REDUNDANT:
                status_item.setForeground(QColor("#e0a63a"))
            status_item.setToolTip(
                f"файлов: {status.provided}, побеждает: {status.winning}, перекрыто: {status.losing}"
                if status
                else "нет данных"
            )
            status_item.setData(MOD_ID_ROLE, mod.id)
            self.setItem(row, 3, status_item)

            path = QTableWidgetItem(mod.path)
            path.setData(MOD_ID_ROLE, mod.id)
            path.setForeground(QColor("#9c8f7a"))
            self.setItem(row, 4, path)
        self.blockSignals(False)
        self.clearSelection()
        self.setCurrentCell(-1, -1)

    def mod_ids_in_order(self) -> list[str]:
        order: list[str] = []
        for row in range(self.rowCount()):
            item = self.item(row, 0)
            if item is not None:
                order.append(str(item.data(MOD_ID_ROLE)))
        return order

    def selected_mod_id(self) -> str:
        items = self.selectedItems()
        return str(items[0].data(MOD_ID_ROLE)) if items else ""

    def dropEvent(self, event) -> None:  # noqa: N802 - Qt API
        super().dropEvent(event)
        self.order_changed.emit(self.mod_ids_in_order())
        self._renumber()

    def _renumber(self) -> None:
        for row in range(self.rowCount()):
            item = self.item(row, 1)
            if item is not None:
                item.setText(str(row + 1))

    def _on_item_changed(self, item: QTableWidgetItem) -> None:
        if item.column() != 0:
            return
        mod_id = str(item.data(MOD_ID_ROLE))
        self.toggled.emit(mod_id, item.checkState() == Qt.Checked)

    def _on_selection(self) -> None:
        self.selection_changed.emit(self.selected_mod_id())


class ReportPane(QWidget):
    """Coloured report view (pre-flight checks, conflict summary, diagnostics)."""

    COLORS = {
        "error": "#e2564b",
        "warning": "#e0a63a",
        "ok": "#7bb662",
        "info": "#9c8f7a",
        "plain": "#e8dcc8",
    }

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        self.view = QTextEdit()
        self.view.setReadOnly(True)
        self.view.setLineWrapMode(QTextEdit.NoWrap)
        self.view.setFont(monospace(12))
        layout.addWidget(self.view)
        self._lines: list[tuple[str, str]] = []

    def set_lines(self, lines: list[tuple[str, str]]) -> None:
        self._lines = list(lines)
        self.view.setHtml(self._as_html(self._lines))

    def set_text(self, text: str, level: str = "plain") -> None:
        self.set_lines([(level, text)])

    def append(self, text: str, level: str = "plain") -> None:
        self._lines.append((level, text))
        self.view.setHtml(self._as_html(self._lines))
        self.view.verticalScrollBar().setValue(self.view.verticalScrollBar().maximum())

    def text(self) -> str:
        return "\n".join(text for _level, text in self._lines)

    @classmethod
    def _as_html(cls, lines: list[tuple[str, str]]) -> str:
        import html as html_mod

        rows = []
        for level, text in lines:
            color = cls.COLORS.get(level, cls.COLORS["plain"])
            rows.append(
                f'<pre style="color:{color}; margin:0; font-family:monospace; white-space:pre-wrap">'
                f"{html_mod.escape(text)}</pre>"
            )
        return "<div>" + "".join(rows) + "</div>"


class StatusStrip(QWidget):
    """Small header strip with the profile headline and quick action buttons."""

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        layout = QHBoxLayout(self)
        layout.setContentsMargins(8, 4, 8, 4)
        self.headline = QLabel("Профиль не выбран")
        self.headline.setObjectName("headline")
        self.detail = QLabel("—")
        self.detail.setObjectName("dim")
        layout.addWidget(self.headline)
        layout.addStretch(1)
        layout.addWidget(self.detail)


def make_button(text: str, *, primary: bool = False) -> QPushButton:
    button = QPushButton(text)
    if primary:
        button.setObjectName("primary")
        font = QFont(button.font())
        font.setBold(True)
        button.setFont(font)
        button.setMinimumHeight(34)
    return button


def summary_line(profile: Profile, status=None) -> str:
    parts = []
    if status is not None and getattr(status, "headline", ""):
        parts.append(status.headline)
    parts.append(f"модов: {len(profile.enabled_mods)}/{len(profile.mods)}")
    if profile.backend == BACKEND_LINK:
        parts.append("backend: ссылки")
    elif profile.backend == BACKEND_FUSE:
        parts.append("backend: fuse-overlayfs")
    else:
        parts.append(f"backend: {profile.backend}")
    return " · ".join(parts)


__all__ = [
    "MOD_ID_ROLE",
    "ModTable",
    "ProfileList",
    "ReportPane",
    "StatusStrip",
    "make_button",
    "summary_line",
]
