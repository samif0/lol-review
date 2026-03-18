"""Post-game review popup window."""

import tkinter as tk
from typing import Callable, Optional

import customtkinter as ctk

from ..config import is_ascent_enabled
from ..constants import (
    COLORS, format_duration, format_number,
    KDA_EXCELLENT_THRESHOLD, KDA_GOOD_THRESHOLD, DAMAGE_DISPLAY_THRESHOLD,
    MENTAL_RATING_MIN, MENTAL_RATING_MAX, MENTAL_RATING_DEFAULT, MENTAL_RATING_STEPS,
)
from ..database.game_events import EVENT_STYLES
from ..lcu import GameStats
from ..vod import format_game_time
from .widgets import ConceptTagSelector, StatCard


class ReviewPanel(ctk.CTkFrame):
    """Inline post-game review panel — same UI as ReviewWindow in a CTkFrame."""

    def __init__(
        self,
        parent,
        stats: GameStats,
        tags: list[dict] = None,
        existing_review: Optional[dict] = None,
        on_save: Optional[Callable] = None,
        on_open_vod: Optional[Callable] = None,
        on_back: Optional[Callable] = None,
        has_vod: bool = False,
        bookmark_count: int = 0,
        bookmarks: Optional[list[dict]] = None,
        pregame_intention: str = "",
        existing_mental_handled: str = "",
        concept_tags: Optional[list[dict]] = None,
        existing_concept_tag_ids: Optional[list[int]] = None,
        active_objectives: Optional[list[dict]] = None,
        existing_game_objectives: Optional[list[dict]] = None,
        matchup_notes_shown: Optional[list[dict]] = None,
        enemy_laner: str = "",
        game_events: Optional[list[dict]] = None,
        derived_event_instances: Optional[list[dict]] = None,
        objective_prompts: Optional[dict] = None,
        existing_prompt_answers: Optional[list[dict]] = None,
        mental_rating: int = MENTAL_RATING_DEFAULT,
        **kwargs,
    ):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kwargs)

        self.stats = stats
        self.on_save = on_save
        self._on_open_vod = on_open_vod
        self._on_back = on_back
        self._has_vod = has_vod
        self._bookmark_count = bookmark_count
        self._bookmarks = bookmarks or []
        self._pregame_intention = pregame_intention
        self._existing_mental_handled = existing_mental_handled
        self._mental_rating = mental_rating
        self._concept_tags = concept_tags or []
        self._existing_concept_tag_ids = existing_concept_tag_ids or []
        self._active_objectives = active_objectives or []
        self._existing_game_objectives = existing_game_objectives or []
        self._matchup_notes_shown = matchup_notes_shown or []
        self._enemy_laner = enemy_laner
        self._game_events = game_events or []
        self._derived_event_instances = derived_event_instances or []
        self._objective_prompts = objective_prompts or {}  # {obj_id: [prompt_dicts]}
        self._existing_prompt_answers = existing_prompt_answers or []
        self._prompt_answer_widgets: list[dict] = []  # prompt answer widget tracking
        self._objective_widgets: dict[int, dict] = {}  # populated by _build_review_section

        # Back button header
        s = self.stats
        result_text = "Victory" if s.win else "Defeat"

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
            back_row, text=f"Review — {s.champion_name} ({result_text})",
            font=ctk.CTkFont(size=16, weight="bold"),
            text_color=COLORS["text"],
        ).pack(side="left", padx=(12, 0))

        # Load existing review data if editing
        er = existing_review or {}

        # Main scrollable container
        container = ctk.CTkScrollableFrame(
            self,
            fg_color=COLORS["bg_dark"],
            scrollbar_button_color=COLORS["border"],
        )
        container.pack(fill="both", expand=True, padx=16, pady=(8, 16))

        # === HEADER: Champion + Result ===
        self._build_header(container)

        # === STAT CARDS GRID ===
        self._build_stats_grid(container)

        # === DAMAGE BREAKDOWN ===
        self._build_damage_section(container)

        # === VOD BUTTON (if Ascent is set up and a recording exists) ===
        if self._has_vod:
            self._build_vod_section(container)

        # === BOOKMARKS (if any exist) ===
        if self._bookmarks:
            self._build_bookmarks_section(container)

        # === REVIEW SECTION ===
        self._build_review_section(
            container, er,
            self._pregame_intention, self._existing_mental_handled,
            self._concept_tags, self._existing_concept_tag_ids,
            self._active_objectives, self._existing_game_objectives,
            self._matchup_notes_shown, self._enemy_laner,
        )

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
        save_btn.pack(fill="x", pady=(16, 8))

        # Skip button
        skip_btn = ctk.CTkButton(
            container,
            text="Skip (save stats only)",
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
        skip_btn.pack(fill="x", pady=(0, 8))

    def _build_header(self, parent):
        """Champion name, win/loss, KDA, and game info."""
        header = ctk.CTkFrame(parent, fg_color="transparent")
        header.pack(fill="x", pady=(0, 12))

        s = self.stats
        result_color = COLORS["win_green"] if s.win else COLORS["loss_red"]
        result_text = "VICTORY" if s.win else "DEFEAT"

        # Left side: champion + result
        left = ctk.CTkFrame(header, fg_color="transparent")
        left.pack(side="left", fill="x", expand=True)

        ctk.CTkLabel(
            left,
            text=s.champion_name,
            font=ctk.CTkFont(size=28, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(anchor="w")

        result_row = ctk.CTkFrame(left, fg_color="transparent")
        result_row.pack(anchor="w")

        ctk.CTkLabel(
            result_row,
            text=result_text,
            font=ctk.CTkFont(size=16, weight="bold"),
            text_color=result_color,
        ).pack(side="left")

        ctk.CTkLabel(
            result_row,
            text=f"  •  {format_duration(s.game_duration)}  •  {s.game_mode}",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text_dim"],
        ).pack(side="left")

        # Right side: KDA display
        right = ctk.CTkFrame(header, fg_color="transparent")
        right.pack(side="right")

        ctk.CTkLabel(
            right,
            text=f"{s.kills} / {s.deaths} / {s.assists}",
            font=ctk.CTkFont(size=26, weight="bold"),
            text_color=COLORS["text"],
        ).pack(anchor="e")

        kda_color = COLORS["win_green"] if s.kda_ratio >= KDA_EXCELLENT_THRESHOLD else (
            COLORS["accent_gold"] if s.kda_ratio >= KDA_GOOD_THRESHOLD else COLORS["text_dim"]
        )
        ctk.CTkLabel(
            right,
            text=f"{s.kda_ratio:.2f} KDA  •  {s.kill_participation:.0f}% KP",
            font=ctk.CTkFont(size=13),
            text_color=kda_color,
        ).pack(anchor="e")

        # Separator
        ctk.CTkFrame(
            parent, fg_color=COLORS["border"], height=1
        ).pack(fill="x", pady=8)

    def _build_stats_grid(self, parent):
        """Grid of stat cards showing key metrics."""
        s = self.stats

        section_label = ctk.CTkLabel(
            parent,
            text="GAME STATS",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text_dim"],
        )
        section_label.pack(anchor="w", pady=(8, 6))

        grid = ctk.CTkFrame(parent, fg_color="transparent")
        grid.pack(fill="x", pady=(0, 8))

        stats_data = [
            ("CS", str(s.cs_total), None),
            ("CS/min", f"{s.cs_per_min}", None),
            ("Gold", format_number(s.gold_earned), COLORS["accent_gold"]),
            ("Vision", str(s.vision_score), None),
            ("Wards", str(s.wards_placed), None),
            ("Ctrl Wards", str(s.control_wards_purchased), None),
            ("Damage", format_number(s.total_damage_to_champions), COLORS["loss_red"]),
            ("Dmg Taken", format_number(s.total_damage_taken), None),
            ("Turrets", str(s.turret_kills), None),
            ("Level", str(s.champ_level), None),
        ]

        # Add multikills if any happened
        if s.double_kills:
            stats_data.append(("Doubles", str(s.double_kills), COLORS["accent_blue"]))
        if s.triple_kills:
            stats_data.append(("Triples", str(s.triple_kills), COLORS["accent_gold"]))
        if s.quadra_kills:
            stats_data.append(("Quadras", str(s.quadra_kills), "#e879f9"))
        if s.penta_kills:
            stats_data.append(("PENTAS", str(s.penta_kills), "#ff0000"))

        cols = 5
        for i, (label, value, color) in enumerate(stats_data):
            row, col = divmod(i, cols)
            card = StatCard(grid, label, value, color)
            card.grid(row=row, column=col, padx=4, pady=4, sticky="nsew")

        for col in range(cols):
            grid.columnconfigure(col, weight=1)

    def _build_damage_section(self, parent):
        """Visual damage breakdown bar."""
        s = self.stats
        total = max(s.total_damage_to_champions, 1)
        phys_pct = s.physical_damage_to_champions / total
        magic_pct = s.magic_damage_to_champions / total
        true_pct = s.true_damage_to_champions / total

        ctk.CTkLabel(
            parent,
            text="DAMAGE BREAKDOWN",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(12, 6))

        bar_frame = ctk.CTkFrame(
            parent, fg_color=COLORS["bg_card"],
            corner_radius=8, height=36,
            border_width=1, border_color=COLORS["border"],
        )
        bar_frame.pack(fill="x", pady=(0, 4))
        bar_frame.pack_propagate(False)

        bar_inner = ctk.CTkFrame(bar_frame, fg_color="transparent")
        bar_inner.pack(fill="both", expand=True, padx=2, pady=2)

        if phys_pct > DAMAGE_DISPLAY_THRESHOLD:
            phys_bar = ctk.CTkFrame(bar_inner, fg_color="#e74c3c", corner_radius=4)
            phys_bar.place(relx=0, rely=0, relwidth=phys_pct, relheight=1)

        if magic_pct > DAMAGE_DISPLAY_THRESHOLD:
            magic_bar = ctk.CTkFrame(bar_inner, fg_color="#3498db", corner_radius=4)
            magic_bar.place(relx=phys_pct, rely=0, relwidth=magic_pct, relheight=1)

        if true_pct > DAMAGE_DISPLAY_THRESHOLD:
            true_bar = ctk.CTkFrame(bar_inner, fg_color="#f1f1f1", corner_radius=4)
            true_bar.place(relx=phys_pct + magic_pct, rely=0, relwidth=true_pct, relheight=1)

        # Legend
        legend = ctk.CTkFrame(parent, fg_color="transparent")
        legend.pack(anchor="w", pady=(0, 8))

        for label, color, value in [
            ("Physical", "#e74c3c", s.physical_damage_to_champions),
            ("Magic", "#3498db", s.magic_damage_to_champions),
            ("True", "#f1f1f1", s.true_damage_to_champions),
        ]:
            dot = ctk.CTkFrame(legend, fg_color=color, width=10, height=10, corner_radius=5)
            dot.pack(side="left", padx=(0, 4))
            ctk.CTkLabel(
                legend,
                text=f"{label}: {format_number(value)}",
                font=ctk.CTkFont(size=11),
                text_color=COLORS["text_dim"],
            ).pack(side="left", padx=(0, 14))

        # Separator
        ctk.CTkFrame(
            parent, fg_color=COLORS["border"], height=1
        ).pack(fill="x", pady=8)

    def _build_vod_section(self, parent):
        """Show a VOD review button when a recording is linked."""
        vod_frame = ctk.CTkFrame(
            parent, fg_color=COLORS["bg_card"], corner_radius=8,
            border_width=1, border_color=COLORS["accent_gold"],
        )
        vod_frame.pack(fill="x", pady=(0, 10))

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
        if self._on_open_vod:
            self._on_open_vod(self.stats.game_id)

    def _build_bookmarks_section(self, parent):
        """Show VOD bookmark notes so they're visible during the review."""
        # Separate clips from regular bookmarks
        clips = [b for b in self._bookmarks if b.get("clip_path")]
        regular = [b for b in self._bookmarks if not b.get("clip_path")]

        if clips:
            ctk.CTkLabel(
                parent,
                text="SAVED CLIPS",
                font=ctk.CTkFont(size=12, weight="bold"),
                text_color="#22c55e",
            ).pack(anchor="w", pady=(8, 6))

            clip_frame = ctk.CTkFrame(
                parent, fg_color=COLORS["bg_card"], corner_radius=8,
                border_width=1, border_color="#22c55e",
            )
            clip_frame.pack(fill="x", pady=(0, 8))

            sorted_clips = sorted(clips, key=lambda b: b.get("clip_start_s", 0))
            for bm in sorted_clips:
                row = ctk.CTkFrame(clip_frame, fg_color="transparent")
                row.pack(fill="x", padx=10, pady=3)

                # Time range
                start = bm.get("clip_start_s", 0)
                end = bm.get("clip_end_s", 0)
                dur = end - start if end > start else 0
                time_text = f"{format_game_time(start)} – {format_game_time(end)} ({dur}s)"
                ctk.CTkLabel(
                    row, text=time_text,
                    font=ctk.CTkFont(size=12, weight="bold"),
                    text_color="#22c55e",
                    anchor="w",
                ).pack(side="left", padx=(0, 8))

                note = bm.get("note", "") or "(no note)"
                ctk.CTkLabel(
                    row, text=note,
                    font=ctk.CTkFont(size=12),
                    text_color=COLORS["text"] if bm.get("note") else COLORS["text_dim"],
                    anchor="w", justify="left",
                    wraplength=500,
                ).pack(side="left", fill="x", expand=True)

                ctk.CTkLabel(
                    row, text="CLIP",
                    font=ctk.CTkFont(size=9, weight="bold"),
                    text_color="#22c55e",
                    fg_color="#1a4d2e",
                    corner_radius=6,
                    padx=6, pady=1,
                ).pack(side="right")

        if regular:
            ctk.CTkLabel(
                parent,
                text="VOD BOOKMARKS",
                font=ctk.CTkFont(size=12, weight="bold"),
                text_color=COLORS["text_dim"],
            ).pack(anchor="w", pady=(8, 6))

            bm_frame = ctk.CTkFrame(
                parent, fg_color=COLORS["bg_card"], corner_radius=8,
                border_width=1, border_color=COLORS["border"],
            )
            bm_frame.pack(fill="x", pady=(0, 8))

            sorted_bm = sorted(regular, key=lambda b: b.get("game_time_s", 0))

            for bm in sorted_bm:
                row = ctk.CTkFrame(bm_frame, fg_color="transparent")
                row.pack(fill="x", padx=10, pady=3)

                time_text = format_game_time(bm.get("game_time_s", 0))
                ctk.CTkLabel(
                    row, text=time_text,
                    font=ctk.CTkFont(size=12, weight="bold"),
                    text_color=COLORS["accent_blue"],
                    width=50, anchor="w",
                ).pack(side="left", padx=(0, 8))

                note = bm.get("note", "") or "(no note)"
                ctk.CTkLabel(
                    row, text=note,
                    font=ctk.CTkFont(size=12),
                    text_color=COLORS["text"] if bm.get("note") else COLORS["text_dim"],
                    anchor="w", justify="left",
                    wraplength=600,
                ).pack(side="left", fill="x", expand=True)

        # Separator
        ctk.CTkFrame(
            parent, fg_color=COLORS["border"], height=1
        ).pack(fill="x", pady=8)

    def _build_review_section(
        self,
        parent,
        er: dict,
        pregame_intention: str = "",
        existing_mental_handled: str = "",
        concept_tags: list[dict] = None,
        existing_concept_tag_ids: list[int] = None,
        active_objectives: list[dict] = None,
        existing_game_objectives: list[dict] = None,
        matchup_notes_shown: list[dict] = None,
        enemy_laner: str = "",
    ):
        """The note-taking and review fields."""
        concept_tags = concept_tags or []
        existing_concept_tag_ids = existing_concept_tag_ids or []
        active_objectives = active_objectives or []
        existing_game_objectives = existing_game_objectives or []
        matchup_notes_shown = matchup_notes_shown or []

        ctk.CTkLabel(
            parent,
            text="YOUR REVIEW",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(8, 6))

        # === MENTAL RATING SLIDER ===
        mental_frame = ctk.CTkFrame(parent, fg_color="transparent")
        mental_frame.pack(fill="x", pady=(4, 8))

        ctk.CTkLabel(
            mental_frame,
            text="Mental Rating (1-10)",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(side="left")

        self._mental_value_label = ctk.CTkLabel(
            mental_frame,
            text=str(self._mental_rating),
            font=ctk.CTkFont(size=18, weight="bold"),
            text_color=COLORS["accent_blue"],
        )
        self._mental_value_label.pack(side="right", padx=(0, 8))

        self._mental_slider = ctk.CTkSlider(
            parent,
            from_=MENTAL_RATING_MIN,
            to=MENTAL_RATING_MAX,
            number_of_steps=MENTAL_RATING_STEPS,
            command=self._on_mental_slider_change,
            button_color=COLORS["accent_blue"],
            button_hover_color="#0077cc",
            progress_color=COLORS["accent_blue"],
        )
        self._mental_slider.set(self._mental_rating)
        self._mental_slider.pack(fill="x", pady=(0, 12))

        # === MENTAL INTENTION REFLECTION ===
        if pregame_intention:
            intention_frame = ctk.CTkFrame(
                parent,
                fg_color=COLORS["bg_card"],
                corner_radius=8,
                border_width=2,
                border_color="#7c3aed",
            )
            intention_frame.pack(fill="x", pady=(0, 10))

            ctk.CTkLabel(
                intention_frame,
                text="YOUR MENTAL INTENTION THIS GAME",
                font=ctk.CTkFont(size=11, weight="bold"),
                text_color="#a78bfa",
            ).pack(padx=14, pady=(10, 4), anchor="w")

            ctk.CTkLabel(
                intention_frame,
                text=pregame_intention,
                font=ctk.CTkFont(size=13),
                text_color=COLORS["text"],
                wraplength=650,
                justify="left",
            ).pack(padx=14, pady=(0, 8), anchor="w")

            ctk.CTkLabel(
                parent,
                text="What triggered you, and what was the story you told yourself in the moment?",
                font=ctk.CTkFont(size=13),
                text_color=COLORS["text"],
            ).pack(anchor="w", pady=(0, 4))

            self.mental_handled = ctk.CTkTextbox(
                parent,
                height=60,
                font=ctk.CTkFont(size=13),
                fg_color=COLORS["bg_input"],
                text_color=COLORS["text"],
                border_width=1,
                border_color="#7c3aed",
                corner_radius=8,
            )
            self.mental_handled.pack(fill="x", pady=(0, 10))
            if existing_mental_handled:
                self.mental_handled.insert("1.0", existing_mental_handled)
        else:
            self.mental_handled = None

        # === ACTIVE OBJECTIVE BLOCK ===
        # Find the primary objective (first one, type='primary')
        primary_obj = next((o for o in active_objectives if o.get("type") == "primary"), None)
        mental_obj = next((o for o in active_objectives if o.get("type") == "mental"), None)

        # Map existing game objectives by objective_id for pre-filling
        existing_go_by_id = {go["objective_id"]: go for go in existing_game_objectives}

        self._objective_widgets: dict[int, dict] = {}  # obj_id → {practiced_var, note_box}

        for obj in [primary_obj, mental_obj]:
            if obj is None:
                continue
            obj_id = obj["id"]
            is_mental = obj.get("type") == "mental"
            existing_go = existing_go_by_id.get(obj_id, {})

            border_color = "#7c3aed" if is_mental else COLORS["accent_blue"]
            label_color = "#a78bfa" if is_mental else COLORS["accent_blue"]
            type_label = "MENTAL OBJECTIVE" if is_mental else "LEARNING OBJECTIVE"

            obj_frame = ctk.CTkFrame(
                parent, fg_color=COLORS["bg_card"], corner_radius=8,
                border_width=2, border_color=border_color,
            )
            obj_frame.pack(fill="x", pady=(0, 10))

            header_row = ctk.CTkFrame(obj_frame, fg_color="transparent")
            header_row.pack(fill="x", padx=14, pady=(10, 4))

            ctk.CTkLabel(
                header_row, text=type_label,
                font=ctk.CTkFont(size=11, weight="bold"),
                text_color=label_color,
            ).pack(side="left")

            ctk.CTkLabel(
                obj_frame,
                text=obj["title"],
                font=ctk.CTkFont(size=14, weight="bold"),
                text_color=COLORS["text"],
                wraplength=650, justify="left",
            ).pack(padx=14, pady=(0, 4), anchor="w")

            if obj.get("completion_criteria"):
                ctk.CTkLabel(
                    obj_frame,
                    text=f"Success looks like: {obj['completion_criteria']}",
                    font=ctk.CTkFont(size=12),
                    text_color=COLORS["text_dim"],
                    wraplength=650, justify="left",
                ).pack(padx=14, pady=(0, 6), anchor="w")

            # Practiced checkbox
            practiced_var = ctk.BooleanVar(value=bool(existing_go.get("practiced", True)))
            ctk.CTkCheckBox(
                obj_frame,
                text="I practiced this objective this game",
                variable=practiced_var,
                font=ctk.CTkFont(size=13),
                text_color=COLORS["text"],
                fg_color=border_color,
                hover_color=border_color,
            ).pack(padx=14, pady=(0, 6), anchor="w")

            # Execution note
            ctk.CTkLabel(
                obj_frame,
                text="How did it go?",
                font=ctk.CTkFont(size=12),
                text_color=COLORS["text_dim"],
            ).pack(padx=14, anchor="w")

            note_box = ctk.CTkTextbox(
                obj_frame, height=52, font=ctk.CTkFont(size=13),
                fg_color=COLORS["bg_input"], text_color=COLORS["text"],
                border_width=1, border_color=border_color, corner_radius=6,
            )
            note_box.pack(fill="x", padx=14, pady=(4, 12))
            if existing_go.get("execution_note"):
                note_box.insert("1.0", existing_go["execution_note"])

            # === REVIEW PROMPTS for this objective ===
            obj_prompts = self._objective_prompts.get(obj_id, [])
            if obj_prompts:
                self._render_objective_prompts(obj_frame, obj_id, obj_prompts, border_color)

            self._objective_widgets[obj_id] = {
                "practiced_var": practiced_var,
                "note_box": note_box,
            }

        # Concept Tags (replaces old tag selector)
        if concept_tags:
            ctk.CTkLabel(
                parent,
                text="Concept Tags",
                font=ctk.CTkFont(size=13),
                text_color=COLORS["text"],
            ).pack(anchor="w", pady=(4, 4))

            self.concept_tag_selector = ConceptTagSelector(
                parent, concept_tags, selected_ids=existing_concept_tag_ids,
            )
            self.concept_tag_selector.pack(fill="x", pady=(0, 10))
        else:
            self.concept_tag_selector = None

        # Key takeaway
        ctk.CTkLabel(
            parent,
            text="Key takeaway from this game",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))
        self.went_well = ctk.CTkTextbox(
            parent, height=60, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
        )
        self.went_well.pack(fill="x", pady=(0, 8))
        self.went_well.insert("1.0", er.get("went_well", ""))

        # Mistakes / improvements
        ctk.CTkLabel(
            parent,
            text="What to improve next game",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))
        self.mistakes = ctk.CTkTextbox(
            parent, height=60, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
        )
        self.mistakes.pack(fill="x", pady=(0, 8))
        self.mistakes.insert("1.0", er.get("mistakes", ""))

        # VOD hint
        if self._has_vod and self._bookmark_count == 0:
            ctk.CTkLabel(
                parent,
                text="Tip: Use the VOD player to bookmark specific moments "
                     "instead of writing them here",
                font=ctk.CTkFont(size=11),
                text_color=COLORS["text_dim"],
                wraplength=650,
            ).pack(anchor="w", pady=(0, 6))

        # Focus for next game
        ctk.CTkLabel(
            parent,
            text="Focus for next game",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))
        self.focus_next = ctk.CTkEntry(
            parent, height=38, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
            placeholder_text="e.g., Track jungle timers, play safer before 6...",
        )
        self.focus_next.pack(fill="x", pady=(0, 8))
        if er.get("focus_next"):
            self.focus_next.insert(0, er["focus_next"])

        # General notes
        ctk.CTkLabel(
            parent,
            text="Additional Notes",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))
        self.notes = ctk.CTkTextbox(
            parent, height=80, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
        )
        self.notes.pack(fill="x", pady=(0, 8))
        self.notes.insert("1.0", er.get("review_notes", ""))

        # === MATCHUP SECTION ===
        # Rate matchup notes that were shown in pregame
        self._matchup_helpful_widgets: list[dict] = []
        if matchup_notes_shown:
            ctk.CTkFrame(
                parent, fg_color=COLORS["border"], height=1
            ).pack(fill="x", pady=8)

            ctk.CTkLabel(
                parent,
                text="MATCHUP NOTES",
                font=ctk.CTkFont(size=12, weight="bold"),
                text_color=COLORS["accent_gold"],
            ).pack(anchor="w", pady=(4, 4))

            ctk.CTkLabel(
                parent,
                text="Were these matchup notes helpful?",
                font=ctk.CTkFont(size=13),
                text_color=COLORS["text"],
            ).pack(anchor="w", pady=(0, 6))

            for mn in matchup_notes_shown:
                mn_frame = ctk.CTkFrame(
                    parent, fg_color=COLORS["bg_card"], corner_radius=8,
                    border_width=1, border_color=COLORS["border"],
                )
                mn_frame.pack(fill="x", pady=(0, 6))

                ctk.CTkLabel(
                    mn_frame,
                    text=mn.get("note", ""),
                    font=ctk.CTkFont(size=12),
                    text_color=COLORS["text"],
                    wraplength=620, justify="left",
                ).pack(padx=12, pady=(8, 4), anchor="w")

                btn_row = ctk.CTkFrame(mn_frame, fg_color="transparent")
                btn_row.pack(fill="x", padx=12, pady=(0, 8))

                helpful_var = tk.IntVar(value=-1)  # -1 = unrated, 1 = helpful, 0 = not helpful

                ctk.CTkButton(
                    btn_row, text="Helpful",
                    font=ctk.CTkFont(size=11), height=26, width=80,
                    corner_radius=13,
                    fg_color=COLORS["tag_bg"], hover_color="#1a4d2e",
                    text_color=COLORS["text_dim"],
                    border_width=1, border_color=COLORS["border"],
                    command=lambda v=helpful_var: v.set(1),
                ).pack(side="left", padx=(0, 6))

                ctk.CTkButton(
                    btn_row, text="Not helpful",
                    font=ctk.CTkFont(size=11), height=26, width=100,
                    corner_radius=13,
                    fg_color=COLORS["tag_bg"], hover_color="#4d1a1a",
                    text_color=COLORS["text_dim"],
                    border_width=1, border_color=COLORS["border"],
                    command=lambda v=helpful_var: v.set(0),
                ).pack(side="left")

                self._matchup_helpful_widgets.append({
                    "note_id": mn.get("id"),
                    "helpful_var": helpful_var,
                })

        # Add Matchup Note section (always shown)
        ctk.CTkFrame(
            parent, fg_color=COLORS["border"], height=1
        ).pack(fill="x", pady=8)

        ctk.CTkLabel(
            parent,
            text="ADD MATCHUP NOTE",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(anchor="w", pady=(4, 4))

        ctk.CTkLabel(
            parent,
            text="Enemy champion",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 2))

        self.matchup_enemy_entry = ctk.CTkEntry(
            parent, height=34, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
            placeholder_text="e.g., Zed, Yasuo...",
        )
        self.matchup_enemy_entry.pack(fill="x", pady=(0, 6))
        if enemy_laner:
            self.matchup_enemy_entry.insert(0, enemy_laner)

        ctk.CTkLabel(
            parent,
            text="Matchup note (optional)",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(2, 2))

        self.matchup_note_entry = ctk.CTkTextbox(
            parent, height=60, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
        )
        self.matchup_note_entry.pack(fill="x", pady=(0, 8))

    def _render_objective_prompts(self, parent, obj_id: int, prompts: list[dict],
                                    border_color: str):
        """Render review prompts for an objective inside its card frame."""
        # Build a lookup of existing answers by (prompt_id, event_time_s)
        existing_by_key = {}
        for ans in self._existing_prompt_answers:
            key = (ans["prompt_id"], ans.get("event_time_s"))
            existing_by_key[key] = ans.get("answer_value", 0)

        prompts_frame = ctk.CTkFrame(parent, fg_color="transparent")
        prompts_frame.pack(fill="x", padx=14, pady=(0, 10))

        ctk.CTkLabel(
            prompts_frame, text="REVIEW PROMPTS",
            font=ctk.CTkFont(size=10, weight="bold"),
            text_color=border_color,
        ).pack(anchor="w", pady=(0, 4))

        for prompt in prompts:
            event_tag = prompt.get("event_tag", "") or ""
            answer_type = prompt.get("answer_type", "yes_no")
            prompt_id = prompt["id"]
            question = prompt.get("question_text", "")

            if not event_tag:
                # General prompt — render once
                self._render_single_prompt(
                    prompts_frame, prompt_id, question, answer_type,
                    event_instance_id=None, event_time_s=None,
                    context_text=None,
                    existing_value=existing_by_key.get((prompt_id, None)),
                )
            else:
                # Event-tagged prompt — find matching events
                matching_events = self._get_matching_events(event_tag)
                if not matching_events:
                    # No matching events — show the prompt once, greyed out
                    no_event_frame = ctk.CTkFrame(prompts_frame, fg_color="transparent")
                    no_event_frame.pack(fill="x", pady=(0, 2))
                    ctk.CTkLabel(
                        no_event_frame,
                        text=f"{question}  (no {event_tag} events this game)",
                        font=ctk.CTkFont(size=12),
                        text_color=COLORS["text_dim"],
                    ).pack(anchor="w")
                else:
                    for evt in matching_events:
                        evt_time = evt.get("game_time_s") or evt.get("start_time_s", 0)
                        evt_id = evt.get("id")
                        time_str = format_game_time(evt_time)
                        context = f"@ {time_str} — {event_tag}"
                        self._render_single_prompt(
                            prompts_frame, prompt_id, question, answer_type,
                            event_instance_id=evt_id, event_time_s=evt_time,
                            context_text=context,
                            existing_value=existing_by_key.get((prompt_id, evt_time)),
                        )

    def _get_matching_events(self, event_tag: str) -> list[dict]:
        """Get game events or derived event instances matching an event tag."""
        # Check if it's a standard event type (from EVENT_STYLES)
        if event_tag in EVENT_STYLES:
            return [e for e in self._game_events
                    if e.get("event_type") == event_tag]
        # Otherwise check derived event instances by definition_name
        return [d for d in self._derived_event_instances
                if d.get("definition_name") == event_tag]

    def _render_single_prompt(self, parent, prompt_id: int, question: str,
                               answer_type: str, event_instance_id=None,
                               event_time_s=None, context_text=None,
                               existing_value=None):
        """Render a single prompt question with its answer widget."""
        row = ctk.CTkFrame(parent, fg_color=COLORS["bg_input"], corner_radius=6)
        row.pack(fill="x", pady=(0, 4))

        inner = ctk.CTkFrame(row, fg_color="transparent")
        inner.pack(fill="x", padx=10, pady=6)

        # Context label (event timestamp) if present
        if context_text:
            ctk.CTkLabel(
                inner, text=context_text,
                font=ctk.CTkFont(size=10, weight="bold"),
                text_color=COLORS["accent_gold"],
            ).pack(anchor="w")

        # Question label
        ctk.CTkLabel(
            inner, text=question,
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text"],
            wraplength=580, justify="left",
        ).pack(anchor="w", pady=(0, 4))

        # Answer widget
        answer_row = ctk.CTkFrame(inner, fg_color="transparent")
        answer_row.pack(fill="x")

        if answer_type == "yes_no":
            var = tk.IntVar(value=existing_value if existing_value is not None else 0)
            ctk.CTkRadioButton(
                answer_row, text="No", variable=var, value=0,
                font=ctk.CTkFont(size=11), text_color=COLORS["text"],
                fg_color=COLORS["loss_red"], height=22,
            ).pack(side="left", padx=(0, 12))
            ctk.CTkRadioButton(
                answer_row, text="Yes", variable=var, value=1,
                font=ctk.CTkFont(size=11), text_color=COLORS["text"],
                fg_color=COLORS["win_green"], height=22,
            ).pack(side="left")
            widget = var
        else:
            # 1-5 scale
            initial = existing_value if existing_value is not None else 3
            var = tk.IntVar(value=initial)

            scale_label = ctk.CTkLabel(
                answer_row, text=str(initial),
                font=ctk.CTkFont(size=13, weight="bold"),
                text_color=COLORS["accent_blue"], width=24,
            )

            def _on_scale_change(val, lbl=scale_label, v=var):
                int_val = int(round(float(val)))
                v.set(int_val)
                lbl.configure(text=str(int_val))

            ctk.CTkLabel(
                answer_row, text="1",
                font=ctk.CTkFont(size=10), text_color=COLORS["text_dim"],
            ).pack(side="left")

            slider = ctk.CTkSlider(
                answer_row, from_=1, to=5, number_of_steps=4,
                width=160, height=16,
                fg_color=COLORS["border"],
                progress_color=COLORS["accent_blue"],
                button_color=COLORS["accent_blue"],
                button_hover_color="#0077cc",
                command=_on_scale_change,
            )
            slider.set(initial)
            slider.pack(side="left", padx=(4, 4))

            ctk.CTkLabel(
                answer_row, text="5",
                font=ctk.CTkFont(size=10), text_color=COLORS["text_dim"],
            ).pack(side="left")

            scale_label.pack(side="left", padx=(8, 0))
            widget = var

        self._prompt_answer_widgets.append({
            "prompt_id": prompt_id,
            "event_instance_id": event_instance_id,
            "event_time_s": event_time_s,
            "widget": widget,
        })

    def _on_mental_slider_change(self, value):
        """Update mental rating label when slider moves."""
        val = int(round(value))
        self._mental_rating = val
        self._mental_value_label.configure(text=str(val))
        # Color code: red for low, yellow for mid, green for high
        if val <= 3:
            color = "#ea5455"
        elif val <= 6:
            color = "#f0c040"
        else:
            color = "#4caf50"
        self._mental_value_label.configure(text_color=color)

    def _save(self):
        """Collect review data and fire the save callback."""
        # Collect objective data
        objectives_data = []
        for obj_id, widgets in self._objective_widgets.items():
            objectives_data.append({
                "objective_id": obj_id,
                "practiced": widgets["practiced_var"].get(),
                "execution_note": widgets["note_box"].get("1.0", "end-1c").strip(),
            })

        # Collect matchup helpful ratings
        matchup_helpful = []
        for mw in self._matchup_helpful_widgets:
            val = mw["helpful_var"].get()
            if val != -1:  # Only include if user actually rated
                matchup_helpful.append({
                    "note_id": mw["note_id"],
                    "helpful": val,
                })

        # Collect new matchup note
        enemy_text = self.matchup_enemy_entry.get().strip()
        note_text = self.matchup_note_entry.get("1.0", "end-1c").strip()
        matchup_note = None
        if enemy_text and note_text:
            matchup_note = {"enemy": enemy_text, "note": note_text}

        review_data = {
            "game_id": self.stats.game_id,
            "win": self.stats.win,
            "notes": self.notes.get("1.0", "end-1c").strip(),
            "rating": 0,
            "mental_rating": self._mental_rating,
            "mistakes": self.mistakes.get("1.0", "end-1c").strip(),
            "went_well": self.went_well.get("1.0", "end-1c").strip(),
            "focus_next": self.focus_next.get().strip(),
            "mental_handled": (
                self.mental_handled.get("1.0", "end-1c").strip()
                if self.mental_handled else ""
            ),
            "concept_tag_ids": (
                self.concept_tag_selector.get() if self.concept_tag_selector else []
            ),
            "objectives_data": objectives_data,
            "matchup_helpful": matchup_helpful,
            "matchup_note": matchup_note,
            "enemy_laner": enemy_text,
            "prompt_answers": [
                {
                    "prompt_id": pw["prompt_id"],
                    "answer_value": pw["widget"].get(),
                    "event_instance_id": pw["event_instance_id"],
                    "event_time_s": pw["event_time_s"],
                }
                for pw in self._prompt_answer_widgets
            ],
        }

        if self.on_save:
            self.on_save(review_data)

        self._go_back()

    def _go_back(self):
        """Navigate back to the previous page."""
        if self._on_back:
            self._on_back()


# Legacy alias — kept so existing imports don't break
ReviewWindow = ReviewPanel
