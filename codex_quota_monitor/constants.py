from __future__ import annotations

APP_NAME = "Codex Quota Float"
DEFAULT_QUOTA_INTERVAL = 180
DEFAULT_WIDTH = 260
DEFAULT_HEIGHT = 48
TOPMOST_REASSERT_MS = 500
PLACEMENT_REASSERT_MS = 1000
GAUGE_SIZE = 34
GAUGE_SCALE = 4
GAUGE_TEXT_FONT_SIZE = 11
SINGLE_INSTANCE_MUTEX_NAME = "Local\CodexQuotaFloatOverlay"
WM_APP = 0x8000
TRAY_CALLBACK_MESSAGE = WM_APP + 41
TRAY_ICON_ID = 1

COLORS = {
    "window": "#070b12",
    "panel": "#0d131d",
    "row": "#111a26",
    "row_alt": "#141f2d",
    "border": "#273449",
    "track": "#2a3548",
    "text": "#f4f7fb",
    "muted": "#91a0b5",
    "soft": "#d8e3f0",
    "green": "#28d989",
    "amber": "#f2bd4d",
    "red": "#ff6678",
}
