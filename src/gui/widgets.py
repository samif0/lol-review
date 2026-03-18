"""Custom widget components for the GUI."""

import customtkinter as ctk

from ..constants import COLORS


class ConceptTagSelector(ctk.CTkFrame):
    """Toggleable concept tag pills with polarity-based coloring.

    Tags have id, name, polarity (positive/negative/neutral), color.
    get() returns a list of selected tag IDs (ints).
    """

    def __init__(
        self,
        master,
        tags: list[dict],
        selected_ids: list[int] = None,
        **kwargs,
    ):
        super().__init__(master, fg_color="transparent", **kwargs)
        self._selected: set[int] = set(selected_ids or [])
        self._buttons: dict[int, tuple[ctk.CTkButton, str]] = {}

        row_frame = ctk.CTkFrame(self, fg_color="transparent")
        row_frame.pack(fill="x")

        for tag in tags:
            tid = tag["id"]
            name = tag["name"]
            color = tag.get("color", "#3b82f6")
            is_selected = tid in self._selected

            btn = ctk.CTkButton(
                row_frame,
                text=name,
                font=ctk.CTkFont(size=12),
                height=28,
                corner_radius=14,
                fg_color=color if is_selected else COLORS["tag_bg"],
                hover_color=color,
                text_color=COLORS["text"],
                border_width=1,
                border_color=color,
                command=lambda i=tid, c=color: self._toggle(i, c),
            )
            btn.pack(side="left", padx=3, pady=3)
            self._buttons[tid] = (btn, color)

    def _toggle(self, tag_id: int, color: str):
        btn, _ = self._buttons[tag_id]
        if tag_id in self._selected:
            self._selected.remove(tag_id)
            btn.configure(fg_color=COLORS["tag_bg"])
        else:
            self._selected.add(tag_id)
            btn.configure(fg_color=color)

    def get(self) -> list[int]:
        return list(self._selected)


class StatCard(ctk.CTkFrame):
    """A small card displaying a single stat with label."""

    def __init__(self, master, label: str, value: str, color: str = None, **kwargs):
        super().__init__(
            master,
            fg_color=COLORS["bg_card"],
            corner_radius=8,
            border_width=1,
            border_color=COLORS["border"],
            **kwargs,
        )

        ctk.CTkLabel(
            self,
            text=label,
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        ).pack(padx=10, pady=(8, 0))

        ctk.CTkLabel(
            self,
            text=str(value),
            font=ctk.CTkFont(size=18, weight="bold"),
            text_color=color or COLORS["text"],
        ).pack(padx=10, pady=(0, 8))
