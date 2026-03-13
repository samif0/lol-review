"""Single-window AppWindow with sidebar navigation."""

import json
import logging
from datetime import datetime, timedelta
from pathlib import Path
from tkinter import filedialog

import customtkinter as ctk

from ..clips import get_clips_folder_size_mb, is_ffmpeg_available
from ..config import (
    get_ascent_folder, set_ascent_folder,
    get_keybinds, set_keybinds,
    get_clips_folder, set_clips_folder,
    get_clips_max_size_mb, set_clips_max_size_mb,
    DEFAULT_KEYBINDS, KEYBIND_LABELS,
)
from ..constants import COLORS, format_duration, format_number
from ..version import __version__
from .claude_context import ClaudeContextWindow
from .game_review import SessionGameReviewWindow

logger = logging.getLogger(__name__)

_KEY_DISPLAY = {
    "space": "Space",
    "Left": "←", "Right": "→", "Up": "↑", "Down": "↓",
    "bracketleft": "[", "bracketright": "]",
    "comma": ",", "period": ".", "slash": "/", "backslash": "\\",
    "semicolon": ";", "quoteright": "'", "minus": "-", "equal": "=",
    "Return": "Enter", "BackSpace": "Backspace", "Escape": "Esc", "Tab": "Tab",
}


def _display_key(tk_key: str) -> str:
    parts = tk_key.split("-")
    return " + ".join(
        p if p in ("Shift", "Control", "Alt")
        else _KEY_DISPLAY.get(p, p.upper() if len(p) == 1 else p)
        for p in parts
    )


def _card(parent, **kwargs) -> ctk.CTkFrame:
    return ctk.CTkFrame(
        parent,
        fg_color=COLORS["bg_card"],
        corner_radius=10,
        border_width=1,
        border_color=COLORS["border"],
        **kwargs,
    )


def _stat_block(parent, label: str, value: str, color: str = None) -> ctk.CTkLabel:
    col = ctk.CTkFrame(parent, fg_color="transparent")
    col.pack(side="left", expand=True, fill="x")
    ctk.CTkLabel(col, text=label, font=ctk.CTkFont(size=10, weight="bold"),
                 text_color=COLORS["text_dim"]).pack()
    lbl = ctk.CTkLabel(col, text=value, font=ctk.CTkFont(size=20, weight="bold"),
                       text_color=color or COLORS["text"])
    lbl.pack()
    return lbl


def _open_review_popup(db, game: dict, on_save, on_open_vod, popup_store: list):
    if popup_store[0] and popup_store[0].winfo_exists():
        popup_store[0].destroy()
    game_id = game.get("game_id")
    session_entry = {
        "game_id": game_id,
        "champion_name": game.get("champion_name"),
        "win": game.get("win", 0),
        "mental_rating": 5,
    }
    vod_info = db.get_vod(game_id) if game_id else None
    has_vod = vod_info is not None
    bookmark_count = db.get_bookmark_count(game_id) if has_vod else 0
    popup_store[0] = SessionGameReviewWindow(
        db=db, session_entry=session_entry, game_data=game,
        on_save=on_save, on_open_vod=on_open_vod,
        has_vod=has_vod, bookmark_count=bookmark_count,
    )


# ══════════════════════════════════════════════════════════
# Page: Home
# ══════════════════════════════════════════════════════════

class HomePage(ctk.CTkFrame):
    def __init__(self, parent, db, on_open_vod, on_open_claude_context, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self.db = db
        self._on_open_vod = on_open_vod
        self._on_open_claude_context = on_open_claude_context
        self._review_popup = [None]
        self._scroll = None
        self._build()

    def _build(self):
        self._scroll = ctk.CTkScrollableFrame(
            self, fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        self._scroll.pack(fill="both", expand=True, padx=16, pady=16)
        self._populate(self._scroll)

    def _populate(self, body):
        hour = datetime.now().hour
        tod = "morning" if hour < 12 else ("afternoon" if hour < 17 else "evening")

        # Greeting row
        grow = ctk.CTkFrame(body, fg_color="transparent")
        grow.pack(fill="x", pady=(0, 16))
        ctk.CTkLabel(grow, text=f"Good {tod} — ready to climb?",
                     font=ctk.CTkFont(size=22, weight="bold"),
                     text_color=COLORS["text"]).pack(side="left")
        ctk.CTkButton(
            grow, text="Claude Context",
            font=ctk.CTkFont(size=13, weight="bold"),
            height=36, width=150, corner_radius=8,
            fg_color=COLORS["accent_purple"], hover_color="#6d28d9",
            command=self._on_open_claude_context,
        ).pack(side="right")

        self._build_today_stats(body)
        self._build_unreviewed(body)

    def _build_today_stats(self, parent):
        today = datetime.now().strftime("%Y-%m-%d")
        stats = self.db.get_session_stats_for_date(today)
        streak = self.db.get_win_streak()
        adherence = self.db.get_adherence_streak()

        card = _card(parent)
        card.pack(fill="x", pady=(0, 12))
        row = ctk.CTkFrame(card, fg_color="transparent")
        row.pack(fill="x", padx=16, pady=14)

        games = stats.get("games", 0)
        wins = stats.get("wins", 0) or 0
        losses = stats.get("losses", 0) or 0
        avg_mental = stats.get("avg_mental", 0) or 0
        sv = streak
        sc = COLORS["win_green"] if sv > 0 else (COLORS["loss_red"] if sv < 0 else COLORS["text"])
        st = f"+{sv}" if sv > 0 else str(sv)
        wl_color = (COLORS["win_green"] if wins > losses and games > 0
                    else COLORS["loss_red"] if losses > wins else COLORS["text"])

        _stat_block(row, "Games", str(games))
        _stat_block(row, "W / L", f"{wins} / {losses}", wl_color)
        _stat_block(row, "Avg Mental", f"{avg_mental}/10" if games > 0 else "—", COLORS["accent_blue"])
        _stat_block(row, "Win Streak", st, sc)
        _stat_block(row, "Adherence", f"{adherence}d",
                    COLORS["win_green"] if adherence >= 3 else COLORS["text"])

        if games > 0:
            if avg_mental >= 7:
                btxt, bfg, btc = "Locked in", "#0d2a1a", COLORS["win_green"]
            elif avg_mental >= 4:
                btxt, bfg, btc = "Decent session", "#2a2a0a", "#d4c017"
            else:
                btxt, bfg, btc = "Consider a break", COLORS["loss_red_dim"], COLORS["loss_red"]
            banner = ctk.CTkFrame(parent, fg_color=bfg, corner_radius=8)
            banner.pack(fill="x", pady=(0, 12))
            ctk.CTkLabel(banner, text=btxt, font=ctk.CTkFont(size=14, weight="bold"),
                         text_color=btc).pack(pady=10)

    def _build_unreviewed(self, parent):
        unreviewed = self.db.get_unreviewed_games(days=3)
        count = len(unreviewed)

        section = _card(parent)
        section.pack(fill="x", pady=(0, 12))
        inner = ctk.CTkFrame(section, fg_color="transparent")
        inner.pack(fill="x", padx=16, pady=14)

        hrow = ctk.CTkFrame(inner, fg_color="transparent")
        hrow.pack(fill="x", pady=(0, 8))
        ctk.CTkLabel(hrow, text="NEEDS REVIEW",
                     font=ctk.CTkFont(size=11, weight="bold"),
                     text_color=COLORS["text_dim"]).pack(side="left")
        ctk.CTkLabel(hrow, text=f"{count} game{'s' if count != 1 else ''}",
                     font=ctk.CTkFont(size=11, weight="bold"),
                     text_color=COLORS["loss_red"] if count > 0 else COLORS["win_green"],
                     ).pack(side="right")

        if count == 0:
            ctk.CTkLabel(inner, text="All caught up — every recent game has been reviewed!",
                         font=ctk.CTkFont(size=13),
                         text_color=COLORS["win_green"]).pack(anchor="w", pady=4)
            return

        ctk.CTkLabel(inner, text="Review these before you queue up.",
                     font=ctk.CTkFont(size=12),
                     text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 8))

        for game in unreviewed[:8]:
            self._build_unreviewed_row(inner, game)

        if count > 8:
            ctk.CTkLabel(inner, text=f"+ {count - 8} more — check Losses page",
                         font=ctk.CTkFont(size=11),
                         text_color=COLORS["text_dim"]).pack(anchor="w", pady=(6, 0))

    def _build_unreviewed_row(self, parent, game: dict):
        is_win = bool(game.get("win"))
        bc = COLORS["win_green"] if is_win else COLORS["loss_red"]
        row = ctk.CTkFrame(parent, fg_color=COLORS["bg_input"], corner_radius=6,
                           border_width=1, border_color=bc)
        row.pack(fill="x", pady=2)
        inner = ctk.CTkFrame(row, fg_color="transparent")
        inner.pack(fill="x", padx=10, pady=6)

        k, d, a = game.get("kills", 0), game.get("deaths", 0), game.get("assists", 0)
        ctk.CTkLabel(
            inner,
            text=f"{'W' if is_win else 'L'}  {game.get('champion_name', '?')}   {k}/{d}/{a} ({game.get('kda_ratio', 0):.1f})",
            font=ctk.CTkFont(size=12, weight="bold"),
            text_color=bc,
        ).pack(side="left")

        right = ctk.CTkFrame(inner, fg_color="transparent")
        right.pack(side="right")
        ctk.CTkLabel(right, text=game.get("date_played", ""),
                     font=ctk.CTkFont(size=11),
                     text_color=COLORS["text_dim"]).pack(side="left", padx=(0, 8))
        ctk.CTkButton(right, text="Review",
                      font=ctk.CTkFont(size=10), height=22, width=70, corner_radius=5,
                      fg_color=COLORS["accent_blue"], hover_color="#0077cc",
                      command=lambda g=game: self._open_review(g),
                      ).pack(side="left", padx=(0, 4))

        game_id = game.get("game_id")
        if game_id and self.db.get_vod(game_id):
            ctk.CTkButton(right, text="Watch VOD",
                          font=ctk.CTkFont(size=10, weight="bold"),
                          height=22, width=80, corner_radius=5,
                          fg_color=COLORS["accent_gold"], hover_color="#a88432",
                          text_color="#0a0a0f",
                          command=lambda gid=game_id: self._on_open_vod(gid) if self._on_open_vod else None,
                          ).pack(side="left")

    def _open_review(self, game: dict):
        _open_review_popup(self.db, game, self.refresh, self._on_open_vod, self._review_popup)

    def refresh(self):
        if self._scroll:
            for w in self._scroll.winfo_children():
                w.destroy()
            self._populate(self._scroll)


# ══════════════════════════════════════════════════════════
# Page: Session
# ══════════════════════════════════════════════════════════

class SessionPage(ctk.CTkFrame):
    def __init__(self, parent, db, on_open_vod, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self.db = db
        self._on_open_vod = on_open_vod
        self._selected_date = datetime.now().strftime("%Y-%m-%d")
        self._review_popup = [None]
        self._last_refresh_hash = None
        self._build_chrome()
        self._refresh()
        self.after(30000, self._auto_refresh)

    def _build_chrome(self):
        outer = ctk.CTkFrame(self, fg_color="transparent")
        outer.pack(fill="both", expand=True, padx=16, pady=16)
        self._outer = outer

        # Page header
        hrow = ctk.CTkFrame(outer, fg_color="transparent")
        hrow.pack(fill="x", pady=(0, 12))
        ctk.CTkLabel(hrow, text="Session", font=ctk.CTkFont(size=22, weight="bold"),
                     text_color=COLORS["text"]).pack(side="left")
        ctk.CTkLabel(hrow, text="Track your mental state and adherence",
                     font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(side="left", padx=(12, 0))

        # Date nav
        date_card = _card(outer)
        date_card.pack(fill="x", pady=(0, 12))
        nav = ctk.CTkFrame(date_card, fg_color="transparent")
        nav.pack(padx=12, pady=8)

        self._prev_btn = ctk.CTkButton(
            nav, text="◀  Prev", font=ctk.CTkFont(size=12),
            width=90, height=28, corner_radius=6,
            fg_color=COLORS["tag_bg"], hover_color="#444455",
            command=self._go_prev_day,
        )
        self._prev_btn.pack(side="left", padx=(0, 12))

        self._date_label = ctk.CTkLabel(
            nav, text="", font=ctk.CTkFont(size=15, weight="bold"),
            text_color=COLORS["text"],
        )
        self._date_label.pack(side="left", padx=12)

        self._next_btn = ctk.CTkButton(
            nav, text="Next  ▶", font=ctk.CTkFont(size=12),
            width=90, height=28, corner_radius=6,
            fg_color=COLORS["tag_bg"], hover_color="#444455",
            command=self._go_next_day,
        )
        self._next_btn.pack(side="left", padx=(12, 12))

        ctk.CTkButton(
            nav, text="Today", font=ctk.CTkFont(size=12),
            width=70, height=28, corner_radius=6,
            fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._go_today,
        ).pack(side="left")

        # Stats row
        stats_card = _card(outer)
        stats_card.pack(fill="x", pady=(0, 12))
        self._stats_row = ctk.CTkFrame(stats_card, fg_color="transparent")
        self._stats_row.pack(fill="x", padx=16, pady=14)

        self._stat_labels = {}
        for label, key in [
            ("Games", "games"), ("Wins", "wins"), ("Losses", "losses"),
            ("Avg Mental", "avg_mental"), ("Rule Breaks", "rule_breaks"),
            ("Adherence", "streak"),
        ]:
            col = ctk.CTkFrame(self._stats_row, fg_color="transparent")
            col.pack(side="left", expand=True, fill="x")
            ctk.CTkLabel(col, text=label, font=ctk.CTkFont(size=10, weight="bold"),
                         text_color=COLORS["text_dim"]).pack()
            lbl = ctk.CTkLabel(col, text="—", font=ctk.CTkFont(size=20, weight="bold"),
                               text_color=COLORS["text"])
            lbl.pack()
            self._stat_labels[key] = lbl

        # Games label
        self._games_heading = ctk.CTkLabel(
            outer, text="GAMES",
            font=ctk.CTkFont(size=11, weight="bold"),
            text_color=COLORS["text_dim"],
        )
        self._games_heading.pack(anchor="w", pady=(0, 6))

        # Scrollable game list
        self.scroll_frame = ctk.CTkScrollableFrame(
            outer, fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        self.scroll_frame.pack(fill="both", expand=True)

    def _refresh(self, force: bool = False):
        is_today = self._selected_date == datetime.now().strftime("%Y-%m-%d")
        try:
            date_obj = datetime.strptime(self._selected_date, "%Y-%m-%d")
            friendly = date_obj.strftime("%A, %b %d")
        except ValueError:
            friendly = self._selected_date

        self._date_label.configure(
            text=f"{friendly}  (Today)" if is_today else friendly
        )
        self._games_heading.configure(
            text="TODAY'S GAMES" if is_today else f"GAMES — {friendly}"
        )

        if is_today:
            self._next_btn.configure(state="disabled", fg_color=COLORS["border"])
        else:
            self._next_btn.configure(state="normal", fg_color=COLORS["tag_bg"])

        stats = self.db.get_session_stats_for_date(self._selected_date)
        streak = self.db.get_adherence_streak()

        self._stat_labels["games"].configure(text=str(stats.get("games", 0)))
        wins = stats.get("wins", 0) or 0
        losses = stats.get("losses", 0) or 0
        self._stat_labels["wins"].configure(
            text=str(wins),
            text_color=COLORS["win_green"] if wins > 0 else COLORS["text"],
        )
        self._stat_labels["losses"].configure(
            text=str(losses),
            text_color=COLORS["loss_red"] if losses > 0 else COLORS["text"],
        )
        self._stat_labels["avg_mental"].configure(
            text=f"{stats['avg_mental']}" if stats.get("games", 0) > 0 else "—",
        )
        rb = stats.get("rule_breaks", 0) or 0
        self._stat_labels["rule_breaks"].configure(
            text=str(rb),
            text_color=COLORS["loss_red"] if rb > 0 else COLORS["win_green"],
        )
        self._stat_labels["streak"].configure(
            text=str(streak),
            text_color=COLORS["win_green"] if streak >= 3 else COLORS["text"],
        )

        entries = self.db.get_session_log_for_date(self._selected_date)
        h = str([(e.get("game_id"), e.get("win"), e.get("mental_rating"),
                  e.get("improvement_note"), e.get("rule_broken")) for e in (entries or [])])
        if not force and h == self._last_refresh_hash:
            return
        self._last_refresh_hash = h

        for w in self.scroll_frame.winfo_children():
            w.destroy()

        if not entries:
            ctk.CTkLabel(
                self.scroll_frame,
                text=("No games logged today.\nGames are logged automatically when detected."
                      if is_today else "No games logged on this date."),
                font=ctk.CTkFont(size=13), text_color=COLORS["text_dim"], justify="center",
            ).pack(pady=30)
            return

        for entry in entries:
            self._build_entry_row(entry)

    def _build_entry_row(self, entry: dict):
        is_win = bool(entry.get("win"))
        bc = COLORS["win_green"] if is_win else COLORS["loss_red"]
        broke_rule = bool(entry.get("rule_broken"))

        game_data = self.db.get_game(entry.get("game_id")) if entry.get("game_id") else None
        has_review = bool(game_data and (
            game_data.get("mistakes", "").strip()
            or game_data.get("went_well", "").strip()
            or game_data.get("focus_next", "").strip()
            or game_data.get("rating", 0) > 0
        ))

        row = ctk.CTkFrame(
            self.scroll_frame, fg_color=COLORS["bg_card"], corner_radius=8,
            border_width=2, border_color="#ff0000" if broke_rule else bc,
        )
        row.pack(fill="x", pady=4, padx=4)
        inner = ctk.CTkFrame(row, fg_color="transparent")
        inner.pack(fill="x", padx=12, pady=10)

        left = ctk.CTkFrame(inner, fg_color="transparent")
        left.pack(side="left", fill="x", expand=True)

        result = "W" if is_win else "L"
        rc = COLORS["win_green"] if is_win else COLORS["loss_red"]
        top_text = f"{result}  {entry.get('champion_name', '?')}"
        if broke_rule:
            top_text += "  [RULE BREAK]"

        ctk.CTkLabel(left, text=top_text, font=ctk.CTkFont(size=14, weight="bold"),
                     text_color="#ff0000" if broke_rule else rc).pack(anchor="w")

        note = entry.get("improvement_note", "").strip()
        if note:
            ctk.CTkLabel(left, text=note, font=ctk.CTkFont(size=11),
                         text_color=COLORS["text_dim"], wraplength=400,
                         justify="left").pack(anchor="w")
        if has_review:
            ctk.CTkLabel(left, text="Reviewed", font=ctk.CTkFont(size=10),
                         text_color=COLORS["win_green"]).pack(anchor="w")

        right = ctk.CTkFrame(inner, fg_color="transparent")
        right.pack(side="right")

        mental = entry.get("mental_rating", 5)
        mc = COLORS["win_green"] if mental >= 8 else (COLORS["accent_blue"] if mental >= 5 else COLORS["loss_red"])
        ctk.CTkLabel(right, text=f"Mental: {mental}/10",
                     font=ctk.CTkFont(size=14, weight="bold"),
                     text_color=mc).pack(anchor="e")

        btn_row = ctk.CTkFrame(right, fg_color="transparent")
        btn_row.pack(anchor="e", pady=(4, 0))

        ctk.CTkButton(
            btn_row,
            text="Edit Review" if has_review else "Review",
            font=ctk.CTkFont(size=11), height=26, width=90, corner_radius=6,
            fg_color=COLORS["tag_bg"] if has_review else COLORS["accent_blue"],
            hover_color="#0077cc",
            command=lambda e=entry, g=game_data: self._open_game_review(e, g),
        ).pack(side="left", padx=(0, 6))

        game_id = entry.get("game_id")
        if game_id and self.db.get_vod(game_id):
            ctk.CTkButton(
                btn_row, text="Watch VOD",
                font=ctk.CTkFont(size=11, weight="bold"),
                height=26, width=90, corner_radius=6,
                fg_color=COLORS["accent_gold"], hover_color="#a88432",
                text_color="#0a0a0f",
                command=lambda gid=game_id: self._on_open_vod(gid) if self._on_open_vod else None,
            ).pack(side="left")

    def _go_prev_day(self):
        d = datetime.strptime(self._selected_date, "%Y-%m-%d") - timedelta(days=1)
        self._selected_date = d.strftime("%Y-%m-%d")
        self._refresh()

    def _go_next_day(self):
        today = datetime.now().strftime("%Y-%m-%d")
        d = datetime.strptime(self._selected_date, "%Y-%m-%d") + timedelta(days=1)
        new = d.strftime("%Y-%m-%d")
        if new <= today:
            self._selected_date = new
            self._refresh()

    def _go_today(self):
        self._selected_date = datetime.now().strftime("%Y-%m-%d")
        self._refresh()

    def _open_game_review(self, session_entry: dict, game_data: dict):
        if self._review_popup[0] and self._review_popup[0].winfo_exists():
            self._review_popup[0].destroy()
        game_id = session_entry.get("game_id")
        vod_info = self.db.get_vod(game_id) if game_id else None
        has_vod = vod_info is not None
        bookmark_count = self.db.get_bookmark_count(game_id) if has_vod else 0
        self._review_popup[0] = SessionGameReviewWindow(
            db=self.db, session_entry=session_entry, game_data=game_data,
            on_save=self._refresh, on_open_vod=self._on_open_vod,
            has_vod=has_vod, bookmark_count=bookmark_count,
        )

    def _auto_refresh(self):
        if not self.winfo_exists():
            return
        try:
            self._refresh()
        except Exception as e:
            logger.warning(f"Session auto-refresh error: {e}")
        finally:
            self.after(30000, self._auto_refresh)

    def refresh(self):
        self._refresh(force=True)


# ══════════════════════════════════════════════════════════
# Page: History
# ══════════════════════════════════════════════════════════

class HistoryPage(ctk.CTkFrame):
    def __init__(self, parent, db, on_open_vod, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self.db = db
        self._on_open_vod = on_open_vod
        self._review_popup = [None]
        self._build()

    def _build(self):
        outer = ctk.CTkFrame(self, fg_color="transparent")
        outer.pack(fill="both", expand=True, padx=16, pady=16)

        hrow = ctk.CTkFrame(outer, fg_color="transparent")
        hrow.pack(fill="x", pady=(0, 12))
        ctk.CTkLabel(hrow, text="History", font=ctk.CTkFont(size=22, weight="bold"),
                     text_color=COLORS["text"]).pack(side="left")
        ctk.CTkLabel(hrow, text="All recorded games",
                     font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(side="left", padx=(12, 0))

        notebook = ctk.CTkTabview(outer, fg_color=COLORS["bg_dark"])
        notebook.pack(fill="both", expand=True)
        self._notebook = notebook

        tab_games = notebook.add("Recent Games")
        tab_champs = notebook.add("By Champion")
        tab_stats = notebook.add("Stats Overview")

        self._populate_games(tab_games)
        self._populate_champs(tab_champs)
        self._populate_stats(tab_stats)

    def _populate_games(self, parent):
        games = self.db.get_recent_games(9999)
        scroll = ctk.CTkScrollableFrame(parent, fg_color="transparent")
        scroll.pack(fill="both", expand=True)

        if not games:
            ctk.CTkLabel(scroll, text="No games recorded yet.",
                         font=ctk.CTkFont(size=14),
                         text_color=COLORS["text_dim"]).pack(pady=40)
            return

        for game in games:
            self._build_game_row(scroll, game)

    def _build_game_row(self, parent, game: dict):
        is_win = bool(game.get("win"))
        bc = COLORS["win_green"] if is_win else COLORS["loss_red"]
        row = ctk.CTkFrame(parent, fg_color=COLORS["bg_card"], corner_radius=8,
                           border_width=2, border_color=bc)
        row.pack(fill="x", pady=4, padx=4)
        inner = ctk.CTkFrame(row, fg_color="transparent")
        inner.pack(fill="x", padx=12, pady=10)

        left = ctk.CTkFrame(inner, fg_color="transparent")
        left.pack(side="left")
        rc = COLORS["win_green"] if is_win else COLORS["loss_red"]
        ctk.CTkLabel(left, text=f"{'W' if is_win else 'L'}  {game.get('champion_name', '?')}",
                     font=ctk.CTkFont(size=15, weight="bold"),
                     text_color=rc).pack(anchor="w")
        ctk.CTkLabel(
            left,
            text=f"{game.get('date_played', '')}  •  {format_duration(game.get('game_duration', 0))}  •  {game.get('game_mode', '')}",
            font=ctk.CTkFont(size=11), text_color=COLORS["text_dim"],
        ).pack(anchor="w")

        tags = json.loads(game.get("tags", "[]")) if isinstance(game.get("tags"), str) else game.get("tags", [])
        if tags:
            ctk.CTkLabel(left, text=" ".join(f"[{t}]" for t in tags),
                         font=ctk.CTkFont(size=10),
                         text_color=COLORS["accent_blue"]).pack(anchor="w")

        right = ctk.CTkFrame(inner, fg_color="transparent")
        right.pack(side="right")
        k, d, a = game.get("kills", 0), game.get("deaths", 0), game.get("assists", 0)
        ctk.CTkLabel(right, text=f"{k}/{d}/{a}  ({game.get('kda_ratio', 0):.1f} KDA)",
                     font=ctk.CTkFont(size=14, weight="bold"),
                     text_color=COLORS["text"]).pack(anchor="e")
        ctk.CTkLabel(
            right,
            text=f"CS {game.get('cs_total', 0)} ({game.get('cs_per_min', 0)}/m)  •  Vision {game.get('vision_score', 0)}  •  {format_number(game.get('total_damage_to_champions', 0))} dmg",
            font=ctk.CTkFont(size=11), text_color=COLORS["text_dim"],
        ).pack(anchor="e")
        rating = game.get("rating", 0)
        if rating > 0:
            ctk.CTkLabel(right, text="★" * rating + "☆" * (5 - rating),
                         font=ctk.CTkFont(size=12),
                         text_color=COLORS["star_active"]).pack(anchor="e")

        btn_row = ctk.CTkFrame(right, fg_color="transparent")
        btn_row.pack(anchor="e", pady=(4, 0))
        has_review = bool(
            game.get("mistakes", "").strip() or game.get("went_well", "").strip()
            or game.get("focus_next", "").strip() or (game.get("rating") or 0) > 0
        )
        ctk.CTkButton(
            btn_row, text="Edit Review" if has_review else "Review",
            font=ctk.CTkFont(size=11), height=26, width=100, corner_radius=6,
            fg_color=COLORS["tag_bg"] if has_review else COLORS["accent_blue"],
            hover_color="#0077cc",
            command=lambda g=game: self._open_review(g),
        ).pack(side="left", padx=(0, 6))

        game_id = game.get("game_id")
        if game_id and self.db.get_vod(game_id):
            ctk.CTkButton(
                btn_row, text="Watch VOD",
                font=ctk.CTkFont(size=11, weight="bold"),
                height=26, width=100, corner_radius=6,
                fg_color=COLORS["accent_gold"], hover_color="#a88432",
                text_color="#0a0a0f",
                command=lambda gid=game_id: self._on_open_vod(gid) if self._on_open_vod else None,
            ).pack(side="left")

    def _populate_champs(self, parent):
        champ_stats = self.db.get_champion_stats()
        scroll = ctk.CTkScrollableFrame(parent, fg_color="transparent")
        scroll.pack(fill="both", expand=True)

        if not champ_stats:
            ctk.CTkLabel(scroll, text="No champion data yet.",
                         font=ctk.CTkFont(size=14),
                         text_color=COLORS["text_dim"]).pack(pady=40)
            return

        header = ctk.CTkFrame(scroll, fg_color=COLORS["bg_input"], corner_radius=0)
        header.pack(fill="x", pady=(0, 4))
        for text, w in zip(["Champion", "Games", "WR%", "Avg KDA", "Avg CS/m", "Avg Dmg"],
                           [140, 60, 60, 80, 80, 90]):
            ctk.CTkLabel(header, text=text, font=ctk.CTkFont(size=11, weight="bold"),
                         text_color=COLORS["text_dim"], width=w).pack(side="left", padx=6, pady=6)

        for champ in champ_stats:
            row = ctk.CTkFrame(scroll, fg_color="transparent")
            row.pack(fill="x")
            wr = champ.get("winrate", 0)
            wr_c = COLORS["win_green"] if wr >= 50 else COLORS["loss_red"]
            for text, color, w in [
                (champ.get("champion_name", "?"), COLORS["accent_gold"], 140),
                (str(champ.get("games_played", 0)), COLORS["text"], 60),
                (f"{wr:.1f}%", wr_c, 60),
                (f"{champ.get('avg_kda', 0):.2f}", COLORS["text"], 80),
                (f"{champ.get('avg_cs_min', 0):.1f}", COLORS["text"], 80),
                (format_number(int(champ.get("avg_damage", 0))), COLORS["text"], 90),
            ]:
                ctk.CTkLabel(row, text=text, font=ctk.CTkFont(size=12),
                             text_color=color, width=w).pack(side="left", padx=6, pady=4)

    def _populate_stats(self, parent):
        overall = self.db.get_overall_stats()
        scroll = ctk.CTkScrollableFrame(parent, fg_color="transparent")
        scroll.pack(fill="both", expand=True)

        if not overall or overall.get("total_games", 0) == 0:
            ctk.CTkLabel(scroll, text="No stats yet — play some games!",
                         font=ctk.CTkFont(size=14),
                         text_color=COLORS["text_dim"]).pack(pady=40)
            return

        total = overall.get("total_games", 0)
        wins = overall.get("total_wins", 0)
        losses = total - wins
        wr = overall.get("winrate", 0)
        wr_c = COLORS["win_green"] if wr >= 50 else COLORS["loss_red"]

        ctk.CTkLabel(scroll, text=f"{wr:.1f}%",
                     font=ctk.CTkFont(size=48, weight="bold"),
                     text_color=wr_c).pack(pady=(20, 0))
        ctk.CTkLabel(scroll, text=f"Win Rate  •  {wins}W {losses}L ({total} games)",
                     font=ctk.CTkFont(size=14),
                     text_color=COLORS["text_dim"]).pack(pady=(0, 20))

        grid = ctk.CTkFrame(scroll, fg_color="transparent")
        grid.pack(fill="x", padx=20)
        stats_data = [
            ("Avg Kills", f"{overall.get('avg_kills', 0):.1f}"),
            ("Avg Deaths", f"{overall.get('avg_deaths', 0):.1f}"),
            ("Avg Assists", f"{overall.get('avg_assists', 0):.1f}"),
            ("Avg KDA", f"{overall.get('avg_kda', 0):.2f}"),
            ("Avg CS/min", f"{overall.get('avg_cs_min', 0):.1f}"),
            ("Avg Vision", f"{overall.get('avg_vision', 0):.1f}"),
            ("Best KDA", f"{overall.get('best_kda', 0):.1f}"),
            ("Max Kills", str(overall.get("max_kills", 0))),
            ("Pentas", str(overall.get("total_pentas", 0))),
            ("Quadras", str(overall.get("total_quadras", 0))),
        ]
        from .widgets import StatCard
        cols = 5
        for i, (label, value) in enumerate(stats_data):
            r, c = divmod(i, cols)
            StatCard(grid, label, value).grid(row=r, column=c, padx=4, pady=4, sticky="nsew")
        for c in range(cols):
            grid.columnconfigure(c, weight=1)

    def _open_review(self, game: dict):
        _open_review_popup(self.db, game, lambda: None, self._on_open_vod, self._review_popup)

    def refresh(self):
        for w in self.winfo_children():
            w.destroy()
        self._build()


# ══════════════════════════════════════════════════════════
# Page: Losses
# ══════════════════════════════════════════════════════════

class LossesPage(ctk.CTkFrame):
    def __init__(self, parent, db, on_open_vod, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self.db = db
        self._on_open_vod = on_open_vod
        self._selected_champion = "All Champions"
        self._review_popup = [None]
        self._build()

    def _build(self):
        outer = ctk.CTkFrame(self, fg_color="transparent")
        outer.pack(fill="both", expand=True, padx=16, pady=16)

        hrow = ctk.CTkFrame(outer, fg_color="transparent")
        hrow.pack(fill="x", pady=(0, 12))
        ctk.CTkLabel(hrow, text="Losses", font=ctk.CTkFont(size=22, weight="bold"),
                     text_color=COLORS["text"]).pack(side="left")
        ctk.CTkLabel(hrow, text="Review what went wrong",
                     font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(side="left", padx=(12, 0))

        # Filter
        filter_card = _card(outer)
        filter_card.pack(fill="x", pady=(0, 12))
        fi = ctk.CTkFrame(filter_card, fg_color="transparent")
        fi.pack(fill="x", padx=12, pady=10)
        ctk.CTkLabel(fi, text="Filter by Champion:",
                     font=ctk.CTkFont(size=13),
                     text_color=COLORS["text"]).pack(side="left", padx=(0, 8))

        champions = ["All Champions"] + self.db.get_unique_champions(losses_only=True)
        self._champion_dropdown = ctk.CTkComboBox(
            fi, values=champions, width=200,
            command=self._on_filter_change,
            fg_color=COLORS["bg_input"],
            button_color=COLORS["accent_blue"],
            button_hover_color="#0077cc",
            border_color=COLORS["border"],
        )
        self._champion_dropdown.set("All Champions")
        self._champion_dropdown.pack(side="left")

        self._scroll = ctk.CTkScrollableFrame(
            outer, fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        self._scroll.pack(fill="both", expand=True)
        self._refresh_losses()

    def _on_filter_change(self, choice: str):
        self._selected_champion = choice
        self._refresh_losses()

    def _refresh_losses(self):
        for w in self._scroll.winfo_children():
            w.destroy()

        champ = None if self._selected_champion == "All Champions" else self._selected_champion
        losses = self.db.get_losses(champion=champ)

        if not losses:
            ctk.CTkLabel(self._scroll,
                         text="No losses recorded yet.",
                         font=ctk.CTkFont(size=14),
                         text_color=COLORS["text_dim"]).pack(pady=40)
            return

        for loss in losses:
            self._build_loss_card(self._scroll, loss)

    def _build_loss_card(self, parent, loss: dict):
        has_review = bool(
            loss.get("mistakes", "").strip() or loss.get("went_well", "").strip()
            or loss.get("focus_next", "").strip() or (loss.get("rating") or 0) > 0
        )
        card = ctk.CTkFrame(parent, fg_color=COLORS["bg_card"], corner_radius=8,
                            border_width=2, border_color=COLORS["loss_red"])
        card.pack(fill="x", pady=6, padx=4)
        inner = ctk.CTkFrame(card, fg_color="transparent")
        inner.pack(fill="both", expand=True, padx=14, pady=12)

        top_row = ctk.CTkFrame(inner, fg_color="transparent")
        top_row.pack(fill="x", pady=(0, 8))

        left = ctk.CTkFrame(top_row, fg_color="transparent")
        left.pack(side="left", fill="x", expand=True)
        ctk.CTkLabel(left, text=loss.get("champion_name", "Unknown"),
                     font=ctk.CTkFont(size=18, weight="bold"),
                     text_color=COLORS["accent_gold"]).pack(anchor="w")
        ctk.CTkLabel(
            left,
            text=f"{loss.get('date_played', '')}  •  {format_duration(loss.get('game_duration', 0))}  •  {loss.get('game_mode', '')}",
            font=ctk.CTkFont(size=11), text_color=COLORS["text_dim"],
        ).pack(anchor="w")

        right = ctk.CTkFrame(top_row, fg_color="transparent")
        right.pack(side="right")
        k, d, a = loss.get("kills", 0), loss.get("deaths", 0), loss.get("assists", 0)
        ctk.CTkLabel(right, text=f"{k}/{d}/{a}  ({loss.get('kda_ratio', 0):.2f} KDA)",
                     font=ctk.CTkFont(size=16, weight="bold"),
                     text_color=COLORS["text"]).pack(anchor="e")

        btn_row = ctk.CTkFrame(right, fg_color="transparent")
        btn_row.pack(anchor="e", pady=(4, 0))
        ctk.CTkButton(
            btn_row, text="Edit Review" if has_review else "Review",
            font=ctk.CTkFont(size=11), height=26, width=100, corner_radius=6,
            fg_color=COLORS["tag_bg"] if has_review else COLORS["accent_blue"],
            hover_color="#0077cc",
            command=lambda g=loss: self._open_review(g),
        ).pack(side="left", padx=(0, 6))

        game_id = loss.get("game_id")
        if game_id and self.db.get_vod(game_id):
            ctk.CTkButton(
                btn_row, text="Watch VOD",
                font=ctk.CTkFont(size=11, weight="bold"),
                height=26, width=100, corner_radius=6,
                fg_color=COLORS["accent_gold"], hover_color="#a88432",
                text_color="#0a0a0f",
                command=lambda gid=game_id: self._on_open_vod(gid) if self._on_open_vod else None,
            ).pack(side="left")

        tags = json.loads(loss.get("tags", "[]")) if isinstance(loss.get("tags"), str) else loss.get("tags", [])
        if tags:
            tf = ctk.CTkFrame(inner, fg_color="transparent")
            tf.pack(fill="x", pady=(0, 6))
            for tag in tags:
                ctk.CTkLabel(tf, text=f"  {tag}  ",
                             font=ctk.CTkFont(size=11),
                             fg_color=COLORS["tag_bg"], corner_radius=12,
                             text_color=COLORS["accent_blue"]).pack(side="left", padx=2)

        ctk.CTkFrame(inner, fg_color=COLORS["border"], height=1).pack(fill="x", pady=8)

        mistakes = loss.get("mistakes", "").strip()
        if mistakes:
            ctk.CTkLabel(inner, text="What to improve:",
                         font=ctk.CTkFont(size=12, weight="bold"),
                         text_color=COLORS["star_active"]).pack(anchor="w", pady=(0, 4))
            tb = ctk.CTkTextbox(inner, height=65, font=ctk.CTkFont(size=12),
                                fg_color=COLORS["bg_input"], text_color=COLORS["text"],
                                border_width=1, border_color=COLORS["border"],
                                corner_radius=6, wrap="word")
            tb.pack(fill="x", pady=(0, 8))
            tb.insert("1.0", mistakes)
            tb.configure(state="disabled")

        focus_next = loss.get("focus_next", "").strip()
        if focus_next:
            ctk.CTkLabel(inner, text="Focus for next game:",
                         font=ctk.CTkFont(size=12, weight="bold"),
                         text_color=COLORS["accent_blue"]).pack(anchor="w", pady=(0, 4))
            fb = ctk.CTkFrame(inner, fg_color=COLORS["bg_input"], border_width=2,
                              border_color=COLORS["accent_blue"], corner_radius=6)
            fb.pack(fill="x", pady=(0, 8))
            ctk.CTkLabel(fb, text=focus_next, font=ctk.CTkFont(size=13),
                         text_color=COLORS["text"], wraplength=840,
                         justify="left").pack(padx=12, pady=10, anchor="w")

    def _open_review(self, loss: dict):
        if self._review_popup[0] and self._review_popup[0].winfo_exists():
            self._review_popup[0].destroy()
        game_id = loss.get("game_id")
        session_entry = {
            "game_id": game_id,
            "champion_name": loss.get("champion_name"),
            "win": 0,
            "mental_rating": 5,
        }
        vod_info = self.db.get_vod(game_id) if game_id else None
        has_vod = vod_info is not None
        bookmark_count = self.db.get_bookmark_count(game_id) if has_vod else 0
        self._review_popup[0] = SessionGameReviewWindow(
            db=self.db, session_entry=session_entry, game_data=loss,
            on_save=self._refresh_losses, on_open_vod=self._on_open_vod,
            has_vod=has_vod, bookmark_count=bookmark_count,
        )

    def refresh(self):
        self._refresh_losses()


# ══════════════════════════════════════════════════════════
# Page: Stats
# ══════════════════════════════════════════════════════════

class StatsPage(ctk.CTkFrame):
    def __init__(self, parent, db, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self.db = db
        self._build()

    def _build(self):
        scroll = ctk.CTkScrollableFrame(
            self, fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        scroll.pack(fill="both", expand=True, padx=16, pady=16)
        self._scroll = scroll
        self._populate(scroll)

    def _populate(self, body):
        hrow = ctk.CTkFrame(body, fg_color="transparent")
        hrow.pack(fill="x", pady=(0, 16))
        ctk.CTkLabel(hrow, text="Stats", font=ctk.CTkFont(size=22, weight="bold"),
                     text_color=COLORS["text"]).pack(side="left")
        ctk.CTkLabel(hrow, text="Performance overview",
                     font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(side="left", padx=(12, 0))

        self._build_overall(body)
        self._build_mental_winrate(body)
        self._build_seven_day(body)

    def _build_overall(self, parent):
        overall = self.db.get_overall_stats()
        section = _card(parent)
        section.pack(fill="x", pady=(0, 12))
        inner = ctk.CTkFrame(section, fg_color="transparent")
        inner.pack(fill="x", padx=16, pady=14)

        ctk.CTkLabel(inner, text="OVERALL STATS",
                     font=ctk.CTkFont(size=11, weight="bold"),
                     text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 10))

        if not overall or overall.get("total_games", 0) == 0:
            ctk.CTkLabel(inner, text="No data yet.",
                         text_color=COLORS["text_dim"]).pack(anchor="w")
            return

        total = overall.get("total_games", 0)
        wins = overall.get("total_wins", 0)
        wr = overall.get("winrate", 0)
        wr_c = COLORS["win_green"] if wr >= 50 else COLORS["loss_red"]

        row = ctk.CTkFrame(inner, fg_color="transparent")
        row.pack(fill="x")
        _stat_block(row, "Total Games", str(total))
        _stat_block(row, "Win Rate", f"{wr:.1f}%", wr_c)
        _stat_block(row, "Wins", str(wins), COLORS["win_green"])
        _stat_block(row, "Losses", str(total - wins), COLORS["loss_red"])
        _stat_block(row, "Avg KDA", f"{overall.get('avg_kda', 0):.2f}", COLORS["accent_blue"])

    def _build_mental_winrate(self, parent):
        try:
            data = self.db.get_mental_winrate_correlation()
        except Exception:
            return

        if not data:
            return

        section = _card(parent)
        section.pack(fill="x", pady=(0, 12))
        inner = ctk.CTkFrame(section, fg_color="transparent")
        inner.pack(fill="x", padx=16, pady=14)

        ctk.CTkLabel(inner, text="MENTAL vs WIN RATE",
                     font=ctk.CTkFont(size=11, weight="bold"),
                     text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 8))

        # Header
        header = ctk.CTkFrame(inner, fg_color=COLORS["bg_input"], corner_radius=6)
        header.pack(fill="x", pady=(0, 4))
        for text, w in zip(["Mental Rating", "Games", "Wins", "Win Rate"], [140, 70, 70, 100]):
            ctk.CTkLabel(header, text=text, font=ctk.CTkFont(size=11, weight="bold"),
                         text_color=COLORS["text_dim"], width=w).pack(side="left", padx=8, pady=6)

        # Rows
        if isinstance(data, dict):
            items = sorted(data.items())
        elif isinstance(data, list):
            items = [(d.get("mental_rating", d.get("rating", "?")),
                      d) for d in data]
        else:
            items = []

        for rating, row_data in items:
            if isinstance(row_data, dict):
                games = row_data.get("games", row_data.get("total", 0))
                wins = row_data.get("wins", 0)
                wr = (wins / games * 100) if games > 0 else 0
            else:
                continue

            rframe = ctk.CTkFrame(inner, fg_color="transparent")
            rframe.pack(fill="x")
            wr_c = COLORS["win_green"] if wr >= 50 else COLORS["loss_red"]

            for text, color, w in [
                (str(rating), COLORS["accent_blue"], 140),
                (str(games), COLORS["text"], 70),
                (str(wins), COLORS["win_green"], 70),
                (f"{wr:.1f}%", wr_c, 100),
            ]:
                ctk.CTkLabel(rframe, text=text, font=ctk.CTkFont(size=12),
                             text_color=color, width=w).pack(side="left", padx=8, pady=3)

    def _build_seven_day(self, parent):
        try:
            summaries = self.db.get_daily_summaries(7)
        except Exception:
            return

        if not summaries:
            return

        section = _card(parent)
        section.pack(fill="x", pady=(0, 12))
        inner = ctk.CTkFrame(section, fg_color="transparent")
        inner.pack(fill="x", padx=16, pady=14)

        ctk.CTkLabel(inner, text="LAST 7 DAYS",
                     font=ctk.CTkFont(size=11, weight="bold"),
                     text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 8))

        header = ctk.CTkFrame(inner, fg_color=COLORS["bg_input"], corner_radius=6)
        header.pack(fill="x", pady=(0, 4))
        for text, w in zip(["Date", "Games", "W/L", "Win Rate", "Avg Mental"], [140, 70, 80, 90, 100]):
            ctk.CTkLabel(header, text=text, font=ctk.CTkFont(size=11, weight="bold"),
                         text_color=COLORS["text_dim"], width=w).pack(side="left", padx=8, pady=6)

        for day in summaries:
            date = day.get("date", day.get("session_date", "?"))
            games = day.get("games", day.get("total_games", 0))
            wins = day.get("wins", 0)
            losses = games - wins if games else 0
            wr = (wins / games * 100) if games > 0 else 0
            avg_m = day.get("avg_mental", day.get("mental_avg", 0)) or 0
            wr_c = COLORS["win_green"] if wr >= 50 else (COLORS["loss_red"] if games > 0 else COLORS["text_dim"])

            rframe = ctk.CTkFrame(inner, fg_color="transparent")
            rframe.pack(fill="x")
            for text, color, w in [
                (str(date), COLORS["text_dim"], 140),
                (str(games), COLORS["text"], 70),
                (f"{wins}W {losses}L", wr_c, 80),
                (f"{wr:.1f}%", wr_c, 90),
                (f"{avg_m:.1f}" if games > 0 else "—", COLORS["accent_blue"], 100),
            ]:
                ctk.CTkLabel(rframe, text=text, font=ctk.CTkFont(size=12),
                             text_color=color, width=w).pack(side="left", padx=8, pady=3)

    def refresh(self):
        for w in self._scroll.winfo_children():
            w.destroy()
        self._populate(self._scroll)


# ══════════════════════════════════════════════════════════
# Page: Settings
# ══════════════════════════════════════════════════════════

class SettingsPage(ctk.CTkFrame):
    def __init__(self, parent, on_save=None, app_window=None, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self._on_save = on_save
        self._app_window = app_window
        self._keybind_entries: dict[str, ctk.CTkButton] = {}
        self._current_keybinds = get_keybinds()
        self._listening_action: str | None = None
        self._build_ui()

    def _build_ui(self):
        scroll = ctk.CTkScrollableFrame(
            self, fg_color=COLORS["bg_dark"],
            scrollbar_button_color=COLORS["border"],
        )
        scroll.pack(fill="both", expand=True, padx=16, pady=16)

        hrow = ctk.CTkFrame(scroll, fg_color="transparent")
        hrow.pack(fill="x", pady=(0, 16))
        ctk.CTkLabel(hrow, text="Settings", font=ctk.CTkFont(size=22, weight="bold"),
                     text_color=COLORS["text"]).pack(side="left")

        # ── Ascent VOD section
        vod_s = _card(scroll)
        vod_s.pack(fill="x", pady=(0, 12))
        inner = ctk.CTkFrame(vod_s, fg_color="transparent")
        inner.pack(fill="x", padx=14, pady=14)

        ctk.CTkLabel(inner, text="ASCENT VOD RECORDINGS",
                     font=ctk.CTkFont(size=12, weight="bold"),
                     text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 6))
        ctk.CTkLabel(
            inner,
            text="Point this to your Ascent recordings folder to enable\nVOD playback and timestamped bookmarks in your reviews.",
            font=ctk.CTkFont(size=12), text_color=COLORS["text_dim"], justify="left",
        ).pack(anchor="w", pady=(0, 10))

        path_row = ctk.CTkFrame(inner, fg_color="transparent")
        path_row.pack(fill="x", pady=(0, 8))
        current = get_ascent_folder() or ""
        self._folder_entry = ctk.CTkEntry(
            path_row, height=36, font=ctk.CTkFont(size=12),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
            placeholder_text="e.g. C:\\Users\\you\\Videos\\Ascent",
        )
        self._folder_entry.pack(side="left", fill="x", expand=True, padx=(0, 8))
        if current:
            self._folder_entry.insert(0, current)
        ctk.CTkButton(
            path_row, text="Browse", font=ctk.CTkFont(size=12),
            height=36, width=80, corner_radius=8,
            fg_color=COLORS["tag_bg"], hover_color="#333344",
            command=self._browse_folder,
        ).pack(side="right")

        self._status_label = ctk.CTkLabel(inner, text="",
                                          font=ctk.CTkFont(size=11),
                                          text_color=COLORS["text_dim"])
        self._status_label.pack(anchor="w")
        if current:
            self._show_folder_status(current)

        # Save + Clear buttons for VOD section
        vod_btns = ctk.CTkFrame(inner, fg_color="transparent")
        vod_btns.pack(fill="x", pady=(8, 0))
        ctk.CTkButton(
            vod_btns, text="Clear Ascent Folder",
            font=ctk.CTkFont(size=12), height=34, corner_radius=8,
            fg_color="transparent", hover_color=COLORS["bg_input"],
            text_color=COLORS["text_dim"], border_width=1,
            border_color=COLORS["border"],
            command=self._clear_folder,
        ).pack(side="left")

        # ── Clips section
        clips_s = _card(scroll)
        clips_s.pack(fill="x", pady=(0, 12))
        ci = ctk.CTkFrame(clips_s, fg_color="transparent")
        ci.pack(fill="x", padx=14, pady=14)

        ctk.CTkLabel(ci, text="CLIP SETTINGS",
                     font=ctk.CTkFont(size=12, weight="bold"),
                     text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 6))
        ffmpeg_ok = is_ffmpeg_available()
        ctk.CTkLabel(
            ci,
            text=f"ffmpeg: {'Available' if ffmpeg_ok else 'Not found — clip saving disabled'}",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["win_green"] if ffmpeg_ok else COLORS["loss_red"],
        ).pack(anchor="w", pady=(0, 8))
        ctk.CTkLabel(
            ci,
            text="Save short video clips from your VOD recordings.\nClips are stored separately from Ascent recordings.",
            font=ctk.CTkFont(size=12), text_color=COLORS["text_dim"], justify="left",
        ).pack(anchor="w", pady=(0, 10))

        cpr = ctk.CTkFrame(ci, fg_color="transparent")
        cpr.pack(fill="x", pady=(0, 8))
        ctk.CTkLabel(cpr, text="Folder:", font=ctk.CTkFont(size=12),
                     text_color=COLORS["text"], width=50).pack(side="left", padx=(0, 4))
        current_clips = get_clips_folder() or ""
        self._clips_folder_entry = ctk.CTkEntry(
            cpr, height=36, font=ctk.CTkFont(size=12),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
        )
        self._clips_folder_entry.pack(side="left", fill="x", expand=True, padx=(0, 8))
        if current_clips:
            self._clips_folder_entry.insert(0, current_clips)
        ctk.CTkButton(
            cpr, text="Browse", font=ctk.CTkFont(size=12), height=36, width=80,
            corner_radius=8, fg_color=COLORS["tag_bg"], hover_color="#333344",
            command=self._browse_clips_folder,
        ).pack(side="right")

        size_row = ctk.CTkFrame(ci, fg_color="transparent")
        size_row.pack(fill="x", pady=(0, 8))
        ctk.CTkLabel(size_row, text="Max folder size (MB):",
                     font=ctk.CTkFont(size=12),
                     text_color=COLORS["text"]).pack(side="left", padx=(0, 8))
        self._clips_max_size_entry = ctk.CTkEntry(
            size_row, height=36, width=100, font=ctk.CTkFont(size=12),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
        )
        self._clips_max_size_entry.pack(side="left")
        self._clips_max_size_entry.insert(0, str(get_clips_max_size_mb()))

        current_size = get_clips_folder_size_mb()
        max_size = get_clips_max_size_mb()
        usage_pct = (current_size / max_size * 100) if max_size > 0 else 0
        usage_color = (COLORS["win_green"] if usage_pct < 80
                       else COLORS["accent_gold"] if usage_pct < 95 else COLORS["loss_red"])
        ctk.CTkLabel(
            size_row,
            text=f"  Using {current_size:.0f} MB / {max_size} MB ({usage_pct:.0f}%)",
            font=ctk.CTkFont(size=11), text_color=usage_color,
        ).pack(side="left", padx=(12, 0))

        # ── Keybinds section
        kb_s = _card(scroll)
        kb_s.pack(fill="x", pady=(0, 12))
        kb = ctk.CTkFrame(kb_s, fg_color="transparent")
        kb.pack(fill="x", padx=14, pady=14)

        kb_hdr = ctk.CTkFrame(kb, fg_color="transparent")
        kb_hdr.pack(fill="x", pady=(0, 8))
        ctk.CTkLabel(kb_hdr, text="VOD PLAYER KEYBINDS",
                     font=ctk.CTkFont(size=12, weight="bold"),
                     text_color=COLORS["text_dim"]).pack(side="left")
        ctk.CTkButton(
            kb_hdr, text="Reset Defaults",
            font=ctk.CTkFont(size=11), height=26, width=100, corner_radius=6,
            fg_color="transparent", hover_color=COLORS["bg_input"],
            text_color=COLORS["text_dim"], border_width=1, border_color=COLORS["border"],
            command=self._reset_keybinds,
        ).pack(side="right")

        ctk.CTkLabel(kb, text="Click a key to rebind, then press the new key.",
                     font=ctk.CTkFont(size=11),
                     text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 8))

        for action in [
            "play_pause", "bookmark",
            "seek_fwd_1", "seek_back_1",
            "seek_fwd_2", "seek_back_2",
            "seek_fwd_5", "seek_back_5",
            "seek_fwd_10", "seek_back_10",
            "speed_up", "speed_down",
        ]:
            label_text = KEYBIND_LABELS.get(action, action)
            current_key = self._current_keybinds.get(action, "")
            row = ctk.CTkFrame(kb, fg_color="transparent")
            row.pack(fill="x", pady=2)
            ctk.CTkLabel(row, text=label_text, font=ctk.CTkFont(size=12),
                         text_color=COLORS["text"], width=120, anchor="w").pack(side="left")
            key_btn = ctk.CTkButton(
                row, text=_display_key(current_key),
                font=ctk.CTkFont(size=12), height=28, width=140, corner_radius=6,
                fg_color=COLORS["bg_input"], hover_color="#333344",
                text_color=COLORS["text"], border_width=1, border_color=COLORS["border"],
                command=lambda a=action: self._start_rebind(a),
            )
            key_btn.pack(side="left", padx=(8, 0))
            self._keybind_entries[action] = key_btn

        self._listen_label = ctk.CTkLabel(kb, text="",
                                          font=ctk.CTkFont(size=11),
                                          text_color=COLORS["accent_gold"])
        self._listen_label.pack(anchor="w", pady=(6, 0))

        # ── Save button
        btn_row = ctk.CTkFrame(scroll, fg_color="transparent")
        btn_row.pack(fill="x", pady=(12, 0))
        ctk.CTkButton(
            btn_row, text="Save Settings",
            font=ctk.CTkFont(size=14, weight="bold"), height=40, corner_radius=8,
            fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._save,
        ).pack(side="right")

    def _start_rebind(self, action: str):
        self._listening_action = action
        label = KEYBIND_LABELS.get(action, action)
        self._listen_label.configure(text=f"Press a key for '{label}'... (Esc to cancel)")
        btn = self._keybind_entries[action]
        btn.configure(fg_color=COLORS["accent_gold"], text_color="#0a0a0f", text="...")
        # Bind to the toplevel (AppWindow)
        win = self._app_window if self._app_window else self.winfo_toplevel()
        win.bind("<Key>", self._on_key_capture)

    def _on_key_capture(self, event):
        if self._listening_action is None:
            return
        if event.keysym == "Escape":
            self._stop_rebind()
            return
        parts = []
        if event.state & 0x4:
            parts.append("Control")
        if event.state & 0x1:
            parts.append("Shift")
        if event.state & 0x20000:
            parts.append("Alt")
        parts.append(event.keysym)
        tk_key = "-".join(parts)
        action = self._listening_action
        self._current_keybinds[action] = tk_key
        self._keybind_entries[action].configure(
            text=_display_key(tk_key),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
        )
        self._stop_rebind()

    def _stop_rebind(self):
        if self._listening_action:
            btn = self._keybind_entries[self._listening_action]
            current_key = self._current_keybinds.get(self._listening_action, "")
            btn.configure(text=_display_key(current_key),
                          fg_color=COLORS["bg_input"], text_color=COLORS["text"])
        self._listening_action = None
        self._listen_label.configure(text="")
        win = self._app_window if self._app_window else self.winfo_toplevel()
        try:
            win.unbind("<Key>")
        except Exception:
            pass

    def _reset_keybinds(self):
        self._current_keybinds = dict(DEFAULT_KEYBINDS)
        for action, btn in self._keybind_entries.items():
            btn.configure(text=_display_key(DEFAULT_KEYBINDS.get(action, "")))

    def _browse_folder(self):
        initial = self._folder_entry.get().strip() or None
        folder = filedialog.askdirectory(title="Select Ascent Recordings Folder",
                                         initialdir=initial)
        if folder:
            self._folder_entry.delete(0, "end")
            self._folder_entry.insert(0, folder)
            self._show_folder_status(folder)

    def _show_folder_status(self, folder: str):
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
        self._folder_entry.delete(0, "end")
        self._status_label.configure(text="Ascent VOD disabled",
                                     text_color=COLORS["text_dim"])
        set_ascent_folder("")

    def _browse_clips_folder(self):
        initial = self._clips_folder_entry.get().strip() or None
        folder = filedialog.askdirectory(title="Select Clips Folder", initialdir=initial)
        if folder:
            self._clips_folder_entry.delete(0, "end")
            self._clips_folder_entry.insert(0, folder)

    def _save(self):
        folder = self._folder_entry.get().strip()
        if folder and Path(folder).is_dir():
            set_ascent_folder(folder)
            logger.info(f"Ascent folder saved: {folder}")
        elif not folder:
            set_ascent_folder("")
            logger.info("Ascent folder cleared")
        else:
            self._status_label.configure(text="Folder does not exist",
                                         text_color=COLORS["loss_red"])
            return

        clips_folder = self._clips_folder_entry.get().strip()
        if clips_folder:
            Path(clips_folder).mkdir(parents=True, exist_ok=True)
            set_clips_folder(clips_folder)

        try:
            max_mb = int(self._clips_max_size_entry.get().strip())
            set_clips_max_size_mb(max_mb)
        except ValueError:
            pass

        set_keybinds(self._current_keybinds)

        if self._on_save:
            self._on_save()

        self._status_label.configure(text="Settings saved!", text_color=COLORS["win_green"])

    def refresh(self):
        pass  # Settings page state is preserved


# ══════════════════════════════════════════════════════════
# AppWindow — root window
# ══════════════════════════════════════════════════════════

class AppWindow(ctk.CTk):
    """Single-window app with sidebar navigation."""

    def __init__(self, db, on_minimize, on_open_vod, on_open_manual_entry,
                 on_settings_saved=None):
        super().__init__()

        self.db = db
        self._on_minimize = on_minimize
        self._on_open_vod = on_open_vod
        self._on_open_manual_entry = on_open_manual_entry
        self._on_settings_saved = on_settings_saved
        self._current_page = "home"
        self._pages: dict[str, ctk.CTkFrame] = {}
        self._nav_items: dict[str, dict] = {}
        self._update_label = None
        self._claude_context_window = None

        self.title("LoL Review")
        self.geometry("1200x800")
        self.minsize(1100, 750)
        self.configure(fg_color=COLORS["bg_dark"])

        self.lift()
        self.attributes("-topmost", True)
        self.after(200, lambda: self.attributes("-topmost", False))

        self._build_layout()

    def _build_layout(self):
        container = ctk.CTkFrame(self, fg_color="transparent", corner_radius=0)
        container.pack(fill="both", expand=True)

        # Sidebar
        self._sidebar = ctk.CTkFrame(container, width=200,
                                     fg_color=COLORS["bg_sidebar"], corner_radius=0)
        self._sidebar.pack(side="left", fill="y")
        self._sidebar.pack_propagate(False)

        # Right panel (update banner + content)
        right_panel = ctk.CTkFrame(container, fg_color=COLORS["bg_dark"], corner_radius=0)
        right_panel.pack(side="left", fill="both", expand=True)

        # Update banner slot (zero-height until needed)
        self._update_banner_slot = ctk.CTkFrame(right_panel, fg_color="transparent")
        self._update_banner_slot.pack(fill="x")

        # Content area
        self._content = ctk.CTkFrame(right_panel, fg_color=COLORS["bg_dark"], corner_radius=0)
        self._content.pack(fill="both", expand=True)

        self._build_sidebar()
        self._build_pages()
        self._navigate("home")

    def _build_sidebar(self):
        sb = self._sidebar

        # Title block
        title_frame = ctk.CTkFrame(sb, fg_color="transparent")
        title_frame.pack(fill="x", padx=16, pady=(20, 4))
        ctk.CTkLabel(title_frame, text="LoL Review",
                     font=ctk.CTkFont(size=18, weight="bold"),
                     text_color=COLORS["accent_gold"]).pack(anchor="w")
        ctk.CTkLabel(title_frame, text=f"v{__version__}",
                     font=ctk.CTkFont(size=11),
                     text_color=COLORS["text_muted"]).pack(anchor="w")

        ctk.CTkFrame(sb, height=1, fg_color=COLORS["border"]).pack(fill="x", pady=(12, 8))

        # Nav items
        for icon, label, page in [
            ("🏠", "Home", "home"),
            ("📋", "Session", "session"),
            ("📜", "History", "history"),
            ("💔", "Losses", "losses"),
            ("📊", "Stats", "stats"),
            ("⚙️", "Settings", "settings"),
        ]:
            self._build_nav_item(sb, icon, label, page)

        # Bottom: connection status + minimize
        self._conn_frame = ctk.CTkFrame(sb, fg_color="transparent")
        self._conn_frame.pack(side="bottom", fill="x", padx=16, pady=(0, 8))
        self._conn_dot = ctk.CTkFrame(self._conn_frame, width=8, height=8,
                                      corner_radius=4, fg_color=COLORS["text_muted"])
        self._conn_dot.pack(side="left", padx=(0, 6))
        self._conn_dot.pack_propagate(False)
        self._conn_label = ctk.CTkLabel(self._conn_frame, text="Waiting for League...",
                                        font=ctk.CTkFont(size=11),
                                        text_color=COLORS["text_muted"])
        self._conn_label.pack(side="left")

        ctk.CTkFrame(sb, height=1, fg_color=COLORS["border"]).pack(
            side="bottom", fill="x", pady=(0, 0))

        ctk.CTkButton(
            sb, text="Minimize to Tray",
            font=ctk.CTkFont(size=12), height=36,
            corner_radius=0, fg_color="transparent",
            hover_color=COLORS["sidebar_hover"],
            text_color=COLORS["text_dim"],
            command=self._minimize_to_tray,
        ).pack(side="bottom", fill="x")

    def _build_nav_item(self, parent, icon: str, label: str, page_name: str):
        container = ctk.CTkFrame(parent, fg_color="transparent", height=40)
        container.pack(fill="x")
        container.pack_propagate(False)

        accent = ctk.CTkFrame(container, width=3, fg_color="transparent", corner_radius=0)
        accent.pack(side="left", fill="y")
        accent.pack_propagate(False)

        btn_frame = ctk.CTkFrame(container, fg_color="transparent")
        btn_frame.pack(side="left", fill="both", expand=True)

        lbl = ctk.CTkLabel(
            btn_frame, text=f"{icon}  {label}",
            font=ctk.CTkFont(size=13), text_color=COLORS["text_dim"],
            cursor="hand2", anchor="w",
        )
        lbl.pack(fill="both", expand=True, padx=(10, 8))

        self._nav_items[page_name] = {
            "container": container,
            "accent": accent,
            "btn_frame": btn_frame,
            "label": lbl,
        }

        for widget in (container, btn_frame, lbl):
            widget.bind("<Button-1>", lambda e, p=page_name: self._navigate(p))

        def on_enter(e, p=page_name):
            if self._current_page != p:
                btn_frame.configure(fg_color=COLORS["sidebar_hover"])

        def on_leave(e, p=page_name):
            if self._current_page != p:
                btn_frame.configure(fg_color="transparent")

        for widget in (container, btn_frame, lbl):
            widget.bind("<Enter>", on_enter)
            widget.bind("<Leave>", on_leave)

    def _build_pages(self):
        def _open_claude():
            if self._claude_context_window and self._claude_context_window.winfo_exists():
                self._claude_context_window.lift()
                return
            self._claude_context_window = ClaudeContextWindow(db=self.db)

        self._pages["home"] = HomePage(
            self._content, db=self.db,
            on_open_vod=self._on_open_vod,
            on_open_claude_context=_open_claude,
        )
        self._pages["session"] = SessionPage(
            self._content, db=self.db, on_open_vod=self._on_open_vod,
        )
        self._pages["history"] = HistoryPage(
            self._content, db=self.db, on_open_vod=self._on_open_vod,
        )
        self._pages["losses"] = LossesPage(
            self._content, db=self.db, on_open_vod=self._on_open_vod,
        )
        self._pages["stats"] = StatsPage(
            self._content, db=self.db,
        )
        self._pages["settings"] = SettingsPage(
            self._content,
            on_save=self._on_settings_saved,
            app_window=self,
        )

    def _navigate(self, page_name: str):
        if page_name not in self._pages:
            return

        for p in self._pages.values():
            p.pack_forget()

        for name, items in self._nav_items.items():
            if name == page_name:
                items["accent"].configure(fg_color=COLORS["sidebar_active"])
                items["btn_frame"].configure(fg_color=COLORS["sidebar_hover"])
                items["label"].configure(text_color=COLORS["text"])
            else:
                items["accent"].configure(fg_color="transparent")
                items["btn_frame"].configure(fg_color="transparent")
                items["label"].configure(text_color=COLORS["text_dim"])

        self._pages[page_name].pack(fill="both", expand=True)
        self._current_page = page_name

    def _minimize_to_tray(self):
        self.withdraw()
        if self._on_minimize:
            self._on_minimize()

    # ── Public API ─────────────────────────────────────────

    def set_connection_status(self, connected: bool):
        color = COLORS["win_green"] if connected else COLORS["text_muted"]
        text = "League Connected" if connected else "Waiting for League..."
        try:
            self._conn_dot.configure(fg_color=color)
            self._conn_label.configure(text=text, text_color=color)
        except Exception:
            pass

    def show_update_banner(self, text: str, color: str = "#a0a0b0"):
        try:
            if self._update_label and self._update_label.winfo_exists():
                self._update_label.configure(text=text, text_color=color)
                return
            banner = ctk.CTkFrame(
                self._update_banner_slot,
                fg_color="#1a2a1a", corner_radius=0,
            )
            banner.pack(fill="x")
            self._update_label = ctk.CTkLabel(
                banner, text=text,
                font=ctk.CTkFont(size=12, weight="bold"),
                text_color=color,
            )
            self._update_label.pack(padx=16, pady=8)
        except Exception as e:
            logger.warning(f"Failed to show update banner: {e}")

    def refresh(self):
        page = self._pages.get(self._current_page)
        if page and hasattr(page, "refresh"):
            page.refresh()

    def navigate_to(self, page: str):
        self._navigate(page)
