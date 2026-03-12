"""Manual game entry window."""

import tkinter as tk
from typing import Callable, Optional

import customtkinter as ctk

from ..constants import COLORS
from .widgets import StarRating, TagSelector


class ManualEntryWindow(ctk.CTkToplevel):
    """Window for manually entering game notes for games that weren't auto-tracked.

    Minimal input form: champion name, win/loss, optional KDA, and review notes.
    """

    def __init__(self, db, on_save: Optional[Callable] = None, *args, **kwargs):
        super().__init__(*args, **kwargs)

        self.db = db
        self.on_save = on_save

        # Window setup
        self.title("Manual Game Entry")
        self.geometry("680x800")
        self.configure(fg_color=COLORS["bg_dark"])
        self.resizable(True, True)
        self.minsize(600, 700)

        # Bring to front
        self.lift()
        self.attributes("-topmost", True)
        self.after(100, lambda: self.attributes("-topmost", False))
        self.focus_force()

        # Main scrollable container
        container = ctk.CTkScrollableFrame(
            self,
            fg_color=COLORS["bg_dark"],
            scrollbar_button_color=COLORS["border"],
        )
        container.pack(fill="both", expand=True, padx=16, pady=16)

        # Header
        ctk.CTkLabel(
            container,
            text="MANUAL GAME ENTRY",
            font=ctk.CTkFont(size=22, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(anchor="w", pady=(0, 4))

        ctk.CTkLabel(
            container,
            text="Add notes for games that weren't automatically tracked",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 16))

        # === BASIC INFO SECTION ===
        ctk.CTkLabel(
            container,
            text="BASIC INFO",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 8))

        # Champion name (required)
        ctk.CTkLabel(
            container,
            text="Champion Name *",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        self.champion_entry = ctk.CTkEntry(
            container,
            height=38,
            font=ctk.CTkFont(size=14),
            fg_color=COLORS["bg_input"],
            text_color=COLORS["text"],
            border_width=2,
            border_color=COLORS["accent_blue"],
            corner_radius=8,
            placeholder_text="e.g., Yasuo, Ahri, Lee Sin...",
        )
        self.champion_entry.pack(fill="x", pady=(0, 12))

        # Win/Loss (required)
        win_loss_frame = ctk.CTkFrame(container, fg_color="transparent")
        win_loss_frame.pack(fill="x", pady=(0, 12))

        ctk.CTkLabel(
            win_loss_frame,
            text="Result *",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(side="left", padx=(0, 12))

        self.result_var = tk.StringVar(value="Loss")

        win_btn = ctk.CTkRadioButton(
            win_loss_frame,
            text="Victory",
            variable=self.result_var,
            value="Win",
            font=ctk.CTkFont(size=13),
            fg_color=COLORS["win_green"],
            hover_color="#1ea05a",
            text_color=COLORS["text"],
        )
        win_btn.pack(side="left", padx=(0, 12))

        loss_btn = ctk.CTkRadioButton(
            win_loss_frame,
            text="Defeat",
            variable=self.result_var,
            value="Loss",
            font=ctk.CTkFont(size=13),
            fg_color=COLORS["loss_red"],
            hover_color="#c43333",
            text_color=COLORS["text"],
        )
        loss_btn.pack(side="left")

        # KDA (optional)
        ctk.CTkLabel(
            container,
            text="KDA (Optional)",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        kda_frame = ctk.CTkFrame(container, fg_color="transparent")
        kda_frame.pack(fill="x", pady=(0, 12))

        kda_fields = []
        for i, label in enumerate(["Kills", "Deaths", "Assists"]):
            field_frame = ctk.CTkFrame(kda_frame, fg_color="transparent")
            field_frame.pack(side="left", expand=True, fill="x", padx=(0, 8 if i < 2 else 0))

            ctk.CTkLabel(
                field_frame,
                text=label,
                font=ctk.CTkFont(size=11),
                text_color=COLORS["text_dim"],
            ).pack(anchor="w")

            entry = ctk.CTkEntry(
                field_frame,
                height=34,
                font=ctk.CTkFont(size=13),
                fg_color=COLORS["bg_input"],
                text_color=COLORS["text"],
                border_width=1,
                border_color=COLORS["border"],
                corner_radius=6,
                placeholder_text="0",
            )
            entry.pack(fill="x")
            entry.insert(0, "0")
            kda_fields.append(entry)

        self.kills_entry, self.deaths_entry, self.assists_entry = kda_fields

        # Game mode (optional)
        ctk.CTkLabel(
            container,
            text="Game Mode (Optional)",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        self.mode_entry = ctk.CTkEntry(
            container,
            height=34,
            font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"],
            text_color=COLORS["text"],
            border_width=1,
            border_color=COLORS["border"],
            corner_radius=6,
            placeholder_text="e.g., Ranked Solo, ARAM, Normal...",
        )
        self.mode_entry.pack(fill="x", pady=(0, 12))
        self.mode_entry.insert(0, "Manual Entry")

        # Separator
        ctk.CTkFrame(container, fg_color=COLORS["border"], height=1).pack(fill="x", pady=12)

        # === REVIEW SECTION ===
        ctk.CTkLabel(
            container,
            text="REVIEW NOTES",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 8))

        # Rating
        rating_row = ctk.CTkFrame(container, fg_color="transparent")
        rating_row.pack(fill="x", pady=(0, 8))

        ctk.CTkLabel(
            rating_row,
            text="Performance Rating",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(side="left")

        self.star_rating = StarRating(rating_row, initial=0)
        self.star_rating.pack(side="right")

        # Tags
        ctk.CTkLabel(
            container,
            text="Tags",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        tags = self.db.get_all_tags()
        self.tag_selector = TagSelector(container, tags, selected=[])
        self.tag_selector.pack(fill="x", pady=(0, 10))

        # What went well
        ctk.CTkLabel(
            container,
            text="What went well?",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        self.went_well = ctk.CTkTextbox(
            container,
            height=60,
            font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"],
            text_color=COLORS["text"],
            border_width=1,
            border_color=COLORS["border"],
            corner_radius=8,
        )
        self.went_well.pack(fill="x", pady=(0, 8))

        # Mistakes
        ctk.CTkLabel(
            container,
            text="What could you improve?",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        self.mistakes = ctk.CTkTextbox(
            container,
            height=60,
            font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"],
            text_color=COLORS["text"],
            border_width=1,
            border_color=COLORS["border"],
            corner_radius=8,
        )
        self.mistakes.pack(fill="x", pady=(0, 8))

        # Focus for next game
        ctk.CTkLabel(
            container,
            text="Focus for next game",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        self.focus_next = ctk.CTkEntry(
            container,
            height=38,
            font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"],
            text_color=COLORS["text"],
            border_width=1,
            border_color=COLORS["border"],
            corner_radius=8,
            placeholder_text="e.g., Track jungle timers, play safer before 6...",
        )
        self.focus_next.pack(fill="x", pady=(0, 8))

        # General notes
        ctk.CTkLabel(
            container,
            text="Additional Notes",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        self.notes = ctk.CTkTextbox(
            container,
            height=80,
            font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"],
            text_color=COLORS["text"],
            border_width=1,
            border_color=COLORS["border"],
            corner_radius=8,
        )
        self.notes.pack(fill="x", pady=(0, 12))

        # === SAVE BUTTON ===
        save_btn = ctk.CTkButton(
            container,
            text="Save Game Entry",
            font=ctk.CTkFont(size=15, weight="bold"),
            height=44,
            corner_radius=8,
            fg_color=COLORS["accent_blue"],
            hover_color="#0077cc",
            command=self._save,
        )
        save_btn.pack(fill="x", pady=(8, 4))

        # Cancel button
        cancel_btn = ctk.CTkButton(
            container,
            text="Cancel",
            font=ctk.CTkFont(size=13),
            height=36,
            corner_radius=8,
            fg_color="transparent",
            hover_color=COLORS["bg_input"],
            text_color=COLORS["text_dim"],
            border_width=1,
            border_color=COLORS["border"],
            command=self.destroy,
        )
        cancel_btn.pack(fill="x", pady=(0, 8))

    def _save(self):
        """Validate input and save the manual entry."""
        # Validate required fields
        champion = self.champion_entry.get().strip()
        if not champion:
            # Show error message
            self.champion_entry.configure(border_color=COLORS["loss_red"])
            self.champion_entry.focus()
            return

        # Get KDA values
        try:
            kills = int(self.kills_entry.get() or 0)
            deaths = int(self.deaths_entry.get() or 0)
            assists = int(self.assists_entry.get() or 0)
        except ValueError:
            kills = deaths = assists = 0

        # Get win/loss
        win = self.result_var.get() == "Win"

        # Get game mode
        game_mode = self.mode_entry.get().strip() or "Manual Entry"

        # Save to database
        game_id = self.db.save_manual_game(
            champion_name=champion,
            win=win,
            kills=kills,
            deaths=deaths,
            assists=assists,
            game_mode=game_mode,
            notes=self.notes.get("1.0", "end-1c").strip(),
            mistakes=self.mistakes.get("1.0", "end-1c").strip(),
            went_well=self.went_well.get("1.0", "end-1c").strip(),
            focus_next=self.focus_next.get().strip(),
            rating=self.star_rating.get(),
            tags=self.tag_selector.get(),
        )

        # Also log to session so it appears in Session Logger
        if game_id and game_id > 0:
            self.db.log_session_game(
                game_id=game_id,
                champion_name=champion,
                win=win,
            )

        # Fire callback if provided
        if self.on_save:
            self.on_save()

        self.destroy()
