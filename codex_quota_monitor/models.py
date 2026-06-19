from __future__ import annotations

import datetime as dt
from dataclasses import dataclass

@dataclass
class LimitWindow:
    label: str
    used_percent: float | None = None
    remaining_percent: float | None = None
    window_mins: int | None = None
    resets_at: int | None = None


@dataclass
class QuotaSnapshot:
    limit_id: str | None = None
    limit_name: str | None = None
    plan_type: str | None = None
    primary: LimitWindow | None = None
    secondary: LimitWindow | None = None
    rate_limit_reached_type: str | None = None
    updated_at: dt.datetime | None = None
    error: str | None = None


@dataclass
class ContextSnapshot:
    model: str | None = None
    input_tokens: int | None = None
    cached_tokens: int | None = None
    output_tokens: int | None = None
    reasoning_tokens: int | None = None
    context_window: int | None = None
    effective_window: int | None = None
    used_percent: float | None = None
    remaining_percent: float | None = None
    event_time: str | None = None
    conversation_id: str | None = None
    source_label: str | None = None
    skipped_rows: int = 0
    updated_at: dt.datetime | None = None
    error: str | None = None
