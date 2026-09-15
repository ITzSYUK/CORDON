"""Post-session diagnostics: game log, crash dumps, and the report the Status window shows."""

from __future__ import annotations

import os
from dataclasses import dataclass, field

from . import util, xray
from .paths import AppPaths

LOG_DIRS = ("logs",)
DUMP_DIRS = ("dumps", "reports")
DUMP_SUFFIXES = (".dmp", ".mdmp", ".core", ".crash")


@dataclass(slots=True)
class SessionDiagnostics:
    log_path: str = ""
    log_tail: list[str] = field(default_factory=list)
    dumps: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)

    @property
    def has_problems(self) -> bool:
        return bool(self.dumps) or any("! " in line or "FATAL" in line.upper() for line in self.log_tail)

    def to_text(self) -> str:
        lines: list[str] = []
        if self.log_path:
            lines.append(f"Последний игровой лог: {self.log_path}")
        if self.dumps:
            lines.append("Дампы/отчёты:")
            lines.extend(f"  • {item}" for item in self.dumps)
        if self.notes:
            lines.append("Заметки:")
            lines.extend(f"  • {item}" for item in self.notes)
        if self.log_tail:
            lines.append("")
            lines.append("Хвост лога:")
            lines.extend(self.log_tail[-25:])
        return "\n".join(lines) or "Диагностика пуста."


def collect(appdata: str, *, since: float = 0.0, tail_lines: int = 40) -> SessionDiagnostics:
    """Look for logs and dumps inside a profile data directory."""
    result = SessionDiagnostics()
    if not os.path.isdir(appdata):
        result.notes.append(f"каталог данных не найден: {appdata}")
        return result

    candidates: list[tuple[float, str]] = []
    for directory in LOG_DIRS:
        root = os.path.join(appdata, directory)
        if not os.path.isdir(root):
            continue
        for dirpath, _dirnames, filenames in util.iter_tree(root):
            for name in filenames:
                path = os.path.join(dirpath, name)
                if not name.lower().endswith((".log", ".txt")):
                    continue
                try:
                    mtime = os.lstat(path).st_mtime
                except OSError:  # pragma: no cover
                    continue
                if mtime + 1 >= since:
                    candidates.append((mtime, path))
    if candidates:
        candidates.sort()
        result.log_path = candidates[-1][1]
        result.log_tail = util.tail_lines(result.log_path, tail_lines)
    else:
        result.notes.append("новый игровой лог не найден — возможно, движок не успел его создать")

    for directory in DUMP_DIRS:
        root = os.path.join(appdata, directory)
        if not os.path.isdir(root):
            continue
        for dirpath, _dirnames, filenames in util.iter_tree(root):
            for name in filenames:
                lower = name.lower()
                path = os.path.join(dirpath, name)
                if not (lower.endswith(DUMP_SUFFIXES) or lower.startswith("crash")):
                    continue
                try:
                    mtime = os.lstat(path).st_mtime
                except OSError:  # pragma: no cover
                    continue
                if mtime + 1 >= since:
                    result.dumps.append(path)
    return result


def standalone_data_dirs(build_root: str) -> list[str]:
    return xray.find_appdata_roots(build_root)


def build_report_text(
    *,
    profile_name: str,
    engine_summary: str,
    executable: str,
    root_path: str,
    overlay_summary: str,
    preflight_text: str,
    diag: SessionDiagnostics | None = None,
    extra_notes: list[str] | None = None,
) -> str:
    lines = [
        "CORDON-LINUX — отчёт о состоянии профиля",
        "=" * 46,
        f"Профиль:        {profile_name}",
        f"Движок:         {engine_summary or '—'}",
        f"Исполняемый:    {executable or '—'}",
        f"Каталог профиля:{root_path or '—'}",
        f"Оверлей:        {overlay_summary or '—'}",
        "",
        "Проверки:",
        preflight_text or "—",
    ]
    if extra_notes:
        lines += ["", "Дополнительно:"]
        lines += [f"  • {note}" for note in extra_notes]
    if diag is not None:
        lines += ["", diag.to_text()]
    return "\n".join(lines)


def write_report(app: AppPaths, name: str, text: str) -> str:
    util.ensure_dir(app.reports_dir)
    stamp = util.short_hash(name + text, 8)
    target = os.path.join(app.reports_dir, f"{name}-{stamp}.txt")
    util.write_text_atomic(target, text)
    return target
