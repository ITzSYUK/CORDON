"""Reading and generating ``fsgame.ltx``.

The engine parses this file as ``alias = recursive|notify|<root>|<add>|<def>|<caption>`` where
``<root>`` is either another alias (``$fs_root$``, ``$game_data$`` …) or a raw path.

CORDON-LINUX never edits the file inside the game installation: it writes a prepared copy into
the profile root and starts the engine with ``-fsltx <profile root>/fsgame.ltx``, which makes
that directory the engine's ``$fs_root$`` (``CLocatorAPI::setup_fs_path``).
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass, field

from . import util
from .errors import ConfigError
from .xray import APP_DATA_ALIAS, FSGAME_NAME

ALIAS_RE = re.compile(r"^\s*(\$[A-Za-z0-9_]+\$)\s*=\s*(.*?)\s*$")

#: Fallback template used when neither the game nor a mod ships its own ``fsgame.ltx``.
#: Alias names are part of the X-Ray format; the layout below mirrors a standard installation
#: with everything relative to ``$fs_root$`` (the directory of this file).
DEFAULT_TEMPLATE = """; CORDON-LINUX generated fsgame.ltx
;abbreviation           = recurs|notif|  root|                  add|        ext|            description
$app_data_root$         = true|  false| $fs_root$|            _appdata_\\
$arch_dir$              = false| false| $fs_root$
$arch_dir_levels$       = false| false| $fs_root$|            levels\\
$arch_dir_resources$    = false| false| $fs_root$|            resources\\
$arch_dir_localization$ = false| false| $fs_root$|            localization\\
$arch_dir_patches$      = false| true|  $fs_root$|            patches\\
$game_data$             = false| true|  $fs_root$|            gamedata\\
$game_ai$               = true|  false| $game_data$|          ai\\
$game_spawn$            = true|  false| $game_data$|          spawns\\
$game_levels$           = true|  false| $game_data$|          levels\\
$game_meshes$           = true|  true|  $game_data$|          meshes\\
$game_anims$            = true|  true|  $game_data$|          anims\\
$game_shaders$          = true|  true|  $game_data$|          shaders\\
$game_sounds$           = true|  true|  $game_data$|          sounds\\
$game_textures$         = true|  true|  $game_data$|          textures\\
$game_config$           = true|  false| $game_data$|          configs\\
$game_weathers$         = true|  false| $game_config$|        environment\\weathers
$game_weather_effects$  = true|  false| $game_config$|        environment\\weather_effects
$game_scripts$          = true|  false| $game_data$|          scripts\\
$logs$                  = true|  false| $app_data_root$|      logs\\
$screenshots$           = true|  false| $app_data_root$|      screenshots\\
$game_saves$            = true|  false| $app_data_root$|      savedgames\\
$downloads$             = false| false| $app_data_root$
"""


@dataclass(slots=True)
class FsGameEntry:
    alias: str
    value: str
    line_index: int = -1

    @property
    def columns(self) -> list[str]:
        return [column.strip() for column in self.value.split("|")]

    @property
    def root(self) -> str:
        columns = self.columns
        return columns[2] if len(columns) > 2 else ""

    @property
    def add(self) -> str:
        columns = self.columns
        return columns[3] if len(columns) > 3 else ""

    def set_columns(self, *, flags: str | None = None, root: str | None = None, add: str | None = None) -> None:
        columns = self.columns
        while len(columns) < 5:
            columns.append("")
        if flags is not None:
            parts = [part.strip() for part in flags.split("|")]
            columns[0] = parts[0] if parts else columns[0]
            if len(parts) > 1:
                columns[1] = parts[1]
        if root is not None:
            columns[2] = root
        if add is not None:
            columns[3] = add
        self.value = "| ".join(columns).rstrip()

    def describe(self) -> str:
        return f"{self.alias:<24} = {self.value}"


@dataclass(slots=True)
class FsGameDocument:
    lines: list[str]
    entries: dict[str, FsGameEntry] = field(default_factory=dict)
    encoding: str = "utf-8"
    newline: str = "\n"
    source: str = ""

    # ------------------------------------------------------------------ parsing
    @classmethod
    def parse(cls, text: str, *, encoding: str = "utf-8", newline: str = "\n", source: str = "") -> FsGameDocument:
        lines = text.splitlines()
        doc = cls(lines=list(lines), encoding=encoding, newline=newline, source=source)
        for index, line in enumerate(lines):
            if not line.strip() or line.lstrip().startswith(";"):
                continue
            match = ALIAS_RE.match(line)
            if not match:
                continue
            alias, value = match.group(1).lower(), match.group(2)
            value = value.split(";")[0].strip()
            if not value:
                continue
            doc.entries[alias] = FsGameEntry(alias=alias, value=value, line_index=index)
        return doc

    @classmethod
    def load(cls, path: str) -> FsGameDocument:
        if not os.path.isfile(path):
            raise ConfigError(f"fsgame.ltx не найден: {path}")
        encoding = util.detect_encoding(path)
        text = util.read_text(path, encoding=encoding)
        newline = "\r\n" if "\r\n" in text else "\n"
        return cls.parse(text, encoding=encoding, newline=newline, source=util.norm(path))

    @classmethod
    def default(cls) -> FsGameDocument:
        return cls.parse(DEFAULT_TEMPLATE, source="<встроенный шаблон>")

    # ------------------------------------------------------------------ access
    def __contains__(self, alias: str) -> bool:
        return alias.lower() in self.entries

    def entry(self, alias: str) -> FsGameEntry | None:
        return self.entries.get(alias.lower())

    def upsert(self, alias: str, *, flags: str | None = None, root: str, add: str | None = None) -> FsGameEntry:
        key = alias.lower()
        existing = self.entries.get(key)
        if existing is not None:
            existing.set_columns(flags=flags, root=root, add=add)
            self._write_back(existing)
            return existing
        entry = FsGameEntry(alias=key, value="")
        entry.set_columns(flags=flags or "true| false", root=root, add=add or "")
        self.entries[key] = entry
        self.lines.append(f"{key} = {entry.value}")
        entry.line_index = len(self.lines) - 1
        return entry

    def _write_back(self, entry: FsGameEntry) -> None:
        if 0 <= entry.line_index < len(self.lines):
            comment = ""
            original = self.lines[entry.line_index]
            if ";" in original and not original.lstrip().startswith(";"):
                comment = "  ;" + original.split(";", 1)[1]
            self.lines[entry.line_index] = f"{entry.alias:<24}= {entry.value}{comment}"

    # ------------------------------------------------------------------ rendering
    def render(self) -> str:
        return self.newline.join(self.lines) + self.newline

    def write(self, path: str) -> str:
        target = util.norm(path)
        util.ensure_dir(os.path.dirname(target))
        # ``newline=""`` keeps the line endings we produced ourselves.
        from . import util as _util

        _util.write_text_atomic(target, self.render(), encoding=self.encoding)
        return target

    # ------------------------------------------------------------------ helpers
    def resolve_alias(self, alias: str, fs_root: str, *, depth: int = 0) -> str | None:
        """Resolve ``alias`` to an absolute path, following alias chains."""
        if depth > 8:
            return None
        entry = self.entry(alias)
        if entry is None:
            return None
        root = entry.root or "$fs_root$"
        add = entry.add.replace("\\", "/").strip("/")
        if root.lower() == "$fs_root$":
            base = fs_root
        elif root.startswith("$"):
            base = self.resolve_alias(root, fs_root, depth=depth + 1)
            if base is None:
                return None
        else:
            base = root if os.path.isabs(root) else os.path.join(fs_root, root)
        return os.path.normpath(os.path.join(base, add)) if add else os.path.normpath(base)

    def app_data_root(self, fs_root: str) -> str | None:
        return self.resolve_alias(APP_DATA_ALIAS, fs_root)


def prepare_profile_fsgame(
    source: FsGameDocument | None,
    *,
    appdata_root: str | None,
    appdata_alias: str = APP_DATA_ALIAS,
    appdata_add: str = "_appdata_\\",
    required_aliases: tuple[str, ...] = ("$game_data$", "$game_config$", "$game_scripts$"),
) -> FsGameDocument:
    """Build the fsgame.ltx that lives in the profile root.

    ``appdata_root`` selects the storage mode:

    * ``None`` - profile-local data: ``$app_data_root$ = $fs_root$|_appdata_\\``;
    * an absolute path - shared data of the base game: that path is written directly, which is
      exactly what the upstream "Game data: base game directory" mode does.
    """
    doc = source if source is not None else FsGameDocument.default()
    doc = FsGameDocument.parse(doc.render(), encoding=doc.encoding, newline=doc.newline, source=doc.source)

    if appdata_root is None:
        doc.upsert(appdata_alias, flags="true| false", root="$fs_root$", add=appdata_add)
    else:
        doc.upsert(appdata_alias, flags="true| false", root=util.norm(appdata_root), add="")

    for alias in required_aliases:
        if alias not in doc:
            template_entry = FsGameDocument.default().entry(alias)
            if template_entry is not None:
                doc.upsert(alias, root=template_entry.root, add=template_entry.add)
    return doc


def profile_fsgame_path(root_path: str, *, name: str = FSGAME_NAME) -> str:
    return os.path.join(util.norm(root_path), name)
