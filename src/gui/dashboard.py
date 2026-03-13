"""Startup dashboard — the first thing you see when opening the app.

Shows a yesterday recap, highlights unreviewed games, and provides
quick access to every major feature. Designed to help players start
their session with intention.
"""

import logging
from datetime import datetime, timedelta

import customtkinter as ctk

from ..constants import COLORS, format_duration, format_number
from ..version import __version__
from .game_review import SessionGameReviewWindow

logger = logging.getLogger(__name__)


class DashboardWindow(ctk.CTkToplevel):
    """Session Start dashboard — your pre-game ritual.

    Sections:
    - Yesterday's recap (W/L, key stats, mental avg)
    - Unreviewed games needing attention
    - Quick-launch buttons for all major features
    """

    def __init__(self, db, on_open_history, on_open_losses, on_open_session_logger,
                 on_open_claude_context, on_open_manual_entry, on_minimize,
                 on_open_settings=None, on_open_vod=None, *args, **kwargs):
        super().__init__(*args, **kwargs)

        self.db = db
        self._on_open_history = on_open_history
        self._on_open_losses = on_open_losses
        self._on_open_session_logger = on_open_session_logger
        self._on_open_claude_context = on_open_claude_context
        self._on_open_manual_entry = on_open_manual_entry
        self._on_open_settings = on_open_settings
        self._on_open_vod = on_open_vod
        self._on_minimize = on_minimize
        self._review_popup = None

        self.title("LoL Review")
        self.geometry("760x720")
        self.configure(fg_color=COLORS["bg_dark"])
        self.minsize(640, 560)

        # Bring to front on startup
        self.lift()
        self.attributes("-topmost", True)
        self.after(200, lambda: self.attributes("-topmost", False))

        self._build_ui()

    def _build_ui(self):
        """Build all dashboard sections."""
        outer = ctk.CTkFrame(self, fg_color=COLORS["bg_dark"])
        outer.pack(fill="both", expand=True, padx=16, pady=16)

        # ── Header ──────────────────────────────────────────────
        header = ctk.CTkFrame(outer, fg_color="transparent")
        header.pack(fill="x", pady=(0, 12))

        ctk.CTkLabel(
            header,
            text=f"LoL Review  v{__version__}",
            font=ctk.CTkFont(size=26, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(side="left")

        ctk.CTkButton(
            header,
            text="↻ Refresh",
            font=ctk.CTkFont(size=12),
            height=30,
            width=90,
            corner_radius=6,
            fg_color=COLORS["tag_bg"],
            hover_color="#333344",
            command=self._rebuild_body,
        ).pack(side="right", padx=(0, 6))

        minimize_btn = ctk.CTkButton(
            header,
            text="Minimize to Tray",
            font=ctk.CTkFont(size=12),
            height=30,
            width=130,
            corner_radius=6,
            fg_color=COLORS["tag_bg"],
            hover_color="#333344",
            command=self._minimize_to_tray,
        )
        minimize_btn.pack(side="right")

        # Update banner placeholder — empty until an update is found
        self._update_banner_frame = ctk.CTkFrame(outer, fg_color="transparent", height=0)
        self._update_banner_frame.pack(fill="x")

        # Scrollable body — stored so _rebuild_body can swap it out
        self._body_frame = ctk.CTkScrollableFrame(
            outer,
            fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        self._body_frame.pack(fill="both", expand=True)
        self._outer = outer

        self._populate_body(self._body_frame)

    def _populate_body(self, body):
        """Populate the scrollable body sections."""
        # ── Yesterday recap ─────────────────────────────────────
        self._build_yesterday_recap(body)

        # ── Unreviewed games ────────────────────────────────────
        self._build_unreviewed_section(body)

        # ── Quick actions ───────────────────────────────────────
        self._build_quick_actions(body)

    def _rebuild_body(self):
        """Destroy and rebuild the scrollable body to refresh all data."""
        self._body_frame.destroy()
        self._body_frame = ctk.CTkScrollableFrame(
            self._outer,
            fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        self._body_frame.pack(fill="both", expand=True)
        self._populate_body(self._body_frame)

    # ── Yesterday recap ─────────────────────────────────────────

    def _build_yesterday_recap(self, parent):
        """Show a summary of yesterday's session to jog the player's memory."""
        yesterday = (datetime.now() - timedelta(days=1)).strftime("%Y-%m-%d")
        yesterday_display = (datetime.now() - timedelta(days=1)).strftime("%A, %b %d")

        stats = self.db.get_session_stats_for_date(yesterday)
        games = self.db.get_games_for_date(yesterday)

        section = ctk.CTkFrame(
            parent,
            fg_color=COLORS["bg_card"],
            corner_radius=10,
            border_width=1,
            border_color=COLORS["border"],
        )
        section.pack(fill="x", pady=(0, 12))

        inner = ctk.CTkFrame(section, fg_color="transparent")
        inner.pack(fill="x", padx=16, pady=14)

        # Section heading
        heading_row = ctk.CTkFrame(inner, fg_color="transparent")
        heading_row.pack(fill="x", pady=(0, 10))

        ctk.CTkLabel(
            heading_row,
            text=f"YESTERDAY  —  {yesterday_display}",
            font=ctk.CTkFont(size=13, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(side="left")

        if stats["games"] == 0:
            ctk.CTkLabel(
                inner,
                text="No games played yesterday. Fresh start today!",
                font=ctk.CTkFont(size=13),
                text_color=COLORS["text_dim"],
            ).pack(anchor="w", pady=4)
            return

        # Stats row
        stats_row = ctk.CTkFrame(inner, fg_color="transparent")
        stats_row.pack(fill="x", pady=(0, 10))

        wins = stats["wins"] or 0
        losses = stats["losses"] or 0
        total = stats["games"]
        wr = round(wins / total * 100) if total > 0 else 0

        stat_items = [
            ("Record", f"{wins}W {losses}L", COLORS["win_green"] if wins > losses else COLORS["loss_red"] if losses > wins else COLORS["text"]),
            ("Win Rate", f"{wr}%", COLORS["win_green"] if wr >= 50 else COLORS["loss_red"]),
            ("Games", str(total), COLORS["text"]),
            ("Avg Mental", str(stats.get("avg_mental", "—")), COLORS["accent_blue"]),
            ("Rule Breaks", str(stats.get("rule_breaks", 0)), COLORS["loss_red"] if (stats.get("rule_breaks") or 0) > 0 else COLORS["win_green"]),
        ]

        for label, value, color in stat_items:
            col = ctk.CTkFrame(stats_row, fg_color="transparent")
            col.pack(side="left", expand=True, fill="x")

            ctk.CTkLabel(
                col, text=label,
                font=ctk.CTkFont(size=10, weight="bold"),
                text_color=COLORS["text_dim"],
            ).pack()
            ctk.CTkLabel(
                col, text=value,
                font=ctk.CTkFont(size=18, weight="bold"),
                text_color=color,
            ).pack()

        # Game-by-game recap (compact)
        if games:
            ctk.CTkLabel(
                inner,
                text="GAME LOG",
                font=ctk.CTkFont(size=10, weight="bold"),
                text_color=COLORS["text_dim"],
            ).pack(anchor="w", pady=(6, 4))

            for game in games:
                self._build_compact_game_row(inner, game)

    def _build_compact_game_row(self, parent, game: dict):
        """A compact single-line game summary for the recap."""
        is_win = bool(game.get("win"))
        result_color = COLORS["win_green"] if is_win else COLORS["loss_red"]

        row = ctk.CTkFrame(parent, fg_color=COLORS["bg_input"], corner_radius=6)
        row.pack(fill="x", pady=2)

        inner = ctk.CTkFrame(row, fg_color="transparent")
        inner.pack(fill="x", padx=10, pady=6)

        # Left: result + champion
        result = "W" if is_win else "L"
        champ = game.get("champion_name", "?")
        k, d, a = game.get("kills", 0), game.get("deaths", 0), game.get("assists", 0)
        kda = game.get("kda_ratio", 0)
        duration = format_duration(game.get("game_duration", 0))

        ctk.CTkLabel(
            inner,
            text=f"{result}  {champ}   {k}/{d}/{a} ({kda:.1f})   {duration}",
            font=ctk.CTkFont(size=12),
            text_color=result_color,
        ).pack(side="left")

        # Right: buttons
        right_frame = ctk.CTkFrame(inner, fg_color="transparent")
        right_frame.pack(side="right")

        has_review = bool(
            (game.get("mistakes") or "").strip()
            or (game.get("went_well") or "").strip()
            or (game.get("focus_next") or "").strip()
            or (game.get("rating") or 0) > 0
        )

        review_text = "Edit Review" if has_review else "Review"
        review_color = COLORS["tag_bg"] if has_review else COLORS["accent_blue"]

        ctk.CTkButton(
            right_frame,
            text=review_text,
            font=ctk.CTkFont(size=10),
            height=22, width=80, corner_radius=5,
            fg_color=review_color,
            hover_color="#0077cc",
            command=lambda g=game: self._open_review(g),
        ).pack(side="left", padx=(0, 4))

        game_id = game.get("game_id")
        if game_id and self.db.get_vod(game_id):
            ctk.CTkButton(
                right_frame,
                text="Watch VOD",
                font=ctk.CTkFont(size=10, weight="bold"),
                height=22, width=80, corner_radius=5,
                fg_color=COLORS["accent_gold"], hover_color="#a88432",
                text_color="#0a0a0f",
                command=lambda gid=game_id: self._on_open_vod(gid) if self._on_open_vod else None,
            ).pack(side="left")

    # ── Unreviewed games ────────────────────────────────────────

    def _build_unreviewed_section(self, parent):
        """Show games from the last few days that still need review."""
        unreviewed = self.db.get_unreviewed_games(days=3)

        section = ctk.CTkFrame(
            parent,
            fg_color=COLORS["bg_card"],
            corner_radius=10,
            border_width=1,
            border_color=COLORS["border"],
        )
        section.pack(fill="x", pady=(0, 12))

        inner = ctk.CTkFrame(section, fg_color="transparent")
        inner.pack(fill="x", padx=16, pady=14)

        heading_row = ctk.CTkFrame(inner, fg_color="transparent")
        heading_row.pack(fill="x", pady=(0, 8))

        count = len(unreviewed)
        heading_color = COLORS["loss_red"] if count > 0 else COLORS["win_green"]

        ctk.CTkLabel(
            heading_row,
            text="NEEDS REVIEW",
            font=ctk.CTkFont(size=13, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(side="left")

        ctk.CTkLabel(
            heading_row,
            text=f"{count} game{'s' if count != 1 else ''}",
            font=ctk.CTkFont(size=13, weight="bold"),
            text_color=heading_color,
        ).pack(side="right")

        if count == 0:
            ctk.CTkLabel(
                inner,
                text="All caught up — every recent game has been reviewed!",
                font=ctk.CTkFont(size=13),
                text_color=COLORS["win_green"],
            ).pack(anchor="w", pady=4)
            return

        ctk.CTkLabel(
            inner,
            text="Review these games before you queue up. What happened? What can you improve?",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text_dim"],
            wraplength=650,
        ).pack(anchor="w", pady=(0, 8))

        # Show up to 8 unreviewed games
        for game in unreviewed[:8]:
            self._build_unreviewed_row(inner, game)

        if count > 8:
            ctk.CTkLabel(
                inner,
                text=f"+ {count - 8} more — open Review Losses to see all",
                font=ctk.CTkFont(size=11),
                text_color=COLORS["text_dim"],
            ).pack(anchor="w", pady=(6, 0))

    def _build_unreviewed_row(self, parent, game: dict):
        """A single unreviewed game with key stats."""
        is_win = bool(game.get("win"))
        border_color = COLORS["win_green"] if is_win else COLORS["loss_red"]

        row = ctk.CTkFrame(
            parent,
            fg_color=COLORS["bg_input"],
            corner_radius=6,
            border_width=1,
            border_color=border_color,
        )
        row.pack(fill="x", pady=2)

        inner = ctk.CTkFrame(row, fg_color="transparent")
        inner.pack(fill="x", padx=10, pady=6)

        result = "W" if is_win else "L"
        champ = game.get("champion_name", "?")
        k, d, a = game.get("kills", 0), game.get("deaths", 0), game.get("assists", 0)
        kda = game.get("kda_ratio", 0)
        date = game.get("date_played", "")

        ctk.CTkLabel(
            inner,
            text=f"{result}  {champ}   {k}/{d}/{a} ({kda:.1f})",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=border_color,
        ).pack(side="left")

        right_frame = ctk.CTkFrame(inner, fg_color="transparent")
        right_frame.pack(side="right")

        ctk.CTkLabel(
            right_frame,
            text=date,
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        ).pack(side="left", padx=(0, 8))

        ctk.CTkButton(
            right_frame,
            text="Review",
            font=ctk.CTkFont(size=10),
            height=22, width=70, corner_radius=5,
            fg_color=COLORS["accent_blue"],
            hover_color="#0077cc",
            command=lambda g=game: self._open_review(g),
        ).pack(side="left", padx=(0, 4))

        game_id = game.get("game_id")
        if game_id and self.db.get_vod(game_id):
            ctk.CTkButton(
                right_frame,
                text="Watch VOD",
                font=ctk.CTkFont(size=10, weight="bold"),
                height=22, width=80, corner_radius=5,
                fg_color=COLORS["accent_gold"], hover_color="#a88432",
                text_color="#0a0a0f",
                command=lambda gid=game_id: self._on_open_vod(gid) if self._on_open_vod else None,
            ).pack(side="left")

    # ── Quick actions ───────────────────────────────────────────

    def _build_quick_actions(self, parent):
        """Quick-launch buttons for all major features."""
        section = ctk.CTkFrame(
            parent,
            fg_color=COLORS["bg_card"],
            corner_radius=10,
            border_width=1,
            border_color=COLORS["border"],
        )
        section.pack(fill="x", pady=(0, 12))

        inner = ctk.CTkFrame(section, fg_color="transparent")
        inner.pack(fill="x", padx=16, pady=14)

        ctk.CTkLabel(
            inner,
            text="QUICK ACTIONS",
            font=ctk.CTkFont(size=13, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 10))

        buttons_frame = ctk.CTkFrame(inner, fg_color="transparent")
        buttons_frame.pack(fill="x")

        actions = [
            ("Game History", COLORS["accent_blue"], "#0077cc", self._on_open_history),
            ("Review Losses", COLORS["loss_red"], "#c03030", self._on_open_losses),
            ("Session Logger", COLORS["accent_gold"], "#a88432", self._on_open_session_logger),
            ("Claude Context", "#8b5cf6", "#7040d0", self._on_open_claude_context),
            ("Manual Entry", COLORS["tag_bg"], "#333344", self._on_open_manual_entry),
        ]

        if self._on_open_settings:
            actions.append(("Settings", "#2a2a3a", "#3a3a4a", self._on_open_settings))

        for text, fg, hover, callback in actions:
            text_color = "#0a0a0f" if fg in (COLORS["accent_gold"],) else COLORS["text"]
            btn = ctk.CTkButton(
                buttons_frame,
                text=text,
                font=ctk.CTkFont(size=13),
                height=38,
                corner_radius=8,
                fg_color=fg,
                hover_color=hover,
                text_color=text_color,
                command=callback,
            )
            btn.pack(side="left", padx=(0, 8), expand=True, fill="x")

        # Tip text
        streak = self.db.get_adherence_streak()
        last_focus = self.db.get_last_review_focus()
        focus_text = last_focus.get("focus_next", "").strip()

        tips_frame = ctk.CTkFrame(inner, fg_color="transparent")
        tips_frame.pack(fill="x", pady=(12, 0))

        if streak > 0:
            ctk.CTkLabel(
                tips_frame,
                text=f"Clean play streak: {streak} day{'s' if streak != 1 else ''}",
                font=ctk.CTkFont(size=12),
                text_color=COLORS["win_green"],
            ).pack(anchor="w")

        if focus_text:
            ctk.CTkLabel(
                tips_frame,
                text=f"Last focus: {focus_text}",
                font=ctk.CTkFont(size=12),
                text_color=COLORS["accent_blue"],
                wraplength=650,
            ).pack(anchor="w", pady=(4, 0))

    # ── Review helper ──────────────────────────────────────────

    def _open_review(self, game: dict):
        """Open a review popup for a game from the dashboard."""
        if self._review_popup and self._review_popup.winfo_exists():
            self._review_popup.destroy()

        game_id = game.get("game_id")
        session_entry = {
            "game_id": game_id,
            "champion_name": game.get("champion_name"),
            "win": game.get("win", 0),
            "mental_rating": 5,
        }

        vod_info = self.db.get_vod(game_id) if game_id else None
        has_vod = vod_info is not None
        bookmark_count = self.db.get_bookmark_count(game_id) if has_vod else 0

        self._review_popup = SessionGameReviewWindow(
            db=self.db,
            session_entry=session_entry,
            game_data=game,
            on_save=self._rebuild_body,
            on_open_vod=self._on_open_vod,
            has_vod=has_vod,
            bookmark_count=bookmark_count,
        )

    # ── Actions ─────────────────────────────────────────────────

    def show_update_banner(self, text: str, color: str = "#a0a0b0"):
        """Show or update a status-only banner (updates are fully automatic)."""
        try:
            if hasattr(self, "_update_label") and self._update_label:
                # Banner already exists — just update the text
                self._update_label.configure(text=text, text_color=color)
                return

            banner = ctk.CTkFrame(
                self._update_banner_frame,
                fg_color="#1a2a1a",
                corner_radius=8,
                border_width=1,
                border_color="#2d8a4e",
            )
            banner.pack(fill="x", pady=(0, 10))

            self._update_label = ctk.CTkLabel(
                banner,
                text=text,
                font=ctk.CTkFont(size=13, weight="bold"),
                text_color=color,
            )
            self._update_label.pack(padx=12, pady=10)

        except Exception as e:
            logger.warning(f"Failed to show update banner: {e}")

    def _minimize_to_tray(self):
        """Hide this window and run in the background."""
        self.withdraw()
        if self._on_minimize:
            self._on_minimize()
