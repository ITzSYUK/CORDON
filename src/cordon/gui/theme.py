"""Palette and stylesheets.

Two looks, like the upstream launcher: the default PDA-inspired dark theme (graphite with amber
accents, referencing the in-game PDA) and a calmer "classic" light theme.
"""

from __future__ import annotations

from dataclasses import dataclass

from PySide6.QtGui import QColor, QFont, QPalette


@dataclass(frozen=True)
class Palette:
    name: str
    window: str
    base: str
    alt_base: str
    text: str
    dim_text: str
    accent: str
    accent_text: str
    border: str
    danger: str
    warning: str
    ok: str


PDA = Palette(
    name="pda",
    window="#171512",
    base="#1f1c18",
    alt_base="#262220",
    text="#e8dcc8",
    dim_text="#9c8f7a",
    accent="#ffb000",
    accent_text="#20180a",
    border="#3a332b",
    danger="#e2564b",
    warning="#e0a63a",
    ok="#7bb662",
)

CLASSIC = Palette(
    name="classic",
    window="#f2f3f5",
    base="#ffffff",
    alt_base="#f7f8fa",
    text="#20232a",
    dim_text="#6b7280",
    accent="#2f6f9f",
    accent_text="#ffffff",
    border="#c9ced6",
    danger="#b42318",
    warning="#a15c07",
    ok="#1c7c3f",
)


def palettes() -> dict[str, Palette]:
    return {PDA.name: PDA, CLASSIC.name: CLASSIC}


def qpalette(theme: Palette) -> QPalette:
    palette = QPalette()
    palette.setColor(QPalette.Window, QColor(theme.window))
    palette.setColor(QPalette.WindowText, QColor(theme.text))
    palette.setColor(QPalette.Base, QColor(theme.base))
    palette.setColor(QPalette.AlternateBase, QColor(theme.alt_base))
    palette.setColor(QPalette.Text, QColor(theme.text))
    palette.setColor(QPalette.Button, QColor(theme.alt_base))
    palette.setColor(QPalette.ButtonText, QColor(theme.text))
    palette.setColor(QPalette.Highlight, QColor(theme.accent))
    palette.setColor(QPalette.HighlightedText, QColor(theme.accent_text))
    palette.setColor(QPalette.ToolTipBase, QColor(theme.base))
    palette.setColor(QPalette.ToolTipText, QColor(theme.text))
    palette.setColor(QPalette.PlaceholderText, QColor(theme.dim_text))
    palette.setColor(QPalette.Disabled, QPalette.Text, QColor(theme.dim_text))
    palette.setColor(QPalette.Disabled, QPalette.ButtonText, QColor(theme.dim_text))
    # Windows-style inactive selections look alien here; keep the accent when focus is lost.
    for group in (QPalette.Inactive, QPalette.Active):
        palette.setColor(group, QPalette.Highlight, QColor(theme.accent))
        palette.setColor(group, QPalette.HighlightedText, QColor(theme.accent_text))
    palette.setColor(QPalette.Inactive, QPalette.WindowText, QColor(theme.text))
    return palette


def stylesheet(theme: Palette) -> str:
    return f"""
    QWidget {{
        background-color: {theme.window};
        color: {theme.text};
        font-size: 13px;
    }}
    QMainWindow::separator {{ background: {theme.border}; width: 1px; height: 1px; }}
    QWidget#header {{
        background: {theme.alt_base};
        border-bottom: 1px solid {theme.border};
    }}
    QToolBar {{
        background: {theme.alt_base};
        border-bottom: 1px solid {theme.border};
        spacing: 6px;
        padding: 4px 6px;
    }}
    QToolButton#menuButton::menu-indicator {{ image: none; }}
    QToolButton, QPushButton {{
        background: {theme.base};
        border: 1px solid {theme.border};
        border-radius: 3px;
        padding: 5px 10px;
    }}
    QToolButton:hover, QPushButton:hover {{ border-color: {theme.accent}; color: {theme.accent}; }}
    QToolButton:disabled, QPushButton:disabled {{ color: {theme.dim_text}; border-color: {theme.border}; }}
    QPushButton#primary {{
        background: {theme.accent};
        color: {theme.accent_text};
        border: 1px solid {theme.accent};
        font-weight: bold;
        padding: 7px 18px;
    }}
    QPushButton#primary:hover {{ background: {theme.text}; }}
    QPushButton#primary:disabled {{ background: {theme.border}; color: {theme.dim_text}; }}
    QListWidget, QTableWidget, QPlainTextEdit, QTextEdit, QLineEdit, QComboBox, QSpinBox {{
        background: {theme.base};
        border: 1px solid {theme.border};
        border-radius: 3px;
        selection-background-color: {theme.accent};
        selection-color: {theme.accent_text};
    }}
    QListWidget::item {{ padding: 6px 4px; }}
    QListWidget::item:selected, QListWidget::item:selected:!active,
    QTableWidget::item:selected, QTableWidget::item:selected:!active,
    QTableView::item:selected, QTableView::item:selected:!active {{
        background: {theme.accent};
        color: {theme.accent_text};
    }}
    QTableView {{ alternate-background-color: {theme.alt_base}; selection-background-color: {theme.accent}; }}
    QTableView::item {{ padding: 2px; }}
    QTableCornerButton::section {{ background: {theme.alt_base}; border: 0; }}
    QHeaderView::section {{
        background: {theme.alt_base};
        color: {theme.dim_text};
        border: 0;
        border-bottom: 1px solid {theme.border};
        padding: 5px;
        font-weight: bold;
    }}
    QTabWidget::pane {{ border: 1px solid {theme.border}; top: -1px; }}
    QTabBar::tab {{
        background: {theme.window};
        border: 1px solid {theme.border};
        padding: 6px 14px;
        color: {theme.dim_text};
    }}
    QTabBar::tab:selected {{ background: {theme.base}; color: {theme.accent}; border-bottom-color: {theme.base}; }}
    QGroupBox {{
        border: 1px solid {theme.border};
        border-radius: 4px;
        margin-top: 12px;
        padding-top: 10px;
    }}
    QGroupBox::title {{ subcontrol-origin: margin; left: 10px; color: {theme.accent}; }}
    QStatusBar {{ background: {theme.alt_base}; border-top: 1px solid {theme.border}; color: {theme.dim_text}; }}
    QSplitter::handle {{ background: {theme.border}; }}
    QScrollBar:vertical, QScrollBar:horizontal {{ background: {theme.window}; border: 0; }}
    QScrollBar::handle {{ background: {theme.border}; border-radius: 3px; min-height: 24px; min-width: 24px; }}
    QScrollBar::handle:hover {{ background: {theme.accent}; }}
    QLabel#headline {{ color: {theme.accent}; font-weight: bold; }}
    QLabel#dim {{ color: {theme.dim_text}; }}
    QLabel#error {{ color: {theme.danger}; }}
    QLabel#warning {{ color: {theme.warning}; }}
    QLabel#ok {{ color: {theme.ok}; }}
    QCheckBox::indicator, QRadioButton::indicator {{ width: 14px; height: 14px; }}
    QToolTip {{ background: {theme.base}; color: {theme.text}; border: 1px solid {theme.accent}; }}
    """


def monospace(pixels: int = 12, *, bold: bool = False) -> QFont:
    font = QFont("monospace")
    font.setStyleHint(QFont.Monospace)
    font.setPixelSize(pixels)
    font.setBold(bold)
    return font


LEVEL_COLORS = {
    "error": PDA.danger,
    "warning": PDA.warning,
    "ok": PDA.ok,
    "info": PDA.dim_text,
}


def level_color(theme: Palette, level: str) -> str:
    return {
        "error": theme.danger,
        "warning": theme.warning,
        "ok": theme.ok,
        "info": theme.dim_text,
    }.get(level, theme.text)
