"""The shared layer model.

Both backends (the symlink overlay and the FUSE overlay) consume one and the same plan, so
"which file does the game actually see" is answered in exactly one place - like
``FileLayerPlan`` in the upstream launcher.

Layer order (lowest priority first)::

    engine data → base game → enabled mods (in UI order) → MO2 overwrite

A mod *lower* in the profile list has a *higher* priority, which is the same convention the
Windows launcher documents.
"""

from __future__ import annotations

import os
import posixpath
from collections.abc import Callable, Iterator
from dataclasses import dataclass, field

from . import util, xray
from .models import ConflictingFile, LayeredStats, LayerSource, ModEntry, Profile

ProgressCallback = Callable[[str], None] | None

#: Files the engine may rewrite at runtime; they are copied instead of symlinked so a mod's
#: source directory is never modified (mirrors the upstream "writable game files" store).
WRITABLE_GAME_PATHS = (
    "gamedata/configs/localization.ltx",
    "gamedata/configs/axr_options.ltx",
    "gamedata/configs/launcher.ltx",
)


@dataclass(slots=True)
class LayerPlan:
    layers: list[LayerSource] = field(default_factory=list)
    excluded: set[str] = field(default_factory=set)
    #: relative path → providers, ascending priority (the last one wins)
    entries: dict[str, list[LayerSource]] = field(default_factory=dict)
    #: relative path → {layer priority: absolute source path}
    paths: dict[str, dict[int, str]] = field(default_factory=dict)
    layer_counts: dict[int, int] = field(default_factory=dict)
    layer_bytes: dict[int, int] = field(default_factory=dict)

    # ------------------------------------------------------------------ construction
    def add_layer(self, layer: LayerSource) -> None:
        self.layers.append(layer)
        self.layers.sort(key=lambda item: item.priority)

    def index(self, progress: ProgressCallback = None) -> None:
        """Walk every layer once and record which layers provide which relative path."""
        self.entries.clear()
        self.paths.clear()
        self.layer_counts.clear()
        self.layer_bytes.clear()
        for layer in self.layers:
            if not os.path.isdir(layer.path):
                self.layer_counts[layer.priority] = 0
                self.layer_bytes[layer.priority] = 0
                continue
            if progress:
                progress(f"Индексация слоя «{layer.name}»…")
            count = 0
            total = 0
            for relative, absolute in mapped_entries(layer):
                if relative in self.excluded:
                    continue
                providers = self.entries.setdefault(relative, [])
                if providers and providers[-1].priority >= layer.priority:
                    continue
                providers.append(layer)
                self.paths.setdefault(relative, {})[layer.priority] = absolute
                count += 1
                try:
                    total += os.lstat(absolute).st_size
                except OSError:
                    pass
            self.layer_counts[layer.priority] = count
            self.layer_bytes[layer.priority] = total

    # ------------------------------------------------------------------ queries
    def winner(self, relative: str) -> LayerSource | None:
        providers = self.entries.get(util.to_posix(relative))
        return providers[-1] if providers else None

    def providers(self, relative: str) -> list[LayerSource]:
        return list(self.entries.get(util.to_posix(relative), []))

    def winner_path(self, relative: str) -> str | None:
        providers = self.entries.get(util.to_posix(relative))
        if not providers:
            return None
        return self.paths.get(util.to_posix(relative), {}).get(providers[-1].priority)

    def source_path(self, relative: str, layer: LayerSource) -> str | None:
        return self.paths.get(util.to_posix(relative), {}).get(layer.priority)

    def conflicts(self) -> list[ConflictingFile]:
        result: list[ConflictingFile] = []
        for relative, providers in self.entries.items():
            if len(providers) < 2:
                continue
            result.append(
                ConflictingFile(
                    relative=relative,
                    winner=providers[-1],
                    losers=providers[:-1],
                    excluded=relative in self.excluded,
                )
            )
        result.sort(key=lambda item: item.relative)
        return result

    def stats(self) -> LayeredStats:
        stats = LayeredStats()
        stats.total_files = len(self.entries)
        for relative, providers in self.entries.items():
            if len(providers) > 1:
                stats.overlapping_files += 1
            path = self.winner_path(relative)
            if path:
                try:
                    stats.total_bytes += os.lstat(path).st_size
                except OSError:
                    pass
            extension = posixpath.splitext(relative)[1].lower() or "<без расширения>"
            stats.extensions[extension] = stats.extensions.get(extension, 0) + 1
            if relative.startswith("gamedata/textures/shaders/"):
                stats.shader_textures += 1
        for layer in self.layers:
            stats.per_layer[layer.name] = self.layer_counts.get(layer.priority, 0)
        stats.conflicts = self.conflicts()
        return stats

    def executable_candidates(self) -> list[tuple[str, LayerSource]]:
        """Executables visible in the plan (a mod may ship its own engine build)."""
        found: list[tuple[str, LayerSource]] = []
        for relative, providers in self.entries.items():
            if "/" in relative and not relative.startswith(("bin/", "bin_x64/")):
                continue
            name = posixpath.basename(relative).lower()
            if not name.startswith(("xr_", "xray", "openxray")):
                continue
            path = self.winner_path(relative)
            if path and os.path.isfile(path) and os.access(path, os.X_OK):
                found.append((relative, providers[-1]))
        found.sort()
        return found

    # ------------------------------------------------------------------ reporting
    def describe(self) -> list[str]:
        lines = ["Слои (низший приоритет → высший):"]
        for layer in self.layers:
            lines.append(f"  {layer.describe()}")
        if self.excluded:
            lines.append(f"  исключено путей: {len(self.excluded)}")
        return lines


def mapped_entries(layer: LayerSource) -> Iterator[tuple[str, str]]:
    """Yield ``(relative posix path, absolute source path)`` for one layer."""
    root = layer.path
    if not os.path.isdir(root):
        return
    if layer.kind == "mod":
        yield from _mapped_mod_entries(layer)
        return
    yield from _walk_with_prefix(root, "")


def _walk_with_prefix(root: str, prefix: str) -> Iterator[tuple[str, str]]:
    for dirpath, _dirnames, filenames in util.iter_tree(root):
        rel_dir = os.path.relpath(dirpath, root)
        rel_dir = "" if rel_dir == "." else rel_dir.replace(os.sep, "/")
        for name in filenames:
            if prefix:
                relative = f"{prefix}/{rel_dir}/{name}" if rel_dir else f"{prefix}/{name}"
            else:
                relative = f"{rel_dir}/{name}" if rel_dir else name
            yield util.to_posix(relative), os.path.join(dirpath, name)


def _mapped_mod_entries(layer: LayerSource) -> Iterator[tuple[str, str]]:
    layout = xray.classify_mod(layer.path)
    if layout.game_data_dir:
        yield from _walk_with_prefix(layout.game_data_dir, "gamedata")
    for key, path in layout.root_entries.items():
        if key.startswith("__gamedata__/"):
            relative = "gamedata/" + key[len("__gamedata__/"):]
            if os.path.isdir(path):
                yield from _walk_with_prefix(path, relative)
            else:
                yield util.to_posix(relative), path
        elif os.path.isdir(path):
            yield from _walk_with_prefix(path, key)
        else:
            yield util.to_posix(key), path
    for name, path in layout.archive_entries.items():
        # Loose mod archives follow the Anomaly/MO2 convention and live in ``db/mods``.
        yield util.to_posix(f"db/mods/{name}"), path


def build_plan(
    profile: Profile,
    *,
    engine_data_path: str = "",
    game_path: str = "",
    include_overwrite: bool = True,
) -> LayerPlan:
    """Create the layer plan for a profile (without indexing it)."""
    plan = LayerPlan(excluded={util.to_posix(path) for path in profile.excluded_paths})
    if profile.is_standalone:
        root = util.norm(profile.game_path)
        if root:
            plan.add_layer(LayerSource(kind="game", name="Сборка", path=root, priority=0))
        return plan

    engine_data = util.norm(engine_data_path) if engine_data_path else ""
    game_root = util.norm(game_path or profile.game_path)
    priority = 0
    if engine_data and os.path.isdir(engine_data) and not util.same_file(engine_data, game_root):
        plan.add_layer(LayerSource(kind="engine", name="Данные движка", path=engine_data, priority=priority))
        priority += 1
    if game_root and os.path.isdir(game_root):
        plan.add_layer(LayerSource(kind="game", name="Базовая игра", path=game_root, priority=priority))
        priority += 1
    for mod in profile.enabled_mods:
        plan.add_layer(LayerSource(kind="mod", name=mod.name, path=mod.path, priority=priority, mod_id=mod.id))
        priority += 1
    if include_overwrite and profile.mo2_overwrite_path and os.path.isdir(profile.mo2_overwrite_path):
        plan.add_layer(
            LayerSource(
                kind="overwrite",
                name="MO2 overwrite",
                path=util.norm(profile.mo2_overwrite_path),
                priority=priority,
            )
        )
    return plan


def reorder_mods(profile: Profile, mod_ids_in_order: list[str]) -> None:
    """Apply a new UI order to the profile (unknown ids keep their relative position at the end)."""
    by_id = {mod.id: mod for mod in profile.mods}
    ordered: list[ModEntry] = [by_id[mod_id] for mod_id in mod_ids_in_order if mod_id in by_id]
    ordered_ids = {mod.id for mod in ordered}
    ordered.extend(mod for mod in profile.mods if mod.id not in ordered_ids)
    profile.mods = ordered


def conflict_summary(plan: LayerPlan) -> dict[str, int]:
    """Counters used by the UI badges (conflict-free, overwriting, overwritten, redundant)."""
    summary = {"conflict_free": 0, "overwriting": 0, "overwritten": 0, "redundant": 0}
    for providers in plan.entries.values():
        if len(providers) == 1:
            summary["conflict_free"] += 1
        elif len(providers) == 2:
            summary["overwriting"] += 1
        else:
            summary["overwritten"] += 1
    return summary
