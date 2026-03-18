"""VOD player window — watch Ascent recordings with timestamp bookmarks.

Uses mpv (libmpv) for embedded video playback when available.
Falls back to opening videos in the system default player.

Features a visual timeline with colored event markers for kills,
deaths, objectives, and user bookmarks.
"""

import json
import logging
import os
import subprocess
import sys
import tkinter as tk
from pathlib import Path
from typing import Callable, Optional

import customtkinter as ctk

from ..clips import extract_clip, is_ffmpeg_available
from ..config import get_keybinds, KEYBIND_LABELS
from ..constants import (
    CLIP_SAVE_FEEDBACK_MS,
    COLORS,
    VOD_ERROR_FLASH_MS,
    VOD_PLAYBACK_SPEEDS,
    VOD_TIME_UPDATE_INTERVAL_MS,
)
from ..database.game_events import EVENT_STYLES
from ..vod import format_game_time, parse_game_time

logger = logging.getLogger(__name__)

# Try to import mpv — it's optional
_mpv = None
try:
    import mpv as _mpv
except (ImportError, OSError):
    logger.info("python-mpv not available — VOD player will use external player")


# ── Timeline Canvas Widget ─────────────────────────────────────


class TimelineCanvas(tk.Canvas):
    """Custom seek bar with visual event markers.

    Draws a horizontal track with:
    - Dark background track
    - Blue progress bar showing current position
    - Colored markers at event timestamps
    - Purple diamonds for user bookmarks
    - Hover tooltips showing event details
    """

    TRACK_HEIGHT = 8
    MARKER_SIZE = 7
    CANVAS_HEIGHT = 40
    BOOKMARK_COLOR = "#8b5cf6"

    CLIP_COLOR = "#22c55e"  # Green for clip range
    CLIP_ALPHA_COLOR = "#1a4d2e"  # Dark green fill

    def __init__(self, master, duration: int, on_seek: Callable,
                 events: list = None, bookmarks: list = None,
                 derived_events: list = None, **kwargs):
        super().__init__(
            master, height=self.CANVAS_HEIGHT,
            bg=COLORS["bg_dark"], highlightthickness=0,
            cursor="hand2", **kwargs,
        )
        self._duration = max(duration, 1)
        self._on_seek = on_seek
        self._events = events or []
        self._bookmarks = bookmarks or []
        self._derived_events = derived_events or []
        self._position = 0.0  # Current position in seconds
        self._dragging = False
        self._hover_item = None
        self._tooltip_window = None
        self._clip_start = None  # Clip start time in seconds
        self._clip_end = None    # Clip end time in seconds

        self.bind("<Configure>", self._on_resize)
        self.bind("<Button-1>", self._on_click)
        self.bind("<B1-Motion>", self._on_drag)
        self.bind("<ButtonRelease-1>", self._on_release)
        self.bind("<Motion>", self._on_hover)
        self.bind("<Leave>", self._on_leave)

    def set_position(self, seconds: float):
        """Update the playback position (0 to duration)."""
        self._position = max(0, min(seconds, self._duration))
        self._redraw()

    def set_events(self, events: list):
        """Update the event markers."""
        self._events = events or []
        self._redraw()

    def set_bookmarks(self, bookmarks: list):
        """Update the bookmark markers."""
        self._bookmarks = bookmarks or []
        self._redraw()

    def set_derived_events(self, derived_events: list):
        """Update the derived event regions."""
        self._derived_events = derived_events or []
        self._redraw()

    def set_duration(self, duration: int):
        """Update the total duration."""
        self._duration = max(duration, 1)
        self._redraw()

    def set_clip_range(self, start: float = None, end: float = None):
        """Set or clear the clip selection range (green overlay)."""
        self._clip_start = start
        self._clip_end = end
        self._redraw()

    def _time_to_x(self, seconds: float) -> float:
        """Convert a time in seconds to an x coordinate."""
        w = self.winfo_width()
        pad = 10  # Left/right padding
        usable = w - 2 * pad
        frac = seconds / self._duration
        return pad + frac * usable

    def _x_to_time(self, x: float) -> float:
        """Convert an x coordinate to time in seconds."""
        w = self.winfo_width()
        pad = 10
        usable = w - 2 * pad
        frac = (x - pad) / max(usable, 1)
        return max(0, min(frac * self._duration, self._duration))

    def _redraw(self):
        """Redraw the entire timeline."""
        self.delete("all")
        w = self.winfo_width()
        h = self.winfo_height()

        if w < 20:
            return

        pad = 10
        track_y = h // 2
        track_top = track_y - self.TRACK_HEIGHT // 2
        track_bot = track_y + self.TRACK_HEIGHT // 2

        # Background track
        self.create_rectangle(
            pad, track_top, w - pad, track_bot,
            fill="#1a1a24", outline="#2a2a3a", width=1,
            tags="track",
        )

        # Progress bar
        progress_x = self._time_to_x(self._position)
        if progress_x > pad + 1:
            self.create_rectangle(
                pad, track_top, progress_x, track_bot,
                fill=COLORS["accent_blue"], outline="",
                tags="progress",
            )

        # Clip range highlight (green overlay on the track)
        if self._clip_start is not None:
            cs_x = self._time_to_x(self._clip_start)
            ce_x = self._time_to_x(self._clip_end) if self._clip_end is not None else progress_x
            if ce_x > cs_x:
                self.create_rectangle(
                    cs_x, track_top - 2, ce_x, track_bot + 2,
                    fill=self.CLIP_ALPHA_COLOR, outline=self.CLIP_COLOR,
                    width=1, tags="clip_range",
                )
            # Clip start marker (green line)
            self.create_line(
                cs_x, track_top - 6, cs_x, track_bot + 6,
                fill=self.CLIP_COLOR, width=2, tags="clip_marker",
            )
            # Clip end marker if set
            if self._clip_end is not None:
                ce_x2 = self._time_to_x(self._clip_end)
                self.create_line(
                    ce_x2, track_top - 6, ce_x2, track_bot + 6,
                    fill=self.CLIP_COLOR, width=2, tags="clip_marker",
                )

        # Derived event regions (translucent colored rectangles spanning time ranges)
        for de in self._derived_events:
            x_start = self._time_to_x(de["start_time_s"])
            x_end = self._time_to_x(de["end_time_s"])
            # Ensure minimum visible width
            if x_end - x_start < 4:
                x_end = x_start + 4
            color = de.get("color", "#ff6b6b")
            # Draw a translucent rectangle spanning the track area
            self.create_rectangle(
                x_start, track_top - 6, x_end, track_bot + 6,
                fill=color, outline=color, width=1,
                stipple="gray25",
                tags=("derived_event", f"de_{id(de)}"),
            )
            # Small label above the region
            mid_x = (x_start + x_end) / 2
            name = de.get("definition_name", "")
            if name:
                self.create_text(
                    mid_x, track_top - 12,
                    text=name, fill=color,
                    font=("Segoe UI", 7),
                    anchor="s",
                    tags=("derived_label", f"de_{id(de)}"),
                )

        # Event markers (draw below the track line, above center)
        marker_y_top = track_top - 3  # Above the track
        marker_y_bot = track_bot + 3  # Below the track

        # Group overlapping events to avoid clutter
        for event in self._events:
            x = self._time_to_x(event["game_time_s"])
            etype = event["event_type"]
            style = EVENT_STYLES.get(etype, {"color": "#888", "symbol": "●"})
            color = style["color"]

            # Draw marker above the track
            s = self.MARKER_SIZE
            # Different shapes per event type
            if etype in ("KILL", "DEATH", "ASSIST"):
                # Triangle (up for kill, down for death, circle for assist)
                if etype == "KILL":
                    self.create_polygon(
                        x, marker_y_top - s,
                        x - s * 0.7, marker_y_top + 2,
                        x + s * 0.7, marker_y_top + 2,
                        fill=color, outline="", tags=("marker", f"evt_{id(event)}"),
                    )
                elif etype == "DEATH":
                    self.create_polygon(
                        x, marker_y_bot + s,
                        x - s * 0.7, marker_y_bot - 2,
                        x + s * 0.7, marker_y_bot - 2,
                        fill=color, outline="", tags=("marker", f"evt_{id(event)}"),
                    )
                else:  # ASSIST
                    self.create_oval(
                        x - 3, marker_y_top - 3, x + 3, marker_y_top + 3,
                        fill=color, outline="", tags=("marker", f"evt_{id(event)}"),
                    )
            elif etype in ("DRAGON", "BARON", "HERALD"):
                # Diamond shape
                self.create_polygon(
                    x, marker_y_top - s,
                    x + s * 0.6, marker_y_top,
                    x, marker_y_top + s,
                    x - s * 0.6, marker_y_top,
                    fill=color, outline="", tags=("marker", f"evt_{id(event)}"),
                )
            elif etype in ("TURRET", "INHIBITOR"):
                # Small square
                self.create_rectangle(
                    x - 3, marker_y_top - 3, x + 3, marker_y_top + 3,
                    fill=color, outline="", tags=("marker", f"evt_{id(event)}"),
                )
            elif etype == "MULTI_KILL":
                # Star — drawn as larger circle with glow
                self.create_oval(
                    x - 5, marker_y_top - 8, x + 5, marker_y_top - 2,
                    fill=color, outline="#fff", width=1,
                    tags=("marker", f"evt_{id(event)}"),
                )
            else:
                # Default: small circle
                self.create_oval(
                    x - 3, marker_y_top - 3, x + 3, marker_y_top + 3,
                    fill=color, outline="", tags=("marker", f"evt_{id(event)}"),
                )

        # Bookmark markers (purple diamonds below the track)
        for bm in self._bookmarks:
            x = self._time_to_x(bm["game_time_s"])
            s = 5
            self.create_polygon(
                x, marker_y_bot + 2,
                x + s, marker_y_bot + 2 + s,
                x, marker_y_bot + 2 + s * 2,
                x - s, marker_y_bot + 2 + s,
                fill=self.BOOKMARK_COLOR, outline="",
                tags=("bookmark", f"bm_{bm.get('id', 0)}"),
            )

        # Playhead indicator (white line)
        self.create_line(
            progress_x, track_top - 4, progress_x, track_bot + 4,
            fill="#ffffff", width=2, tags="playhead",
        )

    def _on_resize(self, event=None):
        self._redraw()

    def _on_click(self, event):
        self._dragging = True
        t = self._x_to_time(event.x)
        self._position = t
        self._redraw()
        self._on_seek(t)

    def _on_drag(self, event):
        if self._dragging:
            t = self._x_to_time(event.x)
            self._position = t
            self._redraw()

    def _on_release(self, event):
        if self._dragging:
            t = self._x_to_time(event.x)
            self._on_seek(t)
            self._dragging = False

    def _on_hover(self, event):
        """Show tooltip for nearby event markers."""
        # Find the closest event or bookmark to the cursor
        closest = None
        closest_dist = 12  # Max pixel distance for tooltip

        hover_time = self._x_to_time(event.x)

        for evt in self._events:
            x = self._time_to_x(evt["game_time_s"])
            dist = abs(event.x - x)
            if dist < closest_dist:
                closest_dist = dist
                style = EVENT_STYLES.get(evt["event_type"], {"label": evt["event_type"]})
                details = evt.get("details", {})
                label = style.get("label", evt["event_type"])

                # Add detail info
                if evt["event_type"] == "DRAGON":
                    dragon = details.get("dragon_type", "").replace("_DRAGON", "").title()
                    if dragon:
                        label = f"{dragon} Dragon"
                elif evt["event_type"] == "MULTI_KILL":
                    label = details.get("label", "Multi Kill")

                closest = f"{format_game_time(evt['game_time_s'])} — {label}"

        for bm in self._bookmarks:
            x = self._time_to_x(bm["game_time_s"])
            dist = abs(event.x - x)
            if dist < closest_dist:
                closest_dist = dist
                note = bm.get("note", "") or "(bookmark)"
                closest = f"{format_game_time(bm['game_time_s'])} — {note}"

        # Check derived event regions (hover within the time range)
        for de in self._derived_events:
            x_start = self._time_to_x(de["start_time_s"])
            x_end = self._time_to_x(de["end_time_s"])
            if x_start <= event.x <= x_end:
                name = de.get("definition_name", "Event")
                count = de.get("event_count", 0)
                time_range = (
                    f"{format_game_time(de['start_time_s'])} - "
                    f"{format_game_time(de['end_time_s'])}"
                )
                closest = f"{name} ({count} events) — {time_range}"
                break  # Derived event regions take priority when hovering inside

        if closest:
            self._show_tooltip(event.x_root, event.y_root, closest)
        else:
            self._hide_tooltip()

    def _on_leave(self, event):
        self._hide_tooltip()

    def _show_tooltip(self, x: int, y: int, text: str):
        """Show a floating tooltip near the cursor."""
        self._hide_tooltip()
        tw = tk.Toplevel(self)
        tw.wm_overrideredirect(True)
        tw.wm_geometry(f"+{x + 12}+{y - 30}")
        tw.attributes("-topmost", True)

        label = tk.Label(
            tw, text=text, justify="left",
            background="#1e1e2e", foreground="#e4e4e8",
            relief="solid", borderwidth=1,
            font=("Segoe UI", 10),
            padx=6, pady=3,
        )
        label.pack()
        self._tooltip_window = tw

    def _hide_tooltip(self):
        if self._tooltip_window:
            self._tooltip_window.destroy()
            self._tooltip_window = None


# ── Timeline Legend ─────────────────────────────────────────────


def build_timeline_legend(parent, has_events: bool) -> ctk.CTkFrame:
    """Build a compact legend showing what each marker color means."""
    legend = ctk.CTkFrame(parent, fg_color="transparent")

    items = [
        ("▲ Kill", EVENT_STYLES["KILL"]["color"]),
        ("▼ Death", EVENT_STYLES["DEATH"]["color"]),
        ("● Assist", EVENT_STYLES["ASSIST"]["color"]),
        ("◆ Dragon", EVENT_STYLES["DRAGON"]["color"]),
        ("◆ Baron", EVENT_STYLES["BARON"]["color"]),
        ("◆ Herald", EVENT_STYLES["HERALD"]["color"]),
        ("■ Turret", EVENT_STYLES["TURRET"]["color"]),
        ("◆ Bookmark", TimelineCanvas.BOOKMARK_COLOR),
    ]

    if not has_events:
        # Only show bookmark legend if no Riot API events
        items = [("◆ Bookmark", TimelineCanvas.BOOKMARK_COLOR)]

    for text, color in items:
        ctk.CTkLabel(
            legend, text=text,
            font=ctk.CTkFont(size=10),
            text_color=color,
        ).pack(side="left", padx=(0, 8))

    return legend


# ── VOD Player Panel (inline frame) ───────────────────────────


class VodPlayerPanel(ctk.CTkFrame):
    """Inline VOD player panel — same UI as VodPlayerWindow but inside a CTkFrame.

    Use activate() after packing to start the player, and deactivate()
    before destroying to cleanly stop mpv and unbind keys.
    """

    def __init__(
        self,
        parent,
        game_id: int,
        vod_path: str,
        game_duration: int,
        champion_name: str,
        bookmarks: list[dict],
        tags: list[dict],
        game_events: list[dict] = None,
        derived_events: list[dict] = None,
        on_add_bookmark: Optional[Callable] = None,
        on_update_bookmark: Optional[Callable] = None,
        on_delete_bookmark: Optional[Callable] = None,
        on_back: Optional[Callable] = None,
        **kwargs,
    ):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kwargs)

        self.game_id = game_id
        self.vod_path = vod_path
        self.game_duration = game_duration
        self.champion_name = champion_name
        self._bookmarks = list(bookmarks)
        self._tags = tags
        self._game_events = game_events or []
        self._derived_events = derived_events or []
        self._on_add_bookmark = on_add_bookmark
        self._on_update_bookmark = on_update_bookmark
        self._on_delete_bookmark = on_delete_bookmark
        self._on_back = on_back

        self._player = None
        self._playing = False
        self._update_job = None
        self._speed = 1.0
        self._clip_start_s = None
        self._clip_end_s = None
        self._clip_saving = False
        self._activated = False

        self._build_ui()

    def activate(self):
        """Initialize the mpv player and bind keyboard shortcuts.

        Call this after the panel is packed and visible.
        """
        if self._activated:
            return
        self._activated = True
        self._init_player()
        self._bind_keys()

    def deactivate(self):
        """Stop the player and unbind keys. Call before destroying."""
        if not self._activated:
            return
        self._activated = False
        self._unbind_keys()
        self._playing = False
        if self._update_job:
            self.after_cancel(self._update_job)
            self._update_job = None
        if self._player:
            try:
                self._player.pause = True
                self._player.command("stop")
            except Exception:
                pass
            try:
                self._player.terminate()
            except Exception:
                pass
            self._player = None

    def _build_ui(self):
        """Build the player + timeline + bookmarks layout."""
        # Back button header
        back_row = ctk.CTkFrame(self, fg_color="transparent")
        back_row.pack(fill="x", padx=12, pady=(8, 0))
        ctk.CTkButton(
            back_row, text="← Back", width=80, height=30,
            font=ctk.CTkFont(size=12), corner_radius=6,
            fg_color=COLORS["tag_bg"], hover_color="#333344",
            text_color=COLORS["text"],
            command=self._go_back,
        ).pack(side="left")
        ctk.CTkLabel(
            back_row, text=f"VOD Review — {self.champion_name}",
            font=ctk.CTkFont(size=16, weight="bold"),
            text_color=COLORS["text"],
        ).pack(side="left", padx=(12, 0))

        outer = ctk.CTkFrame(self, fg_color=COLORS["bg_dark"])
        outer.pack(fill="both", expand=True, padx=12, pady=(4, 12))

        # ── Top: Video area ──────────────────────────────────────
        self._video_frame = ctk.CTkFrame(
            outer, fg_color="#000000", corner_radius=8,
            border_width=1, border_color=COLORS["border"],
        )
        self._video_frame.pack(fill="both", expand=True, pady=(0, 8))

        self._video_canvas = tk.Frame(self._video_frame, bg="#000000")
        self._video_canvas.pack(fill="both", expand=True)
        self._video_canvas.bind("<Button-1>", lambda e: (self.focus_set(), self._toggle_play()))

        # ── Transport controls ───────────────────────────────────
        transport = ctk.CTkFrame(outer, fg_color=COLORS["bg_card"], corner_radius=8,
                                  border_width=1, border_color=COLORS["border"])
        transport.pack(fill="x", pady=(0, 4))

        transport_inner = ctk.CTkFrame(transport, fg_color="transparent")
        transport_inner.pack(fill="x", padx=12, pady=8)

        self._play_btn = ctk.CTkButton(
            transport_inner, text="▶  Play", width=90, height=32,
            font=ctk.CTkFont(size=13, weight="bold"),
            fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._toggle_play,
        )
        self._play_btn.pack(side="left", padx=(0, 8))

        ctk.CTkButton(
            transport_inner, text="⏪ 10s", width=70, height=32,
            font=ctk.CTkFont(size=12),
            fg_color=COLORS["tag_bg"], hover_color="#333344",
            command=lambda: self._seek_relative(-10),
        ).pack(side="left", padx=(0, 4))

        ctk.CTkButton(
            transport_inner, text="10s ⏩", width=70, height=32,
            font=ctk.CTkFont(size=12),
            fg_color=COLORS["tag_bg"], hover_color="#333344",
            command=lambda: self._seek_relative(10),
        ).pack(side="left", padx=(0, 8))

        self._speed_label = ctk.CTkLabel(
            transport_inner, text="1x",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text"],
            width=30,
        )
        self._speed_label.pack(side="left", padx=(0, 2))

        for spd in VOD_PLAYBACK_SPEEDS:
            label = str(spd).rstrip('0').rstrip('.')
            btn = ctk.CTkButton(
                transport_inner, text=label, width=36, height=28,
                font=ctk.CTkFont(size=11),
                fg_color=COLORS["accent_blue"] if spd == 1.0 else COLORS["tag_bg"],
                hover_color="#0077cc" if spd == 1.0 else "#333344",
                corner_radius=6,
                command=lambda s=spd: self._set_speed(s),
            )
            btn.pack(side="left", padx=1)
            btn._speed_value = spd

        self._time_label = ctk.CTkLabel(
            transport_inner, text="0:00 / 0:00",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        )
        self._time_label.pack(side="left", padx=(8, 12))

        self._bookmark_btn = ctk.CTkButton(
            transport_inner, text="🔖 Bookmark", width=110, height=32,
            font=ctk.CTkFont(size=12, weight="bold"),
            fg_color=COLORS["accent_gold"], hover_color="#a88432",
            text_color="#0a0a0f",
            command=self._add_bookmark_at_current,
        )
        self._bookmark_btn.pack(side="right")

        # ── Clip controls ────────────────────────────────────────
        clip_frame = ctk.CTkFrame(transport_inner, fg_color="transparent")
        clip_frame.pack(side="right", padx=(0, 8))

        self._clip_in_btn = ctk.CTkButton(
            clip_frame, text="[ In", width=50, height=32,
            font=ctk.CTkFont(size=12, weight="bold"),
            fg_color="#1a4d2e", hover_color="#22c55e",
            text_color="#22c55e",
            command=self._set_clip_in,
        )
        self._clip_in_btn.pack(side="left", padx=(0, 2))

        self._clip_out_btn = ctk.CTkButton(
            clip_frame, text="Out ]", width=50, height=32,
            font=ctk.CTkFont(size=12, weight="bold"),
            fg_color="#1a4d2e", hover_color="#22c55e",
            text_color="#22c55e",
            command=self._set_clip_out,
        )
        self._clip_out_btn.pack(side="left", padx=(0, 2))

        self._clip_save_btn = ctk.CTkButton(
            clip_frame, text="Save Clip", width=80, height=32,
            font=ctk.CTkFont(size=12, weight="bold"),
            fg_color="#22c55e", hover_color="#1ea05a",
            text_color="#0a0a0f",
            command=self._save_clip,
            state="disabled",
        )
        self._clip_save_btn.pack(side="left", padx=(0, 2))

        self._clip_clear_btn = ctk.CTkButton(
            clip_frame, text="✕", width=28, height=32,
            font=ctk.CTkFont(size=12),
            fg_color="transparent", hover_color=COLORS["loss_red"],
            text_color=COLORS["text_dim"],
            command=self._clear_clip_range,
        )
        self._clip_clear_btn.pack(side="left")

        self._clip_range_label = ctk.CTkLabel(
            clip_frame, text="",
            font=ctk.CTkFont(size=10),
            text_color="#22c55e",
        )
        self._clip_range_label.pack(side="left", padx=(4, 0))

        # ── Key bindings (loaded but not bound until activate()) ─
        self._keybinds = get_keybinds()

        # ── Keyboard shortcut hints ──────────────────────────────
        self._hint_label = ctk.CTkLabel(
            transport, text=self._build_hint_text(),
            font=ctk.CTkFont(size=10),
            text_color=COLORS["text_dim"],
        )
        self._hint_label.pack(pady=(0, 6))

        # ── Visual Timeline ──────────────────────────────────────
        self._timeline = TimelineCanvas(
            outer,
            duration=self.game_duration,
            on_seek=self._on_seek,
            events=self._game_events,
            bookmarks=self._bookmarks,
            derived_events=self._derived_events,
        )
        self._timeline.pack(fill="x", pady=(0, 2))
        self._timeline.bind("<Button-1>", lambda e: self.focus_set(), add="+")

        has_events = len(self._game_events) > 0
        legend = build_timeline_legend(outer, has_events)
        legend.pack(anchor="w", pady=(0, 6))

        # ── Bottom: Bookmarks panel ──────────────────────────────
        bookmarks_section = ctk.CTkFrame(
            outer, fg_color=COLORS["bg_card"], corner_radius=8,
            border_width=1, border_color=COLORS["border"],
        )
        bookmarks_section.pack(fill="x")

        bm_header = ctk.CTkFrame(bookmarks_section, fg_color="transparent")
        bm_header.pack(fill="x", padx=12, pady=(10, 6))

        ctk.CTkLabel(
            bm_header, text="BOOKMARKS",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(side="left")

        self._bm_count_label = ctk.CTkLabel(
            bm_header, text=f"{len(self._bookmarks)} bookmarks",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        )
        self._bm_count_label.pack(side="right")

        add_row = ctk.CTkFrame(bookmarks_section, fg_color="transparent")
        add_row.pack(fill="x", padx=12, pady=(0, 6))

        ctk.CTkLabel(
            add_row, text="Time:",
            font=ctk.CTkFont(size=12), text_color=COLORS["text_dim"],
        ).pack(side="left", padx=(0, 4))

        self._time_entry = ctk.CTkEntry(
            add_row, width=70, height=30,
            font=ctk.CTkFont(size=12),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=6,
            placeholder_text="MM:SS",
        )
        self._time_entry.pack(side="left", padx=(0, 6))

        self._note_entry = ctk.CTkEntry(
            add_row, height=30,
            font=ctk.CTkFont(size=12),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=6,
            placeholder_text="What happened here?",
        )
        self._note_entry.pack(side="left", fill="x", expand=True, padx=(0, 6))

        ctk.CTkButton(
            add_row, text="+ Add", width=60, height=30,
            font=ctk.CTkFont(size=12),
            fg_color=COLORS["win_green"], hover_color="#1ea05a",
            text_color="#0a0a0f",
            command=self._add_bookmark_manual,
        ).pack(side="right")

        self._note_entry.bind("<Return>", lambda e: (self._add_bookmark_manual(), self.focus_set()))
        self._note_entry.bind("<Escape>", lambda e: self.focus_set())
        self._time_entry.bind("<Escape>", lambda e: self.focus_set())

        self._bm_scroll = ctk.CTkScrollableFrame(
            bookmarks_section, fg_color="transparent",
            height=150,
            scrollbar_button_color=COLORS["border"],
        )
        self._bm_scroll.pack(fill="x", padx=8, pady=(0, 8))

        self._refresh_bookmark_list()

    def _go_back(self):
        """Navigate back to the previous page."""
        if self._on_back:
            self._on_back()

    # ── Keybind wiring ──────────────────────────────────────────

    _SPEEDS = VOD_PLAYBACK_SPEEDS

    _ACTION_MAP = {
        "play_pause":   lambda s: s._toggle_play(),
        "seek_fwd_5":   lambda s: s._seek_relative(5),
        "seek_back_5":  lambda s: s._seek_relative(-5),
        "seek_fwd_2":   lambda s: s._seek_relative(2),
        "seek_back_2":  lambda s: s._seek_relative(-2),
        "seek_fwd_10":  lambda s: s._seek_relative(10),
        "seek_back_10": lambda s: s._seek_relative(-10),
        "seek_fwd_1":   lambda s: s._seek_relative(1),
        "seek_back_1":  lambda s: s._seek_relative(-1),
        "bookmark":     lambda s: s._add_bookmark_at_current(),
        "speed_up":     lambda s: s._cycle_speed(1),
        "speed_down":   lambda s: s._cycle_speed(-1),
        "clip_in":      lambda s: s._set_clip_in(),
        "clip_out":     lambda s: s._set_clip_out(),
    }

    def _is_typing(self) -> bool:
        """Check if the user is currently typing in a text input field."""
        focused = self.focus_get()
        if focused is None:
            return False
        widget_class = focused.winfo_class()
        return widget_class in ("Entry", "Text", "TEntry", "TText", "CTkEntry")

    def _bind_keys(self):
        """Bind all configured keybinds to their actions."""
        for action, tk_key in self._keybinds.items():
            if action in self._ACTION_MAP:
                handler = self._ACTION_MAP[action]
                self.winfo_toplevel().bind_all(
                    f"<{tk_key}>",
                    lambda e, h=handler: h(self) if not self._is_typing() else None,
                )

    def _unbind_keys(self):
        """Remove all keybinds."""
        for action, tk_key in self._keybinds.items():
            try:
                self.winfo_toplevel().unbind_all(f"<{tk_key}>")
            except Exception:
                pass

    def _build_hint_text(self) -> str:
        """Build a compact hint string from the current keybinds."""
        kb = self._keybinds
        parts = []

        def _short(tk_key: str) -> str:
            return tk_key.replace("Control-", "Ctrl+").replace("Shift-", "Shift+").replace("Alt-", "Alt+")

        parts.append(f"{_short(kb.get('seek_back_5', '←'))} / {_short(kb.get('seek_fwd_5', '→'))} 5s")
        parts.append(f"{_short(kb.get('seek_back_2', ''))} 2s")
        parts.append(f"{_short(kb.get('seek_back_10', ''))} 10s")
        parts.append(f"{_short(kb.get('seek_back_1', ''))} 1s")
        parts.append(f"{_short(kb.get('play_pause', 'space'))} play/pause")
        parts.append(f"{_short(kb.get('bookmark', 'b'))} bookmark")
        parts.append(f"{_short(kb.get('speed_down', '['))} / {_short(kb.get('speed_up', ']'))} speed")
        if kb.get("clip_in") and kb.get("clip_out"):
            parts.append(f"{_short(kb['clip_in'])} / {_short(kb['clip_out'])} clip in/out")
        return "  |  ".join(parts)

    def _cycle_speed(self, direction: int):
        """Cycle playback speed up (+1) or down (-1)."""
        try:
            idx = self._SPEEDS.index(self._speed)
        except ValueError:
            idx = 2
        idx = max(0, min(idx + direction, len(self._SPEEDS) - 1))
        self._set_speed(self._SPEEDS[idx])

    def _init_player(self):
        """Set up mpv player if available, otherwise show fallback message."""
        if _mpv is None:
            self._show_external_mode()
            return

        vod_exists = Path(self.vod_path).exists()
        if not vod_exists:
            import time
            time.sleep(0.3)
            vod_exists = Path(self.vod_path).exists()
        if not vod_exists:
            logger.warning(f"VOD file not found: {self.vod_path}")
            self._show_external_mode()
            return

        try:
            self._video_canvas.update_idletasks()
            wid = self._video_canvas.winfo_id()

            self._player = _mpv.MPV(
                wid=str(wid),
                vo="gpu",
                hwdec="auto",
                keep_open="yes",
                osd_level=0,
                input_default_bindings=False,
                input_vo_keyboard=False,
                log_handler=lambda lvl, comp, msg: None,
            )

            self._player.play(self.vod_path)
            self._player.pause = True
            self._playing = False

            logger.info(f"mpv player initialized for {self.vod_path}")
        except Exception as e:
            logger.warning(f"Failed to init mpv player: {e}")
            self._player = None
            self._show_external_mode()

    def _show_external_mode(self):
        """Show a message + button for opening in external player."""
        self._video_canvas.destroy()

        placeholder = ctk.CTkFrame(self._video_frame, fg_color="#0a0a12")
        placeholder.pack(fill="both", expand=True, padx=2, pady=2)

        msg = "Embedded playback requires libmpv\n" if _mpv is None else ""
        msg += "Click below to open in your default player"

        ctk.CTkLabel(
            placeholder, text=msg,
            font=ctk.CTkFont(size=14),
            text_color=COLORS["text_dim"],
            justify="center",
        ).pack(expand=True)

        ctk.CTkButton(
            placeholder,
            text="Open in Default Player",
            font=ctk.CTkFont(size=14, weight="bold"),
            height=40, width=220,
            fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._open_external,
        ).pack(pady=(0, 30))

    def _open_external(self):
        """Open the video file in the OS default player."""
        try:
            if sys.platform == "win32":
                os.startfile(self.vod_path)
            elif sys.platform == "darwin":
                subprocess.Popen(["open", self.vod_path])
            else:
                subprocess.Popen(["xdg-open", self.vod_path])
        except Exception as e:
            logger.error(f"Failed to open VOD externally: {e}")

    # ── Playback controls ────────────────────────────────────────

    def _toggle_play(self):
        if not self._player:
            self._open_external()
            return
        if self._playing:
            self._player.pause = True
            self._playing = False
            self._play_btn.configure(text="▶  Play")
            if self._update_job:
                self.after_cancel(self._update_job)
                self._update_job = None
        else:
            self._player.pause = False
            self._playing = True
            self._play_btn.configure(text="⏸  Pause")
            self._start_time_update()

    def _set_speed(self, speed: float):
        self._speed = speed
        if self._player:
            try:
                self._player.speed = speed
            except Exception:
                pass
        self._speed_label.configure(text=f"{speed}x")
        for widget in self._speed_label.master.winfo_children():
            if hasattr(widget, "_speed_value"):
                if widget._speed_value == speed:
                    widget.configure(fg_color=COLORS["accent_blue"])
                else:
                    widget.configure(fg_color=COLORS["tag_bg"])

    def _seek_relative(self, seconds: int):
        if not self._player:
            return
        try:
            self._player.seek(seconds, reference="relative")
        except Exception:
            pass
        self._update_time_display()

    def _on_seek(self, value):
        if not self._player:
            return
        try:
            self._player.seek(float(value), reference="absolute")
        except Exception:
            pass
        self._update_time_display()

    def _start_time_update(self):
        if not self._playing or not self._player:
            return
        self._update_time_display()
        self._update_job = self.after(VOD_TIME_UPDATE_INTERVAL_MS, self._start_time_update)

    def _update_time_display(self):
        if not self._player:
            return
        try:
            current_s = self._player.time_pos or 0
            total_s = self._player.duration or self.game_duration
        except Exception:
            current_s = 0
            total_s = self.game_duration
        if current_s < 0:
            current_s = 0
        self._time_label.configure(
            text=f"{format_game_time(int(current_s))} / {format_game_time(int(total_s))}"
        )
        self._timeline.set_position(current_s)
        if int(total_s) != self._timeline._duration:
            self._timeline.set_duration(int(total_s))

    def _get_current_game_time(self) -> int:
        if self._player:
            try:
                pos = self._player.time_pos
                return max(0, int(pos)) if pos is not None else 0
            except Exception:
                return 0
        return 0

    # ── Bookmarks ────────────────────────────────────────────────

    def _add_bookmark_at_current(self):
        game_time = self._get_current_game_time()
        was_playing = self._playing
        if was_playing:
            self._player.pause = True
            self._playing = False
            self._play_btn.configure(text="▶  Play")
            if self._update_job:
                self.after_cancel(self._update_job)
                self._update_job = None
        self._show_bookmark_note_dialog(game_time, resume_after=was_playing)

    def _show_bookmark_note_dialog(self, game_time_s: int, resume_after: bool = False):
        dialog = ctk.CTkToplevel(self)
        dialog.title("Add Bookmark")
        dialog.geometry("400x150")
        dialog.configure(fg_color=COLORS["bg_dark"])
        dialog.transient(self.winfo_toplevel())
        dialog.grab_set()
        dialog.lift()
        dialog.attributes("-topmost", True)

        ctk.CTkLabel(
            dialog,
            text=f"Bookmark at {format_game_time(game_time_s)}",
            font=ctk.CTkFont(size=13, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(padx=16, pady=(16, 8))

        note_entry = ctk.CTkEntry(
            dialog, height=36,
            font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
            placeholder_text="What happened here? (optional)",
        )
        note_entry.pack(fill="x", padx=16, pady=(0, 12))
        note_entry.focus_set()

        def _save():
            note = note_entry.get().strip()
            dialog.destroy()
            self._do_add_bookmark(game_time_s, note)
            if resume_after:
                self._toggle_play()
            self.focus_set()

        def _cancel():
            dialog.destroy()
            if resume_after:
                self._toggle_play()
            self.focus_set()

        note_entry.bind("<Return>", lambda e: _save())
        note_entry.bind("<Escape>", lambda e: _cancel())
        dialog.protocol("WM_DELETE_WINDOW", _cancel)

        ctk.CTkButton(
            dialog, text="Save Bookmark",
            font=ctk.CTkFont(size=13, weight="bold"),
            height=36, fg_color=COLORS["accent_gold"], hover_color="#a88432",
            text_color="#0a0a0f",
            command=_save,
        ).pack(padx=16, pady=(0, 12))

    def _add_bookmark_manual(self):
        time_text = self._time_entry.get().strip()
        note_text = self._note_entry.get().strip()
        game_time = parse_game_time(time_text)
        if game_time is None:
            self._time_entry.configure(border_color=COLORS["loss_red"])
            self.after(VOD_ERROR_FLASH_MS, lambda: self._time_entry.configure(border_color=COLORS["border"]))
            return
        self._do_add_bookmark(game_time, note_text)
        self._time_entry.delete(0, "end")
        self._note_entry.delete(0, "end")
        self.focus_set()

    def _do_add_bookmark(self, game_time_s: int, note: str):
        if self._on_add_bookmark:
            bm_id = self._on_add_bookmark(self.game_id, game_time_s, note)
            self._bookmarks.append({
                "id": bm_id, "game_id": self.game_id,
                "game_time_s": game_time_s, "note": note, "tags": "[]",
            })
            self._refresh_bookmark_list()
            self._timeline.set_bookmarks(self._bookmarks)

    def _delete_bookmark(self, bookmark_id: int):
        if self._on_delete_bookmark:
            self._on_delete_bookmark(bookmark_id)
        self._bookmarks = [b for b in self._bookmarks if b["id"] != bookmark_id]
        self._refresh_bookmark_list()
        self._timeline.set_bookmarks(self._bookmarks)

    def _seek_to_bookmark(self, game_time_s: int):
        if self._player:
            try:
                self._player.seek(game_time_s, reference="absolute")
            except Exception:
                pass
            self._update_time_display()
        self._timeline.set_position(game_time_s)

    def _refresh_bookmark_list(self):
        for widget in self._bm_scroll.winfo_children():
            widget.destroy()
        self._bm_count_label.configure(
            text=f"{len(self._bookmarks)} bookmark{'s' if len(self._bookmarks) != 1 else ''}"
        )
        sorted_bm = sorted(self._bookmarks, key=lambda b: b["game_time_s"])
        if not sorted_bm:
            ctk.CTkLabel(
                self._bm_scroll,
                text="No bookmarks yet — press 🔖 Bookmark or add one manually",
                font=ctk.CTkFont(size=12),
                text_color=COLORS["text_dim"],
            ).pack(pady=8)
            return
        for bm in sorted_bm:
            self._build_bookmark_row(bm)

    def _build_bookmark_row(self, bm: dict):
        has_clip = bool(bm.get("clip_path"))
        row = ctk.CTkFrame(
            self._bm_scroll, fg_color=COLORS["bg_input"], corner_radius=6,
            border_width=1 if has_clip else 0,
            border_color="#22c55e" if has_clip else COLORS["bg_input"],
        )
        row.pack(fill="x", pady=2)
        inner = ctk.CTkFrame(row, fg_color="transparent")
        inner.pack(fill="x", padx=8, pady=6)

        time_text = format_game_time(bm["game_time_s"])
        if has_clip and bm.get("clip_start_s") is not None:
            time_text = f"{format_game_time(bm['clip_start_s'])} – {format_game_time(bm['clip_end_s'])}"
            seek_to = bm["clip_start_s"]
        else:
            seek_to = bm["game_time_s"]

        ctk.CTkButton(
            inner, text=time_text, width=90 if has_clip else 60, height=26,
            font=ctk.CTkFont(size=12, weight="bold"),
            fg_color="#22c55e" if has_clip else COLORS["accent_blue"],
            hover_color="#1ea05a" if has_clip else "#0077cc",
            text_color="#0a0a0f" if has_clip else COLORS["text"],
            corner_radius=4,
            command=lambda t=seek_to: self._seek_to_bookmark(t),
        ).pack(side="left", padx=(0, 8))

        note = bm.get("note", "")
        if note:
            ctk.CTkLabel(inner, text=note, font=ctk.CTkFont(size=12),
                         text_color=COLORS["text"], wraplength=400, anchor="w",
                         justify="left").pack(side="left", fill="x", expand=True)
        else:
            ctk.CTkLabel(inner, text="(no note)", font=ctk.CTkFont(size=12),
                         text_color=COLORS["text_dim"]).pack(side="left", fill="x", expand=True)

        if has_clip:
            ctk.CTkLabel(inner, text="CLIP", font=ctk.CTkFont(size=9, weight="bold"),
                         text_color="#22c55e", fg_color="#1a4d2e", corner_radius=6,
                         padx=6, pady=1).pack(side="left", padx=4)

        try:
            tag_list = json.loads(bm.get("tags", "[]"))
        except (json.JSONDecodeError, TypeError):
            tag_list = []
        for tag_name in tag_list:
            ctk.CTkLabel(inner, text=tag_name, font=ctk.CTkFont(size=10),
                         text_color=COLORS["text"], fg_color=COLORS["tag_bg"],
                         corner_radius=10, padx=6, pady=2).pack(side="left", padx=2)

        ctk.CTkButton(
            inner, text="✕", width=28, height=26, font=ctk.CTkFont(size=12),
            fg_color="transparent", hover_color=COLORS["loss_red"],
            text_color=COLORS["text_dim"], corner_radius=4,
            command=lambda bid=bm["id"]: self._delete_bookmark(bid),
        ).pack(side="right")

    # ── Clip controls ─────────────────────────────────────────────

    def _set_clip_in(self):
        self._clip_start_s = self._get_current_game_time()
        if self._clip_end_s is not None and self._clip_end_s <= self._clip_start_s:
            self._clip_end_s = None
        self._update_clip_ui()

    def _set_clip_out(self):
        current = self._get_current_game_time()
        if self._clip_start_s is not None and current <= self._clip_start_s:
            return
        self._clip_end_s = current
        if self._clip_start_s is None:
            self._clip_start_s = 0
        self._update_clip_ui()

    def _clear_clip_range(self):
        self._clip_start_s = None
        self._clip_end_s = None
        self._update_clip_ui()

    def _update_clip_ui(self):
        self._timeline.set_clip_range(start=self._clip_start_s, end=self._clip_end_s)
        if self._clip_start_s is not None:
            self._clip_in_btn.configure(text=f"[ {format_game_time(self._clip_start_s)}")
        else:
            self._clip_in_btn.configure(text="[ In")
        if self._clip_end_s is not None:
            self._clip_out_btn.configure(text=f"{format_game_time(self._clip_end_s)} ]")
        else:
            self._clip_out_btn.configure(text="Out ]")
        has_range = self._clip_start_s is not None and self._clip_end_s is not None
        self._clip_save_btn.configure(
            state="normal" if has_range and not self._clip_saving else "disabled"
        )
        if has_range:
            dur = self._clip_end_s - self._clip_start_s
            self._clip_range_label.configure(text=f"{dur}s")
        else:
            self._clip_range_label.configure(text="")

    def _save_clip(self):
        if self._clip_start_s is None or self._clip_end_s is None:
            return
        if self._clip_saving:
            return
        self._clip_saving = True
        self._clip_save_btn.configure(text="Saving...", state="disabled")
        start_s = self._clip_start_s
        end_s = self._clip_end_s
        import threading

        def _do_extract():
            clip_path, error_msg = extract_clip(
                vod_path=self.vod_path, start_s=start_s, end_s=end_s,
                game_id=self.game_id, champion_name=self.champion_name,
            )
            self.after(0, lambda: self._on_clip_extracted(clip_path, start_s, end_s, error_msg))

        threading.Thread(target=_do_extract, daemon=True).start()

    def _on_clip_extracted(self, clip_path: Optional[str], start_s: int, end_s: int,
                           error_msg: str = ""):
        self._clip_saving = False
        self._clip_save_btn.configure(text="Save Clip")
        if clip_path:
            self._show_clip_note_dialog(clip_path, start_s, end_s)
        else:
            self._clip_save_btn.configure(text="Failed!", fg_color=COLORS["loss_red"])
            self.after(CLIP_SAVE_FEEDBACK_MS, lambda: self._clip_save_btn.configure(
                text="Save Clip", fg_color="#22c55e"))
            self._update_clip_ui()
            if error_msg:
                logger.error(f"Clip save failed: {error_msg}")
                self._show_clip_error_dialog(error_msg)

    def _show_clip_error_dialog(self, error_msg: str):
        dialog = ctk.CTkToplevel(self)
        dialog.title("Clip Error")
        dialog.geometry("600x250")
        dialog.configure(fg_color=COLORS["bg_dark"])
        dialog.transient(self.winfo_toplevel())
        dialog.grab_set()
        dialog.lift()
        dialog.attributes("-topmost", True)

        ctk.CTkLabel(dialog, text="Clip extraction failed",
                     font=ctk.CTkFont(size=14, weight="bold"),
                     text_color=COLORS["loss_red"]).pack(padx=16, pady=(16, 8))
        error_box = ctk.CTkTextbox(dialog, height=120,
                                   font=ctk.CTkFont(family="Consolas", size=11),
                                   fg_color=COLORS["bg_input"], text_color=COLORS["text"],
                                   border_width=1, border_color=COLORS["border"], wrap="word")
        error_box.pack(fill="both", expand=True, padx=16, pady=(0, 12))
        error_box.insert("1.0", error_msg)
        error_box.configure(state="disabled")
        ctk.CTkButton(dialog, text="Close", width=80, height=32,
                      fg_color=COLORS["tag_bg"], hover_color="#333344",
                      command=dialog.destroy).pack(pady=(0, 12))

    def _show_clip_note_dialog(self, clip_path: str, start_s: int, end_s: int):
        dialog = ctk.CTkToplevel(self)
        dialog.title("Clip Note")
        dialog.geometry("400x160")
        dialog.configure(fg_color=COLORS["bg_dark"])
        dialog.transient(self.winfo_toplevel())
        dialog.grab_set()
        dialog.lift()
        dialog.attributes("-topmost", True)

        ctk.CTkLabel(dialog,
                     text=f"Clip saved: {format_game_time(start_s)} – {format_game_time(end_s)}",
                     font=ctk.CTkFont(size=13, weight="bold"),
                     text_color="#22c55e").pack(padx=16, pady=(16, 8))

        note_entry = ctk.CTkEntry(
            dialog, height=36, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
            placeholder_text="What happened here? (optional)",
        )
        note_entry.pack(fill="x", padx=16, pady=(0, 12))
        note_entry.focus_set()

        def _save():
            note = note_entry.get().strip()
            if self._on_add_bookmark:
                mid = (start_s + end_s) // 2
                bm_id = self._on_add_bookmark(self.game_id, mid, note)
                if self._on_update_bookmark and bm_id:
                    self._on_update_bookmark(bm_id, clip_start_s=start_s,
                                             clip_end_s=end_s, clip_path=clip_path)
                self._bookmarks.append({
                    "id": bm_id, "game_id": self.game_id, "game_time_s": mid,
                    "note": note, "tags": "[]", "clip_start_s": start_s,
                    "clip_end_s": end_s, "clip_path": clip_path,
                })
                self._refresh_bookmark_list()
                self._timeline.set_bookmarks(self._bookmarks)
            self._clear_clip_range()
            dialog.destroy()

        note_entry.bind("<Return>", lambda e: _save())
        dialog.protocol("WM_DELETE_WINDOW", lambda: (self._clear_clip_range(), dialog.destroy()))

        ctk.CTkButton(dialog, text="Save Bookmark + Clip",
                      font=ctk.CTkFont(size=13, weight="bold"), height=36,
                      fg_color="#22c55e", hover_color="#1ea05a", text_color="#0a0a0f",
                      command=_save).pack(padx=16, pady=(0, 12))


# ── VOD Player Window (legacy — kept for backward compatibility) ──


class VodPlayerWindow(ctk.CTkToplevel):
    """Watch a game recording and add timestamped bookmarks.

    If python-mpv + libmpv are available, plays the video inline.
    Otherwise, opens the file in the system default player and still
    provides the bookmark UI for manual timestamp entry.
    """

    def __init__(
        self,
        game_id: int,
        vod_path: str,
        game_duration: int,
        champion_name: str,
        bookmarks: list[dict],
        tags: list[dict],
        game_events: list[dict] = None,
        on_add_bookmark: Optional[Callable] = None,
        on_update_bookmark: Optional[Callable] = None,
        on_delete_bookmark: Optional[Callable] = None,
        *args,
        **kwargs,
    ):
        super().__init__(*args, **kwargs)

        self.game_id = game_id
        self.vod_path = vod_path
        self.game_duration = game_duration
        self.champion_name = champion_name
        self._bookmarks = list(bookmarks)
        self._tags = tags
        self._game_events = game_events or []
        self._on_add_bookmark = on_add_bookmark
        self._on_update_bookmark = on_update_bookmark
        self._on_delete_bookmark = on_delete_bookmark

        self._player = None  # mpv.MPV instance
        self._playing = False
        self._update_job = None
        self._speed = 1.0
        self._clip_start_s = None  # Clip in-marker time
        self._clip_end_s = None    # Clip out-marker time
        self._clip_saving = False  # True while ffmpeg is running

        self.title(f"VOD Review — {champion_name}")
        self.geometry("960x740")
        self.configure(fg_color=COLORS["bg_dark"])
        self.minsize(800, 600)

        self.lift()
        self.attributes("-topmost", True)
        self.after(100, lambda: self.attributes("-topmost", False))
        self.focus_force()

        self._build_ui()
        self._init_player()

        self.protocol("WM_DELETE_WINDOW", self._on_close)

    def _build_ui(self):
        """Build the player + timeline + bookmarks layout."""
        outer = ctk.CTkFrame(self, fg_color=COLORS["bg_dark"])
        outer.pack(fill="both", expand=True, padx=12, pady=12)

        # ── Top: Video area ──────────────────────────────────────
        self._video_frame = ctk.CTkFrame(
            outer, fg_color="#000000", corner_radius=8,
            border_width=1, border_color=COLORS["border"],
        )
        self._video_frame.pack(fill="both", expand=True, pady=(0, 8))

        # Canvas for mpv to render into (or a placeholder message)
        self._video_canvas = tk.Frame(
            self._video_frame, bg="#000000",
        )
        self._video_canvas.pack(fill="both", expand=True)

        # Click on video area to toggle play/pause and reclaim keyboard focus
        self._video_canvas.bind("<Button-1>", lambda e: (self.focus_set(), self._toggle_play()))

        # ── Transport controls ───────────────────────────────────
        transport = ctk.CTkFrame(outer, fg_color=COLORS["bg_card"], corner_radius=8,
                                  border_width=1, border_color=COLORS["border"])
        transport.pack(fill="x", pady=(0, 4))

        transport_inner = ctk.CTkFrame(transport, fg_color="transparent")
        transport_inner.pack(fill="x", padx=12, pady=8)

        # Play / Pause
        self._play_btn = ctk.CTkButton(
            transport_inner, text="▶  Play", width=90, height=32,
            font=ctk.CTkFont(size=13, weight="bold"),
            fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._toggle_play,
        )
        self._play_btn.pack(side="left", padx=(0, 8))

        # Back 10s / Forward 10s
        ctk.CTkButton(
            transport_inner, text="⏪ 10s", width=70, height=32,
            font=ctk.CTkFont(size=12),
            fg_color=COLORS["tag_bg"], hover_color="#333344",
            command=lambda: self._seek_relative(-10),
        ).pack(side="left", padx=(0, 4))

        ctk.CTkButton(
            transport_inner, text="10s ⏩", width=70, height=32,
            font=ctk.CTkFont(size=12),
            fg_color=COLORS["tag_bg"], hover_color="#333344",
            command=lambda: self._seek_relative(10),
        ).pack(side="left", padx=(0, 8))

        # Speed controls
        self._speed_label = ctk.CTkLabel(
            transport_inner, text="1x",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text"],
            width=30,
        )
        self._speed_label.pack(side="left", padx=(0, 2))

        for spd in VOD_PLAYBACK_SPEEDS:
            label = str(spd).rstrip('0').rstrip('.')
            btn = ctk.CTkButton(
                transport_inner, text=label, width=36, height=28,
                font=ctk.CTkFont(size=11),
                fg_color=COLORS["accent_blue"] if spd == 1.0 else COLORS["tag_bg"],
                hover_color="#0077cc" if spd == 1.0 else "#333344",
                corner_radius=6,
                command=lambda s=spd: self._set_speed(s),
            )
            btn.pack(side="left", padx=1)
            btn._speed_value = spd  # Tag for updating active state

        # Time display
        self._time_label = ctk.CTkLabel(
            transport_inner, text="0:00 / 0:00",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        )
        self._time_label.pack(side="left", padx=(8, 12))

        # Bookmark at current time button
        self._bookmark_btn = ctk.CTkButton(
            transport_inner, text="🔖 Bookmark", width=110, height=32,
            font=ctk.CTkFont(size=12, weight="bold"),
            fg_color=COLORS["accent_gold"], hover_color="#a88432",
            text_color="#0a0a0f",
            command=self._add_bookmark_at_current,
        )
        self._bookmark_btn.pack(side="right")

        # ── Clip controls (right side, next to bookmark) ────────
        clip_frame = ctk.CTkFrame(transport_inner, fg_color="transparent")
        clip_frame.pack(side="right", padx=(0, 8))

        self._clip_in_btn = ctk.CTkButton(
            clip_frame, text="[ In", width=50, height=32,
            font=ctk.CTkFont(size=12, weight="bold"),
            fg_color="#1a4d2e", hover_color="#22c55e",
            text_color="#22c55e",
            command=self._set_clip_in,
        )
        self._clip_in_btn.pack(side="left", padx=(0, 2))

        self._clip_out_btn = ctk.CTkButton(
            clip_frame, text="Out ]", width=50, height=32,
            font=ctk.CTkFont(size=12, weight="bold"),
            fg_color="#1a4d2e", hover_color="#22c55e",
            text_color="#22c55e",
            command=self._set_clip_out,
        )
        self._clip_out_btn.pack(side="left", padx=(0, 2))

        self._clip_save_btn = ctk.CTkButton(
            clip_frame, text="Save Clip", width=80, height=32,
            font=ctk.CTkFont(size=12, weight="bold"),
            fg_color="#22c55e", hover_color="#1ea05a",
            text_color="#0a0a0f",
            command=self._save_clip,
            state="disabled",
        )
        self._clip_save_btn.pack(side="left", padx=(0, 2))

        self._clip_clear_btn = ctk.CTkButton(
            clip_frame, text="✕", width=28, height=32,
            font=ctk.CTkFont(size=12),
            fg_color="transparent", hover_color=COLORS["loss_red"],
            text_color=COLORS["text_dim"],
            command=self._clear_clip_range,
        )
        self._clip_clear_btn.pack(side="left")

        self._clip_range_label = ctk.CTkLabel(
            clip_frame, text="",
            font=ctk.CTkFont(size=10),
            text_color="#22c55e",
        )
        self._clip_range_label.pack(side="left", padx=(4, 0))

        # ── Key bindings (configurable via Settings) ─────────────
        self._keybinds = get_keybinds()
        self._bind_keys()

        # ── Keyboard shortcut hints ──────────────────────────────
        self._hint_label = ctk.CTkLabel(
            transport, text=self._build_hint_text(),
            font=ctk.CTkFont(size=10),
            text_color=COLORS["text_dim"],
        )
        self._hint_label.pack(pady=(0, 6))

        # ── Visual Timeline ──────────────────────────────────────
        self._timeline = TimelineCanvas(
            outer,
            duration=self.game_duration,
            on_seek=self._on_seek,
            events=self._game_events,
            bookmarks=self._bookmarks,
        )
        self._timeline.pack(fill="x", pady=(0, 2))
        # Clicking the timeline should reclaim keybind focus from any entry field
        self._timeline.bind("<Button-1>", lambda e: self.focus_set(), add="+")

        # Timeline legend
        has_events = len(self._game_events) > 0
        legend = build_timeline_legend(outer, has_events)
        legend.pack(anchor="w", pady=(0, 6))

        # ── Bottom: Bookmarks panel ──────────────────────────────
        bookmarks_section = ctk.CTkFrame(
            outer, fg_color=COLORS["bg_card"], corner_radius=8,
            border_width=1, border_color=COLORS["border"],
        )
        bookmarks_section.pack(fill="x")

        bm_header = ctk.CTkFrame(bookmarks_section, fg_color="transparent")
        bm_header.pack(fill="x", padx=12, pady=(10, 6))

        ctk.CTkLabel(
            bm_header, text="BOOKMARKS",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(side="left")

        self._bm_count_label = ctk.CTkLabel(
            bm_header, text=f"{len(self._bookmarks)} bookmarks",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        )
        self._bm_count_label.pack(side="right")

        # Manual add row
        add_row = ctk.CTkFrame(bookmarks_section, fg_color="transparent")
        add_row.pack(fill="x", padx=12, pady=(0, 6))

        ctk.CTkLabel(
            add_row, text="Time:",
            font=ctk.CTkFont(size=12), text_color=COLORS["text_dim"],
        ).pack(side="left", padx=(0, 4))

        self._time_entry = ctk.CTkEntry(
            add_row, width=70, height=30,
            font=ctk.CTkFont(size=12),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=6,
            placeholder_text="MM:SS",
        )
        self._time_entry.pack(side="left", padx=(0, 6))

        self._note_entry = ctk.CTkEntry(
            add_row, height=30,
            font=ctk.CTkFont(size=12),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=6,
            placeholder_text="What happened here?",
        )
        self._note_entry.pack(side="left", fill="x", expand=True, padx=(0, 6))

        ctk.CTkButton(
            add_row, text="+ Add", width=60, height=30,
            font=ctk.CTkFont(size=12),
            fg_color=COLORS["win_green"], hover_color="#1ea05a",
            text_color="#0a0a0f",
            command=self._add_bookmark_manual,
        ).pack(side="right")

        # Return focus to window on Enter/Escape so keybinds work immediately after
        self._note_entry.bind("<Return>", lambda e: (self._add_bookmark_manual(), self.focus_set()))
        self._note_entry.bind("<Escape>", lambda e: self.focus_set())
        self._time_entry.bind("<Escape>", lambda e: self.focus_set())

        # Scrollable bookmark list
        self._bm_scroll = ctk.CTkScrollableFrame(
            bookmarks_section, fg_color="transparent",
            height=150,
            scrollbar_button_color=COLORS["border"],
        )
        self._bm_scroll.pack(fill="x", padx=8, pady=(0, 8))

        self._refresh_bookmark_list()

    # ── Keybind wiring ──────────────────────────────────────────

    _SPEEDS = VOD_PLAYBACK_SPEEDS

    _ACTION_MAP = {
        "play_pause":   lambda s: s._toggle_play(),
        "seek_fwd_5":   lambda s: s._seek_relative(5),
        "seek_back_5":  lambda s: s._seek_relative(-5),
        "seek_fwd_2":   lambda s: s._seek_relative(2),
        "seek_back_2":  lambda s: s._seek_relative(-2),
        "seek_fwd_10":  lambda s: s._seek_relative(10),
        "seek_back_10": lambda s: s._seek_relative(-10),
        "seek_fwd_1":   lambda s: s._seek_relative(1),
        "seek_back_1":  lambda s: s._seek_relative(-1),
        "bookmark":     lambda s: s._add_bookmark_at_current(),
        "speed_up":     lambda s: s._cycle_speed(1),
        "speed_down":   lambda s: s._cycle_speed(-1),
        "clip_in":      lambda s: s._set_clip_in(),
        "clip_out":     lambda s: s._set_clip_out(),
    }

    def _is_typing(self) -> bool:
        """Check if the user is currently typing in a text input field."""
        focused = self.focus_get()
        if focused is None:
            return False
        widget_class = focused.winfo_class()
        # CTkEntry uses "Entry" internally, CTkTextbox uses "Text"
        return widget_class in ("Entry", "Text", "TEntry", "TText", "CTkEntry")

    def _bind_keys(self):
        """Bind all configured keybinds to their actions.

        Uses bind_all so keybinds fire even when a child widget (canvas,
        timeline, scrollable frame) has focus — not just the toplevel itself.
        Gates on _is_typing() to suppress them when an entry field is active.
        """
        for action, tk_key in self._keybinds.items():
            if action in self._ACTION_MAP:
                handler = self._ACTION_MAP[action]
                self.bind_all(
                    f"<{tk_key}>",
                    lambda e, h=handler: h(self) if not self._is_typing() else None,
                )

    def _build_hint_text(self) -> str:
        """Build a compact hint string from the current keybinds."""
        kb = self._keybinds
        parts = []

        def _short(tk_key: str) -> str:
            """Shorten a tk key string for the hint bar."""
            return tk_key.replace("Control-", "Ctrl+").replace("Shift-", "Shift+").replace("Alt-", "Alt+")

        parts.append(f"{_short(kb.get('seek_back_5', '←'))} / {_short(kb.get('seek_fwd_5', '→'))} 5s")
        parts.append(f"{_short(kb.get('seek_back_2', ''))} 2s")
        parts.append(f"{_short(kb.get('seek_back_10', ''))} 10s")
        parts.append(f"{_short(kb.get('seek_back_1', ''))} 1s")
        parts.append(f"{_short(kb.get('play_pause', 'space'))} play/pause")
        parts.append(f"{_short(kb.get('bookmark', 'b'))} bookmark")
        parts.append(f"{_short(kb.get('speed_down', '['))} / {_short(kb.get('speed_up', ']'))} speed")
        if kb.get("clip_in") and kb.get("clip_out"):
            parts.append(f"{_short(kb['clip_in'])} / {_short(kb['clip_out'])} clip in/out")
        return "  |  ".join(parts)

    def _cycle_speed(self, direction: int):
        """Cycle playback speed up (+1) or down (-1)."""
        try:
            idx = self._SPEEDS.index(self._speed)
        except ValueError:
            idx = 2  # default to 1.0
        idx = max(0, min(idx + direction, len(self._SPEEDS) - 1))
        self._set_speed(self._SPEEDS[idx])

    def _init_player(self):
        """Set up mpv player if available, otherwise show fallback message."""
        if _mpv is None:
            self._show_external_mode()
            return

        # Check file exists — retry once after a short delay in case of
        # stale file locks from a previous mpv instance
        vod_exists = Path(self.vod_path).exists()
        if not vod_exists:
            import time
            time.sleep(0.3)
            vod_exists = Path(self.vod_path).exists()
        if not vod_exists:
            logger.warning(f"VOD file not found: {self.vod_path}")
            self._show_external_mode()
            return

        try:
            # Wait for the tk window to be mapped so we get a valid HWND
            self._video_canvas.update_idletasks()
            wid = self._video_canvas.winfo_id()

            self._player = _mpv.MPV(
                wid=str(wid),
                vo="gpu",
                hwdec="auto",
                keep_open="yes",  # Don't close at end of file
                osd_level=0,      # No OSD clutter
                input_default_bindings=False,
                input_vo_keyboard=False,
                log_handler=lambda lvl, comp, msg: None,  # silence mpv logs
            )

            self._player.play(self.vod_path)
            self._player.pause = True  # Start paused
            self._playing = False

            logger.info(f"mpv player initialized for {self.vod_path}")
        except Exception as e:
            logger.warning(f"Failed to init mpv player: {e}")
            self._player = None
            self._show_external_mode()

    def _show_external_mode(self):
        """Show a message + button for opening in external player."""
        self._video_canvas.destroy()

        placeholder = ctk.CTkFrame(self._video_frame, fg_color="#0a0a12")
        placeholder.pack(fill="both", expand=True, padx=2, pady=2)

        msg = "Embedded playback requires libmpv\n" if _mpv is None else ""
        msg += "Click below to open in your default player"

        ctk.CTkLabel(
            placeholder, text=msg,
            font=ctk.CTkFont(size=14),
            text_color=COLORS["text_dim"],
            justify="center",
        ).pack(expand=True)

        ctk.CTkButton(
            placeholder,
            text="Open in Default Player",
            font=ctk.CTkFont(size=14, weight="bold"),
            height=40, width=220,
            fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._open_external,
        ).pack(pady=(0, 30))

    def _open_external(self):
        """Open the video file in the OS default player."""
        try:
            if sys.platform == "win32":
                os.startfile(self.vod_path)
            elif sys.platform == "darwin":
                subprocess.Popen(["open", self.vod_path])
            else:
                subprocess.Popen(["xdg-open", self.vod_path])
        except Exception as e:
            logger.error(f"Failed to open VOD externally: {e}")

    # ── Playback controls ────────────────────────────────────────

    def _toggle_play(self):
        """Play or pause the video."""
        if not self._player:
            self._open_external()
            return

        if self._playing:
            self._player.pause = True
            self._playing = False
            self._play_btn.configure(text="▶  Play")
            if self._update_job:
                self.after_cancel(self._update_job)
                self._update_job = None
        else:
            self._player.pause = False
            self._playing = True
            self._play_btn.configure(text="⏸  Pause")
            self._start_time_update()

    def _set_speed(self, speed: float):
        """Change playback speed."""
        self._speed = speed
        if self._player:
            try:
                self._player.speed = speed
            except Exception:
                pass
        self._speed_label.configure(text=f"{speed}x")

        # Update button highlighting — find all speed buttons in the transport
        for widget in self._speed_label.master.winfo_children():
            if hasattr(widget, "_speed_value"):
                if widget._speed_value == speed:
                    widget.configure(fg_color=COLORS["accent_blue"])
                else:
                    widget.configure(fg_color=COLORS["tag_bg"])

    def _seek_relative(self, seconds: int):
        """Seek forward or backward by the given seconds."""
        if not self._player:
            return
        try:
            self._player.seek(seconds, reference="relative")
        except Exception:
            pass
        self._update_time_display()

    def _on_seek(self, value):
        """Handle timeline click/drag — seek the video."""
        if not self._player:
            return
        try:
            self._player.seek(float(value), reference="absolute")
        except Exception:
            pass
        self._update_time_display()

    def _start_time_update(self):
        """Periodically update the time display and timeline."""
        if not self._playing or not self._player:
            return
        self._update_time_display()
        self._update_job = self.after(VOD_TIME_UPDATE_INTERVAL_MS, self._start_time_update)

    def _update_time_display(self):
        """Sync the time label and timeline with the player position."""
        if not self._player:
            return

        try:
            current_s = self._player.time_pos or 0
            total_s = self._player.duration or self.game_duration
        except Exception:
            current_s = 0
            total_s = self.game_duration

        if current_s < 0:
            current_s = 0

        self._time_label.configure(
            text=f"{format_game_time(int(current_s))} / {format_game_time(int(total_s))}"
        )

        # Update timeline position
        self._timeline.set_position(current_s)
        if int(total_s) != self._timeline._duration:
            self._timeline.set_duration(int(total_s))

    def _get_current_game_time(self) -> int:
        """Get the current playback position in seconds."""
        if self._player:
            try:
                pos = self._player.time_pos
                return max(0, int(pos)) if pos is not None else 0
            except Exception:
                return 0
        return 0

    # ── Bookmarks ────────────────────────────────────────────────

    def _add_bookmark_at_current(self):
        """Pause and show a note dialog, then add a bookmark at the current time."""
        game_time = self._get_current_game_time()

        # Pause while the user types the note
        was_playing = self._playing
        if was_playing:
            self._player.pause = True
            self._playing = False
            self._play_btn.configure(text="▶  Play")
            if self._update_job:
                self.after_cancel(self._update_job)
                self._update_job = None

        self._show_bookmark_note_dialog(game_time, resume_after=was_playing)

    def _show_bookmark_note_dialog(self, game_time_s: int, resume_after: bool = False):
        """Show a small popup to add a note before saving the bookmark."""
        dialog = ctk.CTkToplevel(self)
        dialog.title("Add Bookmark")
        dialog.geometry("400x150")
        dialog.configure(fg_color=COLORS["bg_dark"])
        dialog.transient(self)
        dialog.grab_set()
        dialog.lift()
        dialog.attributes("-topmost", True)

        ctk.CTkLabel(
            dialog,
            text=f"Bookmark at {format_game_time(game_time_s)}",
            font=ctk.CTkFont(size=13, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(padx=16, pady=(16, 8))

        note_entry = ctk.CTkEntry(
            dialog, height=36,
            font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
            placeholder_text="What happened here? (optional)",
        )
        note_entry.pack(fill="x", padx=16, pady=(0, 12))
        note_entry.focus_set()

        def _save():
            note = note_entry.get().strip()
            dialog.destroy()
            self._do_add_bookmark(game_time_s, note)
            if resume_after:
                self._toggle_play()
            self.focus_set()  # Return keybinds to the window

        def _cancel():
            dialog.destroy()
            if resume_after:
                self._toggle_play()
            self.focus_set()

        note_entry.bind("<Return>", lambda e: _save())
        note_entry.bind("<Escape>", lambda e: _cancel())
        dialog.protocol("WM_DELETE_WINDOW", _cancel)

        ctk.CTkButton(
            dialog, text="Save Bookmark",
            font=ctk.CTkFont(size=13, weight="bold"),
            height=36, fg_color=COLORS["accent_gold"], hover_color="#a88432",
            text_color="#0a0a0f",
            command=_save,
        ).pack(padx=16, pady=(0, 12))

    def _add_bookmark_manual(self):
        """Add a bookmark from the manual time + note fields."""
        time_text = self._time_entry.get().strip()
        note_text = self._note_entry.get().strip()

        game_time = parse_game_time(time_text)
        if game_time is None:
            self._time_entry.configure(border_color=COLORS["loss_red"])
            self.after(VOD_ERROR_FLASH_MS, lambda: self._time_entry.configure(border_color=COLORS["border"]))
            return

        self._do_add_bookmark(game_time, note_text)
        self._time_entry.delete(0, "end")
        self._note_entry.delete(0, "end")
        self.focus_set()  # Return keybinds to the window

    def _do_add_bookmark(self, game_time_s: int, note: str):
        """Create a bookmark and refresh the list."""
        if self._on_add_bookmark:
            bm_id = self._on_add_bookmark(self.game_id, game_time_s, note)
            self._bookmarks.append({
                "id": bm_id,
                "game_id": self.game_id,
                "game_time_s": game_time_s,
                "note": note,
                "tags": "[]",
            })
            self._refresh_bookmark_list()
            # Update timeline markers
            self._timeline.set_bookmarks(self._bookmarks)

    def _delete_bookmark(self, bookmark_id: int):
        """Remove a bookmark."""
        if self._on_delete_bookmark:
            self._on_delete_bookmark(bookmark_id)
        self._bookmarks = [b for b in self._bookmarks if b["id"] != bookmark_id]
        self._refresh_bookmark_list()
        self._timeline.set_bookmarks(self._bookmarks)

    def _seek_to_bookmark(self, game_time_s: int):
        """Jump the player to a bookmark's timestamp."""
        if self._player:
            try:
                self._player.seek(game_time_s, reference="absolute")
            except Exception:
                pass
            self._update_time_display()
        # Update timeline
        self._timeline.set_position(game_time_s)

    def _refresh_bookmark_list(self):
        """Rebuild the bookmark list UI."""
        for widget in self._bm_scroll.winfo_children():
            widget.destroy()

        self._bm_count_label.configure(
            text=f"{len(self._bookmarks)} bookmark{'s' if len(self._bookmarks) != 1 else ''}"
        )

        sorted_bm = sorted(self._bookmarks, key=lambda b: b["game_time_s"])

        if not sorted_bm:
            ctk.CTkLabel(
                self._bm_scroll,
                text="No bookmarks yet — press 🔖 Bookmark or add one manually",
                font=ctk.CTkFont(size=12),
                text_color=COLORS["text_dim"],
            ).pack(pady=8)
            return

        for bm in sorted_bm:
            self._build_bookmark_row(bm)

    def _build_bookmark_row(self, bm: dict):
        """Render a single bookmark entry."""
        has_clip = bool(bm.get("clip_path"))

        row = ctk.CTkFrame(
            self._bm_scroll, fg_color=COLORS["bg_input"], corner_radius=6,
            border_width=1 if has_clip else 0,
            border_color="#22c55e" if has_clip else COLORS["bg_input"],
        )
        row.pack(fill="x", pady=2)

        inner = ctk.CTkFrame(row, fg_color="transparent")
        inner.pack(fill="x", padx=8, pady=6)

        # Timestamp (clickable to seek)
        time_text = format_game_time(bm["game_time_s"])

        # If has clip, show range instead of single timestamp
        if has_clip and bm.get("clip_start_s") is not None:
            time_text = (
                f"{format_game_time(bm['clip_start_s'])} – "
                f"{format_game_time(bm['clip_end_s'])}"
            )
            seek_to = bm["clip_start_s"]
        else:
            seek_to = bm["game_time_s"]

        time_btn = ctk.CTkButton(
            inner, text=time_text, width=90 if has_clip else 60, height=26,
            font=ctk.CTkFont(size=12, weight="bold"),
            fg_color="#22c55e" if has_clip else COLORS["accent_blue"],
            hover_color="#1ea05a" if has_clip else "#0077cc",
            text_color="#0a0a0f" if has_clip else COLORS["text"],
            corner_radius=4,
            command=lambda t=seek_to: self._seek_to_bookmark(t),
        )
        time_btn.pack(side="left", padx=(0, 8))

        # Note text
        note = bm.get("note", "")
        if note:
            ctk.CTkLabel(
                inner, text=note,
                font=ctk.CTkFont(size=12),
                text_color=COLORS["text"],
                wraplength=400,
                anchor="w",
                justify="left",
            ).pack(side="left", fill="x", expand=True)
        else:
            ctk.CTkLabel(
                inner, text="(no note)",
                font=ctk.CTkFont(size=12),
                text_color=COLORS["text_dim"],
            ).pack(side="left", fill="x", expand=True)

        # Clip badge
        if has_clip:
            ctk.CTkLabel(
                inner, text="CLIP",
                font=ctk.CTkFont(size=9, weight="bold"),
                text_color="#22c55e",
                fg_color="#1a4d2e",
                corner_radius=6,
                padx=6, pady=1,
            ).pack(side="left", padx=4)

        # Tags display
        try:
            tag_list = json.loads(bm.get("tags", "[]"))
        except (json.JSONDecodeError, TypeError):
            tag_list = []

        if tag_list:
            for tag_name in tag_list:
                ctk.CTkLabel(
                    inner, text=tag_name,
                    font=ctk.CTkFont(size=10),
                    text_color=COLORS["text"],
                    fg_color=COLORS["tag_bg"],
                    corner_radius=10,
                    padx=6,
                    pady=2,
                ).pack(side="left", padx=2)

        # Delete button
        ctk.CTkButton(
            inner, text="✕", width=28, height=26,
            font=ctk.CTkFont(size=12),
            fg_color="transparent", hover_color=COLORS["loss_red"],
            text_color=COLORS["text_dim"],
            corner_radius=4,
            command=lambda bid=bm["id"]: self._delete_bookmark(bid),
        ).pack(side="right")

    # ── Clip controls ─────────────────────────────────────────────

    def _set_clip_in(self):
        """Set the clip start point to the current playback position."""
        self._clip_start_s = self._get_current_game_time()
        # If out marker is before in, clear it
        if self._clip_end_s is not None and self._clip_end_s <= self._clip_start_s:
            self._clip_end_s = None
        self._update_clip_ui()

    def _set_clip_out(self):
        """Set the clip end point to the current playback position."""
        current = self._get_current_game_time()
        # Only set if after in point (or no in point yet)
        if self._clip_start_s is not None and current <= self._clip_start_s:
            return  # Can't set out before in
        self._clip_end_s = current
        if self._clip_start_s is None:
            self._clip_start_s = 0
        self._update_clip_ui()

    def _clear_clip_range(self):
        """Clear both clip markers."""
        self._clip_start_s = None
        self._clip_end_s = None
        self._update_clip_ui()

    def _update_clip_ui(self):
        """Update the clip range display and button states."""
        # Update timeline overlay
        self._timeline.set_clip_range(
            start=self._clip_start_s,
            end=self._clip_end_s,
        )

        # Update in/out button labels
        if self._clip_start_s is not None:
            self._clip_in_btn.configure(text=f"[ {format_game_time(self._clip_start_s)}")
        else:
            self._clip_in_btn.configure(text="[ In")

        if self._clip_end_s is not None:
            self._clip_out_btn.configure(text=f"{format_game_time(self._clip_end_s)} ]")
        else:
            self._clip_out_btn.configure(text="Out ]")

        # Enable Save Clip only when both markers are set
        has_range = self._clip_start_s is not None and self._clip_end_s is not None
        self._clip_save_btn.configure(
            state="normal" if has_range and not self._clip_saving else "disabled"
        )

        # Range label
        if has_range:
            dur = self._clip_end_s - self._clip_start_s
            self._clip_range_label.configure(text=f"{dur}s")
        else:
            self._clip_range_label.configure(text="")

    def _save_clip(self):
        """Extract the clip and create a bookmark with the clip path."""
        if self._clip_start_s is None or self._clip_end_s is None:
            return
        if self._clip_saving:
            return

        self._clip_saving = True
        self._clip_save_btn.configure(text="Saving...", state="disabled")

        start_s = self._clip_start_s
        end_s = self._clip_end_s

        # Run extraction in a thread to avoid freezing the UI
        import threading

        def _do_extract():
            clip_path, error_msg = extract_clip(
                vod_path=self.vod_path,
                start_s=start_s,
                end_s=end_s,
                game_id=self.game_id,
                champion_name=self.champion_name,
            )
            # Back to main thread to update UI
            self.after(0, lambda: self._on_clip_extracted(clip_path, start_s, end_s, error_msg))

        threading.Thread(target=_do_extract, daemon=True).start()

    def _on_clip_extracted(self, clip_path: Optional[str], start_s: int, end_s: int,
                           error_msg: str = ""):
        """Called on the main thread after clip extraction finishes."""
        self._clip_saving = False
        self._clip_save_btn.configure(text="Save Clip")

        if clip_path:
            # Prompt for a note via a simple dialog
            self._show_clip_note_dialog(clip_path, start_s, end_s)
        else:
            # Show error in a dialog so user can see the full message
            self._clip_save_btn.configure(
                text="Failed!", fg_color=COLORS["loss_red"],
            )
            self.after(CLIP_SAVE_FEEDBACK_MS, lambda: self._clip_save_btn.configure(
                text="Save Clip", fg_color="#22c55e",
            ))
            self._update_clip_ui()

            if error_msg:
                logger.error(f"Clip save failed: {error_msg}")
                self._show_clip_error_dialog(error_msg)

    def _show_clip_error_dialog(self, error_msg: str):
        """Show a dialog with the full ffmpeg error for debugging."""
        dialog = ctk.CTkToplevel(self)
        dialog.title("Clip Error")
        dialog.geometry("600x250")
        dialog.configure(fg_color=COLORS["bg_dark"])
        dialog.transient(self)
        dialog.grab_set()
        dialog.lift()
        dialog.attributes("-topmost", True)

        ctk.CTkLabel(
            dialog, text="Clip extraction failed",
            font=ctk.CTkFont(size=14, weight="bold"),
            text_color=COLORS["loss_red"],
        ).pack(padx=16, pady=(16, 8))

        error_box = ctk.CTkTextbox(
            dialog, height=120,
            font=ctk.CTkFont(family="Consolas", size=11),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"],
            wrap="word",
        )
        error_box.pack(fill="both", expand=True, padx=16, pady=(0, 12))
        error_box.insert("1.0", error_msg)
        error_box.configure(state="disabled")

        ctk.CTkButton(
            dialog, text="Close", width=80, height=32,
            fg_color=COLORS["tag_bg"], hover_color="#333344",
            command=dialog.destroy,
        ).pack(pady=(0, 12))

    def _show_clip_note_dialog(self, clip_path: str, start_s: int, end_s: int):
        """Show a small popup to add a note before saving the clip bookmark."""
        dialog = ctk.CTkToplevel(self)
        dialog.title("Clip Note")
        dialog.geometry("400x160")
        dialog.configure(fg_color=COLORS["bg_dark"])
        dialog.transient(self)
        dialog.grab_set()
        dialog.lift()
        dialog.attributes("-topmost", True)

        ctk.CTkLabel(
            dialog,
            text=f"Clip saved: {format_game_time(start_s)} – {format_game_time(end_s)}",
            font=ctk.CTkFont(size=13, weight="bold"),
            text_color="#22c55e",
        ).pack(padx=16, pady=(16, 8))

        note_entry = ctk.CTkEntry(
            dialog, height=36,
            font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
            placeholder_text="What happened here? (optional)",
        )
        note_entry.pack(fill="x", padx=16, pady=(0, 12))
        note_entry.focus_set()

        def _save():
            note = note_entry.get().strip()
            # Create bookmark with clip info
            if self._on_add_bookmark:
                # game_time_s is the midpoint (for timeline display)
                mid = (start_s + end_s) // 2
                bm_id = self._on_add_bookmark(self.game_id, mid, note)
                # Update the bookmark with clip fields
                if self._on_update_bookmark and bm_id:
                    self._on_update_bookmark(
                        bm_id,
                        clip_start_s=start_s,
                        clip_end_s=end_s,
                        clip_path=clip_path,
                    )
                self._bookmarks.append({
                    "id": bm_id,
                    "game_id": self.game_id,
                    "game_time_s": mid,
                    "note": note,
                    "tags": "[]",
                    "clip_start_s": start_s,
                    "clip_end_s": end_s,
                    "clip_path": clip_path,
                })
                self._refresh_bookmark_list()
                self._timeline.set_bookmarks(self._bookmarks)

            # Clear clip range after saving
            self._clear_clip_range()
            dialog.destroy()

        note_entry.bind("<Return>", lambda e: _save())
        dialog.protocol("WM_DELETE_WINDOW", lambda: (self._clear_clip_range(), dialog.destroy()))

        ctk.CTkButton(
            dialog, text="Save Bookmark + Clip",
            font=ctk.CTkFont(size=13, weight="bold"),
            height=36, fg_color="#22c55e", hover_color="#1ea05a",
            text_color="#0a0a0f",
            command=_save,
        ).pack(padx=16, pady=(0, 12))

    # ── Cleanup ──────────────────────────────────────────────────

    def _on_close(self):
        """Clean up mpv resources before closing."""
        # Remove bind_all keybinds so they don't leak to other windows
        for action, tk_key in self._keybinds.items():
            try:
                self.unbind_all(f"<{tk_key}>")
            except Exception:
                pass
        self._playing = False
        if self._update_job:
            self.after_cancel(self._update_job)
            self._update_job = None
        if self._player:
            try:
                self._player.pause = True
                self._player.command("stop")
            except Exception:
                pass
            try:
                self._player.terminate()
            except Exception:
                pass
            self._player = None
        self.destroy()
