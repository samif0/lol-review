"""Session rules overlay window for tracking daily performance."""

import customtkinter as ctk

from ..constants import COLORS


class SessionRulesOverlay(ctk.CTkToplevel):
    """Persistent always-on-top overlay showing session rules and tracking.

    Features:
    - Session W/L record (today's games)
    - Two-loss stop rule reminder with visual warning
    - Mental check rating slider (1-10) to set before each game
    - Flashing red "STOP — WALK AWAY" on 2 consecutive losses
    """

    def __init__(self, db, *args, **kwargs):
        super().__init__(*args, **kwargs)

        self.db = db

        # Window setup — small, compact, always on top
        self.title("Session Rules")
        self.geometry("320x420")
        self.configure(fg_color=COLORS["bg_dark"])
        self.resizable(False, False)

        # Always on top
        self.attributes("-topmost", True)

        # Optional: Make it slightly transparent for less distraction
        self.attributes("-alpha", 0.95)

        # Track flash state for consecutive loss warning
        self.flash_active = False
        self.flash_state = False

        # Main container
        main_frame = ctk.CTkFrame(self, fg_color=COLORS["bg_dark"])
        main_frame.pack(fill="both", expand=True, padx=10, pady=10)

        # Header
        ctk.CTkLabel(
            main_frame,
            text="SESSION RULES",
            font=ctk.CTkFont(size=16, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(pady=(0, 10))

        # Session record section
        record_frame = ctk.CTkFrame(
            main_frame,
            fg_color=COLORS["bg_card"],
            corner_radius=8,
            border_width=1,
            border_color=COLORS["border"],
        )
        record_frame.pack(fill="x", pady=(0, 10))

        ctk.CTkLabel(
            record_frame,
            text="TODAY'S SESSION",
            font=ctk.CTkFont(size=11, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(pady=(8, 4))

        self.record_label = ctk.CTkLabel(
            record_frame,
            text="0W - 0L",
            font=ctk.CTkFont(size=24, weight="bold"),
            text_color=COLORS["text"],
        )
        self.record_label.pack(pady=(0, 8))

        # Two-loss rule reminder
        rule_frame = ctk.CTkFrame(
            main_frame,
            fg_color=COLORS["bg_card"],
            corner_radius=8,
            border_width=1,
            border_color=COLORS["border"],
        )
        rule_frame.pack(fill="x", pady=(0, 10))

        ctk.CTkLabel(
            rule_frame,
            text="TWO-LOSS RULE",
            font=ctk.CTkFont(size=11, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(pady=(8, 4))

        ctk.CTkLabel(
            rule_frame,
            text="Stop after 2 consecutive losses.\nTake a break, reset your mental.",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text"],
            justify="center",
        ).pack(pady=(0, 8))

        # Consecutive loss tracker
        self.loss_streak_frame = ctk.CTkFrame(
            rule_frame,
            fg_color=COLORS["bg_input"],
            corner_radius=6,
        )
        self.loss_streak_frame.pack(fill="x", padx=12, pady=(0, 10))

        self.loss_streak_label = ctk.CTkLabel(
            self.loss_streak_frame,
            text="Consecutive Losses: 0",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        )
        self.loss_streak_label.pack(pady=8)

        # Warning label (hidden by default, shown on 2+ losses)
        self.warning_label = ctk.CTkLabel(
            main_frame,
            text="STOP — WALK AWAY",
            font=ctk.CTkFont(size=20, weight="bold"),
            text_color="#ffffff",
            fg_color=COLORS["loss_red"],
            corner_radius=8,
            height=60,
        )
        # Don't pack yet — will show/hide dynamically

        # Mental check section
        mental_frame = ctk.CTkFrame(
            main_frame,
            fg_color=COLORS["bg_card"],
            corner_radius=8,
            border_width=1,
            border_color=COLORS["border"],
        )
        mental_frame.pack(fill="x", pady=(0, 10))

        ctk.CTkLabel(
            mental_frame,
            text="MENTAL CHECK",
            font=ctk.CTkFont(size=11, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(pady=(8, 4))

        ctk.CTkLabel(
            mental_frame,
            text="Rate your mental state (1-10)",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        ).pack(pady=(0, 6))

        self.mental_rating_label = ctk.CTkLabel(
            mental_frame,
            text="5",
            font=ctk.CTkFont(size=28, weight="bold"),
            text_color=COLORS["accent_blue"],
        )
        self.mental_rating_label.pack()

        self.mental_slider = ctk.CTkSlider(
            mental_frame,
            from_=1,
            to=10,
            number_of_steps=9,
            command=self._on_mental_slider_change,
            button_color=COLORS["accent_blue"],
            button_hover_color="#0077cc",
            progress_color=COLORS["accent_blue"],
        )
        self.mental_slider.set(5)
        self.mental_slider.pack(fill="x", padx=12, pady=(6, 10))

        # Refresh button
        refresh_btn = ctk.CTkButton(
            main_frame,
            text="Refresh Session Data",
            font=ctk.CTkFont(size=12),
            height=32,
            corner_radius=6,
            fg_color=COLORS["accent_blue"],
            hover_color="#0077cc",
            command=self._refresh_session_data,
        )
        refresh_btn.pack(fill="x")

        # Initial data load
        self._refresh_session_data()

        # Auto-refresh every 30 seconds
        self.after(30000, self._auto_refresh)

    def _on_mental_slider_change(self, value):
        """Update mental rating label when slider changes."""
        rating = int(value)
        self.mental_rating_label.configure(text=str(rating))

        # Color code the rating
        if rating >= 8:
            color = COLORS["win_green"]
        elif rating >= 5:
            color = COLORS["accent_blue"]
        else:
            color = COLORS["loss_red"]

        self.mental_rating_label.configure(text_color=color)

    def _refresh_session_data(self):
        """Pull today's games from database and update UI."""
        todays_games = self.db.get_todays_games()

        # Calculate wins/losses
        wins = sum(1 for g in todays_games if g.get("win"))
        losses = len(todays_games) - wins

        # Update record label
        self.record_label.configure(text=f"{wins}W - {losses}L")

        # Color code based on performance
        if wins > losses:
            record_color = COLORS["win_green"]
        elif losses > wins:
            record_color = COLORS["loss_red"]
        else:
            record_color = COLORS["text"]

        self.record_label.configure(text_color=record_color)

        # Calculate consecutive losses
        consecutive_losses = self._calculate_consecutive_losses(todays_games)

        self.loss_streak_label.configure(text=f"Consecutive Losses: {consecutive_losses}")

        # Update loss streak color
        if consecutive_losses == 0:
            streak_color = COLORS["win_green"]
        elif consecutive_losses == 1:
            streak_color = COLORS["star_active"]
        else:
            streak_color = COLORS["loss_red"]

        self.loss_streak_label.configure(text_color=streak_color)

        # Show/hide warning based on consecutive losses
        if consecutive_losses >= 2:
            self._show_warning()
        else:
            self._hide_warning()

    def _calculate_consecutive_losses(self, games: list[dict]) -> int:
        """Calculate consecutive losses from the end of the game list."""
        if not games:
            return 0

        consecutive = 0
        # Games are ordered oldest to newest, so check from the end
        for game in reversed(games):
            if not game.get("win"):
                consecutive += 1
            else:
                break

        return consecutive

    def _show_warning(self):
        """Show the STOP warning and start flashing."""
        if not self.flash_active:
            self.flash_active = True
            self.warning_label.pack(fill="x", pady=(0, 10))
            self._flash_warning()

    def _hide_warning(self):
        """Hide the STOP warning and stop flashing."""
        if self.flash_active:
            self.flash_active = False
            self.warning_label.pack_forget()

    def _flash_warning(self):
        """Flash the warning label between red and darker red."""
        if not self.flash_active:
            return

        if self.flash_state:
            self.warning_label.configure(fg_color=COLORS["loss_red"])
        else:
            self.warning_label.configure(fg_color="#8b0000")  # Darker red

        self.flash_state = not self.flash_state

        # Continue flashing every 500ms
        self.after(500, self._flash_warning)

    def _auto_refresh(self):
        """Auto-refresh session data periodically."""
        if self.winfo_exists():
            self._refresh_session_data()
            self.after(30000, self._auto_refresh)

    def get_mental_rating(self) -> int:
        """Get current mental check rating."""
        return int(self.mental_slider.get())
