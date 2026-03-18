"""Canvas-based chart widgets for the Stats page."""

import tkinter as tk

import customtkinter as ctk

from ..constants import COLORS


class SimpleLineChart(ctk.CTkFrame):
    """A line chart drawn on a tkinter Canvas with the app's dark theme."""

    def __init__(
        self,
        parent,
        data: list[tuple[str, float]],
        color: str = COLORS["accent_blue"],
        target_value: float | None = None,
        target_color: str = COLORS["accent_gold"],
        height: int = 200,
        title: str | None = None,
        **kwargs,
    ):
        super().__init__(parent, fg_color=COLORS["bg_input"], corner_radius=8, **kwargs)
        self._data = data
        self._color = color
        self._target_value = target_value
        self._target_color = target_color
        self._chart_height = height
        self._title = title

        # Margins for axis labels
        self._margin_left = 48
        self._margin_right = 16
        self._margin_top = 30 if title else 14
        self._margin_bottom = 32

        self._canvas = tk.Canvas(
            self,
            bg=COLORS["bg_input"],
            highlightthickness=0,
            height=self._chart_height,
        )
        self._canvas.pack(fill="both", expand=True, padx=2, pady=2)
        self._canvas.bind("<Configure>", self._on_resize)

    def _on_resize(self, event=None):
        self._draw()

    def _draw(self):
        c = self._canvas
        c.delete("all")

        if not self._data or len(self._data) < 2:
            c.create_text(
                c.winfo_width() // 2, c.winfo_height() // 2,
                text="Not enough data", fill=COLORS["text_dim"],
                font=("Segoe UI", 11),
            )
            return

        w = c.winfo_width()
        h = c.winfo_height()
        if w < 10 or h < 10:
            return

        ml, mr, mt, mb = self._margin_left, self._margin_right, self._margin_top, self._margin_bottom
        plot_w = w - ml - mr
        plot_h = h - mt - mb

        if plot_w <= 0 or plot_h <= 0:
            return

        values = [v for _, v in self._data]
        v_min = min(values)
        v_max = max(values)

        # Include target in range if present
        if self._target_value is not None:
            v_min = min(v_min, self._target_value)
            v_max = max(v_max, self._target_value)

        # Add 10% padding to value range
        v_range = v_max - v_min
        if v_range == 0:
            v_range = 1
        v_pad = v_range * 0.1
        v_min -= v_pad
        v_max += v_pad
        v_range = v_max - v_min

        # Title
        if self._title:
            c.create_text(
                ml, 10, text=self._title, fill=COLORS["text_dim"],
                font=("Segoe UI", 10, "bold"), anchor="w",
            )

        # Horizontal grid lines (5 lines)
        num_grid = 5
        for i in range(num_grid + 1):
            y = mt + plot_h - (i / num_grid) * plot_h
            val = v_min + (i / num_grid) * v_range
            c.create_line(ml, y, w - mr, y, fill=COLORS["border"], width=1)
            c.create_text(
                ml - 6, y, text=f"{val:.0f}", fill=COLORS["text_dim"],
                font=("Segoe UI", 9), anchor="e",
            )

        # Target / reference line
        if self._target_value is not None:
            ty = mt + plot_h - ((self._target_value - v_min) / v_range) * plot_h
            # Dashed line
            dash_len = 6
            x = ml
            while x < w - mr:
                x2 = min(x + dash_len, w - mr)
                c.create_line(x, ty, x2, ty, fill=self._target_color, width=1)
                x += dash_len * 2

        # Convert data to pixel coordinates
        n = len(self._data)
        points = []
        for i, (label, val) in enumerate(self._data):
            x = ml + (i / (n - 1)) * plot_w
            y = mt + plot_h - ((val - v_min) / v_range) * plot_h
            points.append((x, y))

        # Draw line segments
        for i in range(len(points) - 1):
            x1, y1 = points[i]
            x2, y2 = points[i + 1]
            c.create_line(x1, y1, x2, y2, fill=self._color, width=2, smooth=False)

        # Draw dots at each point
        dot_r = 3
        for x, y in points:
            c.create_oval(
                x - dot_r, y - dot_r, x + dot_r, y + dot_r,
                fill=self._color, outline=self._color,
            )

        # X-axis labels — show a subset to avoid overlap
        max_labels = max(1, plot_w // 50)
        step = max(1, n // max_labels)
        for i in range(0, n, step):
            label = self._data[i][0]
            x = ml + (i / (n - 1)) * plot_w
            c.create_text(
                x, h - mb + 12, text=label, fill=COLORS["text_dim"],
                font=("Segoe UI", 8), anchor="n",
            )
        # Always show last label if not already shown
        last_i = n - 1
        if last_i % step != 0:
            label = self._data[last_i][0]
            x = ml + (last_i / (n - 1)) * plot_w
            c.create_text(
                x, h - mb + 12, text=label, fill=COLORS["text_dim"],
                font=("Segoe UI", 8), anchor="n",
            )


class SimpleBarChart(ctk.CTkFrame):
    """A bar chart drawn on a tkinter Canvas with the app's dark theme."""

    def __init__(
        self,
        parent,
        data: list[tuple[str, float]],
        color: str = COLORS["accent_blue"],
        height: int = 200,
        title: str | None = None,
        **kwargs,
    ):
        super().__init__(parent, fg_color=COLORS["bg_input"], corner_radius=8, **kwargs)
        self._data = data
        self._color = color
        self._chart_height = height
        self._title = title

        self._margin_left = 48
        self._margin_right = 16
        self._margin_top = 30 if title else 14
        self._margin_bottom = 36

        self._canvas = tk.Canvas(
            self,
            bg=COLORS["bg_input"],
            highlightthickness=0,
            height=self._chart_height,
        )
        self._canvas.pack(fill="both", expand=True, padx=2, pady=2)
        self._canvas.bind("<Configure>", self._on_resize)

    def _on_resize(self, event=None):
        self._draw()

    def _draw(self):
        c = self._canvas
        c.delete("all")

        if not self._data:
            c.create_text(
                c.winfo_width() // 2, c.winfo_height() // 2,
                text="No data", fill=COLORS["text_dim"],
                font=("Segoe UI", 11),
            )
            return

        w = c.winfo_width()
        h = c.winfo_height()
        if w < 10 or h < 10:
            return

        ml, mr, mt, mb = self._margin_left, self._margin_right, self._margin_top, self._margin_bottom
        plot_w = w - ml - mr
        plot_h = h - mt - mb

        if plot_w <= 0 or plot_h <= 0:
            return

        values = [v for _, v in self._data]
        v_max = max(values) if values else 1
        if v_max == 0:
            v_max = 1

        # Add 10% headroom
        v_max_padded = v_max * 1.1

        # Title
        if self._title:
            c.create_text(
                ml, 10, text=self._title, fill=COLORS["text_dim"],
                font=("Segoe UI", 10, "bold"), anchor="w",
            )

        # Horizontal grid lines
        num_grid = 4
        for i in range(num_grid + 1):
            y = mt + plot_h - (i / num_grid) * plot_h
            val = (i / num_grid) * v_max_padded
            c.create_line(ml, y, w - mr, y, fill=COLORS["border"], width=1)
            c.create_text(
                ml - 6, y, text=f"{val:.0f}", fill=COLORS["text_dim"],
                font=("Segoe UI", 9), anchor="e",
            )

        # Bars
        n = len(self._data)
        bar_gap = max(2, plot_w // (n * 8))
        bar_w = max(4, (plot_w - (n + 1) * bar_gap) / n)

        for i, (label, val) in enumerate(self._data):
            x = ml + bar_gap + i * (bar_w + bar_gap)
            bar_h = (val / v_max_padded) * plot_h if v_max_padded > 0 else 0
            y_top = mt + plot_h - bar_h
            y_bot = mt + plot_h

            c.create_rectangle(
                x, y_top, x + bar_w, y_bot,
                fill=self._color, outline=self._color,
            )

            # Value label on top of bar
            c.create_text(
                x + bar_w / 2, y_top - 6,
                text=f"{val:.1f}" if isinstance(val, float) else str(val),
                fill=COLORS["text"], font=("Segoe UI", 8), anchor="s",
            )

            # Category label below
            c.create_text(
                x + bar_w / 2, y_bot + 6,
                text=label, fill=COLORS["text_dim"],
                font=("Segoe UI", 8), anchor="n",
            )
