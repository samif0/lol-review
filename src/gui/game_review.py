"""Session game review window."""

import json
from typing import Callable, Optional

import customtkinter as ctk

from ..constants import (
    COLORS, MENTAL_EXCELLENT_THRESHOLD, MENTAL_DECENT_THRESHOLD,
    MENTAL_RATING_MIN, MENTAL_RATING_MAX, MENTAL_RATING_DEFAULT,
    MENTAL_RATING_STEPS,
)
from .widgets import StarRating, TagSelector


class SessionGameReviewPanel(ctk.CTkFrame):
    """Inline review panel for a game from the session logger.

    Lets you review (or edit a review for) a game that was auto-tracked
    but not fully reviewed at the time. Updates both the session_log
    (mental rating, improvement note) and the games table (mistakes,
    went_well, focus_next, rating, tags).
    """

    def __init__(
        self,
        parent,
        db,
        session_entry: dict,
        game_data: Optional[dict] = None,
        on_save: Optional[Callable] = None,
        on_open_vod: Optional[Callable] = None,
        on_back: Optional[Callable] = None,
        has_vod: bool = False,
        bookmark_count: int = 0,
        **kwargs,
    ):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kwargs)

        self.db = db
        self.session_entry = session_entry
        self.game_data = game_data or {}
        self.on_save = on_save
        self._on_open_vod = on_open_vod
        self._on_back = on_back
        self._has_vod = has_vod
        self._bookmark_count = bookmark_count

        champ = session_entry.get("champion_name", "Unknown")
        is_win = bool(session_entry.get("win"))
        result = "Victory" if is_win else "Defeat"
        result_color = COLORS["win_green"] if is_win else COLORS["loss_red"]

        # Back button header
        back_row = ctk.CTkFrame(self, fg_color="transparent")
        back_row.pack(fill="x", padx=16, pady=(12, 0))
        ctk.CTkButton(
            back_row, text="← Back", width=80, height=30,
            font=ctk.CTkFont(size=12), corner_radius=6,
            fg_color=COLORS["tag_bg"], hover_color="#333344",
            text_color=COLORS["text"],
            command=self._go_back,
        ).pack(side="left")
        ctk.CTkLabel(
            back_row, text=f"Review — {champ} ({result})",
            font=ctk.CTkFont(size=16, weight="bold"),
            text_color=COLORS["text"],
        ).pack(side="left", padx=(12, 0))

        container = ctk.CTkScrollableFrame(
            self,
            fg_color=COLORS["bg_dark"],
            scrollbar_button_color=COLORS["border"],
        )
        container.pack(fill="both", expand=True, padx=16, pady=(8, 16))

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

        # === VOD BUTTON (if a recording is linked) ===
        if self._has_vod:
            self._build_vod_section(container)

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
            text=str(session_entry.get("mental_rating", MENTAL_RATING_DEFAULT)),
            font=ctk.CTkFont(size=18, weight="bold"),
            text_color=COLORS["accent_blue"],
        )
        self.mental_label.pack(side="right", padx=(0, 8))

        self.mental_slider = ctk.CTkSlider(
            container,
            from_=MENTAL_RATING_MIN,
            to=MENTAL_RATING_MAX,
            number_of_steps=MENTAL_RATING_STEPS,
            command=self._on_mental_change,
            button_color=COLORS["accent_blue"],
            button_hover_color="#0077cc",
            progress_color=COLORS["accent_blue"],
        )
        self.mental_slider.set(session_entry.get("mental_rating", MENTAL_RATING_DEFAULT))
        self.mental_slider.pack(fill="x", pady=(0, 12))

        # === MENTAL REFLECTION (shown when mental <= 3) ===
        self._mental_reflection_frame = ctk.CTkFrame(
            container, fg_color=COLORS["bg_card"], corner_radius=8,
            border_width=1, border_color="#7c3aed",
        )
        # Inner content
        refl_inner = ctk.CTkFrame(self._mental_reflection_frame, fg_color="transparent")
        refl_inner.pack(fill="x", padx=12, pady=10)

        ctk.CTkLabel(
            refl_inner, text="MENTAL CHECK-IN",
            font=ctk.CTkFont(size=11, weight="bold"),
            text_color="#7c3aed",
        ).pack(anchor="w", pady=(0, 6))

        ctk.CTkLabel(
            refl_inner,
            text="Your mental was low this game. Take a moment to reflect before moving on.",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text_dim"],
            wraplength=500, justify="left",
        ).pack(anchor="w", pady=(0, 8))

        ctk.CTkLabel(
            refl_inner,
            text="What triggered the tilt / frustration?",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(0, 4))

        self._mental_trigger = ctk.CTkTextbox(
            refl_inner, height=50, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color="#7c3aed", corner_radius=8,
        )
        self._mental_trigger.pack(fill="x", pady=(0, 8))

        ctk.CTkLabel(
            refl_inner,
            text="Should you keep playing or take a break? Why?",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(0, 4))

        self._mental_decision = ctk.CTkTextbox(
            refl_inner, height=50, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color="#7c3aed", corner_radius=8,
        )
        self._mental_decision.pack(fill="x", pady=(0, 4))

        # Pre-fill if previously saved
        existing_handled = session_entry.get("mental_handled", "")
        if existing_handled:
            # Split on the separator if present
            parts = existing_handled.split(" | Decision: ", 1)
            trigger_text = parts[0].replace("Trigger: ", "", 1)
            decision_text = parts[1] if len(parts) > 1 else ""
            self._mental_trigger.insert("1.0", trigger_text)
            self._mental_decision.insert("1.0", decision_text)

        # Show/hide based on initial mental value
        initial_mental = session_entry.get("mental_rating", MENTAL_RATING_DEFAULT)
        if initial_mental <= 3:
            self._mental_reflection_frame.pack(fill="x", pady=(0, 12))
        # (otherwise stays hidden until slider changes)

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
            command=self._go_back,
        )
        cancel_btn.pack(fill="x", pady=(0, 8))

    def _build_vod_section(self, parent):
        """Show a VOD review button when a recording is linked."""
        vod_frame = ctk.CTkFrame(
            parent, fg_color=COLORS["bg_card"], corner_radius=8,
            border_width=1, border_color=COLORS["accent_gold"],
        )
        vod_frame.pack(fill="x", pady=(4, 8))

        inner = ctk.CTkFrame(vod_frame, fg_color="transparent")
        inner.pack(fill="x", padx=12, pady=10)

        ctk.CTkLabel(
            inner, text="VOD AVAILABLE",
            font=ctk.CTkFont(size=11, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(side="left", padx=(0, 10))

        bm_text = ""
        if self._bookmark_count > 0:
            bm_text = f"  ({self._bookmark_count} bookmark{'s' if self._bookmark_count != 1 else ''})"

        ctk.CTkButton(
            inner, text=f"Review VOD{bm_text}",
            font=ctk.CTkFont(size=13, weight="bold"),
            height=34, width=180,
            fg_color=COLORS["accent_gold"], hover_color="#a88432",
            text_color="#0a0a0f",
            command=self._open_vod,
        ).pack(side="right")

    def _open_vod(self):
        """Fire the VOD callback to open the player."""
        game_id = self.session_entry.get("game_id")
        if self._on_open_vod and game_id is not None:
            self._on_open_vod(game_id)

    def _on_mental_change(self, value):
        """Update mental label when slider changes."""
        rating = int(value)
        self.mental_label.configure(text=str(rating))
        if rating >= MENTAL_EXCELLENT_THRESHOLD:
            color = COLORS["win_green"]
        elif rating >= MENTAL_DECENT_THRESHOLD:
            color = COLORS["accent_blue"]
        else:
            color = COLORS["loss_red"]
        self.mental_label.configure(text_color=color)

        # Show/hide mental reflection
        if rating <= 3:
            if not self._mental_reflection_frame.winfo_manager():
                # Insert right after the slider (before performance rating)
                self._mental_reflection_frame.pack(fill="x", pady=(0, 12),
                                                   after=self.mental_slider)
        else:
            if self._mental_reflection_frame.winfo_manager():
                self._mental_reflection_frame.pack_forget()

    def _save(self):
        """Save review data to both session_log and games tables."""
        game_id = self.session_entry.get("game_id")
        mental = int(self.mental_slider.get())

        # Require mental reflection if mental is low
        if mental <= 3:
            trigger = self._mental_trigger.get("1.0", "end-1c").strip()
            decision = self._mental_decision.get("1.0", "end-1c").strip()
            if not trigger or not decision:
                self._mental_reflection_frame.configure(border_color=COLORS["loss_red"])
                return
            mental_handled = f"Trigger: {trigger} | Decision: {decision}"
            if game_id is not None and self.db.has_session_log_entry(game_id):
                self.db.update_mental_handled(game_id, mental_handled)
        else:
            self._mental_reflection_frame.configure(border_color="#7c3aed")

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
        elif game_id is not None and improvement:
            # Game has no session log entry (e.g. reviewed from History/Dashboard).
            # update_review() silently ignores improvement_note via **kwargs — save it
            # to the games table's review_notes field as a fallback so it isn't lost.
            self.db.update_review(
                game_id=game_id,
                notes=improvement,
                rating=rating,
                tags=tags,
                mistakes=mistakes,
                went_well=went_well,
                focus_next=focus_next,
            )

        if self.on_save:
            self.on_save()

        self._go_back()

    def _go_back(self):
        """Navigate back to the previous page."""
        if self._on_back:
            self._on_back()


# Legacy alias — kept so existing imports don't break
SessionGameReviewWindow = SessionGameReviewPanel
