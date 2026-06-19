from __future__ import annotations

import argparse
import logging
import subprocess
import sys
import time
from pathlib import Path

from .app import FloatingApp, check_environment, print_snapshot_once
from .constants import APP_NAME, SINGLE_INSTANCE_MUTEX_NAME
from .logging_setup import setup_logging
from .settings import apply_cli_overrides, ensure_default_settings, load_settings
from .utils import default_codex_home
from .win32_shell import SingleInstanceGuard, activate_existing_app_window

LOGGER = logging.getLogger("codex_quota_monitor")


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=APP_NAME)
    parser.add_argument("--codex-home", default=str(default_codex_home()), help="Codex home directory, default: %%USERPROFILE%%\.codex")
    parser.add_argument("--codex-exe", default="", help="Optional explicit path to codex.exe")
    parser.add_argument("--quota-interval", type=int, default=None, help="Quota refresh interval in seconds")
    parser.add_argument("--context-interval", type=int, default=None, help="Context log refresh interval in seconds")
    tray_group = parser.add_mutually_exclusive_group()
    tray_group.add_argument("--no-tray", dest="no_tray", action="store_true", help="Disable the Win32 notification-area icon")
    tray_group.add_argument("--tray", dest="no_tray", action="store_false", help="Enable the Win32 notification-area icon")
    parser.set_defaults(no_tray=None)
    parser.add_argument("--check", action="store_true", help="Check paths and local context parsing without opening the GUI")
    parser.add_argument("--once", action="store_true", help="Print one quota/context snapshot and exit")
    parser.add_argument(
        "--silence",
        "--silent",
        dest="silence",
        action="store_true",
        help="Launch the GUI in a detached background process and return to the terminal",
    )
    args = parser.parse_args(argv)
    if args.silence and (args.check or args.once):
        parser.error("--silence can only be used for GUI mode; do not combine it with --check or --once")
    return args


def _without_silence_flag(argv: list[str]) -> list[str]:
    return [arg for arg in argv if arg not in {"--silence", "--silent"}]


def _startup_error_log_path() -> Path:
    component_dir = Path(__file__).resolve().parents[1]
    log_dir = component_dir / "logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    return log_dir / "silence_startup_error.log"


def _detached_creation_flags() -> int:
    flags = 0
    for name in ("CREATE_NEW_PROCESS_GROUP", "DETACHED_PROCESS", "CREATE_NO_WINDOW"):
        flags |= int(getattr(subprocess, name, 0))
    return flags


def launch_silenced_gui(argv: list[str], logger: logging.Logger) -> int:
    script_path = Path(sys.argv[0]).resolve()
    if not script_path.exists():
        print(f"Cannot launch detached GUI: script not found: {script_path}", file=sys.stderr)
        return 2

    child_args = [sys.executable, str(script_path), *_without_silence_flag(argv)]
    error_log_path = _startup_error_log_path()
    try:
        with error_log_path.open("w", encoding="utf-8") as stderr_file:
            process = subprocess.Popen(
                child_args,
                cwd=str(script_path.parent),
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=stderr_file,
                close_fds=True,
                creationflags=_detached_creation_flags(),
            )
    except Exception as exc:
        print(f"Cannot launch detached GUI: {exc}", file=sys.stderr)
        logger.exception("failed to launch silenced GUI")
        return 2

    time.sleep(2.0)
    exit_code = process.poll()
    if exit_code is None:
        logger.info("launched silenced GUI pid=%s python=%s", process.pid, sys.executable)
        return 0
    if exit_code == 0:
        logger.info("silenced GUI helper exited successfully during startup pid=%s", process.pid)
        return 0

    detail = ""
    try:
        detail = error_log_path.read_text(encoding="utf-8").strip()
    except Exception:
        detail = ""
    if detail:
        print(detail, file=sys.stderr)
    print(f"Detached GUI exited during startup with code {exit_code}.", file=sys.stderr)
    logger.error("silenced GUI exited during startup code=%s", exit_code)
    return exit_code or 1


def main(argv: list[str] | None = None) -> int:
    logger = setup_logging()
    args = parse_args(argv)
    base_settings = load_settings(logger=logger)
    settings = apply_cli_overrides(base_settings, args)
    logger.info("starting %s check=%s once=%s no_tray=%s quota_interval=%s context_interval=%s", APP_NAME, args.check, args.once, settings.no_tray, settings.quota_interval, settings.context_interval)
    if args.check:
        return check_environment(args, settings)
    if args.once:
        return print_snapshot_once(args)
    if args.silence:
        return launch_silenced_gui(list(sys.argv[1:] if argv is None else argv), logger)
    ensure_default_settings(logger=logger)
    guard = SingleInstanceGuard(SINGLE_INSTANCE_MUTEX_NAME)
    if not guard.acquire():
        logger.info("existing GUI instance detected; activating existing window")
        activate_existing_app_window()
        return 0
    try:
        app = FloatingApp(args, settings)
        app.run()
        return 0
    finally:
        guard.release()


if __name__ == "__main__":
    raise SystemExit(main())
