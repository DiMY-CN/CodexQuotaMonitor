from __future__ import annotations

import argparse
import json
import logging
from dataclasses import asdict, dataclass
from pathlib import Path

from .constants import DEFAULT_CONTEXT_INTERVAL, DEFAULT_QUOTA_INTERVAL, DEFAULT_WIDTH

COMPONENT_DIR = Path(__file__).resolve().parents[1]
SETTINGS_PATH = COMPONENT_DIR / "settings.json"


@dataclass
class AppSettings:
    quota_interval: int = DEFAULT_QUOTA_INTERVAL
    context_interval: int = DEFAULT_CONTEXT_INTERVAL
    no_tray: bool = False
    window_width: int = DEFAULT_WIDTH
    red_threshold: float = 15.0
    amber_threshold: float = 30.0


def _coerce_settings(raw: dict[str, object]) -> AppSettings:
    defaults = AppSettings()
    settings = AppSettings(
        quota_interval=int(raw.get("quota_interval", defaults.quota_interval) or defaults.quota_interval),
        context_interval=int(raw.get("context_interval", defaults.context_interval) or defaults.context_interval),
        no_tray=bool(raw.get("no_tray", defaults.no_tray)),
        window_width=int(raw.get("window_width", defaults.window_width) or defaults.window_width),
        red_threshold=float(raw.get("red_threshold", defaults.red_threshold) or defaults.red_threshold),
        amber_threshold=float(raw.get("amber_threshold", defaults.amber_threshold) or defaults.amber_threshold),
    )
    settings.quota_interval = max(30, settings.quota_interval)
    settings.context_interval = max(3, settings.context_interval)
    settings.window_width = max(210, min(520, settings.window_width))
    settings.red_threshold = max(0.0, min(100.0, settings.red_threshold))
    settings.amber_threshold = max(settings.red_threshold, min(100.0, settings.amber_threshold))
    return settings


def load_settings(path: Path = SETTINGS_PATH, logger: logging.Logger | None = None) -> AppSettings:
    if not path.exists():
        return AppSettings()
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(raw, dict):
            raise ValueError("settings root must be an object")
        return _coerce_settings(raw)
    except Exception as exc:
        if logger:
            logger.warning("failed to load settings from %s: %s", path, exc)
        return AppSettings()


def save_settings(settings: AppSettings, path: Path = SETTINGS_PATH, logger: logging.Logger | None = None) -> None:
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        payload = asdict(settings)
        path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    except Exception as exc:
        if logger:
            logger.warning("failed to save settings to %s: %s", path, exc)


def apply_cli_overrides(settings: AppSettings, args: argparse.Namespace) -> AppSettings:
    merged = AppSettings(**asdict(settings))
    if getattr(args, "quota_interval", None) is not None:
        merged.quota_interval = int(args.quota_interval)
    if getattr(args, "context_interval", None) is not None:
        merged.context_interval = int(args.context_interval)
    if getattr(args, "no_tray", None) is not None:
        merged.no_tray = bool(args.no_tray)
    return _coerce_settings(asdict(merged))


def ensure_default_settings(path: Path = SETTINGS_PATH, logger: logging.Logger | None = None) -> None:
    if path.exists():
        return
    save_settings(AppSettings(), path=path, logger=logger)
