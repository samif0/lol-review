"""Loss review window for analyzing losses."""

import json

import customtkinter as ctk

from ..constants import COLORS, format_duration, format_number
from .game_review import SessionGameReviewWindow


class ReviewLossesWindow(ctk.CTkToplevel):
    """Window showing only losses with review notes for between-game review.

    Displays: champion, date, KDA, mistakes, focus for next game, and tags.
    Filterable by champion to focus on specific matchup improvements.
    """

    def __init__(self, db, on_open_vod=None, *args, **kwargs):
        super().__init__(*args, **kwargs)

        self.db = db
        self._on_open_vod = on_open_vod
        self.selected_champion = "All Champions"
        self._review_popup = None

        self.title("LoL Review — Loss Review")
        self.geometry("920x750")
        self.configure(fg_color=COLORS["bg_dark"])
        self.minsize(800, 600)

        # Bring to front
        self.lift()
        self.attributes("-topmost", True)
        self.after(100, lambda: self.attributes("-topmost", False))

        # Main container
        main_frame = ctk.CTkFrame(self, fg_color=COLORS["bg_dark"])
        main_frame.pack(fill="both", expand=True, padx=12, pady=12)

        # Header
        header_frame = ctk.CTkFrame(main_frame, fg_color="transparent")
        header_frame.pack(fill="x", pady=(0, 12))

        ctk.CTkLabel(
            header_frame,
            text="LOSS REVIEW",
            font=ctk.CTkFont(size=24, weight="bold"),
            text_color=COLORS["loss_red"],
        ).pack(side="left")

        ctk.CTkLabel(
            header_frame,
            text="What to improve before your next game",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text_dim"],
        ).pack(side="left", padx=(12, 0))

        # Filter section
        filter_frame = ctk.CTkFrame(main_frame, fg_color=COLORS["bg_card"], corner_radius=8)
        filter_frame.pack(fill="x", pady=(0, 12))

        filter_inner = ctk.CTkFrame(filter_frame, fg_color="transparent")
        filter_inner.pack(fill="x", padx=12, pady=10)

        ctk.CTkLabel(
            filter_inner,
            text="Filter by Champion:",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
        ).pack(side="left", padx=(0, 8))

        # Get unique champions from losses
        champions = ["All Champions"] + self.db.get_unique_champions(losses_only=True)

        self.champion_dropdown = ctk.CTkComboBox(
            filter_inner,
            values=champions,
            width=200,
            command=self._on_champion_filter_change,
            fg_color=COLORS["bg_input"],
            button_color=COLORS["accent_blue"],
            button_hover_color="#0077cc",
            border_color=COLORS["border"],
        )
        self.champion_dropdown.set("All Champions")
        self.champion_dropdown.pack(side="left")

        # Scrollable losses list
        self.scroll_frame = ctk.CTkScrollableFrame(
            main_frame,
            fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        self.scroll_frame.pack(fill="both", expand=True)

        # Load initial data
        self._refresh_losses()

    def _on_champion_filter_change(self, choice: str):
        """Handle champion filter dropdown change."""
        self.selected_champion = choice
        self._refresh_losses()

    def _refresh_losses(self):
        """Reload the losses list based on current filter."""
        # Clear existing content
        for widget in self.scroll_frame.winfo_children():
            widget.destroy()

        # Get filtered losses
        champion_filter = None if self.selected_champion == "All Champions" else self.selected_champion
        losses = self.db.get_losses(champion=champion_filter)

        if not losses:
            ctk.CTkLabel(
                self.scroll_frame,
                text="No losses recorded yet.\nKeep playing — everyone has losses to learn from!",
                font=ctk.CTkFont(size=14),
                text_color=COLORS["text_dim"],
            ).pack(pady=40)
            return

        # Build loss cards
        for loss in losses:
            self._build_loss_card(self.scroll_frame, loss)

    def _build_loss_card(self, parent, loss: dict):
        """Build a single loss review card with review button."""
        # Determine review status
        has_review = bool(
            loss.get("mistakes", "").strip()
            or loss.get("went_well", "").strip()
            or loss.get("focus_next", "").strip()
        )

        card = ctk.CTkFrame(
            parent,
            fg_color=COLORS["bg_card"],
            corner_radius=8,
            border_width=2,
            border_color=COLORS["loss_red"],
        )
        card.pack(fill="x", pady=6, padx=4)

        inner = ctk.CTkFrame(card, fg_color="transparent")
        inner.pack(fill="both", expand=True, padx=14, pady=12)

        # Top row: Champion + date + KDA
        top_row = ctk.CTkFrame(inner, fg_color="transparent")
        top_row.pack(fill="x", pady=(0, 8))

        # Left: Champion name
        left_section = ctk.CTkFrame(top_row, fg_color="transparent")
        left_section.pack(side="left", fill="x", expand=True)

        ctk.CTkLabel(
            left_section,
            text=loss.get("champion_name", "Unknown"),
            font=ctk.CTkFont(size=18, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(anchor="w")

        date_mode = f"{loss.get('date_played', '')}  •  {format_duration(loss.get('game_duration', 0))}  •  {loss.get('game_mode', '')}"
        ctk.CTkLabel(
            left_section,
            text=date_mode,
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w")

        # Right: KDA + review button
        right_section = ctk.CTkFrame(top_row, fg_color="transparent")
        right_section.pack(side="right")

        k, d, a = loss.get("kills", 0), loss.get("deaths", 0), loss.get("assists", 0)
        kda = loss.get("kda_ratio", 0)

        ctk.CTkLabel(
            right_section,
            text=f"{k}/{d}/{a}  ({kda:.2f} KDA)",
            font=ctk.CTkFont(size=16, weight="bold"),
            text_color=COLORS["text"],
        ).pack(anchor="e")

        # Button row: Review + Watch VOD
        btn_row = ctk.CTkFrame(right_section, fg_color="transparent")
        btn_row.pack(anchor="e", pady=(4, 0))

        review_btn_text = "Edit Review" if has_review else "Review"
        review_btn_color = COLORS["tag_bg"] if has_review else COLORS["accent_blue"]

        ctk.CTkButton(
            btn_row,
            text=review_btn_text,
            font=ctk.CTkFont(size=11),
            height=26, width=100, corner_radius=6,
            fg_color=review_btn_color,
            hover_color="#0077cc",
            command=lambda g=loss: self._open_loss_review(g),
        ).pack(side="left", padx=(0, 6))

        # Watch VOD button — only if a recording is linked
        game_id = loss.get("game_id")
        if game_id and self.db.get_vod(game_id):
            ctk.CTkButton(
                btn_row,
                text="Watch VOD",
                font=ctk.CTkFont(size=11, weight="bold"),
                height=26, width=100, corner_radius=6,
                fg_color=COLORS["accent_gold"], hover_color="#a88432",
                text_color="#0a0a0f",
                command=lambda gid=game_id: self._on_open_vod(gid) if self._on_open_vod else None,
            ).pack(side="left")

        # Show tags if present
        tags = json.loads(loss.get("tags", "[]")) if isinstance(loss.get("tags"), str) else loss.get("tags", [])
        if tags:
            tag_frame = ctk.CTkFrame(inner, fg_color="transparent")
            tag_frame.pack(fill="x", pady=(0, 6))

            for tag in tags:
                tag_label = ctk.CTkLabel(
                    tag_frame,
                    text=f"  {tag}  ",
                    font=ctk.CTkFont(size=11),
                    fg_color=COLORS["tag_bg"],
                    corner_radius=12,
                    text_color=COLORS["accent_blue"],
                )
                tag_label.pack(side="left", padx=2)

        # Separator
        ctk.CTkFrame(inner, fg_color=COLORS["border"], height=1).pack(fill="x", pady=8)

        # Mistakes section
        mistakes = loss.get("mistakes", "").strip()
        if mistakes:
            ctk.CTkLabel(
                inner,
                text="What to improve:",
                font=ctk.CTkFont(size=12, weight="bold"),
                text_color=COLORS["star_active"],
            ).pack(anchor="w", pady=(0, 4))

            mistakes_box = ctk.CTkTextbox(
                inner,
                height=65,
                font=ctk.CTkFont(size=12),
                fg_color=COLORS["bg_input"],
                text_color=COLORS["text"],
                border_width=1,
                border_color=COLORS["border"],
                corner_radius=6,
                wrap="word",
            )
            mistakes_box.pack(fill="x", pady=(0, 8))
            mistakes_box.insert("1.0", mistakes)
            mistakes_box.configure(state="disabled")

        # Focus for next game section
        focus_next = loss.get("focus_next", "").strip()
        if focus_next:
            ctk.CTkLabel(
                inner,
                text="Focus for next game:",
                font=ctk.CTkFont(size=12, weight="bold"),
                text_color=COLORS["accent_blue"],
            ).pack(anchor="w", pady=(0, 4))

            focus_box = ctk.CTkFrame(
                inner,
                fg_color=COLORS["bg_input"],
                border_width=2,
                border_color=COLORS["accent_blue"],
                corner_radius=6,
            )
            focus_box.pack(fill="x", pady=(0, 8))

            ctk.CTkLabel(
                focus_box,
                text=focus_next,
                font=ctk.CTkFont(size=13),
                text_color=COLORS["text"],
                wraplength=840,
                justify="left",
            ).pack(padx=12, pady=10, anchor="w")

        # General notes if present
        notes = loss.get("review_notes", "").strip()
        if notes:
            ctk.CTkLabel(
                inner,
                text="Additional notes:",
                font=ctk.CTkFont(size=11),
                text_color=COLORS["text_dim"],
            ).pack(anchor="w", pady=(0, 4))

            notes_box = ctk.CTkTextbox(
                inner,
                height=50,
                font=ctk.CTkFont(size=11),
                fg_color=COLORS["bg_input"],
                text_color=COLORS["text_dim"],
                border_width=1,
                border_color=COLORS["border"],
                corner_radius=6,
                wrap="word",
            )
            notes_box.pack(fill="x")
            notes_box.insert("1.0", notes)
            notes_box.configure(state="disabled")


    def _open_loss_review(self, loss: dict):
        """Open a review popup for a loss from the loss review list."""
        if self._review_popup and self._review_popup.winfo_exists():
            self._review_popup.destroy()

        # Build a session_entry-like dict for SessionGameReviewWindow
        game_id = loss.get("game_id")
        session_entry = {
            "game_id": game_id,
            "champion_name": loss.get("champion_name"),
            "win": 0,
            "mental_rating": 5,
        }

        # Check for linked VOD
        vod_info = self.db.get_vod(game_id) if game_id else None
        has_vod = vod_info is not None
        bookmark_count = self.db.get_bookmark_count(game_id) if has_vod else 0

        self._review_popup = SessionGameReviewWindow(
            db=self.db,
            session_entry=session_entry,
            game_data=loss,
            on_save=self._refresh_losses,
            on_open_vod=self._on_open_vod,
            has_vod=has_vod,
            bookmark_count=bookmark_count,
        )
