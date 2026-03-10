"""Custom widget components for the GUI."""

import customtkinter as ctk

from ..constants import COLORS


class StarRating(ctk.CTkFrame):
    """Clickable 5-star rating widget."""

    def __init__(self, master, initial: int = 0, **kwargs):
        super().__init__(master, fg_color="transparent", **kwargs)
        self.rating = initial
        self.stars: list[ctk.CTkButton] = []

        for i in range(5):
            star = ctk.CTkButton(
                self,
                text="★",
                width=36,
                height=36,
                font=ctk.CTkFont(size=22),
                fg_color="transparent",
                hover_color=COLORS["bg_input"],
                text_color=COLORS["star_active"] if i < initial else COLORS["star_inactive"],
                command=lambda idx=i: self._set_rating(idx + 1),
            )
            star.pack(side="left", padx=1)
            self.stars.append(star)

    def _set_rating(self, value: int):
        # Toggle off if clicking the same star
        if value == self.rating:
            self.rating = 0
        else:
            self.rating = value

        for i, star in enumerate(self.stars):
            color = COLORS["star_active"] if i < self.rating else COLORS["star_inactive"]
            star.configure(text_color=color)

    def get(self) -> int:
        return self.rating


class TagSelector(ctk.CTkFrame):
    """Toggleable tag pills for categorizing games."""

    def __init__(self, master, tags: list[dict], selected: list[str] = None, **kwargs):
        super().__init__(master, fg_color="transparent", **kwargs)
        self.selected_tags: set[str] = set(selected or [])
        self.tag_buttons: dict[str, ctk.CTkButton] = {}

        # Flow layout using multiple rows
        row_frame = ctk.CTkFrame(self, fg_color="transparent")
        row_frame.pack(fill="x")

        for i, tag in enumerate(tags):
            name = tag["name"]
            color = tag.get("color", "#3b82f6")
            is_selected = name in self.selected_tags

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
                command=lambda n=name, c=color: self._toggle(n, c),
            )
            btn.pack(side="left", padx=3, pady=3)
            self.tag_buttons[name] = (btn, color)

    def _toggle(self, name: str, color: str):
        btn, _ = self.tag_buttons[name]
        if name in self.selected_tags:
            self.selected_tags.remove(name)
            btn.configure(fg_color=COLORS["tag_bg"])
        else:
            self.selected_tags.add(name)
            btn.configure(fg_color=color)

    def get(self) -> list[str]:
        return list(self.selected_tags)


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
