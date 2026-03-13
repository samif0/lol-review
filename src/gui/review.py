"""Post-game review popup window."""

import json
import tkinter as tk
from typing import Callable, Optional

import customtkinter as ctk

from ..config import is_ascent_enabled
from ..constants import COLORS, format_duration, format_number
from ..lcu import GameStats
from ..vod import format_game_time
from .widgets import StarRating, TagSelector, StatCard


class ReviewWindow(ctk.CTkToplevel):
    """The main post-game review popup window."""

    def __init__(
        self,
        stats: GameStats,
        tags: list[dict],
        existing_review: Optional[dict] = None,
        on_save: Optional[Callable] = None,
        on_open_vod: Optional[Callable] = None,
        has_vod: bool = False,
        bookmark_count: int = 0,
        bookmarks: Optional[list[dict]] = None,
        pregame_intention: str = "",
        existing_mental_handled: str = "",
        *args,
        **kwargs,
    ):
        super().__init__(*args, **kwargs)

        self.stats = stats
        self.on_save = on_save
        self._on_open_vod = on_open_vod
        self._has_vod = has_vod
        self._bookmark_count = bookmark_count
        self._bookmarks = bookmarks or []
        self._pregame_intention = pregame_intention
        self._existing_mental_handled = existing_mental_handled

        # Window setup
        self.title("LoL Game Review")
        self.geometry("780x900")
        self.configure(fg_color=COLORS["bg_dark"])
        self.resizable(True, True)
        self.minsize(700, 700)

        # Bring to front
        self.lift()
        self.attributes("-topmost", True)
        self.after(100, lambda: self.attributes("-topmost", False))
        self.focus_force()

        # Load existing review data if editing
        er = existing_review or {}

        # Main scrollable container
        container = ctk.CTkScrollableFrame(
            self,
            fg_color=COLORS["bg_dark"],
            scrollbar_button_color=COLORS["border"],
        )
        container.pack(fill="both", expand=True, padx=16, pady=16)

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
        self._build_review_section(container, tags, er, self._pregame_intention, self._existing_mental_handled)

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
            command=self.destroy,
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

        kda_color = COLORS["win_green"] if s.kda_ratio >= 3.0 else (
            COLORS["accent_gold"] if s.kda_ratio >= 2.0 else COLORS["text_dim"]
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

        if phys_pct > 0.02:
            phys_bar = ctk.CTkFrame(bar_inner, fg_color="#e74c3c", corner_radius=4)
            phys_bar.place(relx=0, rely=0, relwidth=phys_pct, relheight=1)

        if magic_pct > 0.02:
            magic_bar = ctk.CTkFrame(bar_inner, fg_color="#3498db", corner_radius=4)
            magic_bar.place(relx=phys_pct, rely=0, relwidth=magic_pct, relheight=1)

        if true_pct > 0.02:
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
        tags: list[dict],
        er: dict,
        pregame_intention: str = "",
        existing_mental_handled: str = "",
    ):
        """The note-taking and review fields.

        Focused on learning objectives rather than play-by-play.
        Specific moments should be captured as VOD bookmarks instead.
        """
        ctk.CTkLabel(
            parent,
            text="YOUR REVIEW",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(8, 6))

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

        # Rating
        rating_row = ctk.CTkFrame(parent, fg_color="transparent")
        rating_row.pack(fill="x", pady=(0, 8))
        ctk.CTkLabel(
            rating_row,
            text="Performance Rating",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(side="left")
        self.star_rating = StarRating(rating_row, initial=er.get("rating", 0))
        self.star_rating.pack(side="right")

        # Tags
        ctk.CTkLabel(
            parent,
            text="Tags",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        existing_tags = json.loads(er.get("tags", "[]")) if isinstance(er.get("tags"), str) else er.get("tags", [])
        self.tag_selector = TagSelector(parent, tags, selected=existing_tags)
        self.tag_selector.pack(fill="x", pady=(0, 10))

        # Learning objective question — replaces "what went well / what went wrong"
        ctk.CTkLabel(
            parent,
            text="Did you work toward your learning objective?",
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

        # Takeaway — replaces "what could you improve"
        ctk.CTkLabel(
            parent,
            text="Key takeaway from this game",
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

        # VOD hint — encourage using bookmarks for specifics
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

    def _save(self):
        """Collect review data and fire the save callback."""
        review_data = {
            "game_id": self.stats.game_id,
            "notes": self.notes.get("1.0", "end-1c").strip(),
            "rating": self.star_rating.get(),
            "tags": self.tag_selector.get(),
            "mistakes": self.mistakes.get("1.0", "end-1c").strip(),
            "went_well": self.went_well.get("1.0", "end-1c").strip(),
            "focus_next": self.focus_next.get().strip(),
            "mental_handled": (
                self.mental_handled.get("1.0", "end-1c").strip()
                if self.mental_handled else ""
            ),
        }

        if self.on_save:
            self.on_save(review_data)

        self.destroy()
