"""Rotating launcher log plus an in-memory ring buffer for the GUI log pane."""

from __future__ import annotations

import logging
import os
from collections import deque
from logging.handlers import RotatingFileHandler

from . import util
from .paths import AppPaths

LOGGER_NAME = "cordon"
MAX_BYTES = 1024 * 1024


class MemoryHandler(logging.Handler):
    def __init__(self, capacity: int = 4000) -> None:
        super().__init__()
        self.records: deque[str] = deque(maxlen=capacity)

    def emit(self, record: logging.LogRecord) -> None:  # pragma: no cover - trivial
        try:
            self.records.append(self.format(record))
        except Exception:  # noqa: BLE001 - logging must never raise
            pass

    def tail(self, count: int = 400) -> list[str]:
        return list(self.records)[-count:]


def setup_logging(app: AppPaths, *, level: str = "info", console: bool = False) -> tuple[logging.Logger, MemoryHandler]:
    util.ensure_dir(app.cache_dir)
    logger = logging.getLogger(LOGGER_NAME)
    logger.setLevel(getattr(logging, level.upper(), logging.INFO))
    for handler in list(logger.handlers):
        logger.removeHandler(handler)

    formatter = logging.Formatter("%(asctime)s %(levelname)-7s %(message)s", datefmt="%Y-%m-%d %H:%M:%S")

    file_handler = RotatingFileHandler(app.launcher_log, maxBytes=MAX_BYTES, backupCount=1, encoding="utf-8")
    file_handler.setFormatter(formatter)
    logger.addHandler(file_handler)

    memory = MemoryHandler(capacity=4000)
    memory.setFormatter(formatter)
    logger.addHandler(memory)

    if console:  # pragma: no cover - CLI convenience
        stream = logging.StreamHandler()
        stream.setFormatter(formatter)
        logger.addHandler(stream)

    logger.propagate = False
    return logger, memory


def get_logger() -> logging.Logger:
    return logging.getLogger(LOGGER_NAME)
