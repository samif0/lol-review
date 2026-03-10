"""Pre-game focus reminder window."""

from typing import Callable, Optional

import customtkinter as ctk

from ..constants import COLORS


class PreGameWindow(ctk.CTkToplevel):
    """Pre-game focus reminder that appears during champ select.

    Shows your "focus for next game" from the last review, your recent
    performance trends, and lets you set specific intentions before the
    match starts. Auto-closes when loading screen begins, or when you
    hit the "I'm Ready" button.
    """

    def __init__(
        self,
        last_focus: str = "",
        last_mistakes: str = "",
        recent_games: list[dict] = None,
        streak: int = 0,
        on_dismiss: Optional[Callable] = None,
        *args,
        **kwargs,
    ):
        super().__init__(*args, **kwargs)

        self.on_dismiss = on_dismiss
        recent_games = recent_games or []

        # Window setup — compact, stays on top so you see it during champ select
        self.title("Pre-Game Focus")
        self.geometry("480x620")
        self.configure(fg_color=COLORS["bg_dark"])
        self.resizable(True, True)
        self.minsize(420, 500)

        # Keep on top during champ select
        self.lift()
        self.attributes("-topmost", True)
        self.focus_force()

        # Handle window close (X button) gracefully
        self.protocol("WM_DELETE_WINDOW", self._dismiss)

        # Main container
        container = ctk.CTkScrollableFrame(
            self,
            fg_color=COLORS["bg_dark"],
            scrollbar_button_color=COLORS["border"],
        )
        container.pack(fill="both", expand=True, padx=16, pady=16)

        # === HEADER ===
        ctk.CTkLabel(
            container,
            text="BEFORE YOU QUEUE UP...",
            font=ctk.CTkFont(size=20, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(anchor="w", pady=(0, 4))

        ctk.CTkLabel(
            container,
            text="Take a moment to set your intentions for this game.",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 12))

        # === STREAK INDICATOR ===
        if streak != 0:
            streak_frame = ctk.CTkFrame(
                container,
                fg_color=COLORS["bg_card"],
                corner_radius=8,
                border_width=1,
                border_color=COLORS["border"],
            )
            streak_frame.pack(fill="x", pady=(0, 12))

            if streak > 0:
                streak_text = f"You're on a {streak}-game win streak!"
                streak_color = COLORS["win_green"]
                streak_emoji = "  "  # keep it text-only per guidelines
                streak_sub = "Keep the momentum going."
            else:
                streak_text = f"You've lost {abs(streak)} in a row."
                streak_color = COLORS["loss_red"]
                streak_emoji = ""
                streak_sub = "Take a breath. Focus on what you can control."

            ctk.CTkLabel(
                streak_frame,
                text=f"{streak_emoji}{streak_text}",
                font=ctk.CTkFont(size=14, weight="bold"),
                text_color=streak_color,
            ).pack(padx=14, pady=(10, 2), anchor="w")

            ctk.CTkLabel(
                streak_frame,
                text=streak_sub,
                font=ctk.CTkFont(size=12),
                text_color=COLORS["text_dim"],
            ).pack(padx=14, pady=(0, 10), anchor="w")

        # === LAST GAME'S FOCUS (from your last review) ===
        if last_focus:
            focus_frame = ctk.CTkFrame(
                container,
                fg_color=COLORS["bg_card"],
                corner_radius=8,
                border_width=2,
                border_color=COLORS["accent_blue"],
            )
            focus_frame.pack(fill="x", pady=(0, 12))

            ctk.CTkLabel(
                focus_frame,
                text="YOUR FOCUS FROM LAST GAME",
                font=ctk.CTkFont(size=11, weight="bold"),
                text_color=COLORS["accent_blue"],
            ).pack(padx=14, pady=(10, 4), anchor="w")

            ctk.CTkLabel(
                focus_frame,
                text=last_focus,
                font=ctk.CTkFont(size=14),
                text_color=COLORS["text"],
                wraplength=400,
                justify="left",
            ).pack(padx=14, pady=(0, 10), anchor="w")

        # === LAST GAME'S MISTAKES (quick reminder) ===
        if last_mistakes:
            mistakes_frame = ctk.CTkFrame(
                container,
                fg_color=COLORS["bg_card"],
                corner_radius=8,
                border_width=1,
                border_color=COLORS["border"],
            )
            mistakes_frame.pack(fill="x", pady=(0, 12))

            ctk.CTkLabel(
                mistakes_frame,
                text="WHAT YOU WANTED TO IMPROVE",
                font=ctk.CTkFont(size=11, weight="bold"),
                text_color=COLORS["star_active"],
            ).pack(padx=14, pady=(10, 4), anchor="w")

            ctk.CTkLabel(
                mistakes_frame,
                text=last_mistakes,
                font=ctk.CTkFont(size=13),
                text_color=COLORS["text_dim"],
                wraplength=400,
                justify="left",
            ).pack(padx=14, pady=(0, 10), anchor="w")

        # === RECENT FORM (last 5 games mini-summary) ===
        if recent_games:
            ctk.CTkLabel(
                container,
                text="RECENT FORM",
                font=ctk.CTkFont(size=11, weight="bold"),
                text_color=COLORS["text_dim"],
            ).pack(anchor="w", pady=(4, 6))

            form_frame = ctk.CTkFrame(container, fg_color="transparent")
            form_frame.pack(fill="x", pady=(0, 12))

            for game in recent_games[:5]:
                is_win = bool(game.get("win"))
                color = COLORS["win_green"] if is_win else COLORS["loss_red"]
                result = "W" if is_win else "L"
                champ = game.get("champion_name", "?")
                kda = f"{game.get('kills', 0)}/{game.get('deaths', 0)}/{game.get('assists', 0)}"

                pill = ctk.CTkFrame(
                    form_frame,
                    fg_color=COLORS["bg_card"],
                    corner_radius=6,
                    border_width=1,
                    border_color=color,
                )
                pill.pack(side="left", padx=3, pady=2)

                ctk.CTkLabel(
                    pill,
                    text=f"{result} {champ} {kda}",
                    font=ctk.CTkFont(size=11),
                    text_color=color,
                ).pack(padx=8, pady=5)

        # Separator
        ctk.CTkFrame(
            container, fg_color=COLORS["border"], height=1
        ).pack(fill="x", pady=8)

        # === SET YOUR FOCUS FOR THIS GAME ===
        ctk.CTkLabel(
            container,
            text="What's your focus this game?",
            font=ctk.CTkFont(size=14, weight="bold"),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 4))

        self.focus_entry = ctk.CTkTextbox(
            container,
            height=70,
            font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"],
            text_color=COLORS["text"],
            border_width=1,
            border_color=COLORS["border"],
            corner_radius=8,
        )
        self.focus_entry.pack(fill="x", pady=(0, 6))

        # Pre-fill with last game's focus so they can keep or modify it
        if last_focus:
            self.focus_entry.insert("1.0", last_focus)

        # Quick-select focus buttons
        ctk.CTkLabel(
            container,
            text="Quick picks:",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(2, 4))

        quick_frame = ctk.CTkFrame(container, fg_color="transparent")
        quick_frame.pack(fill="x", pady=(0, 12))

        quick_focuses = [
            "CS better early",
            "Track enemy JG",
            "Don't die before 6",
            "Play for teamfights",
            "Ward more",
            "Roam after push",
        ]
        for text in quick_focuses:
            btn = ctk.CTkButton(
                quick_frame,
                text=text,
                font=ctk.CTkFont(size=11),
                height=26,
                corner_radius=13,
                fg_color=COLORS["tag_bg"],
                hover_color=COLORS["accent_blue"],
                text_color=COLORS["text_dim"],
                border_width=1,
                border_color=COLORS["border"],
                command=lambda t=text: self._set_quick_focus(t),
            )
            btn.pack(side="left", padx=3, pady=2)

        # === READY BUTTON ===
        ready_btn = ctk.CTkButton(
            container,
            text="I'm Ready — GL HF!",
            font=ctk.CTkFont(size=16, weight="bold"),
            height=48,
            corner_radius=8,
            fg_color=COLORS["win_green"],
            hover_color="#1ea05a",
            text_color="#ffffff",
            command=self._dismiss,
        )
        ready_btn.pack(fill="x", pady=(8, 4))

        # Subtle note about auto-close
        ctk.CTkLabel(
            container,
            text="This window will close automatically when your game loads.",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        ).pack(pady=(4, 0))

    def _set_quick_focus(self, text: str):
        """Replace focus text with a quick pick."""
        self.focus_entry.delete("1.0", "end")
        self.focus_entry.insert("1.0", text)

    def get_focus_text(self) -> str:
        """Get whatever the user typed/selected as their focus."""
        return self.focus_entry.get("1.0", "end-1c").strip()

    def _dismiss(self):
        """Close the window and fire callback."""
        if self.on_dismiss:
            self.on_dismiss(self.get_focus_text())
        self.destroy()

    def auto_close(self):
        """Called by the monitor when loading screen starts."""
        if self.winfo_exists():
            if self.on_dismiss:
                self.on_dismiss(self.get_focus_text())
            self.destroy()
