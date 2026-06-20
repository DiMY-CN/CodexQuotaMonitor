from __future__ import annotations

import json
import logging
import os
import queue
import subprocess
import threading
import time
from pathlib import Path

from .models import LimitWindow, QuotaSnapshot
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
