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
