from __future__ import annotations

import os
import tkinter as tk
from pathlib import Path

from .constants import COLORS, GAUGE_SCALE, GAUGE_SIZE, GAUGE_TEXT_FONT_SIZE
from .utils import truncate_text

try:
    from PIL import Image, ImageDraw, ImageFont, ImageTk
    PIL_AVAILABLE = True
except Exception:
    Image = ImageDraw = ImageFont = ImageTk = None  # type: ignore[assignment]
    PIL_AVAILABLE = False

_COLOR_THRESHOLDS = {"red": 15.0, "amber": 30.0}


def set_color_thresholds(red_threshold: float, amber_threshold: float) -> None:
    red = max(0.0, min(100.0, float(red_threshold)))
    amber = max(red, min(100.0, float(amber_threshold)))
    _COLOR_THRESHOLDS["red"] = red
    _COLOR_THRESHOLDS["amber"] = amber


class MetricRow(tk.Frame):
    def __init__(self, master: tk.Widget, title: str, row_bg: str = COLORS["row"]) -> None:
        super().__init__(
            master,
            bg=row_bg,
            height=42,
            highlightthickness=1,
            highlightbackground=COLORS["border"],
            highlightcolor=COLORS["border"],
        )
        self.grid_propagate(False)
        self.columnconfigure(1, weight=1)
        self.title_label = tk.Label(
            self,
            text=title,
            width=5,
            anchor="w",
            bg=row_bg,
            fg=COLORS["soft"],
            font=("Segoe UI", 8, "bold"),
        )
        self.title_label.grid(row=0, column=0, rowspan=2, sticky="nsw", padx=(9, 8))
        self.detail_label = tk.Label(
            self,
            text="waiting",
            anchor="w",
            bg=row_bg,
            fg=COLORS["muted"],
            font=("Segoe UI", 8),
        )
        self.detail_label.grid(row=0, column=1, sticky="ew", pady=(5, 0))
        self.percent_label = tk.Label(
            self,
            text="--%",
            width=5,
            anchor="e",
            bg=row_bg,
            fg=COLORS["text"],
            font=("Segoe UI", 10, "bold"),
        )
        self.percent_label.grid(row=0, column=2, sticky="e", padx=(10, 10), pady=(4, 0))
        self.canvas = tk.Canvas(self, height=7, highlightthickness=0, bg=row_bg, bd=0)
        self.canvas.grid(row=1, column=1, columnspan=2, sticky="ew", padx=(0, 10), pady=(1, 7))
        self.remaining: float | None = None
        self.row_bg = row_bg
        self._corner_radius = 3
        self.bind("<Configure>", lambda _event: self._draw())

    def set_metric(self, remaining: float | None, detail: str) -> None:
        self.remaining = remaining
        self.percent_label.configure(text=f"{remaining:.0f}%" if remaining is not None else "--%")
        self.detail_label.configure(text=truncate_text(detail, 38))
        color = color_for_remaining(remaining)
        self.percent_label.configure(foreground=color)
        self._draw()

    def _draw(self) -> None:
        width = max(12, self.canvas.winfo_width())
        height = max(8, self.canvas.winfo_height())
        self.canvas.delete("all")
        self.canvas.create_rectangle(0, 0, width, height, fill=COLORS["track"], outline="")
        if self.remaining is None:
            return
        fill_width = int(width * max(0.0, min(100.0, self.remaining)) / 100.0)
        self.canvas.create_rectangle(0, 0, fill_width, height, fill=color_for_remaining(self.remaining), outline="")


class CompactMetricBlock(tk.Frame):
    def __init__(self, master: tk.Widget, title: str, bg: str) -> None:
        super().__init__(
            master,
            bg=bg,
            width=82,
            highlightthickness=1,
            highlightbackground=COLORS["border"],
            highlightcolor=COLORS["border"],
        )
        self.pack_propagate(False)
        self.title = title
        self.bg = bg
        self.canvas = tk.Canvas(
            self,
            highlightthickness=0,
            bg=bg,
            bd=0,
            relief="flat",
        )
        self.canvas.pack(fill="both", expand=True)
        self.remaining: float | None = None
        self.percent_text = "--"
        self.detail_text = "wait"
        self._gauge_photo: object | None = None
        self.bind("<Configure>", lambda _event: self._draw())
        self.canvas.bind("<Configure>", lambda _event: self._draw())

    def set_metric(self, remaining: float | None, detail: str) -> None:
        self.remaining = remaining
        self.percent_text = f"{remaining:.0f}" if remaining is not None else "--"
        self.detail_text = truncate_text(detail, 8)
        self._draw()

    def _draw(self) -> None:
        width = max(50, self.canvas.winfo_width())
        height = max(34, self.canvas.winfo_height())
        self.canvas.delete("all")
        gauge_size = min(GAUGE_SIZE, max(28, height - 5))
        gauge_x = width - gauge_size - 4
        gauge_y = max(0, int((height - gauge_size) / 2) - 1)
        accent = color_for_remaining(self.remaining)
        text_max = max(3, int((gauge_x - 8) / 5.2))
        detail = truncate_text(self.detail_text, text_max)

        self.canvas.create_text(
            6,
            4,
            text=self.title,
            fill=COLORS["soft"],
            font=("Segoe UI", 7, "bold"),
            anchor="nw",
        )
        self.canvas.create_text(
            6,
            height - 8,
            text=detail,
            fill=COLORS["muted"],
            font=("Segoe UI", 7),
            anchor="sw",
        )
        self._gauge_photo = render_gauge_image(
            self.remaining,
            gauge_size,
            accent,
            COLORS["track"],
            self.percent_text,
        )
        if self._gauge_photo is not None:
            self.canvas.create_image(gauge_x, gauge_y, image=self._gauge_photo, anchor="nw")
            return

        pad = 4
        left = gauge_x + pad
        top = gauge_y + pad
        right = gauge_x + gauge_size - pad
        bottom = gauge_y + gauge_size - pad
        self.canvas.create_oval(left, top, right, bottom, outline=COLORS["track"], width=3)
        if self.remaining is not None:
            extent = -max(0.0, min(100.0, self.remaining)) * 3.6
            self.canvas.create_arc(
                left,
                top,
                right,
                bottom,
                start=90,
                extent=extent,
                style=tk.ARC,
                outline=accent,
                width=3,
            )
        self.canvas.create_text(
            gauge_x + gauge_size / 2,
            gauge_y + gauge_size / 2 - 0.5,
            text=self.percent_text,
            fill=accent,
            font=("Segoe UI", GAUGE_TEXT_FONT_SIZE, "bold"),
        )


class CompactStatusBlock(tk.Frame):
    def __init__(self, master: tk.Widget, title: str, bg: str) -> None:
        super().__init__(
            master,
            bg=bg,
            width=82,
            highlightthickness=1,
            highlightbackground=COLORS["border"],
            highlightcolor=COLORS["border"],
        )
        self.pack_propagate(False)
        self.title = title
        self.bg = bg
        self.canvas = tk.Canvas(
            self,
            highlightthickness=0,
            bg=bg,
            bd=0,
            relief="flat",
        )
        self.canvas.pack(fill="both", expand=True)
        self.refresh_text = "--:--"
        self.current_text = "--:--"
        self.detail_text = "WAIT"
        self.accent = COLORS["muted"]
        self.bind("<Configure>", lambda _event: self._draw())
        self.canvas.bind("<Configure>", lambda _event: self._draw())

    def set_status(self, main: str, detail: str, accent: str | None = None) -> None:
        left, sep, right = main.partition("/")
        self.refresh_text = truncate_text(left or "--:--", 5)
        self.current_text = truncate_text(right if sep else "--:--", 5)
        self.detail_text = truncate_text(detail, 6)
        self.accent = accent or COLORS["text"]
        self._draw()

    def _draw(self) -> None:
        width = max(50, self.canvas.winfo_width())
        height = max(34, self.canvas.winfo_height())
        self.canvas.delete("all")
        self.canvas.create_text(
            6,
            3,
            text=self.title,
            fill=COLORS["soft"],
            font=("Segoe UI", 7, "bold"),
            anchor="nw",
        )

        center_x = width / 2
        time_y = min(max(17, height / 2 + 1), height - 15)
        self.canvas.create_text(
            center_x - 4,
            time_y,
            text=self.refresh_text,
            fill=self.accent,
            font=("Segoe UI", 8, "bold"),
            anchor="e",
        )
        self.canvas.create_text(
            center_x,
            time_y,
            text="/",
            fill=COLORS["muted"],
            font=("Segoe UI", 8, "bold"),
            anchor="center",
        )
        self.canvas.create_text(
            center_x + 4,
            time_y,
            text=self.current_text,
            fill=COLORS["soft"],
            font=("Segoe UI", 8, "bold"),
            anchor="w",
        )

        if self.detail_text:
            self.canvas.create_text(
                center_x,
                height - 3,
                text=self.detail_text,
                fill=self.accent,
                font=("Segoe UI", 6, "bold"),
                anchor="s",
            )


def color_for_remaining(remaining: float | None) -> str:
    if remaining is None:
        return COLORS["muted"]
    if remaining < _COLOR_THRESHOLDS["red"]:
        return COLORS["red"]
    if remaining < _COLOR_THRESHOLDS["amber"]:
        return COLORS["amber"]
    return COLORS["green"]


def hex_to_rgb(color: str) -> tuple[int, int, int]:
    value = color.lstrip("#")
    return int(value[0:2], 16), int(value[2:4], 16), int(value[4:6], 16)


def load_gauge_font(size: int) -> object:
    if not PIL_AVAILABLE or ImageFont is None:
        return object()
    candidates = [
        Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts" / "segoeuib.ttf",
        Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts" / "segoeui.ttf",
        Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts" / "arialbd.ttf",
    ]
    for candidate in candidates:
        if candidate.exists():
            try:
                return ImageFont.truetype(str(candidate), size)
            except Exception:
                continue
    return ImageFont.load_default()


def render_gauge_image(percent: float | None, size: int, accent: str, track: str, text: str) -> object | None:
    if not PIL_AVAILABLE or Image is None or ImageDraw is None or ImageTk is None:
        return None
    scale = GAUGE_SCALE
    canvas_size = size * scale
    line_width = max(3 * scale, int(size * 0.12 * scale))
    margin = line_width // 2 + scale
    image = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    box = (margin, margin, canvas_size - margin, canvas_size - margin)
    draw.ellipse(box, outline=hex_to_rgb(track) + (255,), width=line_width)

    if percent is not None:
        pct = max(0.0, min(100.0, percent))
        if pct > 0:
            import math

            radius = (box[2] - box[0]) / 2
            cx = cy = canvas_size / 2
            steps = max(8, int(96 * pct / 100))
            points = []
            for step in range(steps + 1):
                angle = -90 + (pct * 3.6 * step / steps)
                radians = math.radians(angle)
                points.append((cx + radius * math.cos(radians), cy + radius * math.sin(radians)))
            color = hex_to_rgb(accent) + (255,)
            draw.line(points, fill=color, width=line_width, joint="curve")
            cap_radius = line_width / 2
            for px, py in (points[0], points[-1]):
                draw.ellipse(
                    (px - cap_radius, py - cap_radius, px + cap_radius, py + cap_radius),
                    fill=color,
                )

    font = load_gauge_font(GAUGE_TEXT_FONT_SIZE * scale)
    color = hex_to_rgb(accent) + (255,)
    bbox = draw.textbbox((0, 0), text, font=font)
    text_w = bbox[2] - bbox[0]
    text_h = bbox[3] - bbox[1]
    draw.text(
        ((canvas_size - text_w) / 2 - bbox[0], (canvas_size - text_h) / 2 - bbox[1]),
        text,
        fill=color,
        font=font,
    )
    resampling = getattr(getattr(Image, "Resampling", Image), "LANCZOS")
    image = image.resize((size, size), resampling)
    return ImageTk.PhotoImage(image)
