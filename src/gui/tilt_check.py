"""Tilt Check — guided mental reset exercise between games.

Based on:
- Affect labeling (Lieberman et al. 2007) — naming emotions reduces amygdala activity
- 90-second neurochemical window (Bolte Taylor) — breathing through the cortisol surge
- Cognitive reframing (CBT) — catching and replacing distorted thoughts
- Self-compassion (Neff) — normalising tilt to prevent shame spirals
- Implementation intentions (Gollwitzer 1999) — forward-focused cue words
"""

import tkinter as tk
import logging

import customtkinter as ctk

from ..constants import COLORS

logger = logging.getLogger(__name__)

# Emotion options for affect labeling (step 1)
EMOTIONS = [
    ("Angry", "#ef4444"),
    ("Frustrated", "#f97316"),
    ("Anxious", "#eab308"),
    ("Hopeless", "#8b5cf6"),
    ("Numb", "#6b7280"),
    ("Restless", "#3b82f6"),
]

# Cue words for reset (step 4)
CUE_WORDS = ["Calm", "Patient", "Focused", "Aggressive", "Clean", "Fun"]

# Breathing: 4-7-8 pattern (inhale 4s, hold 7s, exhale 8s) × 5 cycles ≈ 95 seconds
BREATHE_PHASES = [
    ("Breathe in...", 4000),
    ("Hold...", 7000),
    ("Breathe out...", 8000),
]
BREATHE_CYCLES = 5  # ~95 seconds total


class TiltCheckPage(ctk.CTkFrame):
    """Multi-step guided tilt reset exercise.

    Steps:
      1. CHECK IN  — affect labeling + intensity slider
      2. BREATHE   — guided 4-7-8 breathing (~95 seconds)
      3. REFRAME   — catch the distorted thought, challenge it
      4. RESET     — cue word + one controllable focus
      5. DONE      — before/after intensity, encouragement
    """

    def __init__(self, parent, db, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self.db = db
        self._step = 0
        self._emotion = None
        self._emotion_color = None
        self._intensity_before = 5
        self._intensity_after = 5
        self._thought_type = ""
        self._cue_word = ""
        self._breathe_after_id = None
        self._breathe_cycle = 0
        self._breathe_phase = 0

        self._scroll = ctk.CTkScrollableFrame(
            self, fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        self._scroll.pack(fill="both", expand=True, padx=16, pady=16)

        self._step_frame = ctk.CTkFrame(self._scroll, fg_color="transparent")
        self._step_frame.pack(fill="both", expand=True)

        self._build_step_0()

    # ── Helpers ────────────────────────────────────────────────────

    def _card(self, parent, **kw) -> ctk.CTkFrame:
        defaults = dict(
            fg_color=COLORS["bg_card"], corner_radius=10,
            border_width=1, border_color=COLORS["border"],
        )
        defaults.update(kw)
        return ctk.CTkFrame(parent, **defaults)

    def _clear_step(self):
        self._cancel_breathe()
        for w in self._step_frame.winfo_children():
            w.destroy()

    def _cancel_breathe(self):
        if self._breathe_after_id is not None:
            self.after_cancel(self._breathe_after_id)
            self._breathe_after_id = None

    def _pill_button(self, parent, text, color, command, width=100):
        return ctk.CTkButton(
            parent, text=text,
            font=ctk.CTkFont(size=13), height=34, width=width,
            corner_radius=17, fg_color=COLORS["tag_bg"],
            hover_color=color, text_color=COLORS["text_dim"],
            border_width=1, border_color=COLORS["border"],
            command=command,
        )

    # ── Step 0: CHECK IN ──────────────────────────────────────────

    def _build_step_0(self):
        self._step = 0
        self._clear_step()
        f = self._step_frame

        # Header
        ctk.CTkLabel(
            f, text="TILT CHECK",
            font=ctk.CTkFont(size=22, weight="bold"),
            text_color=COLORS["accent_purple"],
        ).pack(anchor="w", pady=(0, 4))
        ctk.CTkLabel(
            f, text="A quick guided reset — takes about 2 minutes.",
            font=ctk.CTkFont(size=13), text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 20))

        # Card
        card = self._card(f, border_color=COLORS["accent_purple"])
        card.pack(fill="x")
        inner = ctk.CTkFrame(card, fg_color="transparent")
        inner.pack(fill="x", padx=20, pady=18)

        ctk.CTkLabel(
            inner, text="How are you feeling right now?",
            font=ctk.CTkFont(size=16, weight="bold"),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(0, 12))

        ctk.CTkLabel(
            inner, text="Name it to tame it — labeling emotions reduces their intensity.",
            font=ctk.CTkFont(size=11), text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 14))

        # Emotion pills
        pill_frame = ctk.CTkFrame(inner, fg_color="transparent")
        pill_frame.pack(anchor="w", pady=(0, 20))

        self._emotion_buttons = {}
        row = ctk.CTkFrame(pill_frame, fg_color="transparent")
        row.pack(anchor="w")
        for i, (emotion, color) in enumerate(EMOTIONS):
            if i == 3:
                row = ctk.CTkFrame(pill_frame, fg_color="transparent")
                row.pack(anchor="w", pady=(6, 0))
            btn = self._pill_button(
                row, emotion, color,
                lambda e=emotion, c=color: self._select_emotion(e, c),
                width=110,
            )
            btn.pack(side="left", padx=(0, 8))
            self._emotion_buttons[emotion] = (btn, color)

        # Intensity slider
        ctk.CTkLabel(
            inner, text="Intensity",
            font=ctk.CTkFont(size=13, weight="bold"),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(0, 4))

        slider_row = ctk.CTkFrame(inner, fg_color="transparent")
        slider_row.pack(fill="x", pady=(0, 4))

        ctk.CTkLabel(
            slider_row, text="1", font=ctk.CTkFont(size=11),
            text_color=COLORS["text_muted"], width=20,
        ).pack(side="left")

        self._intensity_var = tk.IntVar(value=5)
        self._intensity_slider = ctk.CTkSlider(
            slider_row, from_=1, to=10, number_of_steps=9,
            variable=self._intensity_var,
            fg_color=COLORS["border"], progress_color=COLORS["accent_purple"],
            button_color=COLORS["accent_purple"],
            button_hover_color="#9b59b6",
            command=self._on_intensity_change,
        )
        self._intensity_slider.pack(side="left", fill="x", expand=True, padx=8)

        ctk.CTkLabel(
            slider_row, text="10", font=ctk.CTkFont(size=11),
            text_color=COLORS["text_muted"], width=20,
        ).pack(side="left")

        self._intensity_label = ctk.CTkLabel(
            inner, text="5 / 10",
            font=ctk.CTkFont(size=20, weight="bold"),
            text_color=COLORS["accent_purple"],
        )
        self._intensity_label.pack(anchor="w", pady=(0, 16))

        # Next button
        self._next_btn_0 = ctk.CTkButton(
            inner, text="Next →",
            font=ctk.CTkFont(size=14, weight="bold"), height=40, width=140,
            corner_radius=8, fg_color=COLORS["tag_bg"],
            text_color=COLORS["text_muted"],
            state="disabled",
            command=self._go_step_1,
        )
        self._next_btn_0.pack(anchor="e")

    def _select_emotion(self, emotion, color):
        self._emotion = emotion
        self._emotion_color = color
        # Highlight selected, dim others
        for name, (btn, c) in self._emotion_buttons.items():
            if name == emotion:
                btn.configure(fg_color=c, text_color=COLORS["text"])
            else:
                btn.configure(fg_color=COLORS["tag_bg"], text_color=COLORS["text_dim"])
        # Enable next
        self._next_btn_0.configure(
            state="normal", fg_color=COLORS["accent_purple"],
            text_color=COLORS["text"], hover_color="#9b59b6",
        )

    def _on_intensity_change(self, val):
        v = int(val)
        self._intensity_before = v
        self._intensity_label.configure(text=f"{v} / 10")

    # ── Step 1: BREATHE ───────────────────────────────────────────

    def _go_step_1(self):
        self._step = 1
        self._clear_step()
        f = self._step_frame

        # Header
        ctk.CTkLabel(
            f, text="BREATHE",
            font=ctk.CTkFont(size=22, weight="bold"),
            text_color=COLORS["accent_purple"],
        ).pack(anchor="w", pady=(0, 4))
        ctk.CTkLabel(
            f, text="The chemical surge of tilt lasts ~90 seconds. Let's ride it out.",
            font=ctk.CTkFont(size=13), text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 20))

        card = self._card(f, border_color=COLORS["accent_purple"])
        card.pack(fill="x")
        inner = ctk.CTkFrame(card, fg_color="transparent")
        inner.pack(fill="x", padx=20, pady=24)

        # Breathe instruction — large central text
        self._breathe_label = ctk.CTkLabel(
            inner, text="Breathe in...",
            font=ctk.CTkFont(size=28, weight="bold"),
            text_color=COLORS["text"],
        )
        self._breathe_label.pack(pady=(10, 4))

        self._breathe_timer = ctk.CTkLabel(
            inner, text="4",
            font=ctk.CTkFont(size=48, weight="bold"),
            text_color=COLORS["accent_purple"],
        )
        self._breathe_timer.pack(pady=(0, 10))

        self._breathe_sub = ctk.CTkLabel(
            inner, text="4-7-8 breathing — exhale longer than you inhale",
            font=ctk.CTkFont(size=11), text_color=COLORS["text_dim"],
        )
        self._breathe_sub.pack(pady=(0, 16))

        # Progress bar
        self._breathe_progress = ctk.CTkProgressBar(
            inner, fg_color=COLORS["border"],
            progress_color=COLORS["accent_purple"],
            height=8, corner_radius=4,
        )
        self._breathe_progress.pack(fill="x", pady=(0, 6))
        self._breathe_progress.set(0)

        self._breathe_cycle_label = ctk.CTkLabel(
            inner, text="Cycle 1 of 5",
            font=ctk.CTkFont(size=12), text_color=COLORS["text_dim"],
        )
        self._breathe_cycle_label.pack(pady=(0, 16))

        # Skip button
        ctk.CTkButton(
            inner, text="Skip →",
            font=ctk.CTkFont(size=13), height=34, width=100,
            corner_radius=8, fg_color=COLORS["tag_bg"],
            hover_color=COLORS["border_bright"],
            text_color=COLORS["text_dim"],
            command=self._go_step_2,
        ).pack(anchor="e")

        # Start breathing
        self._breathe_cycle = 0
        self._breathe_phase = 0
        self._breathe_countdown = 0
        self._run_breathe_phase()

    def _run_breathe_phase(self):
        """Start a new breathing phase (in / hold / out)."""
        if self._breathe_cycle >= BREATHE_CYCLES:
            # Done breathing
            self._go_step_2()
            return

        phase_text, phase_ms = BREATHE_PHASES[self._breathe_phase]
        phase_seconds = phase_ms // 1000

        self._breathe_label.configure(text=phase_text)
        self._breathe_countdown = phase_seconds
        self._breathe_timer.configure(text=str(phase_seconds))

        progress = (self._breathe_cycle * 3 + self._breathe_phase) / (BREATHE_CYCLES * 3)
        self._breathe_progress.set(progress)
        self._breathe_cycle_label.configure(
            text=f"Cycle {self._breathe_cycle + 1} of {BREATHE_CYCLES}"
        )

        self._tick_breathe()

    def _tick_breathe(self):
        """Count down the current phase second by second."""
        if self._breathe_countdown <= 0:
            # Move to next phase
            self._breathe_phase += 1
            if self._breathe_phase >= len(BREATHE_PHASES):
                self._breathe_phase = 0
                self._breathe_cycle += 1
            self._breathe_after_id = self.after(200, self._run_breathe_phase)
            return

        try:
            self._breathe_timer.configure(text=str(self._breathe_countdown))
        except Exception:
            return  # Widget destroyed

        self._breathe_countdown -= 1
        self._breathe_after_id = self.after(1000, self._tick_breathe)

    # ── Step 2: REFRAME ───────────────────────────────────────────

    def _go_step_2(self):
        self._step = 2
        self._clear_step()
        f = self._step_frame

        ctk.CTkLabel(
            f, text="REFRAME",
            font=ctk.CTkFont(size=22, weight="bold"),
            text_color=COLORS["accent_purple"],
        ).pack(anchor="w", pady=(0, 4))
        ctk.CTkLabel(
            f, text="Catch the thought. Challenge it. Replace it.",
            font=ctk.CTkFont(size=13), text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 20))

        card = self._card(f, border_color=COLORS["accent_purple"])
        card.pack(fill="x")
        inner = ctk.CTkFrame(card, fg_color="transparent")
        inner.pack(fill="x", padx=20, pady=18)

        # Thought entry
        ctk.CTkLabel(
            inner, text="What thought keeps replaying?",
            font=ctk.CTkFont(size=14, weight="bold"),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(0, 6))
        ctk.CTkLabel(
            inner, text="e.g., \"My jungler never ganked\" or \"I always lose lane\"",
            font=ctk.CTkFont(size=11), text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 8))

        self._thought_entry = ctk.CTkTextbox(
            inner, height=60, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=6,
        )
        self._thought_entry.pack(fill="x", pady=(0, 16))

        # Fact or interpretation?
        ctk.CTkLabel(
            inner, text="Is this a fact, or an interpretation?",
            font=ctk.CTkFont(size=14, weight="bold"),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(0, 8))

        type_row = ctk.CTkFrame(inner, fg_color="transparent")
        type_row.pack(anchor="w", pady=(0, 16))

        self._type_buttons = {}
        for label, val in [("Fact", "fact"), ("Interpretation", "interpretation")]:
            btn = self._pill_button(
                type_row, label, COLORS["accent_purple"],
                lambda v=val: self._select_thought_type(v),
                width=130,
            )
            btn.pack(side="left", padx=(0, 8))
            self._type_buttons[val] = btn

        # Reframe
        ctk.CTkLabel(
            inner, text="What would you tell a friend who said this about their game?",
            font=ctk.CTkFont(size=14, weight="bold"),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(0, 6))
        ctk.CTkLabel(
            inner, text="Respond with the same kindness you'd give a teammate.",
            font=ctk.CTkFont(size=11), text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 8))

        self._reframe_entry = ctk.CTkTextbox(
            inner, height=60, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=6,
        )
        self._reframe_entry.pack(fill="x", pady=(0, 16))

        # Buttons
        btn_row = ctk.CTkFrame(inner, fg_color="transparent")
        btn_row.pack(fill="x")

        ctk.CTkButton(
            btn_row, text="Skip →",
            font=ctk.CTkFont(size=13), height=34, width=80,
            corner_radius=8, fg_color=COLORS["tag_bg"],
            hover_color=COLORS["border_bright"],
            text_color=COLORS["text_dim"],
            command=self._go_step_3,
        ).pack(side="right", padx=(8, 0))

        ctk.CTkButton(
            btn_row, text="Next →",
            font=ctk.CTkFont(size=14, weight="bold"), height=40, width=120,
            corner_radius=8, fg_color=COLORS["accent_purple"],
            hover_color="#9b59b6", text_color=COLORS["text"],
            command=self._go_step_3,
        ).pack(side="right")

    def _select_thought_type(self, val):
        self._thought_type = val
        for name, btn in self._type_buttons.items():
            if name == val:
                btn.configure(fg_color=COLORS["accent_purple"], text_color=COLORS["text"])
            else:
                btn.configure(fg_color=COLORS["tag_bg"], text_color=COLORS["text_dim"])

    # ── Step 3: RESET ─────────────────────────────────────────────

    def _go_step_3(self):
        # Capture reframe data before clearing
        try:
            self._reframe_thought = self._thought_entry.get("1.0", "end-1c").strip()
            self._reframe_response = self._reframe_entry.get("1.0", "end-1c").strip()
        except Exception:
            self._reframe_thought = ""
            self._reframe_response = ""

        self._step = 3
        self._clear_step()
        f = self._step_frame

        ctk.CTkLabel(
            f, text="RESET",
            font=ctk.CTkFont(size=22, weight="bold"),
            text_color=COLORS["accent_purple"],
        ).pack(anchor="w", pady=(0, 4))
        ctk.CTkLabel(
            f, text="That game is over. Set your intention for the next one.",
            font=ctk.CTkFont(size=13), text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 20))

        card = self._card(f, border_color=COLORS["accent_purple"])
        card.pack(fill="x")
        inner = ctk.CTkFrame(card, fg_color="transparent")
        inner.pack(fill="x", padx=20, pady=18)

        # Cue word
        ctk.CTkLabel(
            inner, text="Pick one word for your next game:",
            font=ctk.CTkFont(size=14, weight="bold"),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(0, 10))

        cue_row = ctk.CTkFrame(inner, fg_color="transparent")
        cue_row.pack(anchor="w", pady=(0, 20))

        self._cue_buttons = {}
        for word in CUE_WORDS:
            btn = self._pill_button(
                cue_row, word, COLORS["accent_purple"],
                lambda w=word: self._select_cue(w),
                width=100,
            )
            btn.pack(side="left", padx=(0, 8))
            self._cue_buttons[word] = btn

        # Focus intention
        ctk.CTkLabel(
            inner, text="What's ONE thing in your control to focus on?",
            font=ctk.CTkFont(size=14, weight="bold"),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(0, 6))
        ctk.CTkLabel(
            inner, text="e.g., \"CS better in the first 10 minutes\" or \"Track enemy jungler\"",
            font=ctk.CTkFont(size=11), text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 8))

        self._focus_entry = ctk.CTkEntry(
            inner, height=38, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=6,
            placeholder_text="One specific, controllable thing...",
        )
        self._focus_entry.pack(fill="x", pady=(0, 20))

        ctk.CTkButton(
            inner, text="Finish",
            font=ctk.CTkFont(size=14, weight="bold"), height=40, width=140,
            corner_radius=8, fg_color=COLORS["accent_purple"],
            hover_color="#9b59b6", text_color=COLORS["text"],
            command=self._go_step_4,
        ).pack(anchor="e")

    def _select_cue(self, word):
        self._cue_word = word
        for name, btn in self._cue_buttons.items():
            if name == word:
                btn.configure(fg_color=COLORS["accent_purple"], text_color=COLORS["text"])
            else:
                btn.configure(fg_color=COLORS["tag_bg"], text_color=COLORS["text_dim"])

    # ── Step 4: DONE ──────────────────────────────────────────────

    def _go_step_4(self):
        # Capture focus intention before clearing
        try:
            self._focus_intention = self._focus_entry.get().strip()
        except Exception:
            self._focus_intention = ""

        self._step = 4
        self._clear_step()
        f = self._step_frame

        ctk.CTkLabel(
            f, text="DONE",
            font=ctk.CTkFont(size=22, weight="bold"),
            text_color=COLORS["win_green"],
        ).pack(anchor="w", pady=(0, 20))

        card = self._card(f, border_color=COLORS["win_green"])
        card.pack(fill="x")
        inner = ctk.CTkFrame(card, fg_color="transparent")
        inner.pack(fill="x", padx=20, pady=18)

        # Summary of what they picked
        if self._emotion:
            ctk.CTkLabel(
                inner, text=f"You were feeling: {self._emotion} ({self._intensity_before}/10)",
                font=ctk.CTkFont(size=14), text_color=COLORS["text"],
            ).pack(anchor="w", pady=(0, 12))

        # After intensity
        ctk.CTkLabel(
            inner, text="How intense is the feeling now?",
            font=ctk.CTkFont(size=14, weight="bold"),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(0, 8))

        slider_row = ctk.CTkFrame(inner, fg_color="transparent")
        slider_row.pack(fill="x", pady=(0, 4))

        ctk.CTkLabel(
            slider_row, text="1", font=ctk.CTkFont(size=11),
            text_color=COLORS["text_muted"], width=20,
        ).pack(side="left")

        self._after_var = tk.IntVar(value=self._intensity_before)
        self._after_slider = ctk.CTkSlider(
            slider_row, from_=1, to=10, number_of_steps=9,
            variable=self._after_var,
            fg_color=COLORS["border"], progress_color=COLORS["win_green"],
            button_color=COLORS["win_green"],
            button_hover_color="#16a34a",
            command=self._on_after_change,
        )
        self._after_slider.pack(side="left", fill="x", expand=True, padx=8)

        ctk.CTkLabel(
            slider_row, text="10", font=ctk.CTkFont(size=11),
            text_color=COLORS["text_muted"], width=20,
        ).pack(side="left")

        self._after_label = ctk.CTkLabel(
            inner, text=f"{self._intensity_before} / 10",
            font=ctk.CTkFont(size=20, weight="bold"),
            text_color=COLORS["win_green"],
        )
        self._after_label.pack(anchor="w", pady=(0, 12))

        # Result message (updates when slider moves)
        self._result_msg = ctk.CTkLabel(
            inner, text="",
            font=ctk.CTkFont(size=13), text_color=COLORS["text_dim"],
            wraplength=550, justify="left",
        )
        self._result_msg.pack(anchor="w", pady=(0, 6))

        # Encouragement
        ctk.CTkLabel(
            inner,
            text="Every pro tilts. The difference is what you do next.",
            font=ctk.CTkFont(size=13, slant="italic"),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 20))

        # Action buttons
        btn_row = ctk.CTkFrame(inner, fg_color="transparent")
        btn_row.pack(fill="x")

        ctk.CTkButton(
            btn_row, text="Save & Close",
            font=ctk.CTkFont(size=14, weight="bold"), height=40, width=140,
            corner_radius=8, fg_color=COLORS["accent_purple"],
            hover_color="#9b59b6", text_color=COLORS["text"],
            command=self._save_and_close,
        ).pack(side="right")

        ctk.CTkButton(
            btn_row, text="Start Over",
            font=ctk.CTkFont(size=13), height=34, width=100,
            corner_radius=8, fg_color=COLORS["tag_bg"],
            hover_color=COLORS["border_bright"],
            text_color=COLORS["text_dim"],
            command=self._build_step_0,
        ).pack(side="right", padx=(0, 8))

    def _on_after_change(self, val):
        v = int(val)
        self._intensity_after = v
        self._after_label.configure(text=f"{v} / 10")

        diff = self._intensity_before - v
        if diff > 0:
            self._result_msg.configure(
                text=f"Nice. You went from {self._intensity_before} → {v}. That's {diff} points down.",
                text_color=COLORS["win_green"],
            )
        elif diff == 0:
            self._result_msg.configure(
                text="Same intensity — that's okay. Sometimes just pausing is the win.",
                text_color=COLORS["text_dim"],
            )
        else:
            self._result_msg.configure(
                text="Higher than before — consider taking a longer break. Your future self will thank you.",
                text_color=COLORS["loss_red"],
            )

    def _save_and_close(self):
        """Persist the tilt check to the database and reset the page."""
        try:
            self.db.tilt_checks.save(
                emotion=self._emotion or "",
                intensity_before=self._intensity_before,
                intensity_after=self._intensity_after,
                reframe_thought=getattr(self, "_reframe_thought", ""),
                reframe_response=getattr(self, "_reframe_response", ""),
                thought_type=self._thought_type,
                cue_word=self._cue_word,
                focus_intention=getattr(self, "_focus_intention", ""),
            )
        except Exception as e:
            logger.error("Failed to save tilt check: %s", e)

        # Reset to step 0 for next use
        self._emotion = None
        self._emotion_color = None
        self._intensity_before = 5
        self._intensity_after = 5
        self._thought_type = ""
        self._cue_word = ""
        self._reframe_thought = ""
        self._reframe_response = ""
        self._focus_intention = ""
        self._build_step_0()

    # ── Public API (called by AppWindow) ──────────────────────────

    def clear(self):
        """Reset the exercise when navigating away."""
        self._cancel_breathe()

    def refresh(self):
        """No-op — the tilt check is stateless between uses."""
        pass
