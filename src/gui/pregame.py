"""Pre-game focus reminder window."""

from typing import Callable, Optional

import customtkinter as ctk

from ..constants import COLORS


class PreGameWindow(ctk.CTkToplevel):
    """Pre-game focus reminder that appears during champ select.

    Shows your current learning objective and last-game focus, then lets
    you set gameplay and mental intentions for the upcoming match.
    Auto-closes when loading screen begins, or when you hit "I'm Ready".
    """

    def __init__(
        self,
        last_focus: str = "",
        last_mistakes: str = "",
        last_mental_intention: str = "",
        on_dismiss: Optional[Callable] = None,
        active_objective: Optional[dict] = None,
        matchup_notes: list[dict] = None,
        *args,
        **kwargs,
    ):
        super().__init__(*args, **kwargs)

        self.on_dismiss = on_dismiss
        self._active_objective = active_objective

        # Window setup — compact, stays on top during champ select
        self.title("Pre-Game Focus")
        self.geometry("460x520")
        self.configure(fg_color=COLORS["bg_dark"])
        self.resizable(True, True)
        self.minsize(400, 400)

        self.lift()
        self.attributes("-topmost", True)
        self.focus_force()
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

        # === ACTIVE LEARNING OBJECTIVE ===
        if self._active_objective:
            obj = self._active_objective
            obj_frame = ctk.CTkFrame(
                container,
                fg_color=COLORS["bg_card"],
                corner_radius=8,
                border_width=2,
                border_color=COLORS["accent_blue"],
            )
            obj_frame.pack(fill="x", pady=(0, 12))

            ctk.CTkLabel(
                obj_frame,
                text="YOUR CURRENT OBJECTIVE",
                font=ctk.CTkFont(size=11, weight="bold"),
                text_color=COLORS["accent_blue"],
            ).pack(padx=14, pady=(10, 4), anchor="w")

            ctk.CTkLabel(
                obj_frame,
                text=obj["title"],
                font=ctk.CTkFont(size=14, weight="bold"),
                text_color=COLORS["text"],
                wraplength=380, justify="left",
            ).pack(padx=14, pady=(0, 4), anchor="w")

            if obj.get("completion_criteria"):
                ctk.CTkLabel(
                    obj_frame,
                    text=f"Success: {obj['completion_criteria']}",
                    font=ctk.CTkFont(size=12),
                    text_color=COLORS["text_dim"],
                    wraplength=380, justify="left",
                ).pack(padx=14, pady=(0, 10), anchor="w")
            else:
                ctk.CTkFrame(obj_frame, height=6, fg_color="transparent").pack()

            # Pre-fill focus with objective title if no prior focus
            if not last_focus:
                last_focus = obj["title"]

        # === LAST GAME'S FOCUS ===
        if last_focus:
            focus_frame = ctk.CTkFrame(
                container,
                fg_color=COLORS["bg_card"],
                corner_radius=8,
                border_width=1,
                border_color=COLORS["border"],
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
                wraplength=380,
                justify="left",
            ).pack(padx=14, pady=(0, 10), anchor="w")

        # === WHAT YOU WANTED TO IMPROVE ===
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
                wraplength=380,
                justify="left",
            ).pack(padx=14, pady=(0, 10), anchor="w")

        # === MATCHUP NOTES ===
        if matchup_notes:
            matchup_frame = ctk.CTkFrame(
                container,
                fg_color=COLORS["bg_card"],
                corner_radius=8,
                border_width=2,
                border_color=COLORS["accent_gold"],
            )
            matchup_frame.pack(fill="x", pady=(0, 12))

            ctk.CTkLabel(
                matchup_frame,
                text="MATCHUP NOTES",
                font=ctk.CTkFont(size=11, weight="bold"),
                text_color=COLORS["accent_gold"],
            ).pack(padx=14, pady=(10, 4), anchor="w")

            for mn in matchup_notes:
                note_row = ctk.CTkFrame(matchup_frame, fg_color="transparent")
                note_row.pack(fill="x", padx=14, pady=(0, 4))

                ctk.CTkLabel(
                    note_row,
                    text=mn.get("note", ""),
                    font=ctk.CTkFont(size=13),
                    text_color=COLORS["text"],
                    wraplength=380,
                    justify="left",
                ).pack(side="left", fill="x", expand=True)

                if mn.get("helpful") == 1:
                    ctk.CTkLabel(
                        note_row,
                        text="Helpful",
                        font=ctk.CTkFont(size=9, weight="bold"),
                        text_color="#22c55e",
                        fg_color="#1a4d2e",
                        corner_radius=6,
                        padx=6, pady=1,
                    ).pack(side="right", padx=(6, 0))
                elif mn.get("helpful") == 0:
                    ctk.CTkLabel(
                        note_row,
                        text="Not helpful",
                        font=ctk.CTkFont(size=9, weight="bold"),
                        text_color=COLORS["loss_red"],
                        fg_color="#4d1a1a",
                        corner_radius=6,
                        padx=6, pady=1,
                    ).pack(side="right", padx=(6, 0))

            # Bottom padding
            ctk.CTkFrame(matchup_frame, height=6, fg_color="transparent").pack()

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

        if last_focus:
            self.focus_entry.insert("1.0", last_focus)

        # Quick-select focus buttons
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

        # Separator
        ctk.CTkFrame(
            container, fg_color=COLORS["border"], height=1
        ).pack(fill="x", pady=8)

        # === MENTAL INTENTION ===
        ctk.CTkLabel(
            container,
            text="Mental intention this game?",
            font=ctk.CTkFont(size=14, weight="bold"),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(4, 2))

        ctk.CTkLabel(
            container,
            text="Expect it before it happens. Set how you'll respond — before the tilt hits.",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
            wraplength=380,
            justify="left",
        ).pack(anchor="w", pady=(0, 6))

        self.mental_intention_entry = ctk.CTkTextbox(
            container,
            height=60,
            font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"],
            text_color=COLORS["text"],
            border_width=1,
            border_color=COLORS["border"],
            corner_radius=8,
        )
        self.mental_intention_entry.pack(fill="x", pady=(0, 6))

        if last_mental_intention:
            self.mental_intention_entry.insert("1.0", last_mental_intention)

        # Quick mental intention buttons
        mental_quick_frame = ctk.CTkFrame(container, fg_color="transparent")
        mental_quick_frame.pack(fill="x", pady=(0, 12))

        quick_mental = [
            "Bad call \u2192 one ping",
            "Ints \u2192 my game",
            "Behind \u2192 breathe",
            "I mess up \u2192 let go",
        ]
        for text in quick_mental:
            btn = ctk.CTkButton(
                mental_quick_frame,
                text=text,
                font=ctk.CTkFont(size=11),
                height=26,
                corner_radius=13,
                fg_color=COLORS["tag_bg"],
                hover_color="#7c3aed",
                text_color=COLORS["text_dim"],
                border_width=1,
                border_color=COLORS["border"],
                command=lambda t=text: self._set_quick_mental(t),
            )
            btn.pack(side="left", padx=3, pady=2)

        # === READY BUTTON ===
        ctk.CTkButton(
            container,
            text="I'm Ready \u2014 GL HF!",
            font=ctk.CTkFont(size=16, weight="bold"),
            height=48,
            corner_radius=8,
            fg_color=COLORS["win_green"],
            hover_color="#1ea05a",
            text_color="#ffffff",
            command=self._dismiss,
        ).pack(fill="x", pady=(8, 4))

        ctk.CTkLabel(
            container,
            text="This window will close automatically when your game loads.",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        ).pack(pady=(4, 0))

    def _set_quick_focus(self, text: str):
        self.focus_entry.delete("1.0", "end")
        self.focus_entry.insert("1.0", text)

    def _set_quick_mental(self, text: str):
        self.mental_intention_entry.delete("1.0", "end")
        self.mental_intention_entry.insert("1.0", text)

    def get_focus_text(self) -> str:
        return self.focus_entry.get("1.0", "end-1c").strip()

    def get_mental_intention_text(self) -> str:
        return self.mental_intention_entry.get("1.0", "end-1c").strip()

    def _dismiss(self):
        if self.on_dismiss:
            self.on_dismiss(self.get_focus_text(), self.get_mental_intention_text())
        self.destroy()

    def auto_close(self):
        if self.winfo_exists():
            if self.on_dismiss:
                self.on_dismiss(self.get_focus_text(), self.get_mental_intention_text())
            self.destroy()
