from __future__ import annotations

import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path

COMPONENT_DIR = Path(__file__).resolve().parents[1]
LOG_DIR = COMPONENT_DIR / "logs"
LOG_PATH = LOG_DIR / "codex_quota_float.log"


def setup_logging() -> logging.Logger:
    logger = logging.getLogger("codex_quota_monitor")
    logger.setLevel(logging.INFO)
    if logger.handlers:
        return logger
    try:
        LOG_DIR.mkdir(parents=True, exist_ok=True)
        handler: logging.Handler = RotatingFileHandler(
            LOG_PATH,
            maxBytes=512 * 1024,
            backupCount=3,
            encoding="utf-8",
        )
        formatter = logging.Formatter("%(asctime)s %(levelname)s %(message)s")
        handler.setFormatter(formatter)
    except OSError:
        handler = logging.NullHandler()
    logger.addHandler(handler)
    logger.propagate = False
    return logger
