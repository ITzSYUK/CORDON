"""Exception hierarchy used across CORDON-LINUX.

Every failure that a user can act upon is raised as a :class:`CordonError` subclass with a
message that is safe to show in the CLI and the GUI.
"""

from __future__ import annotations


class CordonError(Exception):
    """Base class for all expected CORDON-LINUX failures."""


class ConfigError(CordonError):
    """Settings file is unreadable, damaged beyond repair or has an unsupported schema."""


class ProfileError(CordonError):
    """A profile references missing data or is otherwise unusable."""


class SafetyError(CordonError):
    """An operation was refused because it could damage user data outside the launcher."""


class OverlayError(CordonError):
    """The file overlay could not be prepared."""


class LaunchError(CordonError):
    """The game could not be started."""


class ToolMissingError(CordonError):
    """An external helper (mount tool, archive extractor) is not installed."""

    def __init__(self, tool: str, hint: str = "") -> None:
        message = f"внешняя утилита «{tool}» не найдена"
        if hint:
            message = f"{message}. {hint}"
        super().__init__(message)
        self.tool = tool
        self.hint = hint


class EngineNotFoundError(LaunchError):
    """No X-Ray/OpenXRay executable could be located for a profile."""
