"""Conflict analysis: which mod wins, which files are unique and which mods are redundant.

Mirrors the upstream ``ModConflictAnalyzer``: every enabled mod is classified as
conflict-free, overwriting earlier mods, overwritten by later ones, mixed, or fully redundant,
and the *effective tree* answers "where does this file ultimately come from".
"""

from __future__ import annotations

from dataclasses import dataclass, field

from . import util
from .layers import LayerPlan

STATUS_CONFLICT_FREE = "conflict_free"
STATUS_OVERWRITES = "overwrites"
STATUS_OVERWRITTEN = "overwritten"
STATUS_MIXED = "mixed"
STATUS_REDUNDANT = "redundant"

STATUS_LABELS = {
    STATUS_CONFLICT_FREE: "Без конфликтов",
    STATUS_OVERWRITES: "Перекрывает другие",
    STATUS_OVERWRITTEN: "Перекрыт другими",
    STATUS_MIXED: "Смешанный",
    STATUS_REDUNDANT: "Полностью перекрыт",
}


@dataclass(slots=True)
class ModConflictStatus:
    mod_id: str
    name: str
    priority: int
    provided: int = 0
    winning: int = 0
    losing: int = 0
    unique: int = 0
    #: files this mod supplies that another layer (base game, engine data or another mod) also has
    overriding: int = 0
    #: files this mod supplies that no other layer has
    status: str = STATUS_CONFLICT_FREE

    @property
    def label(self) -> str:
        return STATUS_LABELS.get(self.status, self.status)

    def describe(self) -> str:
        parts = [
            f"{self.name}: файлов {self.provided}",
            f"побеждает {self.winning}",
            f"перекрыто {self.losing}",
            f"уникальных {self.unique}",
        ]
        if self.overriding:
            parts.append(f"перекрывает чужие {self.overriding}")
        return ", ".join(parts) + f" — {self.label}"


@dataclass(slots=True)
class ConflictReport:
    per_mod: dict[str, ModConflictStatus] = field(default_factory=dict)
    total_files: int = 0
    overlapping: int = 0
    files: list[tuple[str, list[str], str]] = field(default_factory=list)

    @property
    def redundant(self) -> list[ModConflictStatus]:
        return [item for item in self.per_mod.values() if item.status == STATUS_REDUNDANT]

    @property
    def conflicting(self) -> list[tuple[str, list[str], str]]:
        """Files that more than one layer provides (see :func:`conflicting_files`)."""
        return self.files

    def status_of(self, key: str) -> ModConflictStatus | None:
        """Look a mod up by id (falling back to its name)."""
        return self.per_mod.get(key)

    def to_text(self, *, limit: int = 40) -> str:
        lines = [f"Файлов в итоговом дереве: {self.total_files}, пересечений: {self.overlapping}", ""]
        for item in sorted(self.per_mod.values(), key=lambda status: status.priority):
            lines.append(item.describe())
        redundant = self.redundant
        if redundant:
            lines.append("")
            lines.append("Полностью перекрытые моды (можно отключить):")
            for item in redundant[:limit]:
                lines.append(f"  • {item.name}")
        return "\n".join(lines)


def analyze(plan: LayerPlan) -> ConflictReport:
    """Classify every enabled mod that contributes files to the plan."""
    report = ConflictReport(total_files=len(plan.entries))
    priority_by_id: dict[str, ModConflictStatus] = {}
    for layer in plan.layers:
        if layer.kind == "mod":
            status = ModConflictStatus(mod_id=layer.mod_id, name=layer.name, priority=layer.priority)
            report.per_mod[layer.mod_id or layer.name] = status
            if layer.mod_id:
                priority_by_id[layer.mod_id] = status

    for relative in sorted(plan.entries):
        providers = plan.providers(relative)
        if len(providers) < 1:
            continue
        if len(providers) > 1:
            report.overlapping += 1
            report.files.append((relative, [layer.name for layer in providers], providers[-1].name))
        winner = providers[-1]
        for provider in providers:
            status = report.per_mod.get(provider.mod_id or provider.name)
            if status is None:
                continue  # base game / engine data layers are not "mods"
            status.provided += 1
            if len(providers) == 1:
                status.unique += 1
                status.winning += 1
            elif provider is winner:
                status.winning += 1
                status.overriding += 1
            else:
                status.losing += 1

    for status in report.per_mod.values():
        if status.provided == 0:
            status.status = STATUS_CONFLICT_FREE
        elif status.losing == 0 and status.overriding == 0:
            status.status = STATUS_CONFLICT_FREE
        elif status.winning == 0:
            status.status = STATUS_REDUNDANT
        elif status.losing == 0:
            status.status = STATUS_OVERWRITES
        elif status.overriding == 0:
            status.status = STATUS_OVERWRITTEN
        else:
            status.status = STATUS_MIXED
    return report


def effective_tree(plan: LayerPlan, *, limit: int = 0) -> str:
    """Human readable winner listing ("path ← mod"), sorted by path."""
    lines: list[str] = []
    for index, relative in enumerate(sorted(plan.entries)):
        if limit and index >= limit:
            lines.append(f"… ещё {len(plan.entries) - limit} записей")
            break
        winner = plan.winner(relative)
        name = winner.name if winner else "?"
        lines.append(f"{relative} ← {name}")
    return "\n".join(lines)


def conflicting_files(plan: LayerPlan, *, include_unique: bool = False) -> list[tuple[str, list[str], str]]:
    """``[(relative, provider names ascending, winner name)]``."""
    result: list[tuple[str, list[str], str]] = []
    for relative in sorted(plan.entries):
        providers = plan.providers(relative)
        if len(providers) < 2 and not include_unique:
            continue
        result.append((relative, [layer.name for layer in providers], providers[-1].name))
    return result


def exclude_conflicting_file(profile, relative: str) -> None:
    """Exclude one file from the overlay without touching the mod folder (upstream feature)."""
    normalised = util.to_posix(relative)
    if normalised not in profile.excluded_paths:
        profile.excluded_paths.append(normalised)


def restore_excluded_file(profile, relative: str) -> None:
    normalised = util.to_posix(relative)
    if normalised in profile.excluded_paths:
        profile.excluded_paths.remove(normalised)
