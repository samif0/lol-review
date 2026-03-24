"""Post-game review popup window."""

import tkinter as tk
from typing import Callable, Optional

import customtkinter as ctk

from ..config import is_ascent_enabled, is_tilt_fix_enabled
from ..constants import (
    COLORS, format_duration, format_number,
    KDA_EXCELLENT_THRESHOLD, KDA_GOOD_THRESHOLD, DAMAGE_DISPLAY_THRESHOLD,
    MENTAL_RATING_MIN, MENTAL_RATING_MAX, MENTAL_RATING_DEFAULT, MENTAL_RATING_STEPS,
    ATTRIBUTION_OPTIONS,
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

        # === BOOKMARKS (refreshable container) ===
        self._bookmarks_frame = ctk.CTkFrame(container, fg_color="transparent")
        self._bookmarks_frame.pack(fill="x")
        if self._bookmarks:
            self._build_bookmarks_section(self._bookmarks_frame)

        # === REVIEW SECTION ===
        self._build_review_section(
            container, er,
            self._concept_tags, self._existing_concept_tag_ids,
            self._active_objectives, self._existing_game_objectives,
            self._matchup_notes_shown, self._enemy_laner,
        )

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

    def _card(self, parent, border_color=None):
        """Create a standard card frame matching the app's visual language."""
        kw = {
            "fg_color": COLORS["bg_card"],
            "corner_radius": 10,
            "border_width": 1,
            "border_color": border_color or COLORS["border"],
        }
        return ctk.CTkFrame(parent, **kw)

    def _build_review_section(
        self,
        parent,
        er: dict,
        concept_tags: list[dict] = None,
        existing_concept_tag_ids: list[int] = None,
        active_objectives: list[dict] = None,
        existing_game_objectives: list[dict] = None,
        matchup_notes_shown: list[dict] = None,
        enemy_laner: str = "",
    ):
        """The note-taking and review fields — card-based layout."""
        concept_tags = concept_tags or []
        existing_concept_tag_ids = existing_concept_tag_ids or []
        active_objectives = active_objectives or []
        existing_game_objectives = existing_game_objectives or []
        matchup_notes_shown = matchup_notes_shown or []
        tilt_fix = is_tilt_fix_enabled()

        # ┌─────────────────────────────────────────────────┐
        # │  CARD 1: Learning Objective (the focus)         │
        # └─────────────────────────────────────────────────┘
        primary_obj = next((o for o in active_objectives if o.get("type") == "primary"), None)
        mental_obj = next((o for o in active_objectives if o.get("type") == "mental"), None)
        existing_go_by_id = {go["objective_id"]: go for go in existing_game_objectives}
        self._objective_widgets: dict[int, dict] = {}

        for obj in [primary_obj, mental_obj]:
            if obj is None:
                continue
            obj_id = obj["id"]
            is_mental = obj.get("type") == "mental"
            existing_go = existing_go_by_id.get(obj_id, {})
            border_color = "#7c3aed" if is_mental else COLORS["accent_blue"]
            label_color = "#a78bfa" if is_mental else COLORS["accent_blue"]
            type_label = "MENTAL OBJECTIVE" if is_mental else "LEARNING OBJECTIVE"

            card = self._card(parent, border_color=border_color)
            card.configure(border_width=2)
            card.pack(fill="x", pady=(0, 12))
            inner = ctk.CTkFrame(card, fg_color="transparent")
            inner.pack(fill="x", padx=16, pady=14)

            ctk.CTkLabel(
                inner, text=type_label,
                font=ctk.CTkFont(size=11, weight="bold"),
                text_color=label_color,
            ).pack(anchor="w")
            ctk.CTkLabel(
                inner, text=obj["title"],
                font=ctk.CTkFont(size=14, weight="bold"),
                text_color=COLORS["text"], wraplength=650, justify="left",
            ).pack(anchor="w", pady=(2, 4))
            if obj.get("completion_criteria"):
                ctk.CTkLabel(
                    inner, text=f"Success: {obj['completion_criteria']}",
                    font=ctk.CTkFont(size=12), text_color=COLORS["text_dim"],
                    wraplength=650, justify="left",
                ).pack(anchor="w", pady=(0, 6))

            practiced_var = ctk.BooleanVar(value=bool(existing_go.get("practiced", True)))
            ctk.CTkCheckBox(
                inner, text="I practiced this objective",
                variable=practiced_var, font=ctk.CTkFont(size=13),
                text_color=COLORS["text"],
                fg_color=border_color, hover_color=border_color,
            ).pack(anchor="w", pady=(0, 6))

            note_box = ctk.CTkTextbox(
                inner, height=50, font=ctk.CTkFont(size=13),
                fg_color=COLORS["bg_input"], text_color=COLORS["text"],
                border_width=1, border_color=border_color, corner_radius=6,
            )
            note_box.pack(fill="x", pady=(0, 4))
            note_box.insert("1.0", existing_go.get("execution_note", ""))

            obj_prompts = self._objective_prompts.get(obj_id, [])
            if obj_prompts:
                self._render_objective_prompts(inner, obj_id, obj_prompts, border_color)

            self._objective_widgets[obj_id] = {
                "practiced_var": practiced_var,
                "note_box": note_box,
            }

        # ┌─────────────────────────────────────────────────┐
        # │  CARD 2: Game Review (core fields)              │
        # └─────────────────────────────────────────────────┘
        review_card = self._card(parent)
        review_card.pack(fill="x", pady=(0, 12))
        rc = ctk.CTkFrame(review_card, fg_color="transparent")
        rc.pack(fill="x", padx=16, pady=14)

        # Contextual prompt
        if tilt_fix and not self.stats.win:
            prompt_text = "What happened and what can you control next time?"
        elif tilt_fix and self.stats.win:
            prompt_text = "What did you do well and what's your key takeaway?"
        else:
            prompt_text = "Key takeaway — what went well and what to improve"

        ctk.CTkLabel(
            rc, text=prompt_text,
            font=ctk.CTkFont(size=13), text_color=COLORS["text"],
        ).pack(anchor="w")
        self.went_well = ctk.CTkTextbox(
            rc, height=70, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=6,
        )
        self.went_well.pack(fill="x", pady=(4, 10))
        existing_parts = []
        if er.get("went_well"):
            existing_parts.append(er["went_well"])
        if er.get("mistakes"):
            existing_parts.append(er["mistakes"])
        self.went_well.insert("1.0", "\n".join(existing_parts))
        self.mistakes = self.went_well

        ctk.CTkLabel(
            rc, text="Focus for next game",
            font=ctk.CTkFont(size=13), text_color=COLORS["text"],
        ).pack(anchor="w")
        self.focus_next = ctk.CTkEntry(
            rc, height=36, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=6,
            placeholder_text="e.g., Track jungle timers, play safer before 6...",
        )
        self.focus_next.pack(fill="x", pady=(4, 10))
        if er.get("focus_next"):
            self.focus_next.insert(0, er["focus_next"])

        # Mental + attribution in compact row
        ctk.CTkFrame(rc, fg_color=COLORS["border"], height=1).pack(fill="x", pady=(0, 8))

        mental_row = ctk.CTkFrame(rc, fg_color="transparent")
        mental_row.pack(fill="x")
        ctk.CTkLabel(
            mental_row, text="Mental",
            font=ctk.CTkFont(size=12), text_color=COLORS["text_dim"],
        ).pack(side="left")
        self._mental_value_label = ctk.CTkLabel(
            mental_row, text=str(self._mental_rating),
            font=ctk.CTkFont(size=16, weight="bold"),
            text_color=COLORS["accent_blue"],
        )
        self._mental_value_label.pack(side="right")

        self._mental_slider = ctk.CTkSlider(
            rc, from_=MENTAL_RATING_MIN, to=MENTAL_RATING_MAX,
            number_of_steps=MENTAL_RATING_STEPS,
            command=self._on_mental_slider_change,
            button_color=COLORS["accent_blue"],
            button_hover_color="#0077cc",
            progress_color=COLORS["accent_blue"],
        )
        self._mental_slider.set(self._mental_rating)
        self._mental_slider.pack(fill="x", pady=(4, 0))

        self.mental_handled = None
        self.outside_control = None
        self.within_control = None
        self.personal_contribution = None
        self._attribution_var = er.get("attribution", "")
        self._attribution_buttons: dict[str, ctk.CTkButton] = {}

        if tilt_fix:
            attr_row = ctk.CTkFrame(rc, fg_color="transparent")
            attr_row.pack(fill="x", pady=(8, 0))
            ctk.CTkLabel(
                attr_row, text="Factor:",
                font=ctk.CTkFont(size=11), text_color=COLORS["text_dim"],
            ).pack(side="left", padx=(0, 6))
            for key, label in ATTRIBUTION_OPTIONS:
                btn = ctk.CTkButton(
                    attr_row, text=label,
                    font=ctk.CTkFont(size=11), height=26, corner_radius=13,
                    fg_color=COLORS["tag_bg"], hover_color=COLORS["accent_blue"],
                    text_color=COLORS["text_dim"],
                    border_width=1, border_color=COLORS["border"],
                    command=lambda k=key: self._select_attribution(k),
                )
                btn.pack(side="left", padx=2)
                self._attribution_buttons[key] = btn
            if self._attribution_var:
                self._select_attribution(self._attribution_var)

        # ┌─────────────────────────────────────────────────┐
        # │  SAVE / SKIP                                    │
        # └─────────────────────────────────────────────────┘
        ctk.CTkButton(
            parent, text="Save Review",
            font=ctk.CTkFont(size=15, weight="bold"), height=44,
            corner_radius=8, fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._save,
        ).pack(fill="x", pady=(4, 8))

        ctk.CTkButton(
            parent, text="Skip (save stats only)",
            font=ctk.CTkFont(size=13), height=36, corner_radius=8,
            fg_color="transparent", hover_color=COLORS["bg_input"],
            text_color=COLORS["text_dim"],
            border_width=1, border_color=COLORS["border"],
            command=self._go_back,
        ).pack(fill="x", pady=(0, 12))

        # ┌─────────────────────────────────────────────────┐
        # │  CARD 3: Optional Details (below save)          │
        # └─────────────────────────────────────────────────┘
        has_optional = bool(
            concept_tags or er.get("review_notes", "").strip()
            or er.get("spotted_problems", "").strip()
            or matchup_notes_shown or enemy_laner
            or (tilt_fix and (er.get("outside_control", "").strip()
                              or er.get("personal_contribution", "").strip()))
        )

        if has_optional or concept_tags or tilt_fix:
            opt_card = self._card(parent)
            opt_card.pack(fill="x", pady=(0, 12))
            oc = ctk.CTkFrame(opt_card, fg_color="transparent")
            oc.pack(fill="x", padx=16, pady=14)

            ctk.CTkLabel(
                oc, text="ADDITIONAL DETAILS",
                font=ctk.CTkFont(size=11, weight="bold"),
                text_color=COLORS["text_dim"],
            ).pack(anchor="w", pady=(0, 8))

            # Concept tags
            self.concept_tag_selector = None
            if concept_tags:
                ctk.CTkLabel(
                    oc, text="Concept Tags",
                    font=ctk.CTkFont(size=12), text_color=COLORS["text"],
                ).pack(anchor="w", pady=(0, 4))
                self.concept_tag_selector = ConceptTagSelector(
                    oc, concept_tags, selected_ids=existing_concept_tag_ids,
                )
                self.concept_tag_selector.pack(fill="x", pady=(0, 8))

            # Tilt fix detail fields
            if tilt_fix and not self.stats.win:
                ctk.CTkLabel(
                    oc, text="What was outside your control?",
                    font=ctk.CTkFont(size=12), text_color=COLORS["accent_purple"],
                ).pack(anchor="w", pady=(4, 2))
                self.outside_control = ctk.CTkTextbox(
                    oc, height=40, font=ctk.CTkFont(size=13),
                    fg_color=COLORS["bg_input"], text_color=COLORS["text"],
                    border_width=1, border_color=COLORS["accent_purple"], corner_radius=6,
                )
                self.outside_control.pack(fill="x", pady=(0, 6))
                self.outside_control.insert("1.0", er.get("outside_control", ""))

                ctk.CTkLabel(
                    oc, text="What can you control differently?",
                    font=ctk.CTkFont(size=12), text_color=COLORS["accent_purple"],
                ).pack(anchor="w", pady=(2, 2))
                self.within_control = ctk.CTkTextbox(
                    oc, height=40, font=ctk.CTkFont(size=13),
                    fg_color=COLORS["bg_input"], text_color=COLORS["text"],
                    border_width=1, border_color=COLORS["accent_purple"], corner_radius=6,
                )
                self.within_control.pack(fill="x", pady=(0, 8))
                self.within_control.insert("1.0", er.get("within_control", ""))

            if tilt_fix and self.stats.win:
                ctk.CTkLabel(
                    oc, text="What did YOU do well that contributed?",
                    font=ctk.CTkFont(size=12), text_color=COLORS["win_green"],
                ).pack(anchor="w", pady=(4, 2))
                self.personal_contribution = ctk.CTkTextbox(
                    oc, height=40, font=ctk.CTkFont(size=13),
                    fg_color=COLORS["bg_input"], text_color=COLORS["text"],
                    border_width=1, border_color=COLORS["win_green"], corner_radius=6,
                )
                self.personal_contribution.pack(fill="x", pady=(0, 8))
                self.personal_contribution.insert("1.0", er.get("personal_contribution", ""))

            # Notes
            self.notes = ctk.CTkTextbox(
                oc, height=50, font=ctk.CTkFont(size=13),
                fg_color=COLORS["bg_input"], text_color=COLORS["text"],
                border_width=1, border_color=COLORS["border"], corner_radius=6,
            )
            ctk.CTkLabel(
                oc, text="Notes",
                font=ctk.CTkFont(size=12), text_color=COLORS["text_dim"],
            ).pack(anchor="w", pady=(4, 2))
            self.notes.pack(fill="x", pady=(0, 8))
            self.notes.insert("1.0", er.get("review_notes", ""))

            # Spotted problems
            ctk.CTkLabel(
                oc, text="Spotted problems (for future objectives)",
                font=ctk.CTkFont(size=12), text_color="#f59e0b",
            ).pack(anchor="w", pady=(4, 2))
            self.spotted_problems = ctk.CTkTextbox(
                oc, height=40, font=ctk.CTkFont(size=13),
                fg_color=COLORS["bg_input"], text_color=COLORS["text"],
                border_width=1, border_color="#f59e0b", corner_radius=6,
            )
            self.spotted_problems.pack(fill="x", pady=(0, 4))
            self.spotted_problems.insert("1.0", er.get("spotted_problems", ""))
        else:
            self.concept_tag_selector = None
            self.notes = None
            self.spotted_problems = None

        # ┌─────────────────────────────────────────────────┐
        # │  CARD 4: Matchup Notes (optional)               │
        # └─────────────────────────────────────────────────┘
        # === MATCHUP SECTION ===
        self._matchup_helpful_widgets: list[dict] = []

        matchup_card = self._card(parent, border_color=COLORS["accent_gold"])
        matchup_card.pack(fill="x", pady=(0, 12))
        mc = ctk.CTkFrame(matchup_card, fg_color="transparent")
        mc.pack(fill="x", padx=16, pady=14)

        ctk.CTkLabel(
            mc, text="MATCHUP",
            font=ctk.CTkFont(size=11, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(anchor="w", pady=(0, 6))

        if matchup_notes_shown:
            ctk.CTkLabel(
                mc, text="Were these matchup notes helpful?",
                font=ctk.CTkFont(size=12), text_color=COLORS["text_dim"],
            ).pack(anchor="w", pady=(0, 6))

            for mn in matchup_notes_shown:
                mn_row = ctk.CTkFrame(mc, fg_color=COLORS["bg_input"], corner_radius=6)
                mn_row.pack(fill="x", pady=(0, 6))
                mn_inner = ctk.CTkFrame(mn_row, fg_color="transparent")
                mn_inner.pack(fill="x", padx=10, pady=6)

                ctk.CTkLabel(
                    mn_inner, text=mn.get("note", ""),
                    font=ctk.CTkFont(size=12), text_color=COLORS["text"],
                    wraplength=550, justify="left",
                ).pack(anchor="w", pady=(0, 4))

                btn_row = ctk.CTkFrame(mn_inner, fg_color="transparent")
                btn_row.pack(anchor="w")
                helpful_var = tk.IntVar(value=-1)
                helpful_btn = ctk.CTkButton(
                    btn_row, text="Helpful",
                    font=ctk.CTkFont(size=10), height=22, width=70, corner_radius=11,
                    fg_color=COLORS["tag_bg"], hover_color="#1a4d2e",
                    text_color=COLORS["text_dim"],
                )
                helpful_btn.pack(side="left", padx=(0, 4))
                not_helpful_btn = ctk.CTkButton(
                    btn_row, text="Not helpful",
                    font=ctk.CTkFont(size=10), height=22, width=85, corner_radius=11,
                    fg_color=COLORS["tag_bg"], hover_color="#4d1a1a",
                    text_color=COLORS["text_dim"],
                )
                not_helpful_btn.pack(side="left")

                def _on_helpful(v=helpful_var, hb=helpful_btn, nhb=not_helpful_btn):
                    v.set(1)
                    hb.configure(fg_color="#1a4d2e", text_color=COLORS["text"])
                    nhb.configure(fg_color=COLORS["tag_bg"], text_color=COLORS["text_dim"])

                def _on_not_helpful(v=helpful_var, hb=helpful_btn, nhb=not_helpful_btn):
                    v.set(0)
                    nhb.configure(fg_color="#4d1a1a", text_color=COLORS["text"])
                    hb.configure(fg_color=COLORS["tag_bg"], text_color=COLORS["text_dim"])

                helpful_btn.configure(command=_on_helpful)
                not_helpful_btn.configure(command=_on_not_helpful)
                self._matchup_helpful_widgets.append({
                    "note_id": mn.get("id"), "helpful_var": helpful_var,
                })

            ctk.CTkFrame(mc, fg_color=COLORS["border"], height=1).pack(fill="x", pady=8)

        # Add matchup note
        enemy_row = ctk.CTkFrame(mc, fg_color="transparent")
        enemy_row.pack(fill="x", pady=(0, 4))
        ctk.CTkLabel(
            enemy_row, text="Enemy:", font=ctk.CTkFont(size=12),
            text_color=COLORS["text_dim"], width=50,
        ).pack(side="left")
        self.matchup_enemy_entry = ctk.CTkEntry(
            enemy_row, height=30, font=ctk.CTkFont(size=12),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=6,
            placeholder_text="e.g., Zed",
        )
        self.matchup_enemy_entry.pack(side="left", fill="x", expand=True, padx=(4, 0))
        if enemy_laner:
            self.matchup_enemy_entry.insert(0, enemy_laner)

        self.matchup_note_entry = ctk.CTkTextbox(
            mc, height=45, font=ctk.CTkFont(size=12),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=6,
        )
        self.matchup_note_entry.pack(fill="x", pady=(4, 0))

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

    def _select_attribution(self, key: str):
        """Highlight the selected attribution button."""
        self._attribution_var = key
        for k, btn in self._attribution_buttons.items():
            if k == key:
                btn.configure(fg_color=COLORS["accent_blue"], text_color="#ffffff")
            else:
                btn.configure(fg_color=COLORS["tag_bg"], text_color=COLORS["text_dim"])

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
            "notes": self.notes.get("1.0", "end-1c").strip() if self.notes else "",
            "rating": 0,
            "mental_rating": self._mental_rating,
            "mistakes": "",
            "went_well": self.went_well.get("1.0", "end-1c").strip(),
            "focus_next": self.focus_next.get().strip(),
            "spotted_problems": self.spotted_problems.get("1.0", "end-1c").strip() if self.spotted_problems else "",
            "outside_control": (
                self.outside_control.get("1.0", "end-1c").strip()
                if self.outside_control else ""
            ),
            "within_control": (
                self.within_control.get("1.0", "end-1c").strip()
                if self.within_control else ""
            ),
            "attribution": self._attribution_var,
            "personal_contribution": (
                self.personal_contribution.get("1.0", "end-1c").strip()
                if self.personal_contribution else ""
            ),
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

    def update_bookmarks(self, bookmarks: list[dict]):
        """Refresh the bookmarks section with new data (e.g. after VOD player)."""
        self._bookmarks = bookmarks
        for child in self._bookmarks_frame.winfo_children():
            child.destroy()
        if self._bookmarks:
            self._build_bookmarks_section(self._bookmarks_frame)


# Legacy alias — kept so existing imports don't break
ReviewWindow = ReviewPanel
