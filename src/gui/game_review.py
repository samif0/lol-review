"""Session game review window."""

import json
from typing import Callable, Optional

import customtkinter as ctk

from ..constants import COLORS
from .widgets import StarRating, TagSelector


class SessionGameReviewWindow(ctk.CTkToplevel):
    """Lightweight review popup for a game from the session logger.

    Lets you review (or edit a review for) a game that was auto-tracked
    but not fully reviewed at the time. Updates both the session_log
    (mental rating, improvement note) and the games table (mistakes,
    went_well, focus_next, rating, tags).
    """

    def __init__(
        self,
        db,
        session_entry: dict,
        game_data: Optional[dict] = None,
        on_save: Optional[Callable] = None,
        *args,
        **kwargs,
    ):
        super().__init__(*args, **kwargs)

        self.db = db
        self.session_entry = session_entry
        self.game_data = game_data or {}
        self.on_save = on_save

        champ = session_entry.get("champion_name", "Unknown")
        is_win = bool(session_entry.get("win"))
        result = "Victory" if is_win else "Defeat"
        result_color = COLORS["win_green"] if is_win else COLORS["loss_red"]

        self.title(f"Review — {champ} ({result})")
        self.geometry("620x750")
        self.configure(fg_color=COLORS["bg_dark"])
        self.resizable(True, True)
        self.minsize(500, 600)

        self.lift()
        self.attributes("-topmost", True)
        self.after(100, lambda: self.attributes("-topmost", False))
        self.focus_force()

        container = ctk.CTkScrollableFrame(
            self,
            fg_color=COLORS["bg_dark"],
            scrollbar_button_color=COLORS["border"],
        )
        container.pack(fill="both", expand=True, padx=16, pady=16)

        # Header
        header = ctk.CTkFrame(container, fg_color="transparent")
        header.pack(fill="x", pady=(0, 12))

        ctk.CTkLabel(
            header,
            text=champ,
            font=ctk.CTkFont(size=24, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(side="left")

        ctk.CTkLabel(
            header,
            text=result,
            font=ctk.CTkFont(size=18, weight="bold"),
            text_color=result_color,
        ).pack(side="right")

        # KDA if available from games table
        if self.game_data:
            k = self.game_data.get("kills", 0)
            d = self.game_data.get("deaths", 0)
            a = self.game_data.get("assists", 0)
            kda = self.game_data.get("kda_ratio", 0)
            if k or d or a:
                ctk.CTkLabel(
                    container,
                    text=f"{k}/{d}/{a}  ({kda:.2f} KDA)",
                    font=ctk.CTkFont(size=14),
                    text_color=COLORS["text"],
                ).pack(anchor="w", pady=(0, 8))

        ctk.CTkFrame(
            container, fg_color=COLORS["border"], height=1
        ).pack(fill="x", pady=8)

        # === MENTAL RATING (session log field) ===
        mental_frame = ctk.CTkFrame(container, fg_color="transparent")
        mental_frame.pack(fill="x", pady=(4, 8))

        ctk.CTkLabel(
            mental_frame,
            text="Mental Rating (1-10)",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(side="left")

        self.mental_label = ctk.CTkLabel(
            mental_frame,
            text=str(session_entry.get("mental_rating", 5)),
            font=ctk.CTkFont(size=18, weight="bold"),
            text_color=COLORS["accent_blue"],
        )
        self.mental_label.pack(side="right", padx=(0, 8))

        self.mental_slider = ctk.CTkSlider(
            container,
            from_=1,
            to=10,
            number_of_steps=9,
            command=self._on_mental_change,
            button_color=COLORS["accent_blue"],
            button_hover_color="#0077cc",
            progress_color=COLORS["accent_blue"],
        )
        self.mental_slider.set(session_entry.get("mental_rating", 5))
        self.mental_slider.pack(fill="x", pady=(0, 12))

        # === PERFORMANCE RATING (games table field) ===
        rating_row = ctk.CTkFrame(container, fg_color="transparent")
        rating_row.pack(fill="x", pady=(0, 8))

        ctk.CTkLabel(
            rating_row,
            text="Performance Rating",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(side="left")

        self.star_rating = StarRating(
            rating_row, initial=self.game_data.get("rating", 0)
        )
        self.star_rating.pack(side="right")

        # === TAGS ===
        ctk.CTkLabel(
            container,
            text="Tags",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        all_tags = self.db.get_all_tags()
        existing_tags = json.loads(self.game_data.get("tags", "[]")) if isinstance(
            self.game_data.get("tags"), str
        ) else self.game_data.get("tags", [])
        self.tag_selector = TagSelector(container, all_tags, selected=existing_tags)
        self.tag_selector.pack(fill="x", pady=(0, 10))

        # === WHAT WENT WELL ===
        ctk.CTkLabel(
            container,
            text="What went well?",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        self.went_well = ctk.CTkTextbox(
            container, height=55, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
        )
        self.went_well.pack(fill="x", pady=(0, 8))
        self.went_well.insert("1.0", self.game_data.get("went_well", ""))

        # === MISTAKES ===
        ctk.CTkLabel(
            container,
            text="What could you improve?",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        self.mistakes = ctk.CTkTextbox(
            container, height=55, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
        )
        self.mistakes.pack(fill="x", pady=(0, 8))
        self.mistakes.insert("1.0", self.game_data.get("mistakes", ""))

        # === FOCUS NEXT ===
        ctk.CTkLabel(
            container,
            text="Focus for next game",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        self.focus_next = ctk.CTkEntry(
            container, height=38, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
            placeholder_text="e.g., Track jungle timers, play safer before 6...",
        )
        self.focus_next.pack(fill="x", pady=(0, 8))
        if self.game_data.get("focus_next"):
            self.focus_next.insert(0, self.game_data["focus_next"])

        # === IMPROVEMENT NOTE (session log field — one-liner for quick context) ===
        ctk.CTkLabel(
            container,
            text="Quick improvement note (shown in session log & Claude context)",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        self.improvement_note = ctk.CTkEntry(
            container, height=38, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
            placeholder_text="e.g., Died to 3 ganks, need to ward better",
        )
        self.improvement_note.pack(fill="x", pady=(0, 12))
        if session_entry.get("improvement_note"):
            self.improvement_note.insert(0, session_entry["improvement_note"])

        # === SAVE BUTTON ===
        save_btn = ctk.CTkButton(
            container,
            text="Save Review",
            font=ctk.CTkFont(size=15, weight="bold"),
            height=44,
            corner_radius=8,
            fg_color=COLORS["accent_blue"],
            hover_color="#0077cc",
            command=self._save,
        )
        save_btn.pack(fill="x", pady=(8, 4))

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

    def _on_mental_change(self, value):
        """Update mental label when slider changes."""
        rating = int(value)
        self.mental_label.configure(text=str(rating))
        if rating >= 8:
            color = COLORS["win_green"]
        elif rating >= 5:
            color = COLORS["accent_blue"]
        else:
            color = COLORS["loss_red"]
        self.mental_label.configure(text_color=color)

    def _save(self):
        """Save review data to both session_log and games tables."""
        game_id = self.session_entry.get("game_id")
        mental = int(self.mental_slider.get())
        improvement = self.improvement_note.get().strip()

        # Collect review fields before any DB writes
        rating = self.star_rating.get()
        tags = self.tag_selector.get()
        mistakes = self.mistakes.get("1.0", "end-1c").strip()
        went_well = self.went_well.get("1.0", "end-1c").strip()
        focus_next = self.focus_next.get().strip()

        # Update games table review fields first (most important save)
        if game_id is not None:
            self.db.update_review(
                game_id=game_id,
                notes="",
                rating=rating,
                tags=tags,
                mistakes=mistakes,
                went_well=went_well,
                focus_next=focus_next,
                improvement_note=improvement,
            )

        # Only update session_log if this game already has an entry there.
        # Without this check, reviewing a past game from Review Losses
        # would create a bogus "today" entry for a game played days ago.
        if game_id is not None and self.db.has_session_log_entry(game_id):
            self.db.log_session_game(
                game_id=game_id,
                champion_name=self.session_entry.get("champion_name", ""),
                win=bool(self.session_entry.get("win")),
                mental_rating=mental,
                improvement_note=improvement,
            )

        if self.on_save:
            self.on_save()

        self.destroy()
