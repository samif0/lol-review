"""Session logger window for tracking daily games."""

from datetime import datetime, timedelta

import customtkinter as ctk

from ..constants import COLORS
from .claude_context import ClaudeContextWindow
from .game_review import SessionGameReviewWindow


class SessionLoggerWindow(ctk.CTkToplevel):
    """Session Logger window — tracks daily games with mental ratings.

    Features:
    - Today's session stats (games, W/L, avg mental, rule breaks)
    - Scrollable list of today's logged games
    - Mental rating entry for each game
    - Visual rule-break flagging
    """

    def __init__(self, db, *args, **kwargs):
        super().__init__(*args, **kwargs)

        self.db = db
        self._selected_date = datetime.now().strftime("%Y-%m-%d")

        self.title("LoL Review — Session Logger")
        self.geometry("700x700")
        self.configure(fg_color=COLORS["bg_dark"])
        self.minsize(600, 550)

        self.lift()
        self.attributes("-topmost", True)
        self.after(100, lambda: self.attributes("-topmost", False))

        main_frame = ctk.CTkFrame(self, fg_color=COLORS["bg_dark"])
        main_frame.pack(fill="both", expand=True, padx=12, pady=12)

        # Header
        header = ctk.CTkFrame(main_frame, fg_color="transparent")
        header.pack(fill="x", pady=(0, 8))

        ctk.CTkLabel(
            header,
            text="SESSION LOGGER",
            font=ctk.CTkFont(size=22, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(side="left")

        ctk.CTkLabel(
            header,
            text="Track your mental state and adherence",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text_dim"],
        ).pack(side="left", padx=(12, 0))

        # Date navigation bar
        date_nav = ctk.CTkFrame(main_frame, fg_color=COLORS["bg_card"], corner_radius=8)
        date_nav.pack(fill="x", pady=(0, 12))

        nav_inner = ctk.CTkFrame(date_nav, fg_color="transparent")
        nav_inner.pack(padx=12, pady=8)

        self._prev_btn = ctk.CTkButton(
            nav_inner, text="◀  Prev Day", font=ctk.CTkFont(size=12),
            width=100, height=28, corner_radius=6,
            fg_color=COLORS["tag_bg"], hover_color="#444455",
            command=self._go_prev_day,
        )
        self._prev_btn.pack(side="left", padx=(0, 12))

        self._date_label = ctk.CTkLabel(
            nav_inner, text="", font=ctk.CTkFont(size=15, weight="bold"),
            text_color=COLORS["text"],
        )
        self._date_label.pack(side="left", padx=12)

        self._next_btn = ctk.CTkButton(
            nav_inner, text="Next Day  ▶", font=ctk.CTkFont(size=12),
            width=100, height=28, corner_radius=6,
            fg_color=COLORS["tag_bg"], hover_color="#444455",
            command=self._go_next_day,
        )
        self._next_btn.pack(side="left", padx=(12, 12))

        self._today_btn = ctk.CTkButton(
            nav_inner, text="Today", font=ctk.CTkFont(size=12),
            width=70, height=28, corner_radius=6,
            fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._go_today,
        )
        self._today_btn.pack(side="left")

        # Stats cards row
        self.stats_frame = ctk.CTkFrame(
            main_frame,
            fg_color=COLORS["bg_card"],
            corner_radius=8,
            border_width=1,
            border_color=COLORS["border"],
        )
        self.stats_frame.pack(fill="x", pady=(0, 12))

        self.stats_inner = ctk.CTkFrame(self.stats_frame, fg_color="transparent")
        self.stats_inner.pack(fill="x", padx=12, pady=10)

        # Create stat display labels
        self._stat_labels = {}
        stat_names = [
            ("Games", "games"),
            ("Wins", "wins"),
            ("Losses", "losses"),
            ("Avg Mental", "avg_mental"),
            ("Rule Breaks", "rule_breaks"),
            ("Adherence Streak", "streak"),
        ]

        for label_text, key in stat_names:
            col = ctk.CTkFrame(self.stats_inner, fg_color="transparent")
            col.pack(side="left", expand=True, fill="x")

            ctk.CTkLabel(
                col,
                text=label_text,
                font=ctk.CTkFont(size=10, weight="bold"),
                text_color=COLORS["text_dim"],
            ).pack()

            value_label = ctk.CTkLabel(
                col,
                text="0",
                font=ctk.CTkFont(size=20, weight="bold"),
                text_color=COLORS["text"],
            )
            value_label.pack()
            self._stat_labels[key] = value_label

        # Scrollable game log
        self._games_heading = ctk.CTkLabel(
            main_frame,
            text="GAMES",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text_dim"],
        )
        self._games_heading.pack(anchor="w", pady=(4, 6))

        self.scroll_frame = ctk.CTkScrollableFrame(
            main_frame,
            fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        self.scroll_frame.pack(fill="both", expand=True, pady=(0, 10))

        # Bottom buttons row
        btn_frame = ctk.CTkFrame(main_frame, fg_color="transparent")
        btn_frame.pack(fill="x")

        refresh_btn = ctk.CTkButton(
            btn_frame,
            text="Refresh",
            font=ctk.CTkFont(size=13),
            height=36,
            corner_radius=6,
            fg_color=COLORS["accent_blue"],
            hover_color="#0077cc",
            command=self._refresh,
        )
        refresh_btn.pack(side="left", padx=(0, 8))

        context_btn = ctk.CTkButton(
            btn_frame,
            text="Open Claude Context Generator",
            font=ctk.CTkFont(size=13),
            height=36,
            corner_radius=6,
            fg_color=COLORS["accent_gold"],
            hover_color="#a88432",
            text_color="#0a0a0f",
            command=self._open_context_generator,
        )
        context_btn.pack(side="left")

        self._context_window = None

        # Initial data load
        self._refresh()

        # Auto-refresh every 30s
        self.after(30000, self._auto_refresh)

    def _refresh(self):
        """Reload session data for the selected date."""
        # Update date display
        is_today = self._selected_date == datetime.now().strftime("%Y-%m-%d")
        try:
            date_obj = datetime.strptime(self._selected_date, "%Y-%m-%d")
            friendly = date_obj.strftime("%A, %b %d")
        except ValueError:
            friendly = self._selected_date

        date_text = f"{friendly}  (Today)" if is_today else friendly
        self._date_label.configure(text=date_text)
        self._games_heading.configure(
            text="TODAY'S GAMES" if is_today else f"GAMES — {friendly}",
        )

        # Disable next button if already on today
        if is_today:
            self._next_btn.configure(state="disabled", fg_color=COLORS["border"])
            self._today_btn.configure(state="disabled", fg_color=COLORS["border"])
        else:
            self._next_btn.configure(state="normal", fg_color=COLORS["tag_bg"])
            self._today_btn.configure(state="normal", fg_color=COLORS["accent_blue"])

        # Update stats for the selected date
        stats = self.db.get_session_stats_for_date(self._selected_date)
        streak = self.db.get_adherence_streak()

        self._stat_labels["games"].configure(text=str(stats["games"]))

        wins = stats["wins"] or 0
        losses = stats["losses"] or 0
        self._stat_labels["wins"].configure(
            text=str(wins),
            text_color=COLORS["win_green"] if wins > 0 else COLORS["text"],
        )
        self._stat_labels["losses"].configure(
            text=str(losses),
            text_color=COLORS["loss_red"] if losses > 0 else COLORS["text"],
        )
        self._stat_labels["avg_mental"].configure(
            text=f"{stats['avg_mental']}" if stats["games"] > 0 else "—",
        )

        rule_breaks = stats["rule_breaks"] or 0
        self._stat_labels["rule_breaks"].configure(
            text=str(rule_breaks),
            text_color=COLORS["loss_red"] if rule_breaks > 0 else COLORS["win_green"],
        )
        self._stat_labels["streak"].configure(
            text=str(streak),
            text_color=COLORS["win_green"] if streak >= 3 else COLORS["text"],
        )

        # Reload game entries
        for widget in self.scroll_frame.winfo_children():
            widget.destroy()

        entries = self.db.get_session_log_for_date(self._selected_date)
        if not entries:
            empty_msg = (
                "No games logged today.\nGames are logged automatically when detected,\n"
                "or you can use Manual Entry from the tray menu."
                if is_today
                else "No games logged on this date."
            )
            ctk.CTkLabel(
                self.scroll_frame,
                text=empty_msg,
                font=ctk.CTkFont(size=13),
                text_color=COLORS["text_dim"],
                justify="center",
            ).pack(pady=30)
            return

        for entry in entries:
            self._build_entry_row(entry)

    def _build_entry_row(self, entry: dict):
        """Build a single session log entry row with review button."""
        is_win = bool(entry.get("win"))
        border_color = COLORS["win_green"] if is_win else COLORS["loss_red"]
        broke_rule = bool(entry.get("rule_broken"))

        # Check if this game has been reviewed in the games table
        game_data = self.db.get_game(entry.get("game_id")) if entry.get("game_id") else None
        has_review = False
        if game_data:
            has_review = bool(
                game_data.get("mistakes", "").strip()
                or game_data.get("went_well", "").strip()
                or game_data.get("focus_next", "").strip()
                or game_data.get("rating", 0) > 0
            )

        row = ctk.CTkFrame(
            self.scroll_frame,
            fg_color=COLORS["bg_card"],
            corner_radius=8,
            border_width=2,
            border_color="#ff0000" if broke_rule else border_color,
        )
        row.pack(fill="x", pady=4, padx=4)

        inner = ctk.CTkFrame(row, fg_color="transparent")
        inner.pack(fill="x", padx=12, pady=10)

        # Left: champion + result
        left = ctk.CTkFrame(inner, fg_color="transparent")
        left.pack(side="left", fill="x", expand=True)

        result = "W" if is_win else "L"
        result_color = COLORS["win_green"] if is_win else COLORS["loss_red"]

        top_text = f"{result}  {entry.get('champion_name', '?')}"
        if broke_rule:
            top_text += "  [RULE BREAK]"

        ctk.CTkLabel(
            left,
            text=top_text,
            font=ctk.CTkFont(size=14, weight="bold"),
            text_color="#ff0000" if broke_rule else result_color,
        ).pack(anchor="w")

        # Improvement note if present
        note = entry.get("improvement_note", "").strip()
        if note:
            ctk.CTkLabel(
                left,
                text=note,
                font=ctk.CTkFont(size=11),
                text_color=COLORS["text_dim"],
                wraplength=400,
                justify="left",
            ).pack(anchor="w")

        # Reviewed indicator
        if has_review:
            ctk.CTkLabel(
                left,
                text="Reviewed",
                font=ctk.CTkFont(size=10),
                text_color=COLORS["win_green"],
            ).pack(anchor="w")

        # Right: mental rating + review button
        right = ctk.CTkFrame(inner, fg_color="transparent")
        right.pack(side="right")

        mental = entry.get("mental_rating", 5)
        if mental >= 8:
            mental_color = COLORS["win_green"]
        elif mental >= 5:
            mental_color = COLORS["accent_blue"]
        else:
            mental_color = COLORS["loss_red"]

        ctk.CTkLabel(
            right,
            text=f"Mental: {mental}/10",
            font=ctk.CTkFont(size=14, weight="bold"),
            text_color=mental_color,
        ).pack(anchor="e")

        # Review / Edit Review button
        review_btn_text = "Edit Review" if has_review else "Review"
        review_btn_color = COLORS["tag_bg"] if has_review else COLORS["accent_blue"]

        review_btn = ctk.CTkButton(
            right,
            text=review_btn_text,
            font=ctk.CTkFont(size=11),
            height=26,
            width=90,
            corner_radius=6,
            fg_color=review_btn_color,
            hover_color="#0077cc",
            command=lambda e=entry, g=game_data: self._open_game_review(e, g),
        )
        review_btn.pack(anchor="e", pady=(4, 0))

    def _go_prev_day(self):
        """Navigate to the previous day."""
        date_obj = datetime.strptime(self._selected_date, "%Y-%m-%d")
        date_obj -= timedelta(days=1)
        self._selected_date = date_obj.strftime("%Y-%m-%d")
        self._refresh()

    def _go_next_day(self):
        """Navigate to the next day (capped at today)."""
        today = datetime.now().strftime("%Y-%m-%d")
        date_obj = datetime.strptime(self._selected_date, "%Y-%m-%d")
        date_obj += timedelta(days=1)
        new_date = date_obj.strftime("%Y-%m-%d")
        if new_date <= today:
            self._selected_date = new_date
            self._refresh()

    def _go_today(self):
        """Jump back to today."""
        self._selected_date = datetime.now().strftime("%Y-%m-%d")
        self._refresh()

    def _open_game_review(self, session_entry: dict, game_data: dict):
        """Open a review popup for a specific game."""
        if getattr(self, "_review_popup", None) and self._review_popup.winfo_exists():
            self._review_popup.destroy()

        self._review_popup = SessionGameReviewWindow(
            db=self.db,
            session_entry=session_entry,
            game_data=game_data,
            on_save=self._refresh,
        )

    def _open_context_generator(self):
        """Open the Claude Context Generator window."""
        if self._context_window and self._context_window.winfo_exists():
            self._context_window.lift()
            return
        self._context_window = ClaudeContextWindow(db=self.db)

    def _auto_refresh(self):
        """Auto-refresh periodically."""
        if self.winfo_exists():
            self._refresh()
            self.after(30000, self._auto_refresh)
