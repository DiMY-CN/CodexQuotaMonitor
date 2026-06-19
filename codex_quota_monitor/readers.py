from __future__ import annotations

import json
import logging
import os
import queue
import re
import sqlite3
import subprocess
import sys
import threading
import time
from pathlib import Path

from .constants import CONTEXT_ROW_LOOKBACK
from .models import ContextSnapshot, LimitWindow, QuotaSnapshot
from .utils import compact_error, find_codex_exe, now_local

LOGGER = logging.getLogger("codex_quota_monitor")

class CodexRpcError(RuntimeError):
    pass


class CodexQuotaReader:
    def __init__(self, codex_home: Path, codex_exe: Path | None = None, timeout_seconds: float = 18.0) -> None:
        self.codex_home = codex_home
        self.codex_exe = codex_exe or find_codex_exe()
        self.timeout_seconds = timeout_seconds

    def read(self) -> QuotaSnapshot:
        if self.codex_exe is None:
            return QuotaSnapshot(error="codex.exe not found")
        try:
            result = self._call_app_server("account/rateLimits/read", None)
            return self._parse_result(result)
        except Exception as exc:
            return QuotaSnapshot(error=compact_error(exc), updated_at=now_local())

    def _call_app_server(self, method: str, params: object) -> object:
        env = os.environ.copy()
        env["CODEX_HOME"] = str(self.codex_home)
        creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        proc = subprocess.Popen(
            [str(self.codex_exe), "app-server", "--listen", "stdio://"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=env,
            creationflags=creationflags,
        )
        line_queue: queue.Queue[str] = queue.Queue()
        reader = threading.Thread(target=self._pump_stdout, args=(proc, line_queue), daemon=True)
        reader.start()
        try:
            self._send(proc, 0, "initialize", {
                "clientInfo": {"name": "codex-quota-float", "version": "0.1.0"},
                "capabilities": {"experimentalApi": True, "optOutNotificationMethods": []},
            })
            init_response = self._read_response(proc, line_queue, 0)
            if "error" in init_response:
                raise CodexRpcError(json.dumps(init_response["error"], ensure_ascii=False))

            self._send(proc, 1, method, params)
            response = self._read_response(proc, line_queue, 1)
            if "error" in response:
                raise CodexRpcError(json.dumps(response["error"], ensure_ascii=False))
            return response.get("result")
        finally:
            self._stop_process(proc)

    @staticmethod
    def _send(proc: subprocess.Popen[str], msg_id: int, method: str, params: object) -> None:
        if proc.stdin is None:
            raise CodexRpcError("app-server stdin unavailable")
        payload = {"jsonrpc": "2.0", "id": msg_id, "method": method, "params": params}
        proc.stdin.write(json.dumps(payload, separators=(",", ":"), ensure_ascii=False) + "\n")
        proc.stdin.flush()

    @staticmethod
    def _pump_stdout(proc: subprocess.Popen[str], line_queue: queue.Queue[str]) -> None:
        if proc.stdout is None:
            return
        try:
            for line in proc.stdout:
                line_queue.put(line)
        except Exception:
            return

    def _read_response(self, proc: subprocess.Popen[str], line_queue: queue.Queue[str], expected_id: int) -> dict:
        deadline = time.monotonic() + self.timeout_seconds
        while time.monotonic() < deadline:
            timeout = max(0.2, min(1.0, deadline - time.monotonic()))
            try:
                line = line_queue.get(timeout=timeout)
            except queue.Empty:
                if proc.poll() is not None:
                    raise CodexRpcError(f"app-server exited with code {proc.returncode}")
                continue
            try:
                message = json.loads(line)
            except json.JSONDecodeError:
                continue
            if message.get("id") == expected_id:
                return message
        raise CodexRpcError("app-server JSON-RPC timeout")

    @staticmethod
    def _stop_process(proc: subprocess.Popen[str]) -> None:
        if proc.stdin:
            try:
                proc.stdin.close()
            except Exception:
                pass
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                proc.kill()

    @staticmethod
    def _parse_result(result: object) -> QuotaSnapshot:
        if not isinstance(result, dict):
            return QuotaSnapshot(error="unexpected rate limit response", updated_at=now_local())
        data = result.get("rateLimits") or {}
        if not isinstance(data, dict):
            return QuotaSnapshot(error="missing rateLimits", updated_at=now_local())

        return QuotaSnapshot(
            limit_id=data.get("limitId"),
            limit_name=data.get("limitName"),
            plan_type=data.get("planType"),
            primary=CodexQuotaReader._parse_window("5h", data.get("primary")),
            secondary=CodexQuotaReader._parse_window("Week", data.get("secondary")),
            rate_limit_reached_type=data.get("rateLimitReachedType"),
            updated_at=now_local(),
        )

    @staticmethod
    def _parse_window(label: str, raw: object) -> LimitWindow:
        if not isinstance(raw, dict):
            return LimitWindow(label=label)
        used = raw.get("usedPercent")
        remaining = None
        if isinstance(used, (int, float)):
            remaining = max(0.0, min(100.0, 100.0 - float(used)))
        return LimitWindow(
            label=label,
            used_percent=float(used) if isinstance(used, (int, float)) else None,
            remaining_percent=remaining,
            window_mins=raw.get("windowDurationMins") if isinstance(raw.get("windowDurationMins"), int) else None,
            resets_at=raw.get("resetsAt") if isinstance(raw.get("resetsAt"), int) else None,
        )


class ContextReader:
    KEY_PATTERN = re.compile(r'([A-Za-z0-9_.-]+)=(".*?"|\S+)')
    SOURCE_LABEL = "latest global"

    def __init__(self, codex_home: Path) -> None:
        self.codex_home = codex_home

    def read(self) -> ContextSnapshot:
        try:
            model_windows = self._load_model_windows()
            rows = self._recent_completed_rows()
            if not rows:
                return ContextSnapshot(
                    error="no response.completed usage row found",
                    source_label=self.SOURCE_LABEL,
                    updated_at=now_local(),
                )

            skipped_rows = 0
            for row in rows:
                snapshot = self._snapshot_from_row(row, model_windows, skipped_rows)
                if snapshot:
                    return snapshot
                skipped_rows += 1

            return ContextSnapshot(
                error=f"no valid response.completed usage row found in latest {len(rows)} rows",
                source_label=self.SOURCE_LABEL,
                skipped_rows=skipped_rows,
                updated_at=now_local(),
            )
        except Exception as exc:
            return ContextSnapshot(error=compact_error(exc), source_label=self.SOURCE_LABEL, updated_at=now_local())

    def _latest_completed_row(self) -> str | None:
        rows = self._recent_completed_rows(limit=1)
        return rows[0] if rows else None

    def _recent_completed_rows(self, limit: int = CONTEXT_ROW_LOOKBACK) -> list[str]:
        path = self.codex_home / "logs_2.sqlite"
        if not path.exists():
            raise FileNotFoundError(path)
        con = sqlite3.connect(f"file:{path}?mode=ro", uri=True, timeout=2)
        try:
            cur = con.cursor()
            cur.execute("PRAGMA busy_timeout=1500")
            columns = {row[1] for row in cur.execute("PRAGMA table_info(logs)")}
            if "feedback_log_body" not in columns:
                raise RuntimeError("logs.feedback_log_body column not found")
            rows = cur.execute(
                """
                SELECT feedback_log_body
                FROM logs
                WHERE feedback_log_body LIKE '%event.kind=response.completed%'
                  AND feedback_log_body LIKE '%input_token_count=%'
                ORDER BY ts DESC, id DESC
                LIMIT ?
                """,
                (max(1, int(limit)),),
            ).fetchall()
            return [str(row[0]) for row in rows if row and row[0]]
        finally:
            con.close()

    def _snapshot_from_row(
        self,
        row: str,
        model_windows: dict[str, dict[str, float | int]],
        skipped_rows: int,
    ) -> ContextSnapshot | None:
        metadata = self._parse_key_values(row)
        model = metadata.get("model") or metadata.get("slug")
        if not model:
            return None

        input_tokens = self._to_int(metadata.get("input_token_count"))
        if input_tokens is None or input_tokens < 0:
            return None

        model_window = model_windows.get(model, {})
        context_window = int(model_window.get("context_window") or 0)
        if context_window <= 0:
            context_window = 272000
        effective_percent = float(model_window.get("effective_context_window_percent") or 0)
        if effective_percent > 0:
            effective_window = int(context_window * effective_percent / 100)
        else:
            effective_window = context_window
        if effective_window <= 0:
            return None

        used_percent = min(100.0, max(0.0, input_tokens * 100.0 / effective_window))
        remaining_percent = max(0.0, 100.0 - used_percent)
        return ContextSnapshot(
            model=model,
            input_tokens=input_tokens,
            cached_tokens=self._to_int(metadata.get("cached_token_count")),
            output_tokens=self._to_int(metadata.get("output_token_count")),
            reasoning_tokens=self._to_int(metadata.get("reasoning_token_count")),
            context_window=context_window,
            effective_window=effective_window,
            used_percent=used_percent,
            remaining_percent=remaining_percent,
            event_time=metadata.get("event.timestamp"),
            conversation_id=metadata.get("conversation.id"),
            source_label=self.SOURCE_LABEL,
            skipped_rows=skipped_rows,
            updated_at=now_local(),
        )

    def _load_model_windows(self) -> dict[str, dict[str, float | int]]:
        path = self.codex_home / "models_cache.json"
        if not path.exists():
            return {}
        data = json.loads(path.read_text(encoding="utf-8"))
        result: dict[str, dict[str, float | int]] = {}
        for model in data.get("models", []):
            if not isinstance(model, dict):
                continue
            slug = model.get("slug")
            if not slug:
                continue
            result[str(slug)] = {
                "context_window": int(model.get("context_window") or 0),
                "effective_context_window_percent": float(model.get("effective_context_window_percent") or 0),
            }
        return result

    @classmethod
    def _parse_key_values(cls, text: str) -> dict[str, str]:
        values: dict[str, str] = {}
        for key, value in cls.KEY_PATTERN.findall(text):
            if len(value) >= 2 and value[0] == '"' and value[-1] == '"':
                value = value[1:-1]
            values[key] = value
        return values

    @staticmethod
    def _to_int(value: str | None) -> int | None:
        if value is None:
            return None
        try:
            return int(value)
        except ValueError:
            return None
