"""Settings window — configure optional features like Ascent VOD integration."""

import logging
from pathlib import Path
from tkinter import filedialog

import customtkinter as ctk

from ..clips import get_clips_folder_size_mb, is_ffmpeg_available
from ..config import (
    get_ascent_folder, set_ascent_folder,
    get_keybinds, set_keybinds,
    get_clips_folder, set_clips_folder,
    get_clips_max_size_mb, set_clips_max_size_mb,
    get_backup_enabled, set_backup_enabled,
    get_backup_folder, set_backup_folder,
    DEFAULT_KEYBINDS, KEYBIND_LABELS,
)
from ..constants import COLORS

logger = logging.getLogger(__name__)

# Human-friendly display for common key names
_KEY_DISPLAY = {
    "space": "Space",
    "Left": "←",
    "Right": "→",
    "Up": "↑",
    "Down": "↓",
    "bracketleft": "[",
    "bracketright": "]",
    "comma": ",",
    "period": ".",
    "slash": "/",
    "backslash": "\\",
    "semicolon": ";",
    "quoteright": "'",
    "minus": "-",
    "equal": "=",
    "Return": "Enter",
    "BackSpace": "Backspace",
    "Escape": "Esc",
    "Tab": "Tab",
}


def _display_key(tk_key: str) -> str:
    """Convert a tk key-event string to a readable label."""
    parts = tk_key.split("-")
    display_parts = []
    for p in parts:
        if p in ("Shift", "Control", "Alt"):
            display_parts.append(p)
        else:
            display_parts.append(_KEY_DISPLAY.get(p, p.upper() if len(p) == 1 else p))
    return " + ".join(display_parts)


class SettingsWindow(ctk.CTkToplevel):
    """Settings for optional features."""

    def __init__(self, on_save=None, *args, **kwargs):
        super().__init__(*args, **kwargs)

        self._on_save = on_save

        self.title("Settings")
        self.geometry("560x700")
        self.configure(fg_color=COLORS["bg_dark"])
        self.resizable(False, False)

        self.lift()
        self.attributes("-topmost", True)
        self.after(100, lambda: self.attributes("-topmost", False))
        self.focus_force()

        self._keybind_entries: dict[str, ctk.CTkButton] = {}
        self._current_keybinds = get_keybinds()
        self._listening_action: str | None = None  # Action currently being rebound

        self._build_ui()

    def _build_ui(self):
        # Scrollable container for everything
        scroll = ctk.CTkScrollableFrame(
            self, fg_color=COLORS["bg_dark"],
            scrollbar_button_color=COLORS["border"],
        )
        scroll.pack(fill="both", expand=True, padx=20, pady=20)

        # Header
        ctk.CTkLabel(
            scroll,
            text="Settings",
            font=ctk.CTkFont(size=22, weight="bold"),
            text_color=COLORS["accent_gold"],
        ).pack(anchor="w", pady=(0, 16))

        # ── Ascent VOD Section ───────────────────────────────────
        vod_section = ctk.CTkFrame(
            scroll,
            fg_color=COLORS["bg_card"],
            corner_radius=10,
            border_width=1,
            border_color=COLORS["border"],
        )
        vod_section.pack(fill="x", pady=(0, 12))

        inner = ctk.CTkFrame(vod_section, fg_color="transparent")
        inner.pack(fill="x", padx=14, pady=14)

        ctk.CTkLabel(
            inner,
            text="ASCENT VOD RECORDINGS",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 6))

        ctk.CTkLabel(
            inner,
            text="Point this to your Ascent recordings folder to enable\n"
                 "VOD playback and timestamped bookmarks in your reviews.",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text_dim"],
            justify="left",
        ).pack(anchor="w", pady=(0, 10))

        # Folder path row
        path_row = ctk.CTkFrame(inner, fg_color="transparent")
        path_row.pack(fill="x", pady=(0, 8))

        current = get_ascent_folder() or ""
        self._folder_entry = ctk.CTkEntry(
            path_row,
            height=36,
            font=ctk.CTkFont(size=12),
            fg_color=COLORS["bg_input"],
            text_color=COLORS["text"],
            border_width=1,
            border_color=COLORS["border"],
            corner_radius=8,
            placeholder_text="e.g. C:\\Users\\you\\Videos\\Ascent",
        )
        self._folder_entry.pack(side="left", fill="x", expand=True, padx=(0, 8))
        if current:
            self._folder_entry.insert(0, current)

        browse_btn = ctk.CTkButton(
            path_row,
            text="Browse",
            font=ctk.CTkFont(size=12),
            height=36,
            width=80,
            corner_radius=8,
            fg_color=COLORS["tag_bg"],
            hover_color="#333344",
            command=self._browse_folder,
        )
        browse_btn.pack(side="right")

        # Status label
        self._status_label = ctk.CTkLabel(
            inner,
            text="",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        )
        self._status_label.pack(anchor="w")

        if current:
            self._show_folder_status(current)

        # ── Clips Section ─────────────────────────────────────────
        clips_section = ctk.CTkFrame(
            scroll,
            fg_color=COLORS["bg_card"],
            corner_radius=10,
            border_width=1,
            border_color=COLORS["border"],
        )
        clips_section.pack(fill="x", pady=(0, 12))

        clips_inner = ctk.CTkFrame(clips_section, fg_color="transparent")
        clips_inner.pack(fill="x", padx=14, pady=14)

        ctk.CTkLabel(
            clips_inner,
            text="CLIP SETTINGS",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 6))

        # ffmpeg status
        ffmpeg_ok = is_ffmpeg_available()
        ctk.CTkLabel(
            clips_inner,
            text=f"ffmpeg: {'Available' if ffmpeg_ok else 'Not found — clip saving disabled'}",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["win_green"] if ffmpeg_ok else COLORS["loss_red"],
        ).pack(anchor="w", pady=(0, 8))

        ctk.CTkLabel(
            clips_inner,
            text="Save short video clips from your VOD recordings.\n"
                 "Clips are stored separately from Ascent recordings.",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text_dim"],
            justify="left",
        ).pack(anchor="w", pady=(0, 10))

        # Clips folder path row
        clips_path_row = ctk.CTkFrame(clips_inner, fg_color="transparent")
        clips_path_row.pack(fill="x", pady=(0, 8))

        ctk.CTkLabel(
            clips_path_row, text="Folder:",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text"],
            width=50,
        ).pack(side="left", padx=(0, 4))

        current_clips = get_clips_folder() or ""
        self._clips_folder_entry = ctk.CTkEntry(
            clips_path_row, height=36,
            font=ctk.CTkFont(size=12),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
        )
        self._clips_folder_entry.pack(side="left", fill="x", expand=True, padx=(0, 8))
        if current_clips:
            self._clips_folder_entry.insert(0, current_clips)

        ctk.CTkButton(
            clips_path_row, text="Browse",
            font=ctk.CTkFont(size=12), height=36, width=80,
            corner_radius=8, fg_color=COLORS["tag_bg"], hover_color="#333344",
            command=self._browse_clips_folder,
        ).pack(side="right")

        # Max size row
        size_row = ctk.CTkFrame(clips_inner, fg_color="transparent")
        size_row.pack(fill="x", pady=(0, 8))

        ctk.CTkLabel(
            size_row, text="Max folder size (MB):",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text"],
        ).pack(side="left", padx=(0, 8))

        self._clips_max_size_entry = ctk.CTkEntry(
            size_row, height=36, width=100,
            font=ctk.CTkFont(size=12),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
        )
        self._clips_max_size_entry.pack(side="left")
        self._clips_max_size_entry.insert(0, str(get_clips_max_size_mb()))

        # Current usage
        current_size = get_clips_folder_size_mb()
        max_size = get_clips_max_size_mb()
        usage_pct = (current_size / max_size * 100) if max_size > 0 else 0
        usage_color = COLORS["win_green"] if usage_pct < 80 else (
            COLORS["accent_gold"] if usage_pct < 95 else COLORS["loss_red"]
        )
        ctk.CTkLabel(
            size_row,
            text=f"  Using {current_size:.0f} MB / {max_size} MB ({usage_pct:.0f}%)",
            font=ctk.CTkFont(size=11),
            text_color=usage_color,
        ).pack(side="left", padx=(12, 0))

        ctk.CTkLabel(
            clips_inner,
            text="Oldest clips are automatically deleted when the folder exceeds this size.",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w")

        # ── Database Backup Section ───────────────────────────────
        backup_section = ctk.CTkFrame(
            scroll,
            fg_color=COLORS["bg_card"],
            corner_radius=10,
            border_width=1,
            border_color=COLORS["border"],
        )
        backup_section.pack(fill="x", pady=(0, 12))

        backup_inner = ctk.CTkFrame(backup_section, fg_color="transparent")
        backup_inner.pack(fill="x", padx=14, pady=14)

        ctk.CTkLabel(
            backup_inner,
            text="DATABASE BACKUP",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 4))

        ctk.CTkLabel(
            backup_inner,
            text="Back up your database automatically when the app starts. Keeps last 5 backups.",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text_dim"],
            wraplength=480,
        ).pack(anchor="w", pady=(0, 8))

        self._backup_enabled_var = ctk.BooleanVar(value=get_backup_enabled())
        ctk.CTkCheckBox(
            backup_inner,
            text="Enable automatic backups",
            variable=self._backup_enabled_var,
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text"],
            fg_color=COLORS["accent_blue"],
            hover_color="#0077cc",
        ).pack(anchor="w", pady=(0, 10))

        ctk.CTkLabel(
            backup_inner,
            text="Backup folder:",
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text"],
        ).pack(anchor="w", pady=(0, 4))

        backup_row = ctk.CTkFrame(backup_inner, fg_color="transparent")
        backup_row.pack(fill="x", pady=(0, 4))

        self._backup_folder_entry = ctk.CTkEntry(
            backup_row,
            font=ctk.CTkFont(size=12),
            fg_color=COLORS["bg_input"],
            border_color=COLORS["border"],
            text_color=COLORS["text"],
        )
        self._backup_folder_entry.pack(side="left", fill="x", expand=True, padx=(0, 8))
        existing_backup = get_backup_folder()
        if existing_backup:
            self._backup_folder_entry.insert(0, existing_backup)

        ctk.CTkButton(
            backup_row,
            text="Browse",
            font=ctk.CTkFont(size=12),
            height=30,
            width=80,
            fg_color=COLORS["tag_bg"],
            hover_color="#333344",
            command=self._browse_backup_folder,
        ).pack(side="right")

        # ── Keybinds Section ──────────────────────────────────────
        kb_section = ctk.CTkFrame(
            scroll,
            fg_color=COLORS["bg_card"],
            corner_radius=10,
            border_width=1,
            border_color=COLORS["border"],
        )
        kb_section.pack(fill="x", pady=(0, 12))

        kb_inner = ctk.CTkFrame(kb_section, fg_color="transparent")
        kb_inner.pack(fill="x", padx=14, pady=14)

        kb_header = ctk.CTkFrame(kb_inner, fg_color="transparent")
        kb_header.pack(fill="x", pady=(0, 8))

        ctk.CTkLabel(
            kb_header,
            text="VOD PLAYER KEYBINDS",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=COLORS["text_dim"],
        ).pack(side="left")

        ctk.CTkButton(
            kb_header,
            text="Reset Defaults",
            font=ctk.CTkFont(size=11),
            height=26, width=100,
            corner_radius=6,
            fg_color="transparent",
            hover_color=COLORS["bg_input"],
            text_color=COLORS["text_dim"],
            border_width=1,
            border_color=COLORS["border"],
            command=self._reset_keybinds,
        ).pack(side="right")

        ctk.CTkLabel(
            kb_inner,
            text="Click a key to rebind, then press the new key.",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w", pady=(0, 8))

        # Build keybind rows
        # Display order
        action_order = [
            "play_pause", "bookmark",
            "seek_fwd_1", "seek_back_1",
            "seek_fwd_2", "seek_back_2",
            "seek_fwd_5", "seek_back_5",
            "seek_fwd_10", "seek_back_10",
            "speed_up", "speed_down",
        ]

        for action in action_order:
            label_text = KEYBIND_LABELS.get(action, action)
            current_key = self._current_keybinds.get(action, "")

            row = ctk.CTkFrame(kb_inner, fg_color="transparent")
            row.pack(fill="x", pady=2)

            ctk.CTkLabel(
                row, text=label_text,
                font=ctk.CTkFont(size=12),
                text_color=COLORS["text"],
                width=120, anchor="w",
            ).pack(side="left")

            key_btn = ctk.CTkButton(
                row,
                text=_display_key(current_key),
                font=ctk.CTkFont(size=12),
                height=28, width=140,
                corner_radius=6,
                fg_color=COLORS["bg_input"],
                hover_color="#333344",
                text_color=COLORS["text"],
                border_width=1,
                border_color=COLORS["border"],
                command=lambda a=action: self._start_rebind(a),
            )
            key_btn.pack(side="left", padx=(8, 0))

            self._keybind_entries[action] = key_btn

        # Listening indicator
        self._listen_label = ctk.CTkLabel(
            kb_inner, text="",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["accent_gold"],
        )
        self._listen_label.pack(anchor="w", pady=(6, 0))

        # ── Buttons ──────────────────────────────────────────────
        btn_row = ctk.CTkFrame(scroll, fg_color="transparent")
        btn_row.pack(fill="x", pady=(12, 0))

        save_btn = ctk.CTkButton(
            btn_row,
            text="Save",
            font=ctk.CTkFont(size=14, weight="bold"),
            height=40,
            corner_radius=8,
            fg_color=COLORS["accent_blue"],
            hover_color="#0077cc",
            command=self._save,
        )
        save_btn.pack(side="right", padx=(8, 0))

        clear_btn = ctk.CTkButton(
            btn_row,
            text="Clear Ascent Folder",
            font=ctk.CTkFont(size=12),
            height=36,
            corner_radius=8,
            fg_color="transparent",
            hover_color=COLORS["bg_input"],
            text_color=COLORS["text_dim"],
            border_width=1,
            border_color=COLORS["border"],
            command=self._clear_folder,
        )
        clear_btn.pack(side="right")

    # ── Keybind rebinding ─────────────────────────────────────

    def _start_rebind(self, action: str):
        """Start listening for a key press to rebind an action."""
        self._listening_action = action
        label = KEYBIND_LABELS.get(action, action)
        self._listen_label.configure(text=f"Press a key for '{label}'... (Esc to cancel)")

        # Highlight the button being rebound
        btn = self._keybind_entries[action]
        btn.configure(fg_color=COLORS["accent_gold"], text_color="#0a0a0f", text="...")

        # Bind key capture on the whole window
        self.bind("<Key>", self._on_key_capture)

    def _on_key_capture(self, event):
        """Capture a key press and assign it to the listening action."""
        if self._listening_action is None:
            return

        # Cancel on Escape
        if event.keysym == "Escape":
            self._stop_rebind()
            return

        # Build the tk key string with modifiers
        parts = []
        if event.state & 0x4:  # Control
            parts.append("Control")
        if event.state & 0x1:  # Shift
            parts.append("Shift")
        if event.state & 0x20000:  # Alt on Windows
            parts.append("Alt")

        key = event.keysym
        parts.append(key)
        tk_key = "-".join(parts)

        action = self._listening_action
        self._current_keybinds[action] = tk_key

        # Update button text
        btn = self._keybind_entries[action]
        btn.configure(
            text=_display_key(tk_key),
            fg_color=COLORS["bg_input"],
            text_color=COLORS["text"],
        )

        self._stop_rebind()

    def _stop_rebind(self):
        """Stop listening for key presses."""
        if self._listening_action:
            # Reset button appearance if it was cancelled
            btn = self._keybind_entries[self._listening_action]
            current_key = self._current_keybinds.get(self._listening_action, "")
            btn.configure(
                text=_display_key(current_key),
                fg_color=COLORS["bg_input"],
                text_color=COLORS["text"],
            )
        self._listening_action = None
        self._listen_label.configure(text="")
        self.unbind("<Key>")

    def _reset_keybinds(self):
        """Reset all keybinds to defaults."""
        self._current_keybinds = dict(DEFAULT_KEYBINDS)
        for action, btn in self._keybind_entries.items():
            default_key = DEFAULT_KEYBINDS.get(action, "")
            btn.configure(text=_display_key(default_key))

    # ── Ascent folder ─────────────────────────────────────────

    def _browse_folder(self):
        """Open a folder picker dialog."""
        initial = self._folder_entry.get().strip() or None
        folder = filedialog.askdirectory(
            title="Select Ascent Recordings Folder",
            initialdir=initial,
        )
        if folder:
            self._folder_entry.delete(0, "end")
            self._folder_entry.insert(0, folder)
            self._show_folder_status(folder)

    def _show_folder_status(self, folder: str):
        """Show how many video files are in the selected folder."""
        from ..vod import find_recordings
        recs = find_recordings(folder)
        if recs:
            self._status_label.configure(
                text=f"Found {len(recs)} recording{'s' if len(recs) != 1 else ''}",
                text_color=COLORS["win_green"],
            )
        else:
            self._status_label.configure(
                text="No video files found in this folder",
                text_color=COLORS["loss_red"],
            )

    def _clear_folder(self):
        """Clear the Ascent folder setting."""
        self._folder_entry.delete(0, "end")
        self._status_label.configure(text="Ascent VOD disabled", text_color=COLORS["text_dim"])
        set_ascent_folder("")

    def _browse_backup_folder(self):
        """Open a folder picker for the backup folder."""
        initial = self._backup_folder_entry.get().strip() or None
        folder = filedialog.askdirectory(
            title="Select Backup Folder",
            initialdir=initial,
        )
        if folder:
            self._backup_folder_entry.delete(0, "end")
            self._backup_folder_entry.insert(0, folder)

    def _browse_clips_folder(self):
        """Open a folder picker for the clips folder."""
        initial = self._clips_folder_entry.get().strip() or None
        folder = filedialog.askdirectory(
            title="Select Clips Folder",
            initialdir=initial,
        )
        if folder:
            self._clips_folder_entry.delete(0, "end")
            self._clips_folder_entry.insert(0, folder)

    def _save(self):
        """Save settings and close."""
        folder = self._folder_entry.get().strip()
        if folder and Path(folder).is_dir():
            set_ascent_folder(folder)
            logger.info(f"Ascent folder saved: {folder}")
        elif not folder:
            set_ascent_folder("")
            logger.info("Ascent folder cleared")
        else:
            self._status_label.configure(
                text="Folder does not exist",
                text_color=COLORS["loss_red"],
            )
            return

        # Save clips settings
        clips_folder = self._clips_folder_entry.get().strip()
        if clips_folder:
            Path(clips_folder).mkdir(parents=True, exist_ok=True)
            set_clips_folder(clips_folder)

        try:
            max_mb = int(self._clips_max_size_entry.get().strip())
            set_clips_max_size_mb(max_mb)
        except ValueError:
            pass  # Keep existing value

        # Save backup settings
        set_backup_enabled(bool(self._backup_enabled_var.get()))
        backup_folder = self._backup_folder_entry.get().strip()
        if backup_folder:
            Path(backup_folder).mkdir(parents=True, exist_ok=True)
            set_backup_folder(backup_folder)
        else:
            set_backup_folder("")

        # Save keybinds
        set_keybinds(self._current_keybinds)

        if self._on_save:
            self._on_save()

        self.destroy()
