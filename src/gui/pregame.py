"""Pre-game focus reminder window."""

from typing import Callable, Optional

import customtkinter as ctk

from ..config import is_tilt_fix_enabled
from ..constants import COLORS, MOOD_LABELS, MOOD_COLORS


class PreGameWindow(ctk.CTkToplevel):
    """Pre-game focus reminder that appears during champ select.

    Shows your current learning objective and last-game focus, then lets
    you set gameplay focus for the upcoming match.
    Auto-closes when loading screen begins, or when you hit "I'm Ready".
    """

    def __init__(
        self,
        last_focus: str = "",
        last_mistakes: str = "",
        on_dismiss: Optional[Callable] = None,
        active_objective: Optional[dict] = None,
        matchup_notes: list[dict] = None,
        is_first_game: bool = False,
        session_intention: str = "",
        *args,
        **kwargs,
    ):
        super().__init__(*args, **kwargs)

        self.on_dismiss = on_dismiss
        self._active_objective = active_objective
        self._is_first_game = is_first_game
        self._mood_value = 0
        self._mood_buttons: dict[int, ctk.CTkButton] = {}

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
            text="Take a moment to set your focus for this game.",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 12))

        tilt_fix = is_tilt_fix_enabled()

        # === PRE-GAME MOOD CHECK-IN (Lieberman et al. 2007 — affect labeling) ===
        if tilt_fix:
            mood_frame = ctk.CTkFrame(
                container, fg_color=COLORS["bg_card"], corner_radius=8,
                border_width=1, border_color=COLORS["accent_purple"],
            )
            mood_frame.pack(fill="x", pady=(0, 12))

            ctk.CTkLabel(
                mood_frame, text="HOW ARE YOU FEELING RIGHT NOW?",
                font=ctk.CTkFont(size=11, weight="bold"),
                text_color=COLORS["accent_purple"],
            ).pack(padx=14, pady=(10, 6), anchor="w")

            mood_btn_row = ctk.CTkFrame(mood_frame, fg_color="transparent")
            mood_btn_row.pack(fill="x", padx=14, pady=(0, 10))

            for val, label in MOOD_LABELS.items():
                btn = ctk.CTkButton(
                    mood_btn_row, text=label,
                    font=ctk.CTkFont(size=11), height=28, width=75,
                    corner_radius=14,
                    fg_color=COLORS["tag_bg"],
                    hover_color=MOOD_COLORS[val],
                    text_color=COLORS["text_dim"],
                    border_width=1, border_color=COLORS["border"],
                    command=lambda v=val: self._select_mood(v),
                )
                btn.pack(side="left", padx=2)
                self._mood_buttons[val] = btn

        # === SESSION INTENTION (Gollwitzer 1999 — first game only) ===
        if tilt_fix and self._is_first_game:
            intention_frame = ctk.CTkFrame(
                container, fg_color=COLORS["bg_card"], corner_radius=8,
                border_width=2, border_color=COLORS["accent_gold"],
            )
            intention_frame.pack(fill="x", pady=(0, 12))

            ctk.CTkLabel(
                intention_frame, text="SESSION MINDSET GOAL",
                font=ctk.CTkFont(size=11, weight="bold"),
                text_color=COLORS["accent_gold"],
            ).pack(padx=14, pady=(10, 2), anchor="w")

            ctk.CTkLabel(
                intention_frame,
                text='Try: "When [trigger] happens, I will [response]"',
                font=ctk.CTkFont(size=11),
                text_color=COLORS["text_dim"],
            ).pack(padx=14, pady=(0, 6), anchor="w")

            self.intention_entry = ctk.CTkTextbox(
                intention_frame, height=50, font=ctk.CTkFont(size=13),
                fg_color=COLORS["bg_input"], text_color=COLORS["text"],
                border_width=1, border_color=COLORS["border"], corner_radius=6,
            )
            self.intention_entry.pack(fill="x", padx=14, pady=(0, 6))

            if session_intention:
                self.intention_entry.insert("1.0", session_intention)

            quick_intentions = ctk.CTkFrame(intention_frame, fg_color="transparent")
            quick_intentions.pack(fill="x", padx=14, pady=(0, 10))
            for text in [
                "When I die, I'll review why",
                "When tilted, I'll take 3 breaths",
                "When behind, I'll focus on farm",
            ]:
                ctk.CTkButton(
                    quick_intentions, text=text,
                    font=ctk.CTkFont(size=10), height=22, corner_radius=11,
                    fg_color=COLORS["tag_bg"], hover_color=COLORS["accent_gold"],
                    text_color=COLORS["text_dim"],
                    border_width=1, border_color=COLORS["border"],
                    command=lambda t=text: self._set_quick_intention(t),
                ).pack(side="left", padx=2)
        else:
            self.intention_entry = None

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

    def _select_mood(self, value: int):
        self._mood_value = value
        for v, btn in self._mood_buttons.items():
            if v == value:
                btn.configure(fg_color=MOOD_COLORS[v], text_color="#ffffff")
            else:
                btn.configure(fg_color=COLORS["tag_bg"], text_color=COLORS["text_dim"])

    def _set_quick_intention(self, text: str):
        if self.intention_entry:
            self.intention_entry.delete("1.0", "end")
            self.intention_entry.insert("1.0", text)

    def get_focus_text(self) -> str:
        return self.focus_entry.get("1.0", "end-1c").strip()

    def get_mood(self) -> int:
        return self._mood_value

    def get_intention(self) -> str:
        if self.intention_entry:
            return self.intention_entry.get("1.0", "end-1c").strip()
        return ""

    def _dismiss(self):
        if self.on_dismiss:
            self.on_dismiss(self.get_focus_text(), self.get_mood(), self.get_intention())
        self.destroy()

    def auto_close(self):
        if self.winfo_exists():
            if self.on_dismiss:
                self.on_dismiss(self.get_focus_text(), self.get_mood(), self.get_intention())
            self.destroy()


class SessionDebriefWindow(ctk.CTkToplevel):
    """End-of-session debrief — did you stick to your mindset goal?

    Based on Gollwitzer 1999 implementation intentions research.
    """

    def __init__(
        self,
        intention: str = "",
        on_save: Optional[Callable] = None,
        *args,
        **kwargs,
    ):
        super().__init__(*args, **kwargs)

        self._on_save = on_save
        self._rating = 0

        self.title("Session Debrief")
        self.geometry("420x380")
        self.configure(fg_color=COLORS["bg_dark"])
        self.resizable(False, False)
        self.attributes("-topmost", True)
        self.focus_force()
        self.protocol("WM_DELETE_WINDOW", self._skip)

        container = ctk.CTkFrame(self, fg_color=COLORS["bg_dark"])
        container.pack(fill="both", expand=True, padx=16, pady=16)

        ctk.CTkLabel(
            container, text="SESSION DEBRIEF",
            font=ctk.CTkFont(size=18, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(pady=(0, 10))

        if intention:
            goal_frame = ctk.CTkFrame(
                container, fg_color=COLORS["bg_card"], corner_radius=8,
                border_width=1, border_color=COLORS["accent_gold"],
            )
            goal_frame.pack(fill="x", pady=(0, 12))

            ctk.CTkLabel(
                goal_frame, text="YOUR SESSION GOAL",
                font=ctk.CTkFont(size=11, weight="bold"),
                text_color=COLORS["accent_gold"],
            ).pack(padx=14, pady=(10, 4), anchor="w")
            ctk.CTkLabel(
                goal_frame, text=intention,
                font=ctk.CTkFont(size=13), text_color=COLORS["text"],
                wraplength=350, justify="left",
            ).pack(padx=14, pady=(0, 10), anchor="w")

        ctk.CTkLabel(
            container, text="Did you stick to your session goal?",
            font=ctk.CTkFont(size=14, weight="bold"),
            text_color=COLORS["text"],
        ).pack(pady=(4, 8))

        self._rating_buttons: dict[int, ctk.CTkButton] = {}
        rating_row = ctk.CTkFrame(container, fg_color="transparent")
        rating_row.pack(pady=(0, 12))

        for val, label, color in [
            (3, "Yes", COLORS["win_green"]),
            (2, "Partially", COLORS["star_active"]),
            (1, "No", COLORS["loss_red"]),
        ]:
            btn = ctk.CTkButton(
                rating_row, text=label,
                font=ctk.CTkFont(size=13), height=34, width=110,
                corner_radius=8, fg_color=COLORS["tag_bg"],
                hover_color=color, text_color=COLORS["text_dim"],
                border_width=1, border_color=COLORS["border"],
                command=lambda v=val, c=color: self._select_rating(v, c),
            )
            btn.pack(side="left", padx=4)
            self._rating_buttons[val] = btn

        ctk.CTkLabel(
            container, text="Any notes on how it went?",
            font=ctk.CTkFont(size=12), text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(4, 4))

        self._note_entry = ctk.CTkTextbox(
            container, height=60, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
        )
        self._note_entry.pack(fill="x", pady=(0, 12))

        ctk.CTkButton(
            container, text="Save & Close",
            font=ctk.CTkFont(size=14, weight="bold"), height=40,
            corner_radius=8, fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._save,
        ).pack(fill="x")

    def _select_rating(self, value: int, color: str):
        self._rating = value
        for v, btn in self._rating_buttons.items():
            if v == value:
                btn.configure(fg_color=color, text_color="#ffffff")
            else:
                btn.configure(fg_color=COLORS["tag_bg"], text_color=COLORS["text_dim"])

    def _save(self):
        note = self._note_entry.get("1.0", "end-1c").strip()
        if self._on_save:
            self._on_save(self._rating, note)
        self.destroy()

    def _skip(self):
        self.destroy()
