"""Claude context generator window."""

import customtkinter as ctk

from ..constants import COLORS


class ClaudeContextWindow(ctk.CTkToplevel):
    """Claude Context Generator — generates paste-ready text for Claude conversations.

    Features:
    - One-click generation of context block with last 7 days, patterns, notes
    - Editable persistent notes section
    - Copy-to-clipboard button
    - Preview of the generated context
    """

    def __init__(self, db, *args, **kwargs):
        super().__init__(*args, **kwargs)

        self.db = db

        self.title("LoL Review — Claude Context Generator")
        self.geometry("760x850")
        self.configure(fg_color=COLORS["bg_dark"])
        self.minsize(650, 700)

        self.lift()
        self.attributes("-topmost", True)
        self.after(100, lambda: self.attributes("-topmost", False))
        self.focus_force()

        # Main container
        container = ctk.CTkScrollableFrame(
            self,
            fg_color=COLORS["bg_dark"],
            scrollbar_button_color=COLORS["border"],
        )
        container.pack(fill="both", expand=True, padx=16, pady=16)

        # Header
        ctk.CTkLabel(
            container,
            text="CLAUDE CONTEXT GENERATOR",
            font=ctk.CTkFont(size=22, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(anchor="w", pady=(0, 4))

        ctk.CTkLabel(
            container,
            text="Generate a paste-ready context block for a new Claude conversation.\nClaude will use this to hold you accountable and spot patterns.",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text_dim"],
            justify="left",
        ).pack(anchor="w", pady=(0, 16))

        # === PERSISTENT NOTES SECTION ===
        notes_frame = ctk.CTkFrame(
            container,
            fg_color=COLORS["bg_card"],
            corner_radius=8,
            border_width=2,
            border_color=COLORS["accent_blue"],
        )
        notes_frame.pack(fill="x", pady=(0, 16))

        notes_header = ctk.CTkFrame(notes_frame, fg_color="transparent")
        notes_header.pack(fill="x", padx=14, pady=(12, 4))

        ctk.CTkLabel(
            notes_header,
            text="YOUR PERSISTENT NOTES",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["accent_blue"],
        ).pack(side="left")

        save_notes_btn = ctk.CTkButton(
            notes_header,
            text="Save Notes",
            font=ctk.CTkFont(size=11),
            height=26,
            width=90,
            corner_radius=6,
            fg_color=COLORS["win_green"],
            hover_color="#1ea05a",
            command=self._save_notes,
        )
        save_notes_btn.pack(side="right")

        ctk.CTkLabel(
            notes_frame,
            text="Add patterns, tendencies, or anything Claude should know about you.\nThese persist across sessions and are included in every context export.",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
            justify="left",
        ).pack(padx=14, pady=(0, 6), anchor="w")

        self.notes_textbox = ctk.CTkTextbox(
            notes_frame,
            height=120,
            font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"],
            text_color=COLORS["text"],
            border_width=1,
            border_color=COLORS["border"],
            corner_radius=8,
            wrap="word",
        )
        self.notes_textbox.pack(fill="x", padx=14, pady=(0, 12))

        # Load existing notes
        existing_notes = self.db.get_persistent_notes()
        if existing_notes:
            self.notes_textbox.insert("1.0", existing_notes)
        else:
            self.notes_textbox.insert(
                "1.0",
                "e.g., I take ego plays over macro plays\n"
                "I tilt after dying to ganks early\n"
                "I play better on 2nd game of the day",
            )

        # Quick-add note buttons
        quick_notes_frame = ctk.CTkFrame(notes_frame, fg_color="transparent")
        quick_notes_frame.pack(fill="x", padx=14, pady=(0, 12))

        ctk.CTkLabel(
            quick_notes_frame,
            text="Quick add:",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        ).pack(side="left", padx=(0, 6))

        quick_notes = [
            "I overtrade in lane",
            "I don't track summs",
            "I chase kills too much",
            "I tilt after early deaths",
        ]
        for note_text in quick_notes:
            btn = ctk.CTkButton(
                quick_notes_frame,
                text=note_text,
                font=ctk.CTkFont(size=10),
                height=24,
                corner_radius=12,
                fg_color=COLORS["tag_bg"],
                hover_color=COLORS["accent_blue"],
                text_color=COLORS["text_dim"],
                border_width=1,
                border_color=COLORS["border"],
                command=lambda t=note_text: self._append_quick_note(t),
            )
            btn.pack(side="left", padx=2)

        # Separator
        ctk.CTkFrame(
            container, fg_color=COLORS["border"], height=1
        ).pack(fill="x", pady=8)

        # === GENERATE + COPY SECTION ===
        action_frame = ctk.CTkFrame(container, fg_color="transparent")
        action_frame.pack(fill="x", pady=(8, 12))

        generate_btn = ctk.CTkButton(
            action_frame,
            text="Generate Context",
            font=ctk.CTkFont(size=15, weight="bold"),
            height=44,
            corner_radius=8,
            fg_color=COLORS["accent_blue"],
            hover_color="#0077cc",
            command=self._generate,
        )
        generate_btn.pack(side="left", expand=True, fill="x", padx=(0, 8))

        self.copy_btn = ctk.CTkButton(
            action_frame,
            text="Copy to Clipboard",
            font=ctk.CTkFont(size=15, weight="bold"),
            height=44,
            corner_radius=8,
            fg_color=COLORS["win_green"],
            hover_color="#1ea05a",
            text_color="#ffffff",
            command=self._copy_to_clipboard,
        )
        self.copy_btn.pack(side="left", expand=True, fill="x")

        # === PREVIEW ===
        ctk.CTkLabel(
            container,
            text="PREVIEW",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(4, 6))

        self.preview_textbox = ctk.CTkTextbox(
            container,
            height=350,
            font=ctk.CTkFont(family="Consolas", size=12),
            fg_color=COLORS["bg_input"],
            text_color=COLORS["text"],
            border_width=1,
            border_color=COLORS["border"],
            corner_radius=8,
            wrap="word",
        )
        self.preview_textbox.pack(fill="both", expand=True)

        # Auto-generate on open
        self._generate()

    def _save_notes(self):
        """Save the persistent notes to the database."""
        content = self.notes_textbox.get("1.0", "end-1c").strip()
        self.db.save_persistent_notes(content)

    def _append_quick_note(self, text: str):
        """Append a quick note to the persistent notes textbox."""
        current = self.notes_textbox.get("1.0", "end-1c").strip()
        if current and not current.endswith("\n"):
            self.notes_textbox.insert("end", "\n")
        self.notes_textbox.insert("end", text)

    def _generate(self):
        """Generate and display the Claude context block."""
        # Save notes first so they're included
        self._save_notes()

        context = self.db.generate_claude_context()

        self.preview_textbox.configure(state="normal")
        self.preview_textbox.delete("1.0", "end")
        self.preview_textbox.insert("1.0", context)

    def _copy_to_clipboard(self):
        """Copy the generated context to system clipboard."""
        context = self.preview_textbox.get("1.0", "end-1c")
        if not context.strip():
            self._generate()
            context = self.preview_textbox.get("1.0", "end-1c")

        self.clipboard_clear()
        self.clipboard_append(context)

        # Visual feedback
        original_text = self.copy_btn.cget("text")
        self.copy_btn.configure(text="Copied!", fg_color="#1ea05a")
        self.after(
            1500,
            lambda: self.copy_btn.configure(
                text=original_text, fg_color=COLORS["win_green"]
            ),
        )
