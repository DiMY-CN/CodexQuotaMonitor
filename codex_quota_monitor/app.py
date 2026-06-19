from __future__ import annotations

import argparse
import ctypes
import datetime as dt
import json
import logging
import queue
import sys
import threading
import time
import tkinter as tk
from pathlib import Path
from tkinter import ttk

from .constants import APP_NAME, DEFAULT_HEIGHT, PLACEMENT_REASSERT_MS, TOPMOST_REASSERT_MS
from .models import ContextSnapshot, LimitWindow, QuotaSnapshot
from .readers import CodexQuotaReader, ContextReader
from .settings import AppSettings, save_settings
from .utils import clamp, compact_error, compact_token_pair, find_codex_exe, format_countdown, now_local
from .widgets import COLORS, CompactMetricBlock, set_color_thresholds
from .win32_shell import (
    Win32TrayIcon,
    apply_overlay_window_styles,
    get_root_window_handle,
    get_taskbar_rect,
    set_window_position_topmost,
    set_window_topmost,
)

LOGGER = logging.getLogger("codex_quota_monitor")

class FloatingApp:
    def __init__(self, args: argparse.Namespace, settings: AppSettings) -> None:
        self.args = args
        self.settings = settings
        self.window_width = int(settings.window_width)
        set_color_thresholds(settings.red_threshold, settings.amber_threshold)
        self.codex_home = Path(args.codex_home).expanduser()
        self.quota_reader = CodexQuotaReader(self.codex_home, Path(args.codex_exe) if args.codex_exe else None)
        self.context_reader = ContextReader(self.codex_home)
        self.root = tk.Tk()
        self.root.report_callback_exception = self._report_callback_exception
        self.root.title(APP_NAME)
        self.root.geometry(f"{self.window_width}x{DEFAULT_HEIGHT}")
        self.root.resizable(False, False)
        self.root.overrideredirect(True)
        self.root.attributes("-topmost", True)
        self.root.attributes("-alpha", 1.0)

        self._drag_start: tuple[int, int] | None = None
        self._last_quota: QuotaSnapshot | None = None
        self._last_context: ContextSnapshot | None = None
        self._quota_last_error: str | None = None
        self._context_last_error: str | None = None
        self._quota_last_success_at: dt.datetime | None = None
        self._context_last_success_at: dt.datetime | None = None
        self._quota_in_flight = False
        self._context_in_flight = False
        self._quota_pending_refresh = False
        self._context_pending_refresh = False
        self._next_quota_at = 0.0
        self._next_context_at = 0.0
        self._worker_queue: queue.Queue[tuple[str, object]] = queue.Queue()
        self.quota_interval = int(settings.quota_interval)
        self.context_interval = int(settings.context_interval)
        self.quota_interval_var = tk.IntVar(value=self.quota_interval)
        self.context_interval_var = tk.IntVar(value=self.context_interval)
        self.tray_icon: Win32TrayIcon | None = None
        self.tray_available = False

        self._setup_style()
        self._build_ui()
        self._bind_window()
        self._apply_overlay_styles()
        self._setup_tray_icon()
        self.root.after(80, self.snap_to_taskbar)
        self.root.after(800, self.snap_to_taskbar)
        self.root.after(1000, self._placement_watchdog)
        self.root.after(200, self._topmost_watchdog)
        self.root.after(250, self._tick)
        self.refresh_now()

    def _setup_style(self) -> None:
        style = ttk.Style()
        style.theme_use("clam")
        style.configure("TFrame", background=COLORS["panel"])

    def _report_callback_exception(
        self,
        exc_type: type[BaseException],
        exc: BaseException,
        tb: object,
    ) -> None:
        LOGGER.exception("tk callback failed", exc_info=(exc_type, exc, tb))

    def _build_ui(self) -> None:
        self.root.configure(bg=COLORS["window"])
        outer = tk.Frame(self.root, bg=COLORS["border"])
        outer.pack(fill="both", expand=True)
        panel = tk.Frame(outer, bg=COLORS["panel"])
        panel.pack(fill="both", expand=True, padx=1, pady=1)
        content = tk.Frame(panel, bg=COLORS["panel"])
        content.pack(fill="both", expand=True, padx=4, pady=4)
        for column in range(3):
            content.columnconfigure(column, weight=1, uniform="metric")
        content.rowconfigure(0, weight=1)

        self.row_5h = CompactMetricBlock(content, "5H", COLORS["row"])
        self.row_5h.grid(row=0, column=0, sticky="nsew", padx=(0, 3))
        self.row_week = CompactMetricBlock(content, "WK", COLORS["row_alt"])
        self.row_week.grid(row=0, column=1, sticky="nsew", padx=(0, 3))
        self.row_ctx = CompactMetricBlock(content, "CTX", COLORS["row"])
        self.row_ctx.grid(row=0, column=2, sticky="nsew")

        self.menu = tk.Menu(self.root, tearoff=False)
        self.menu.add_command(label="Refresh now", command=self.refresh_now)
        self.menu.add_command(label="Snap to taskbar left", command=self.snap_to_taskbar)
        self.menu.add_command(
            label="Tray icon: pending",
            state="disabled",
        )
        self.menu.add_separator()

        quota_menu = tk.Menu(self.menu, tearoff=False)
        for label, seconds in (("1 min", 60), ("3 min", 180), ("5 min", 300), ("10 min", 600), ("15 min", 900)):
            quota_menu.add_radiobutton(
                label=label,
                value=seconds,
                variable=self.quota_interval_var,
                command=lambda value=seconds: self.set_quota_interval(value),
            )
        self.menu.add_cascade(label="Quota interval", menu=quota_menu)

        context_menu = tk.Menu(self.menu, tearoff=False)
        for label, seconds in (("5 sec", 5), ("15 sec", 15), ("30 sec", 30), ("1 min", 60), ("2 min", 120)):
            context_menu.add_radiobutton(
                label=label,
                value=seconds,
                variable=self.context_interval_var,
                command=lambda value=seconds: self.set_context_interval(value),
            )
        self.menu.add_cascade(label="Context interval", menu=context_menu)
        self.menu.add_separator()
        self.menu.add_command(label="Exit", command=self.exit_app)

    def _bind_window(self) -> None:
        self.root.bind("<ButtonPress-1>", self._start_drag)
        self.root.bind("<B1-Motion>", self._drag)
        self.root.bind("<ButtonRelease-1>", self._end_drag)
        self.root.bind("<Button-3>", self._show_menu)
        self.root.bind("<Escape>", lambda _event: self.exit_app())
        self.root.protocol("WM_DELETE_WINDOW", self.exit_app)

    def _apply_overlay_styles(self) -> None:
        try:
            self.root.update_idletasks()
            apply_overlay_window_styles(int(self.root.winfo_id()))
        except tk.TclError:
            return

    def _setup_tray_icon(self) -> None:
        if bool(self.settings.no_tray):
            self._set_menu_label("Tray icon: off")
            return
        if sys.platform != "win32":
            self._set_menu_label("Tray icon: unavailable")
            return
        self.root.update_idletasks()
        icon = Win32TrayIcon(
            self.root,
            APP_NAME,
            self._tray_left_click,
            self._show_menu_at,
        )
        if icon.install():
            self.tray_icon = icon
            self.tray_available = True
            self._set_menu_label("Tray icon: on")
        else:
            self._set_menu_label("Tray icon: unavailable")

    def _set_menu_label(self, label: str) -> None:
        try:
            end_index = int(self.menu.index("end") or 0)
            for index in range(end_index + 1):
                if str(self.menu.entrycget(index, "label")).startswith("Tray icon:"):
                    self.menu.entryconfigure(index, label=label)
                    return
        except tk.TclError:
            return

    def _tray_left_click(self) -> None:
        self.snap_to_taskbar()
        self._force_topmost()
        self.refresh_now()

    def _start_drag(self, event: tk.Event) -> None:
        self._drag_start = (event.x_root - self.root.winfo_x(), event.y_root - self.root.winfo_y())

    def _drag(self, event: tk.Event) -> None:
        if self._drag_start is None:
            return
        dx, dy = self._drag_start
        self.root.geometry(f"+{event.x_root - dx}+{event.y_root - dy}")

    def _end_drag(self, _event: tk.Event) -> None:
        self._drag_start = None

    def _show_menu(self, event: tk.Event) -> None:
        self._show_menu_at(event.x_root, event.y_root)

    def _show_menu_at(self, x: int, y: int) -> None:
        self._force_topmost()
        if sys.platform == "win32":
            try:
                ctypes.windll.user32.SetForegroundWindow(ctypes.c_void_p(get_root_window_handle(int(self.root.winfo_id()))))
            except Exception:
                pass
        try:
            self.menu.tk_popup(x, y)
        finally:
            try:
                self.menu.grab_release()
            except tk.TclError:
                pass

    def _force_topmost(self) -> None:
        try:
            self.root.attributes("-topmost", True)
            self.root.lift()
            self.root.update_idletasks()
            apply_overlay_window_styles(int(self.root.winfo_id()))
            set_window_topmost(int(self.root.winfo_id()))
        except tk.TclError:
            return

    def _topmost_watchdog(self) -> None:
        self._force_topmost()
        self.root.after(TOPMOST_REASSERT_MS, self._topmost_watchdog)

    def _placement_watchdog(self) -> None:
        self.snap_to_taskbar()
        self.root.after(PLACEMENT_REASSERT_MS, self._placement_watchdog)

    def snap_to_taskbar(self) -> None:
        self.root.update_idletasks()
        taskbar = get_taskbar_rect()
        if taskbar:
            edge, rect = taskbar
            taskbar_width = max(1, rect.right - rect.left)
            taskbar_height = max(1, rect.bottom - rect.top)
            if edge in (1, 3):
                width = min(self.window_width, max(210, int(taskbar_width * 0.18)))
                height = taskbar_height
                x = rect.left
                y = rect.top
            else:
                width = taskbar_width
                height = min(self.window_width, max(140, int((rect.bottom - rect.top) * 0.18)))
                x = rect.left
                y = rect.top
        else:
            screen_w = self.root.winfo_screenwidth()
            screen_h = self.root.winfo_screenheight()
            width = self.window_width
            height = DEFAULT_HEIGHT
            x = 0
            y = screen_h - height
        screen_w = self.root.winfo_screenwidth()
        screen_h = self.root.winfo_screenheight()
        x = clamp(x, 0, max(0, screen_w - width))
        y = clamp(y, 0, max(0, screen_h - height))
        self.root.geometry(f"{width}x{height}+{x}+{y}")
        self.root.update_idletasks()
        set_window_position_topmost(int(self.root.winfo_id()), x, y, width, height)
        self._force_topmost()

    def refresh_now(self) -> None:
        self._next_quota_at = time.monotonic() + self.quota_interval
        self._next_context_at = time.monotonic() + self.context_interval
        self._start_quota_refresh()
        self._start_context_refresh()

    def _start_quota_refresh(self, pending_if_busy: bool = True) -> bool:
        if self._quota_in_flight:
            if pending_if_busy:
                self._quota_pending_refresh = True
            self._update_title()
            return False
        self._quota_in_flight = True
        self._update_title()
        threading.Thread(target=self._quota_worker, daemon=True).start()
        return True

    def _start_context_refresh(self, pending_if_busy: bool = True) -> bool:
        if self._context_in_flight:
            if pending_if_busy:
                self._context_pending_refresh = True
            self._update_title()
            return False
        self._context_in_flight = True
        self._update_title()
        threading.Thread(target=self._context_worker, daemon=True).start()
        return True

    def set_quota_interval(self, seconds: int) -> None:
        self.quota_interval = int(seconds)
        self.quota_interval_var.set(self.quota_interval)
        self._next_quota_at = time.monotonic() + self.quota_interval
        self._save_settings()
        self._update_title()

    def set_context_interval(self, seconds: int) -> None:
        self.context_interval = int(seconds)
        self.context_interval_var.set(self.context_interval)
        self._next_context_at = time.monotonic() + self.context_interval
        self._save_settings()
        self._update_title()

    def _save_settings(self) -> None:
        self.settings.quota_interval = int(self.quota_interval)
        self.settings.context_interval = int(self.context_interval)
        save_settings(self.settings, logger=LOGGER)

    def _quota_worker(self) -> None:
        try:
            result = self.quota_reader.read()
        except Exception as exc:
            result = QuotaSnapshot(error=compact_error(exc), updated_at=now_local())
        self._worker_queue.put(("quota", result))

    def _context_worker(self) -> None:
        try:
            result = self.context_reader.read()
        except Exception as exc:
            result = ContextSnapshot(error=compact_error(exc), source_label=ContextReader.SOURCE_LABEL, updated_at=now_local())
        self._worker_queue.put(("context", result))

    def _handle_quota_result(self, value: object) -> None:
        self._quota_in_flight = False
        if isinstance(value, QuotaSnapshot):
            if value.error:
                self._quota_last_error = value.error
                if self._last_quota is None or self._last_quota.error:
                    self._last_quota = value
            else:
                self._last_quota = value
                self._quota_last_error = None
                self._quota_last_success_at = value.updated_at or now_local()
        else:
            self._quota_last_error = "invalid quota worker result"
            if self._last_quota is None:
                self._last_quota = QuotaSnapshot(error=self._quota_last_error, updated_at=now_local())

        if self._quota_pending_refresh:
            self._quota_pending_refresh = False
            self._next_quota_at = time.monotonic() + self.quota_interval
            self._start_quota_refresh(pending_if_busy=False)

    def _handle_context_result(self, value: object) -> None:
        self._context_in_flight = False
        if isinstance(value, ContextSnapshot):
            if value.error:
                self._context_last_error = value.error
                if self._last_context is None or self._last_context.error:
                    self._last_context = value
            else:
                self._last_context = value
                self._context_last_error = None
                self._context_last_success_at = value.updated_at or now_local()
        else:
            self._context_last_error = "invalid context worker result"
            if self._last_context is None:
                self._last_context = ContextSnapshot(
                    error=self._context_last_error,
                    source_label=ContextReader.SOURCE_LABEL,
                    updated_at=now_local(),
                )

        if self._context_pending_refresh:
            self._context_pending_refresh = False
            self._next_context_at = time.monotonic() + self.context_interval
            self._start_context_refresh(pending_if_busy=False)

    def _tick(self) -> None:
        while True:
            try:
                kind, value = self._worker_queue.get_nowait()
            except queue.Empty:
                break
            if kind == "quota":
                self._handle_quota_result(value)
            if kind == "context":
                self._handle_context_result(value)
            self._render()

        mono = time.monotonic()
        if mono >= self._next_quota_at:
            self._next_quota_at = mono + self.quota_interval
            self._start_quota_refresh()
        if mono >= self._next_context_at:
            self._next_context_at = mono + self.context_interval
            self._start_context_refresh()
        self._render_countdowns()
        self.root.after(1000, self._tick)

    def _render(self) -> None:
        self._render_quota()
        self._render_context()
        self._update_title()

    def _latest_display_update(self) -> dt.datetime | None:
        updated_parts: list[dt.datetime] = []
        if self._quota_last_success_at:
            updated_parts.append(self._quota_last_success_at)
        elif self._last_quota and self._last_quota.updated_at:
            updated_parts.append(self._last_quota.updated_at)
        if self._context_last_success_at:
            updated_parts.append(self._context_last_success_at)
        elif self._last_context and self._last_context.updated_at:
            updated_parts.append(self._last_context.updated_at)
        return max(updated_parts) if updated_parts else None

    def _is_stale_time(self, updated_at: dt.datetime | None, interval_seconds: int) -> bool:
        if updated_at is None:
            return False
        age_seconds = max(0.0, (now_local() - updated_at).total_seconds())
        return age_seconds > max(float(interval_seconds) * 2.0, float(interval_seconds) + 5.0)

    def _is_quota_stale(self) -> bool:
        updated_at = self._quota_last_success_at
        if updated_at is None and self._last_quota and not self._last_quota.error:
            updated_at = self._last_quota.updated_at
        return self._is_stale_time(updated_at, self.quota_interval)

    def _is_context_stale(self) -> bool:
        updated_at = self._context_last_success_at
        if updated_at is None and self._last_context and not self._last_context.error:
            updated_at = self._last_context.updated_at
        return self._is_stale_time(updated_at, self.context_interval)

    def _status_parts(self, include_error_details: bool = False) -> list[str]:
        context_source = ContextReader.SOURCE_LABEL
        if self._last_context and self._last_context.source_label:
            context_source = self._last_context.source_label
        parts = [f"CTX {context_source}"]
        if self._quota_in_flight:
            parts.append("quota reading")
        if self._context_in_flight:
            parts.append("ctx reading")
        if self._quota_pending_refresh:
            parts.append("quota pending")
        if self._context_pending_refresh:
            parts.append("ctx pending")
        if self._is_quota_stale():
            parts.append("quota stale")
        if self._is_context_stale():
            parts.append("ctx stale")
        if self._quota_last_error:
            if include_error_details:
                parts.append(f"quota error: {truncate_text(self._quota_last_error, 36)}")
            else:
                parts.append("quota last error")
        if self._context_last_error:
            if include_error_details:
                parts.append(f"ctx error: {truncate_text(self._context_last_error, 36)}")
            else:
                parts.append("ctx last error")
        return parts

    def _update_title(self, updated_at: dt.datetime | None = None) -> None:
        if updated_at is None:
            updated_at = self._latest_display_update()
        if updated_at:
            stamp = updated_at.strftime("%H:%M")
        else:
            stamp = "--:--"
        status = " | ".join(self._status_parts())
        self.root.title(
            f"{APP_NAME} | updated {stamp} | quota {self.quota_interval}s | context {self.context_interval}s | {status}"
        )
        if self.tray_icon:
            tooltip_status = " | ".join(self._status_parts(include_error_details=True))
            self.tray_icon.set_tooltip(f"{APP_NAME} | updated {stamp} | {tooltip_status}")

    def _render_countdowns(self) -> None:
        if self._last_quota and not self._last_quota.error:
            self._render_quota()
        self._update_title()

    def _render_quota(self) -> None:
        quota = self._last_quota
        if not quota:
            self.row_5h.set_metric(None, "Quota waiting")
            self.row_week.set_metric(None, "Quota waiting")
            return
        if quota.error:
            self.row_5h.set_metric(None, "Quota unavailable")
            self.row_week.set_metric(None, "Right-click to refresh")
            return
        primary = quota.primary or LimitWindow("5h")
        secondary = quota.secondary or LimitWindow("Week")
        self.row_5h.set_metric(
            primary.remaining_percent,
            format_countdown(primary.resets_at),
        )
        self.row_week.set_metric(
            secondary.remaining_percent,
            format_countdown(secondary.resets_at),
        )

    def _render_context(self) -> None:
        context = self._last_context
        if not context:
            self.row_ctx.set_metric(None, "Context waiting")
            return
        if context.error:
            self.row_ctx.set_metric(None, "Context unavailable")
            return
        detail = "Context waiting"
        if context.input_tokens is not None and context.effective_window:
            detail = compact_token_pair(context.input_tokens, context.effective_window)
        self.row_ctx.set_metric(context.remaining_percent, detail)

    def run(self) -> None:
        try:
            self.root.mainloop()
        finally:
            self._cleanup_shell_integrations()

    def exit_app(self) -> None:
        self._cleanup_shell_integrations()
        self.root.destroy()

    def _cleanup_shell_integrations(self) -> None:
        if self.tray_icon:
            self.tray_icon.uninstall()
            self.tray_icon = None


def check_environment(args: argparse.Namespace, settings: AppSettings) -> int:
    codex_home = Path(args.codex_home).expanduser()
    codex_exe = Path(args.codex_exe) if args.codex_exe else find_codex_exe()
    context = ContextReader(codex_home).read()
    taskbar = get_taskbar_rect()
    print(f"codex_home={codex_home}")
    print(f"codex_exe={codex_exe if codex_exe else 'NOT_FOUND'}")
    print(f"logs_2.sqlite={(codex_home / 'logs_2.sqlite').exists()}")
    print(f"models_cache.json={(codex_home / 'models_cache.json').exists()}")
    print(f"tray={'disabled' if settings.no_tray else 'enabled'}")
    if context.error:
        print(f"context_error={context.error}")
    else:
        remaining = f"{context.remaining_percent:.1f}%" if context.remaining_percent is not None else "unknown"
        print(
            "context="
            f"model={context.model} input={context.input_tokens} "
            f"effective_window={context.effective_window} remaining={remaining} "
            f"source={context.source_label or ContextReader.SOURCE_LABEL} skipped_rows={context.skipped_rows}"
        )
    if taskbar:
        edge, rect = taskbar
        print(f"taskbar=edge:{edge} rect:{rect.left},{rect.top},{rect.right},{rect.bottom}")
    else:
        print("taskbar=unavailable")
    return 0 if codex_exe and not context.error else 2


def print_snapshot_once(args: argparse.Namespace) -> int:
    codex_home = Path(args.codex_home).expanduser()
    codex_exe = Path(args.codex_exe) if args.codex_exe else find_codex_exe()
    quota = CodexQuotaReader(codex_home, codex_exe).read()
    context = ContextReader(codex_home).read()
    payload = {
        "quota": {
            "error": quota.error,
            "limit_id": quota.limit_id,
            "plan_type": quota.plan_type,
            "primary_remaining_percent": quota.primary.remaining_percent if quota.primary else None,
            "primary_resets_at": quota.primary.resets_at if quota.primary else None,
            "secondary_remaining_percent": quota.secondary.remaining_percent if quota.secondary else None,
            "secondary_resets_at": quota.secondary.resets_at if quota.secondary else None,
        },
        "context": {
            "error": context.error,
            "model": context.model,
            "input_tokens": context.input_tokens,
            "effective_window": context.effective_window,
            "remaining_percent": context.remaining_percent,
            "event_time": context.event_time,
            "conversation_id": context.conversation_id,
            "source_label": context.source_label,
            "skipped_rows": context.skipped_rows,
        },
    }
    print(json.dumps(payload, ensure_ascii=False, indent=2))
    return 0 if not quota.error and not context.error else 2
