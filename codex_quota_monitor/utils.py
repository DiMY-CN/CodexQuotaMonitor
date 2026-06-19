from __future__ import annotations

import datetime as dt
import os
import shutil
import time
from pathlib import Path

def default_codex_home() -> Path:
    return Path(os.environ.get("CODEX_HOME") or (Path.home() / ".codex"))


def find_codex_exe() -> Path | None:
    env_value = os.environ.get("CODEX_EXE")
    if env_value:
        path = Path(env_value)
        if path.exists():
            return path

    local_app_data = os.environ.get("LOCALAPPDATA")
    candidates: list[Path] = []
    if local_app_data:
        root = Path(local_app_data)
        candidates.extend(
            sorted(
                (root / "OpenAI" / "Codex" / "bin").glob("*/codex.exe"),
                key=lambda p: p.stat().st_mtime if p.exists() else 0,
                reverse=True,
            )
        )
        candidates.append(
            root
            / "Packages"
            / "OpenAI.Codex_2p2nqsd0c76g0"
            / "LocalCache"
            / "Local"
            / "OpenAI"
            / "Codex"
            / "bin"
            / "codex.exe"
        )

    which = shutil.which("codex")
    if which:
        candidates.append(Path(which))

    for candidate in candidates:
        if candidate.exists():
            return candidate
    return None


def now_local() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc).astimezone()


def format_local_time(epoch_seconds: int | None) -> str:
    if not epoch_seconds:
        return "--:--"
    return dt.datetime.fromtimestamp(epoch_seconds, tz=dt.timezone.utc).astimezone().strftime("%H:%M")


def format_countdown(epoch_seconds: int | None) -> str:
    if not epoch_seconds:
        return "reset --"
    seconds = int(epoch_seconds - time.time())
    if seconds <= 0:
        return "reset now"
    days, rem = divmod(seconds, 86400)
    hours, rem = divmod(rem, 3600)
    minutes = rem // 60
    if days:
        return f"{days}d {hours}h"
    if hours:
        return f"{hours}h {minutes}m"
    return f"{minutes}m"


def compact_number(value: int | None) -> str:
    if value is None:
        return "--"
    if abs(value) >= 1_000_000:
        return f"{value / 1_000_000:.1f}M"
    if abs(value) >= 1_000:
        return f"{value / 1_000:.0f}k"
    return str(value)


def compact_token_pair(used: int | None, limit: int | None) -> str:
    if used is None or limit is None:
        return "--"
    if abs(used) >= 1_000 and abs(limit) >= 1_000:
        return f"{used // 1_000}/{limit // 1_000}"
    return f"{used}/{limit}"


def truncate_text(text: str, max_chars: int) -> str:
    clean = " ".join(str(text).split())
    if len(clean) <= max_chars:
        return clean
    return clean[: max(0, max_chars - 3)].rstrip() + "..."


def compact_error(exc: BaseException) -> str:
    text = str(exc).strip()
    return text if len(text) <= 180 else text[:177] + "..."


def clamp(value: int, lower: int, upper: int) -> int:
    return max(lower, min(upper, value))
