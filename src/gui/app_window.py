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
from ..constants import (
    COLORS, format_duration, format_number,
    AUTO_REFRESH_INTERVAL_MS,
    MENTAL_EXCELLENT_THRESHOLD, MENTAL_DECENT_THRESHOLD,
    ADHERENCE_STREAK_LOCKED_IN, UNREVIEWED_GAMES_DAYS,
    UNREVIEWED_GAMES_DISPLAY_LIMIT, HISTORY_PAGE_SIZE,
)
from ..database.game_events import EVENT_STYLES
from ..lcu import GameStats
from ..version import __version__
from .claude_context import generate_and_copy
from .charts import SimpleLineChart
from .review import ReviewPanel
from .vod_player import VodPlayerPanel

logger = logging.getLogger(__name__)

_KEY_DISPLAY = {
    "space": "Space",
    "Left": "←", "Right": "→", "Up": "↑", "Down": "↓",
    "bracketleft": "[", "bracketright": "]",
    "comma": ",", "period": ".", "slash": "/", "backslash": "\\",
    "semicolon": ";", "quoteright": "'", "minus": "-", "equal": "=",
    "Return": "Enter", "BackSpace": "Backspace", "Escape": "Esc", "Tab": "Tab",
}


def _game_dict_to_stats(game: dict) -> GameStats:
    """Construct a GameStats from a database game row dict."""
    import json as _json
    items_raw = game.get("items", "[]")
    if isinstance(items_raw, str):
        try:
            items = _json.loads(items_raw)
        except (ValueError, TypeError):
            items = []
    else:
        items = items_raw or []

    return GameStats(
        game_id=game.get("game_id", 0),
        timestamp=game.get("timestamp", 0),
        game_duration=game.get("game_duration", 0),
        game_mode=game.get("game_mode", ""),
        game_type=game.get("game_type", ""),
        queue_type=game.get("queue_type", ""),
        summoner_name=game.get("summoner_name", ""),
        champion_name=game.get("champion_name", "Unknown"),
        champion_id=game.get("champion_id", 0),
        team_id=game.get("team_id", 0),
        position=game.get("position", ""),
        role=game.get("role", ""),
        win=bool(game.get("win", False)),
        kills=game.get("kills", 0),
        deaths=game.get("deaths", 0),
        assists=game.get("assists", 0),
        kda_ratio=game.get("kda_ratio", 0.0),
        largest_killing_spree=game.get("largest_killing_spree", 0),
        largest_multi_kill=game.get("largest_multi_kill", 0),
        double_kills=game.get("double_kills", 0),
        triple_kills=game.get("triple_kills", 0),
        quadra_kills=game.get("quadra_kills", 0),
        penta_kills=game.get("penta_kills", 0),
        first_blood=bool(game.get("first_blood", False)),
        total_damage_dealt=game.get("total_damage_dealt", 0),
        total_damage_to_champions=game.get("total_damage_to_champions", 0),
        physical_damage_to_champions=game.get("physical_damage_to_champions", 0),
        magic_damage_to_champions=game.get("magic_damage_to_champions", 0),
        true_damage_to_champions=game.get("true_damage_to_champions", 0),
        total_damage_taken=game.get("total_damage_taken", 0),
        damage_self_mitigated=game.get("damage_self_mitigated", 0),
        largest_critical_strike=game.get("largest_critical_strike", 0),
        gold_earned=game.get("gold_earned", 0),
        gold_spent=game.get("gold_spent", 0),
        total_minions_killed=game.get("total_minions_killed", 0),
        neutral_minions_killed=game.get("neutral_minions_killed", 0),
        cs_total=game.get("cs_total", 0),
        cs_per_min=game.get("cs_per_min", 0.0),
        vision_score=game.get("vision_score", 0),
        wards_placed=game.get("wards_placed", 0),
        wards_killed=game.get("wards_killed", 0),
        control_wards_purchased=game.get("control_wards_purchased", 0),
        turret_kills=game.get("turret_kills", 0),
        inhibitor_kills=game.get("inhibitor_kills", 0),
        dragon_kills=game.get("dragon_kills", 0),
        baron_kills=game.get("baron_kills", 0),
        rift_herald_kills=game.get("rift_herald_kills", 0),
        total_heal=game.get("total_heal", 0),
        total_heals_on_teammates=game.get("total_heals_on_teammates", 0),
        total_damage_shielded_on_teammates=game.get("total_damage_shielded_on_teammates", 0),
        total_time_cc_dealt=game.get("total_time_cc_dealt", 0),
        time_ccing_others=game.get("time_ccing_others", 0),
        spell1_casts=game.get("spell1_casts", 0),
        spell2_casts=game.get("spell2_casts", 0),
        spell3_casts=game.get("spell3_casts", 0),
        spell4_casts=game.get("spell4_casts", 0),
        summoner1_id=game.get("summoner1_id", 0),
        summoner2_id=game.get("summoner2_id", 0),
        items=items,
        champ_level=game.get("champ_level", 0),
        team_kills=game.get("team_kills", 0),
        team_deaths=game.get("team_deaths", 0),
        kill_participation=game.get("kill_participation", 0.0),
    )


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


def _refresh_scroll(scroll, rebuild_fn, pack_kw=None):
    """Rebuild a CTkScrollableFrame's content without visible flicker.

    Hides the frame, destroys children, calls rebuild_fn to repopulate,
    re-shows the frame, and restores scroll position.
    """
    pack_kw = pack_kw or {"fill": "both", "expand": True, "padx": 16, "pady": 16}
    try:
        pos = scroll._parent_canvas.yview()[0]
    except Exception:
        pos = 0.0
    scroll.pack_forget()
    for w in scroll.winfo_children():
        w.destroy()
    rebuild_fn()
    scroll.pack(**pack_kw)
    if pos > 0.01:
        scroll.after_idle(lambda: scroll._parent_canvas.yview_moveto(pos))


def _stat_block(parent, label: str, value: str, color: str = None) -> ctk.CTkLabel:
    col = ctk.CTkFrame(parent, fg_color="transparent")
    col.pack(side="left", expand=True, fill="x")
    ctk.CTkLabel(col, text=label, font=ctk.CTkFont(size=10, weight="bold"),
                 text_color=COLORS["text_dim"]).pack()
    lbl = ctk.CTkLabel(col, text=value, font=ctk.CTkFont(size=20, weight="bold"),
                       text_color=color or COLORS["text"])
    lbl.pack()
    return lbl


# ══════════════════════════════════════════════════════════
# Page: Home
# ══════════════════════════════════════════════════════════

class HomePage(ctk.CTkFrame):
    def __init__(self, parent, db, on_open_vod,
                 on_open_review=None, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self.db = db
        self._on_open_vod = on_open_vod
        self._on_open_review = on_open_review
        self._scroll = None
        self._claude_btn = None
        self._build()

    def _build(self):
        self._scroll = ctk.CTkScrollableFrame(
            self, fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        self._scroll.pack(fill="both", expand=True, padx=16, pady=16)
        self._populate(self._scroll)

    def _copy_claude_context(self):
        """Generate Claude context, copy to clipboard, flash button text."""
        context = generate_and_copy(self.db)
        self.clipboard_clear()
        self.clipboard_append(context)
        # Flash button text
        self._claude_btn.configure(text="Copied!")
        self.after(1500, lambda: self._claude_btn.configure(text="Claude Context"))

    def _populate(self, body):
        hour = datetime.now().hour
        tod = "morning" if hour < 12 else ("afternoon" if hour < 17 else "evening")

        # Greeting row
        grow = ctk.CTkFrame(body, fg_color="transparent")
        grow.pack(fill="x", pady=(0, 16))
        ctk.CTkLabel(grow, text=f"Good {tod} — let's go.",
                     font=ctk.CTkFont(size=22, weight="bold"),
                     text_color=COLORS["text"]).pack(side="left")
        self._claude_btn = ctk.CTkButton(
            grow, text="Claude Context",
            font=ctk.CTkFont(size=13, weight="bold"),
            height=36, width=150, corner_radius=8,
            fg_color=COLORS["accent_purple"], hover_color="#6d28d9",
            command=self._copy_claude_context,
        )
        self._claude_btn.pack(side="right")

        self._build_today_stats(body)
        self._build_objectives_summary(body)
        self._build_unreviewed(body)

    def _build_today_stats(self, parent):
        today = datetime.now().strftime("%Y-%m-%d")
        stats = self.db.get_session_stats_for_date(today)
        adherence = self.db.get_adherence_streak()

        card = _card(parent)
        card.pack(fill="x", pady=(0, 12))
        row = ctk.CTkFrame(card, fg_color="transparent")
        row.pack(fill="x", padx=16, pady=14)

        games = stats.get("games", 0)
        wins = stats.get("wins", 0) or 0
        losses = stats.get("losses", 0) or 0
        avg_mental = stats.get("avg_mental", 0) or 0
        wl_color = (COLORS["win_green"] if wins > losses and games > 0
                    else COLORS["loss_red"] if losses > wins else COLORS["text"])

        _stat_block(row, "Games", str(games))
        _stat_block(row, "W / L", f"{wins} / {losses}", wl_color)
        _stat_block(row, "Avg Mental", f"{avg_mental}/10" if games > 0 else "—", COLORS["accent_blue"])
        _stat_block(row, "Adherence", f"{adherence}d",
                    COLORS["win_green"] if adherence >= ADHERENCE_STREAK_LOCKED_IN else COLORS["text"])

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

    def _build_objectives_summary(self, parent):
        objectives = self.db.objectives.get_active()
        if not objectives:
            return

        card = _card(parent)
        card.pack(fill="x", pady=(0, 12))
        inner = ctk.CTkFrame(card, fg_color="transparent")
        inner.pack(fill="x", padx=16, pady=14)

        ctk.CTkLabel(inner, text="ACTIVE OBJECTIVES",
                     font=ctk.CTkFont(size=11, weight="bold"),
                     text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 10))

        for obj in objectives:
            info = self.db.objectives.get_level_info(obj["score"], obj["game_count"])
            level_color = _LEVEL_COLORS[min(info["level_index"], len(_LEVEL_COLORS) - 1)]

            obj_row = ctk.CTkFrame(inner, fg_color="transparent")
            obj_row.pack(fill="x", pady=(0, 8))

            # Title + level
            ctk.CTkLabel(
                obj_row, text=obj["title"],
                font=ctk.CTkFont(size=13, weight="bold"),
                text_color=COLORS["text"], anchor="w",
            ).pack(side="left")

            ctk.CTkLabel(
                obj_row,
                text=f"{info['level_name']}  •  {obj['score']} pts  •  {obj['game_count']} games",
                font=ctk.CTkFont(size=11),
                text_color=level_color,
            ).pack(side="right")

            # Progress bar
            bar = ctk.CTkProgressBar(
                inner, height=6, corner_radius=3,
                fg_color=COLORS["border"],
                progress_color=level_color,
            )
            bar.set(info["progress"])
            bar.pack(fill="x", pady=(0, 6))

    def _build_unreviewed(self, parent):
        unreviewed = self.db.get_unreviewed_games(days=UNREVIEWED_GAMES_DAYS)
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

        for game in unreviewed[:UNREVIEWED_GAMES_DISPLAY_LIMIT]:
            self._build_unreviewed_row(inner, game)

        if count > UNREVIEWED_GAMES_DISPLAY_LIMIT:
            ctk.CTkLabel(inner, text=f"+ {count - UNREVIEWED_GAMES_DISPLAY_LIMIT} more — check Losses page",
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
        if self._on_open_review:
            self._on_open_review("session_game", game=game, on_save=self.refresh)

    def refresh(self):
        if self._scroll:
            _refresh_scroll(self._scroll, lambda: self._populate(self._scroll))


# ══════════════════════════════════════════════════════════
# Page: Session
# ══════════════════════════════════════════════════════════

class SessionPage(ctk.CTkFrame):
    def __init__(self, parent, db, on_open_vod, on_open_review=None, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self.db = db
        self._on_open_vod = on_open_vod
        self._on_open_review = on_open_review
        self._selected_date = datetime.now().strftime("%Y-%m-%d")
        self._last_refresh_hash = None
        self._build_chrome()
        self._refresh()
        self.after(AUTO_REFRESH_INTERVAL_MS, self._auto_refresh)

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
            text_color=COLORS["win_green"] if streak >= ADHERENCE_STREAK_LOCKED_IN else COLORS["text"],
        )

        entries = self.db.get_session_log_for_date(self._selected_date)
        h = str([(e.get("game_id"), e.get("win"), e.get("mental_rating"),
                  e.get("improvement_note"), e.get("rule_broken")) for e in (entries or [])])
        if not force and h == self._last_refresh_hash:
            return
        self._last_refresh_hash = h

        def _rebuild_games():
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

        _refresh_scroll(self.scroll_frame, _rebuild_games,
                        pack_kw={"fill": "both", "expand": True})

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
        mc = COLORS["win_green"] if mental >= MENTAL_EXCELLENT_THRESHOLD else (COLORS["accent_blue"] if mental >= MENTAL_DECENT_THRESHOLD else COLORS["loss_red"])
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
        if self._on_open_review:
            # Build a game dict from session_entry + game_data for the review page
            game = dict(game_data) if game_data else {}
            game.setdefault("game_id", session_entry.get("game_id"))
            game.setdefault("champion_name", session_entry.get("champion_name"))
            game.setdefault("win", session_entry.get("win", 0))
            self._on_open_review(
                "session_game", game=game,
                session_entry=session_entry,
                on_save=self._refresh,
            )

    def _auto_refresh(self):
        if not self.winfo_exists():
            return
        try:
            self._refresh()
        except Exception as e:
            logger.warning(f"Session auto-refresh error: {e}")
        finally:
            self.after(AUTO_REFRESH_INTERVAL_MS, self._auto_refresh)

    def refresh(self):
        self._refresh(force=True)


# ══════════════════════════════════════════════════════════
# Page: History
# ══════════════════════════════════════════════════════════

class HistoryPage(ctk.CTkFrame):
    _page_size = HISTORY_PAGE_SIZE

    def __init__(self, parent, db, on_open_vod, on_open_review=None, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self.db = db
        self._on_open_vod = on_open_vod
        self._on_open_review = on_open_review
        self._current_page = 0
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
        self._games_scroll = ctk.CTkScrollableFrame(parent, fg_color="transparent")
        self._games_scroll.pack(fill="both", expand=True)
        self._load_more_btn = None
        self._load_games_page()

    def _load_games_page(self):
        offset = self._current_page * self._page_size
        games = self.db.get_recent_games(self._page_size, offset=offset)

        scroll = self._games_scroll

        # Remove the previous "Load More" button if present
        if self._load_more_btn is not None:
            self._load_more_btn.destroy()
            self._load_more_btn = None

        if not games and self._current_page == 0:
            ctk.CTkLabel(scroll, text="No games recorded yet.",
                         font=ctk.CTkFont(size=14),
                         text_color=COLORS["text_dim"]).pack(pady=40)
            return

        for game in games:
            self._build_game_row(scroll, game)

        # Show "Load More" button if we got a full page (more may exist)
        if len(games) >= self._page_size:
            self._load_more_btn = ctk.CTkButton(
                scroll, text="Load More", font=ctk.CTkFont(size=13),
                height=32, width=160, corner_radius=8,
                fg_color=COLORS["accent_blue"], hover_color="#0077cc",
                command=self._on_load_more,
            )
            self._load_more_btn.pack(pady=12)

    def _on_load_more(self):
        self._current_page += 1
        self._load_games_page()

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
            wr = champ.get("winrate") or 0
            wr_c = COLORS["win_green"] if wr >= 50 else COLORS["loss_red"]
            avg_kda = champ.get("avg_kda") or 0
            avg_cs = champ.get("avg_cs_min") or 0
            avg_dmg = champ.get("avg_damage") or 0
            for text, color, w in [
                (champ.get("champion_name") or "?", COLORS["accent_gold"], 140),
                (str(champ.get("games_played") or 0), COLORS["text"], 60),
                (f"{wr:.1f}%", wr_c, 60),
                (f"{avg_kda:.2f}", COLORS["text"], 80),
                (f"{avg_cs:.1f}", COLORS["text"], 80),
                (format_number(int(avg_dmg)), COLORS["text"], 90),
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
        if self._on_open_review:
            self._on_open_review("session_game", game=game, on_save=self.refresh)

    def refresh(self):
        self._current_page = 0
        # Only rebuild game list content, preserve tabs
        if hasattr(self, "_games_scroll") and self._games_scroll:
            _refresh_scroll(
                self._games_scroll,
                self._load_games_page,
                pack_kw={"fill": "both", "expand": True},
            )
        else:
            for w in self.winfo_children():
                w.destroy()
            self._build()


# ══════════════════════════════════════════════════════════
# Page: Losses
# ══════════════════════════════════════════════════════════

class LossesPage(ctk.CTkFrame):
    def __init__(self, parent, db, on_open_vod, on_open_review=None, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self.db = db
        self._on_open_vod = on_open_vod
        self._on_open_review = on_open_review
        self._selected_champion = "All Champions"
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
        champ = None if self._selected_champion == "All Champions" else self._selected_champion
        losses = self.db.get_losses(champion=champ)

        def _rebuild():
            if not losses:
                ctk.CTkLabel(self._scroll,
                             text="No losses recorded yet.",
                             font=ctk.CTkFont(size=14),
                             text_color=COLORS["text_dim"]).pack(pady=40)
                return
            for loss in losses:
                self._build_loss_card(self._scroll, loss)

        _refresh_scroll(self._scroll, _rebuild,
                        pack_kw={"fill": "both", "expand": True})

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
        if self._on_open_review:
            self._on_open_review("session_game", game=loss, on_save=self._refresh_losses)

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
        self._build_winrate_trend(body)
        self._build_mental_trend(body)
        self._build_deaths_trend(body)

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

    def _build_winrate_trend(self, parent):
        try:
            games = self.db.games.get_recent_for_charts(100)
        except Exception:
            return
        if len(games) < 5:
            return

        section = _card(parent)
        section.pack(fill="x", pady=(0, 12))
        inner = ctk.CTkFrame(section, fg_color="transparent")
        inner.pack(fill="x", padx=16, pady=14)

        ctk.CTkLabel(inner, text="WIN RATE TREND (20-game rolling)",
                     font=ctk.CTkFont(size=11, weight="bold"),
                     text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 8))

        window = 20
        if len(games) < window:
            window = len(games)
        data = []
        for i in range(window - 1, len(games)):
            chunk = games[i - window + 1:i + 1]
            wr = sum(1 for g in chunk if g["win"]) / window * 100
            label = f"#{i + 1}"
            data.append((label, wr))

        if len(data) < 2:
            return

        chart = SimpleLineChart(
            inner, data=data,
            color=COLORS["accent_blue"],
            target_value=50.0,
            target_color=COLORS["accent_gold"],
            height=200,
        )
        chart.pack(fill="x", pady=(0, 4))

    def _build_mental_trend(self, parent):
        try:
            entries = self.db.session_log.get_mental_trend(50)
        except Exception:
            return
        if len(entries) < 3:
            return

        section = _card(parent)
        section.pack(fill="x", pady=(0, 12))
        inner = ctk.CTkFrame(section, fg_color="transparent")
        inner.pack(fill="x", padx=16, pady=14)

        ctk.CTkLabel(inner, text="MENTAL RATING TREND",
                     font=ctk.CTkFont(size=11, weight="bold"),
                     text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 8))

        data = []
        for e in entries:
            ts = e.get("timestamp", "")
            # Show short date from timestamp if available
            if ts:
                label = str(ts)[:10]
            else:
                label = ""
            rating = e.get("mental_rating", 5)
            data.append((label, rating))

        if len(data) < 2:
            return

        chart = SimpleLineChart(
            inner, data=data,
            color=COLORS["accent_purple"],
            target_value=7.0,
            target_color=COLORS["win_green"],
            height=200,
        )
        chart.pack(fill="x", pady=(0, 4))

    def _build_deaths_trend(self, parent):
        try:
            games = self.db.games.get_recent_for_charts(50)
        except Exception:
            return
        if len(games) < 5:
            return

        section = _card(parent)
        section.pack(fill="x", pady=(0, 12))
        inner = ctk.CTkFrame(section, fg_color="transparent")
        inner.pack(fill="x", padx=16, pady=14)

        ctk.CTkLabel(inner, text="DEATHS PER GAME TREND",
                     font=ctk.CTkFont(size=11, weight="bold"),
                     text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 8))

        data = [(f"#{i + 1}", g["deaths"]) for i, g in enumerate(games)]

        if len(data) < 2:
            return

        chart = SimpleLineChart(
            inner, data=data,
            color=COLORS["loss_red"],
            height=200,
        )
        chart.pack(fill="x", pady=(0, 4))

    def refresh(self):
        _refresh_scroll(self._scroll, lambda: self._populate(self._scroll))


# ══════════════════════════════════════════════════════════
# Page: Settings
# ══════════════════════════════════════════════════════════

class SettingsPage(ctk.CTkFrame):
    def __init__(self, parent, on_save=None, app_window=None, db=None, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self._on_save = on_save
        self._app_window = app_window
        self.db = db
        self._keybind_entries: dict[str, ctk.CTkButton] = {}
        self._current_keybinds = get_keybinds()
        self._listening_action: str | None = None
        self._notes_textbox = None
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

        # ── Persistent Notes section
        notes_s = _card(scroll)
        notes_s.pack(fill="x", pady=(0, 12))
        ni = ctk.CTkFrame(notes_s, fg_color="transparent")
        ni.pack(fill="x", padx=14, pady=14)

        ctk.CTkLabel(ni, text="PERSISTENT NOTES",
                     font=ctk.CTkFont(size=12, weight="bold"),
                     text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 6))
        ctk.CTkLabel(
            ni,
            text="Notes that get included in Claude context exports.",
            font=ctk.CTkFont(size=12), text_color=COLORS["text_dim"], justify="left",
        ).pack(anchor="w", pady=(0, 10))

        self._notes_textbox = ctk.CTkTextbox(
            ni, height=120, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
            wrap="word",
        )
        self._notes_textbox.pack(fill="x", pady=(0, 8))
        if self.db:
            existing_notes = self.db.notes.get()
            if existing_notes:
                self._notes_textbox.insert("1.0", existing_notes)

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

        if self.db and self._notes_textbox:
            notes_content = self._notes_textbox.get("1.0", "end-1c").strip()
            self.db.notes.save(notes_content)

        if self._on_save:
            self._on_save()

        self._status_label.configure(text="Settings saved!", text_color=COLORS["win_green"])

    def refresh(self):
        pass  # Settings page state is preserved


# ══════════════════════════════════════════════════════════
# AppWindow — root window
# ══════════════════════════════════════════════════════════

# ══════════════════════════════════════════════════════════
# Page: Objectives
# ══════════════════════════════════════════════════════════

_LEVEL_COLORS = ["#6b7280", "#3b82f6", "#8b5cf6", "#eab308"]


class _NewObjectiveDialog(ctk.CTkToplevel):
    """Modal form for creating a new learning objective."""

    def __init__(self, parent, db, on_created, **kw):
        super().__init__(parent, **kw)
        self.db = db
        self._on_created = on_created

        self.title("New Learning Objective")
        self.geometry("520x680")
        self.configure(fg_color=COLORS["bg_dark"])
        self.resizable(False, False)
        self.lift()
        self.attributes("-topmost", True)
        self.after(100, lambda: self.attributes("-topmost", False))
        self.grab_set()

        scroll = ctk.CTkScrollableFrame(
            self, fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        scroll.pack(fill="both", expand=True, padx=20, pady=16)

        ctk.CTkLabel(scroll, text="New Objective",
                     font=ctk.CTkFont(size=20, weight="bold"),
                     text_color=COLORS["text"]).pack(anchor="w", pady=(0, 16))

        # Type selector
        ctk.CTkLabel(scroll, text="Type", font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(anchor="w")
        self._type_var = ctk.StringVar(value="primary")
        type_row = ctk.CTkFrame(scroll, fg_color="transparent")
        type_row.pack(fill="x", pady=(4, 12))
        for label, val in [("Primary (gameplay)", "primary"), ("Mental (mindset)", "mental")]:
            ctk.CTkRadioButton(
                type_row, text=label, variable=self._type_var, value=val,
                font=ctk.CTkFont(size=13), text_color=COLORS["text"],
                fg_color=COLORS["accent_blue"],
            ).pack(side="left", padx=(0, 20))

        # Title
        ctk.CTkLabel(scroll, text="Title *", font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(anchor="w")
        self._title_entry = ctk.CTkEntry(
            scroll, height=36, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_color=COLORS["border"], placeholder_text="e.g., Hit wave before roaming",
        )
        self._title_entry.pack(fill="x", pady=(4, 12))

        # Skill area
        ctk.CTkLabel(scroll, text="Skill Area", font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(anchor="w")
        self._skill_entry = ctk.CTkEntry(
            scroll, height=36, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_color=COLORS["border"],
            placeholder_text="e.g., Laning, Macro, Mental, Teamfighting",
        )
        self._skill_entry.pack(fill="x", pady=(4, 12))

        # Completion criteria
        ctk.CTkLabel(scroll, text="What does success look like in-game? *",
                     font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(anchor="w")
        self._criteria_box = ctk.CTkTextbox(
            scroll, height=80, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
        )
        self._criteria_box.pack(fill="x", pady=(4, 12))

        # Description (optional)
        ctk.CTkLabel(scroll, text="Description (optional)",
                     font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(anchor="w")
        self._desc_box = ctk.CTkTextbox(
            scroll, height=60, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
        )
        self._desc_box.pack(fill="x", pady=(4, 12))

        # Review Prompts (optional)
        ctk.CTkLabel(scroll, text="Review Prompts (optional)",
                     font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(anchor="w")

        # Build event tag options: "" (General) + EVENT_STYLES keys + derived event names
        self._event_tag_options = [""]
        self._event_tag_options.extend(sorted(EVENT_STYLES.keys()))
        try:
            for defn in self.db.derived_events.get_all_definitions():
                self._event_tag_options.append(defn["name"])
        except Exception:
            pass

        self._prompt_rows_frame = ctk.CTkFrame(scroll, fg_color="transparent")
        self._prompt_rows_frame.pack(fill="x", pady=(4, 4))
        self._prompt_rows: list[dict] = []

        ctk.CTkButton(
            scroll, text="+ Add Prompt",
            font=ctk.CTkFont(size=12), height=30, width=120,
            fg_color=COLORS["tag_bg"], hover_color="#333344",
            text_color=COLORS["text"],
            command=self._add_prompt_row,
        ).pack(anchor="w", pady=(0, 12))

        # Buttons
        btn_row = ctk.CTkFrame(scroll, fg_color="transparent")
        btn_row.pack(fill="x", pady=(8, 0))
        ctk.CTkButton(
            btn_row, text="Create Objective",
            font=ctk.CTkFont(size=14, weight="bold"), height=40,
            fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._create,
        ).pack(side="left", fill="x", expand=True, padx=(0, 6))
        ctk.CTkButton(
            btn_row, text="Cancel",
            font=ctk.CTkFont(size=14), height=40,
            fg_color="transparent", hover_color=COLORS["bg_input"],
            text_color=COLORS["text_dim"], border_width=1,
            border_color=COLORS["border"],
            command=self.destroy,
        ).pack(side="left", fill="x", expand=True)

    def _add_prompt_row(self):
        """Add a new prompt row to the dialog."""
        row_frame = ctk.CTkFrame(self._prompt_rows_frame, fg_color=COLORS["bg_card"],
                                 corner_radius=6, border_width=1,
                                 border_color=COLORS["border"])
        row_frame.pack(fill="x", pady=(0, 6))

        inner = ctk.CTkFrame(row_frame, fg_color="transparent")
        inner.pack(fill="x", padx=8, pady=6)

        # Question text entry
        question_entry = ctk.CTkEntry(
            inner, height=30, font=ctk.CTkFont(size=12),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_color=COLORS["border"],
            placeholder_text="Prompt question...",
        )
        question_entry.pack(fill="x", pady=(0, 4))

        options_row = ctk.CTkFrame(inner, fg_color="transparent")
        options_row.pack(fill="x")

        # Event tag dropdown
        tag_display = [t if t else "(General)" for t in self._event_tag_options]
        tag_var = ctk.StringVar(value=tag_display[0])
        tag_menu = ctk.CTkOptionMenu(
            options_row, variable=tag_var, values=tag_display,
            font=ctk.CTkFont(size=11), height=28, width=130,
            fg_color=COLORS["bg_input"], button_color=COLORS["border"],
            text_color=COLORS["text"],
        )
        tag_menu.pack(side="left", padx=(0, 6))

        # Answer type radio buttons
        answer_var = ctk.StringVar(value="yes_no")
        ctk.CTkRadioButton(
            options_row, text="Yes / No", variable=answer_var, value="yes_no",
            font=ctk.CTkFont(size=11), text_color=COLORS["text"],
            fg_color=COLORS["accent_blue"], height=24,
        ).pack(side="left", padx=(0, 8))
        ctk.CTkRadioButton(
            options_row, text="1-5 Scale", variable=answer_var, value="scale",
            font=ctk.CTkFont(size=11), text_color=COLORS["text"],
            fg_color=COLORS["accent_blue"], height=24,
        ).pack(side="left", padx=(0, 8))

        # Remove button
        row_data = {
            "frame": row_frame,
            "question_entry": question_entry,
            "tag_var": tag_var,
            "answer_var": answer_var,
        }
        ctk.CTkButton(
            options_row, text="X", width=28, height=28,
            font=ctk.CTkFont(size=12, weight="bold"),
            fg_color="transparent", hover_color="#3f1111",
            text_color=COLORS["loss_red"],
            command=lambda rd=row_data: self._remove_prompt_row(rd),
        ).pack(side="right")

        self._prompt_rows.append(row_data)

    def _remove_prompt_row(self, row_data: dict):
        """Remove a prompt row from the dialog."""
        row_data["frame"].destroy()
        if row_data in self._prompt_rows:
            self._prompt_rows.remove(row_data)

    def _create(self):
        title = self._title_entry.get().strip()
        if not title:
            self._title_entry.configure(border_color=COLORS["loss_red"])
            return
        criteria = self._criteria_box.get("1.0", "end-1c").strip()
        obj_id = self.db.objectives.create(
            title=title,
            skill_area=self._skill_entry.get().strip(),
            obj_type=self._type_var.get(),
            completion_criteria=criteria,
            description=self._desc_box.get("1.0", "end-1c").strip(),
        )

        # Save review prompts
        for idx, row in enumerate(self._prompt_rows):
            question = row["question_entry"].get().strip()
            if not question:
                continue
            tag_display = row["tag_var"].get()
            event_tag = "" if tag_display == "(General)" else tag_display
            answer_type = row["answer_var"].get()
            self.db.prompts.create_prompt(
                objective_id=obj_id,
                question_text=question,
                event_tag=event_tag,
                answer_type=answer_type,
                sort_order=idx,
            )

        self.destroy()
        if self._on_created:
            self._on_created()


class _ConfirmDialog(ctk.CTkToplevel):
    """Small modal confirmation dialog consistent with the app's dark theme."""

    def __init__(self, parent, message: str, confirm_text: str = "Delete",
                 confirm_color: str = COLORS["loss_red"],
                 confirm_hover: str = "#b91c1c",
                 on_confirm=None, **kw):
        super().__init__(parent, **kw)
        self._on_confirm = on_confirm

        self.title("Confirm")
        self.geometry("380x160")
        self.configure(fg_color=COLORS["bg_dark"])
        self.resizable(False, False)
        self.lift()
        self.attributes("-topmost", True)
        self.after(100, lambda: self.attributes("-topmost", False))
        self.grab_set()

        ctk.CTkLabel(
            self, text=message,
            font=ctk.CTkFont(size=14),
            text_color=COLORS["text"],
            wraplength=340, justify="center",
        ).pack(padx=20, pady=(24, 20))

        btn_row = ctk.CTkFrame(self, fg_color="transparent")
        btn_row.pack(fill="x", padx=20, pady=(0, 20))

        ctk.CTkButton(
            btn_row, text=confirm_text,
            font=ctk.CTkFont(size=14, weight="bold"), height=38,
            fg_color=confirm_color, hover_color=confirm_hover,
            command=self._confirm,
        ).pack(side="left", fill="x", expand=True, padx=(0, 6))

        ctk.CTkButton(
            btn_row, text="Cancel",
            font=ctk.CTkFont(size=14), height=38,
            fg_color="transparent", hover_color=COLORS["bg_input"],
            text_color=COLORS["text_dim"],
            border_width=1, border_color=COLORS["border"],
            command=self.destroy,
        ).pack(side="left", fill="x", expand=True)

    def _confirm(self):
        self.destroy()
        if self._on_confirm:
            self._on_confirm()


class ObjectivesPage(ctk.CTkFrame):
    def __init__(self, parent, db, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self.db = db
        self._scroll = None
        self._build()

    def _build(self):
        self._scroll = ctk.CTkScrollableFrame(
            self, fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        self._scroll.pack(fill="both", expand=True, padx=16, pady=16)
        self._populate()

    def _populate(self):
        body = self._scroll

        # Header
        hrow = ctk.CTkFrame(body, fg_color="transparent")
        hrow.pack(fill="x", pady=(0, 16))
        ctk.CTkLabel(hrow, text="Learning Objectives",
                     font=ctk.CTkFont(size=22, weight="bold"),
                     text_color=COLORS["text"]).pack(side="left")
        ctk.CTkButton(
            hrow, text="+ New Objective",
            font=ctk.CTkFont(size=13), height=34, width=140,
            fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._open_new_dialog,
        ).pack(side="right")

        objectives = self.db.objectives.get_all()
        active = [o for o in objectives if o["status"] == "active"]
        completed = [o for o in objectives if o["status"] != "active"]

        if not objectives:
            empty = ctk.CTkFrame(body, fg_color=COLORS["bg_card"], corner_radius=10,
                                 border_width=1, border_color=COLORS["border"])
            empty.pack(fill="x", pady=(0, 12))
            ctk.CTkLabel(
                empty,
                text="No objectives yet.\n\nCreate your first learning objective to start tracking progress.",
                font=ctk.CTkFont(size=14),
                text_color=COLORS["text_dim"],
                justify="center",
            ).pack(padx=20, pady=30)
            return

        if active:
            ctk.CTkLabel(body, text="ACTIVE",
                         font=ctk.CTkFont(size=11, weight="bold"),
                         text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 8))
            for obj in active:
                self._build_objective_card(body, obj)

        if completed:
            ctk.CTkLabel(body, text="COMPLETED",
                         font=ctk.CTkFont(size=11, weight="bold"),
                         text_color=COLORS["text_dim"]).pack(anchor="w", pady=(16, 8))
            for obj in completed:
                self._build_completed_card(body, obj)

    def _build_objective_card(self, parent, obj: dict):
        info = self.db.objectives.get_level_info(obj["score"], obj["game_count"])

        is_mental = obj.get("type") == "mental"
        border_color = "#7c3aed" if is_mental else COLORS["accent_blue"]
        level_color = _LEVEL_COLORS[min(info["level_index"], len(_LEVEL_COLORS) - 1)]

        card = ctk.CTkFrame(parent, fg_color=COLORS["bg_card"], corner_radius=10,
                            border_width=2, border_color=border_color)
        card.pack(fill="x", pady=(0, 12))

        inner = ctk.CTkFrame(card, fg_color="transparent")
        inner.pack(fill="x", padx=16, pady=14)

        # Title row
        title_row = ctk.CTkFrame(inner, fg_color="transparent")
        title_row.pack(fill="x", pady=(0, 4))

        type_badge_color = "#7c3aed" if is_mental else "#1e40af"
        type_text = "MENTAL" if is_mental else "PRIMARY"
        ctk.CTkLabel(
            title_row, text=type_text,
            font=ctk.CTkFont(size=9, weight="bold"),
            text_color="#ffffff", fg_color=type_badge_color,
            corner_radius=4, padx=6, pady=1,
        ).pack(side="left", padx=(0, 8))

        ctk.CTkLabel(
            title_row, text=obj["title"],
            font=ctk.CTkFont(size=16, weight="bold"),
            text_color=COLORS["text"], anchor="w",
        ).pack(side="left", fill="x", expand=True)

        prompt_count = len(self.db.prompts.get_prompts_for_objective(obj["id"]))
        if prompt_count > 0:
            ctk.CTkLabel(
                title_row,
                text=f"{prompt_count} review prompt{'s' if prompt_count != 1 else ''}",
                font=ctk.CTkFont(size=11),
                text_color=COLORS["text_dim"],
            ).pack(side="right", padx=(8, 0))

        if obj.get("skill_area"):
            ctk.CTkLabel(inner, text=obj["skill_area"],
                         font=ctk.CTkFont(size=12),
                         text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 6))

        if obj.get("completion_criteria"):
            ctk.CTkLabel(
                inner,
                text=f"Success: {obj['completion_criteria']}",
                font=ctk.CTkFont(size=12),
                text_color=COLORS["text_dim"],
                wraplength=600, justify="left",
            ).pack(anchor="w", pady=(0, 10))

        # Level + score row
        level_row = ctk.CTkFrame(inner, fg_color="transparent")
        level_row.pack(fill="x", pady=(0, 6))

        ctk.CTkLabel(
            level_row, text=info["level_name"],
            font=ctk.CTkFont(size=14, weight="bold"),
            text_color=level_color,
        ).pack(side="left")

        ctk.CTkLabel(
            level_row,
            text=f"  {obj['score']} pts  •  {obj['game_count']} games",
            font=ctk.CTkFont(size=13),
            text_color=COLORS["text_dim"],
        ).pack(side="left")

        if info["next_threshold"] is not None:
            pts_needed = info["next_threshold"] - obj["score"]
            ctk.CTkLabel(
                level_row,
                text=f"  ({pts_needed} pts to next level)",
                font=ctk.CTkFont(size=11),
                text_color=COLORS["text_dim"],
            ).pack(side="left")

        # Progress bar
        progress_bar = ctk.CTkProgressBar(
            inner, height=8, corner_radius=4,
            fg_color=COLORS["border"],
            progress_color=level_color,
        )
        progress_bar.set(info["progress"])
        progress_bar.pack(fill="x", pady=(0, 12))

        # Action buttons
        btn_row = ctk.CTkFrame(inner, fg_color="transparent")
        btn_row.pack(fill="x")

        if info["suggest_complete"]:
            ctk.CTkLabel(
                btn_row,
                text="Ready to complete! You've mastered this objective.",
                font=ctk.CTkFont(size=12, weight="bold"),
                text_color=COLORS["accent_gold"],
            ).pack(side="left", padx=(0, 10))

        if info["can_complete"]:
            ctk.CTkButton(
                btn_row, text="Mark Complete",
                font=ctk.CTkFont(size=12), height=30, width=130,
                fg_color="#22c55e", hover_color="#16a34a",
                text_color="#ffffff",
                command=lambda oid=obj["id"]: self._complete_objective(oid),
            ).pack(side="right", padx=(6, 0))
        else:
            remaining = max(0, 30 - obj["game_count"])
            ctk.CTkLabel(
                btn_row,
                text=f"{remaining} more games to unlock completion",
                font=ctk.CTkFont(size=11),
                text_color=COLORS["text_dim"],
            ).pack(side="right")

        ctk.CTkButton(
            btn_row, text="Delete",
            font=ctk.CTkFont(size=12), height=30, width=70,
            fg_color="transparent", hover_color="#3f1111",
            text_color=COLORS["loss_red"],
            border_width=1, border_color=COLORS["loss_red"],
            command=lambda oid=obj["id"]: self._delete_objective(oid),
        ).pack(side="right")

    def _build_completed_card(self, parent, obj: dict):
        card = ctk.CTkFrame(parent, fg_color=COLORS["bg_card"], corner_radius=8,
                            border_width=1, border_color=COLORS["border"])
        card.pack(fill="x", pady=(0, 8))

        row = ctk.CTkFrame(card, fg_color="transparent")
        row.pack(fill="x", padx=14, pady=10)

        ctk.CTkLabel(
            row, text=obj["title"],
            font=ctk.CTkFont(size=14),
            text_color=COLORS["text_dim"], anchor="w",
        ).pack(side="left", fill="x", expand=True)

        ctk.CTkLabel(
            row, text=f"{obj['score']} pts  •  {obj['game_count']} games",
            font=ctk.CTkFont(size=12), text_color=COLORS["text_dim"],
        ).pack(side="right", padx=(8, 0))

        ctk.CTkLabel(
            row, text="DONE",
            font=ctk.CTkFont(size=10, weight="bold"),
            text_color="#22c55e", fg_color="#1a3a1a",
            corner_radius=4, padx=6, pady=1,
        ).pack(side="right")

    def _open_new_dialog(self):
        _NewObjectiveDialog(self, db=self.db, on_created=self.refresh)

    def _complete_objective(self, obj_id: int):
        def _do_complete():
            self.db.objectives.mark_complete(obj_id)
            self.refresh()

        _ConfirmDialog(
            self,
            message="Mark this objective as complete? This cannot be undone.",
            confirm_text="Complete",
            confirm_color=COLORS["win_green"],
            confirm_hover="#16a34a",
            on_confirm=_do_complete,
        )

    def _delete_objective(self, obj_id: int):
        def _do_delete():
            self.db.objectives.delete(obj_id)
            self.refresh()

        _ConfirmDialog(
            self,
            message="Delete this objective? This cannot be undone.",
            confirm_text="Delete",
            confirm_color=COLORS["loss_red"],
            confirm_hover="#b91c1c",
            on_confirm=_do_delete,
        )

    def refresh(self):
        _refresh_scroll(self._scroll, self._populate)


# ══════════════════════════════════════════════════════════
# Page: Rules
# ══════════════════════════════════════════════════════════

_RULE_TYPES = [
    ("custom",        "Custom",          "Personal reminder — not auto-checked"),
    ("no_play_day",   "No-Play Day",     "Don't play on specific days"),
    ("no_play_after", "No Play After",   "Don't play after a certain hour"),
    ("loss_streak",   "Loss Streak",     "Stop after N consecutive losses"),
    ("max_games",     "Max Games/Day",   "Limit how many games you play per day"),
    ("min_mental",    "Minimum Mental",  "Don't queue if mental rating is too low"),
]

_RULE_TYPE_MAP = {rt[0]: rt[1] for rt in _RULE_TYPES}

_DAYS_OF_WEEK = [
    "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
]


class RulesPage(ctk.CTkFrame):
    def __init__(self, parent, db, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self.db = db
        self._scroll = None
        self._build()

    def _build(self):
        self._scroll = ctk.CTkScrollableFrame(
            self, fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        self._scroll.pack(fill="both", expand=True, padx=16, pady=16)
        self._populate()

    def _populate(self):
        body = self._scroll

        # Header
        hrow = ctk.CTkFrame(body, fg_color="transparent")
        hrow.pack(fill="x", pady=(0, 16))
        ctk.CTkLabel(hrow, text="My Rules",
                     font=ctk.CTkFont(size=22, weight="bold"),
                     text_color=COLORS["text"]).pack(side="left")
        ctk.CTkButton(
            hrow, text="+ New Rule",
            font=ctk.CTkFont(size=13), height=34, width=120,
            fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._open_new_dialog,
        ).pack(side="right")

        rules = self.db.rules.get_all()
        active = [r for r in rules if r["is_active"]]
        inactive = [r for r in rules if not r["is_active"]]

        if not rules:
            empty = _card(body)
            empty.pack(fill="x", pady=(0, 12))
            ctk.CTkLabel(
                empty,
                text=(
                    "No rules yet.\n\n"
                    "Set rules to keep yourself accountable.\n"
                    "e.g., \"Don't play on Sunday\", \"Stop after 2 losses\""
                ),
                font=ctk.CTkFont(size=14),
                text_color=COLORS["text_dim"],
                justify="center",
            ).pack(padx=20, pady=30)
        else:
            # Check violations for active rules
            todays_games = self.db.get_todays_games()
            violations = self.db.rules.check_violations(todays_games=todays_games)
            violation_map = {v["rule"]["id"]: v for v in violations}

            if active:
                ctk.CTkLabel(body, text="ACTIVE",
                             font=ctk.CTkFont(size=11, weight="bold"),
                             text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 8))
                for rule in active:
                    vinfo = violation_map.get(rule["id"])
                    self._build_rule_card(body, rule, vinfo)

            if inactive:
                ctk.CTkLabel(body, text="DISABLED",
                             font=ctk.CTkFont(size=11, weight="bold"),
                             text_color=COLORS["text_dim"]).pack(anchor="w", pady=(16, 8))
                for rule in inactive:
                    self._build_rule_card(body, rule, None, dimmed=True)

        # ── Custom Events Section ─────────────────────────────────
        separator = ctk.CTkFrame(body, fg_color=COLORS["border"], height=1)
        separator.pack(fill="x", pady=(24, 24))

        de_hrow = ctk.CTkFrame(body, fg_color="transparent")
        de_hrow.pack(fill="x", pady=(0, 16))
        ctk.CTkLabel(de_hrow, text="Custom Events",
                     font=ctk.CTkFont(size=22, weight="bold"),
                     text_color=COLORS["text"]).pack(side="left")
        ctk.CTkButton(
            de_hrow, text="+ New Custom Event",
            font=ctk.CTkFont(size=13), height=34, width=160,
            fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._open_new_derived_event_dialog,
        ).pack(side="right")

        definitions = self.db.derived_events.get_all_definitions()
        if not definitions:
            empty = _card(body)
            empty.pack(fill="x", pady=(0, 12))
            ctk.CTkLabel(
                empty,
                text=(
                    "No custom events yet.\n\n"
                    "Define custom events to detect patterns like teamfights,\n"
                    "skirmishes, or objective contests on your VOD timeline."
                ),
                font=ctk.CTkFont(size=14),
                text_color=COLORS["text_dim"],
                justify="center",
            ).pack(padx=20, pady=30)
        else:
            for defn in definitions:
                self._build_derived_event_card(body, defn)

    def _build_rule_card(self, parent, rule: dict, violation: dict | None,
                         dimmed: bool = False):
        is_violated = violation and violation["violated"]
        border_color = COLORS["loss_red"] if is_violated else COLORS["border"]

        card = ctk.CTkFrame(parent, fg_color=COLORS["bg_card"], corner_radius=10,
                            border_width=2 if is_violated else 1,
                            border_color=border_color)
        card.pack(fill="x", pady=(0, 10))

        inner = ctk.CTkFrame(card, fg_color="transparent")
        inner.pack(fill="x", padx=16, pady=12)

        # Top row: type badge + name + violation status
        top_row = ctk.CTkFrame(inner, fg_color="transparent")
        top_row.pack(fill="x", pady=(0, 4))

        type_label = _RULE_TYPE_MAP.get(rule["rule_type"], "Custom")
        badge_color = "#7c3aed" if rule["rule_type"] == "custom" else "#1e40af"
        ctk.CTkLabel(
            top_row, text=type_label.upper(),
            font=ctk.CTkFont(size=9, weight="bold"),
            text_color="#ffffff", fg_color=badge_color,
            corner_radius=4, padx=6, pady=1,
        ).pack(side="left", padx=(0, 8))

        name_color = COLORS["text_dim"] if dimmed else COLORS["text"]
        ctk.CTkLabel(
            top_row, text=rule["name"],
            font=ctk.CTkFont(size=15, weight="bold"),
            text_color=name_color, anchor="w",
        ).pack(side="left", fill="x", expand=True)

        # Status indicator for auto-checkable rules
        if is_violated:
            ctk.CTkLabel(
                top_row, text="VIOLATED",
                font=ctk.CTkFont(size=10, weight="bold"),
                text_color="#ffffff", fg_color=COLORS["loss_red"],
                corner_radius=4, padx=8, pady=2,
            ).pack(side="right")
        elif violation and rule["rule_type"] != "custom":
            ctk.CTkLabel(
                top_row, text="OK",
                font=ctk.CTkFont(size=10, weight="bold"),
                text_color="#ffffff", fg_color=COLORS["win_green"],
                corner_radius=4, padx=8, pady=2,
            ).pack(side="right")

        # Violation reason
        if is_violated and violation["reason"]:
            ctk.CTkLabel(
                inner, text=violation["reason"],
                font=ctk.CTkFont(size=12),
                text_color=COLORS["loss_red"],
            ).pack(anchor="w", pady=(0, 4))

        # Description
        if rule.get("description"):
            ctk.CTkLabel(
                inner, text=rule["description"],
                font=ctk.CTkFont(size=12),
                text_color=COLORS["text_dim"],
                wraplength=600, justify="left",
            ).pack(anchor="w", pady=(0, 4))

        # Condition display for auto-checkable rules
        condition_text = self._format_condition(rule)
        if condition_text:
            ctk.CTkLabel(
                inner, text=condition_text,
                font=ctk.CTkFont(size=11),
                text_color=COLORS["text_muted"],
            ).pack(anchor="w", pady=(0, 4))

        # Action buttons
        btn_row = ctk.CTkFrame(inner, fg_color="transparent")
        btn_row.pack(fill="x", pady=(4, 0))

        toggle_text = "Disable" if rule["is_active"] else "Enable"
        toggle_color = COLORS["text_dim"] if rule["is_active"] else COLORS["win_green"]
        ctk.CTkButton(
            btn_row, text=toggle_text,
            font=ctk.CTkFont(size=12), height=28, width=80,
            fg_color="transparent", hover_color=COLORS["bg_input"],
            text_color=toggle_color,
            border_width=1, border_color=COLORS["border"],
            command=lambda rid=rule["id"]: self._toggle_rule(rid),
        ).pack(side="right", padx=(6, 0))

        ctk.CTkButton(
            btn_row, text="Delete",
            font=ctk.CTkFont(size=12), height=28, width=70,
            fg_color="transparent", hover_color="#3f1111",
            text_color=COLORS["loss_red"],
            border_width=1, border_color=COLORS["loss_red"],
            command=lambda rid=rule["id"]: self._delete_rule(rid),
        ).pack(side="right")

    @staticmethod
    def _format_condition(rule: dict) -> str:
        rt = rule["rule_type"]
        cv = rule["condition_value"]
        if not cv:
            return ""
        if rt == "no_play_day":
            return f"Days: {cv}"
        if rt == "no_play_after":
            try:
                h = int(cv)
                suffix = "AM" if h < 12 else "PM"
                display_h = h if h <= 12 else h - 12
                if display_h == 0:
                    display_h = 12
                return f"No play after {display_h}:00 {suffix}"
            except ValueError:
                return ""
        if rt == "loss_streak":
            return f"Stop after {cv} consecutive losses"
        if rt == "max_games":
            return f"Max {cv} games per day"
        if rt == "min_mental":
            return f"Don't queue below mental {cv}"
        return ""

    def _open_new_dialog(self):
        _NewRuleDialog(self, db=self.db, on_created=self.refresh)

    def _toggle_rule(self, rule_id: int):
        self.db.rules.toggle(rule_id)
        self.refresh()

    def _delete_rule(self, rule_id: int):
        def _do_delete():
            self.db.rules.delete(rule_id)
            self.refresh()

        _ConfirmDialog(
            self,
            message="Delete this rule? This cannot be undone.",
            confirm_text="Delete",
            confirm_color=COLORS["loss_red"],
            confirm_hover="#b91c1c",
            on_confirm=_do_delete,
        )

    def _build_derived_event_card(self, parent, defn: dict):
        is_default = defn.get("is_default", 0)
        card = ctk.CTkFrame(parent, fg_color=COLORS["bg_card"], corner_radius=10,
                            border_width=1, border_color=COLORS["border"])
        card.pack(fill="x", pady=(0, 10))

        inner = ctk.CTkFrame(card, fg_color="transparent")
        inner.pack(fill="x", padx=16, pady=12)

        # Top row: color dot + name + default badge
        top_row = ctk.CTkFrame(inner, fg_color="transparent")
        top_row.pack(fill="x", pady=(0, 4))

        color = defn.get("color", "#ff6b6b")
        ctk.CTkLabel(
            top_row, text="\u25cf",
            font=ctk.CTkFont(size=16),
            text_color=color,
        ).pack(side="left", padx=(0, 6))

        ctk.CTkLabel(
            top_row, text=defn["name"],
            font=ctk.CTkFont(size=15, weight="bold"),
            text_color=COLORS["text"], anchor="w",
        ).pack(side="left", fill="x", expand=True)

        if is_default:
            ctk.CTkLabel(
                top_row, text="DEFAULT",
                font=ctk.CTkFont(size=9, weight="bold"),
                text_color="#ffffff", fg_color=COLORS["text_muted"],
                corner_radius=4, padx=6, pady=1,
            ).pack(side="right")

        # Details row: source types, min count, window
        source_types = defn.get("source_types", [])
        source_str = ", ".join(source_types) if source_types else "None"
        details_text = (
            f"Sources: {source_str}  |  "
            f"Min count: {defn.get('min_count', 0)}  |  "
            f"Window: {defn.get('window_seconds', 0)}s"
        )
        ctk.CTkLabel(
            inner, text=details_text,
            font=ctk.CTkFont(size=12),
            text_color=COLORS["text_dim"],
            wraplength=600, justify="left",
        ).pack(anchor="w", pady=(0, 4))

        # Action buttons (only for non-default definitions)
        if not is_default:
            btn_row = ctk.CTkFrame(inner, fg_color="transparent")
            btn_row.pack(fill="x", pady=(4, 0))
            ctk.CTkButton(
                btn_row, text="Delete",
                font=ctk.CTkFont(size=12), height=28, width=70,
                fg_color="transparent", hover_color="#3f1111",
                text_color=COLORS["loss_red"],
                border_width=1, border_color=COLORS["loss_red"],
                command=lambda did=defn["id"]: self._delete_derived_event(did),
            ).pack(side="right")

    def _open_new_derived_event_dialog(self):
        _NewDerivedEventDialog(self, db=self.db, on_created=self.refresh)

    def _delete_derived_event(self, def_id: int):
        def _do_delete():
            self.db.derived_events.delete_definition(def_id)
            self.refresh()

        _ConfirmDialog(
            self,
            message="Delete this custom event? All computed instances will also be removed.",
            confirm_text="Delete",
            confirm_color=COLORS["loss_red"],
            confirm_hover="#b91c1c",
            on_confirm=_do_delete,
        )

    def refresh(self):
        _refresh_scroll(self._scroll, self._populate)


class _NewRuleDialog(ctk.CTkToplevel):
    """Modal form for creating a new rule."""

    def __init__(self, parent, db, on_created, **kw):
        super().__init__(parent, **kw)
        self.db = db
        self._on_created = on_created

        self.title("New Rule")
        self.geometry("480x520")
        self.configure(fg_color=COLORS["bg_dark"])
        self.resizable(False, False)
        self.lift()
        self.attributes("-topmost", True)
        self.after(100, lambda: self.attributes("-topmost", False))
        self.grab_set()

        scroll = ctk.CTkScrollableFrame(
            self, fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        scroll.pack(fill="both", expand=True, padx=20, pady=16)

        ctk.CTkLabel(scroll, text="New Rule",
                     font=ctk.CTkFont(size=20, weight="bold"),
                     text_color=COLORS["text"]).pack(anchor="w", pady=(0, 16))

        # Type selector
        ctk.CTkLabel(scroll, text="Rule Type", font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(anchor="w")

        type_values = [rt[1] for rt in _RULE_TYPES]
        self._type_key_map = {rt[1]: rt[0] for rt in _RULE_TYPES}
        self._type_menu = ctk.CTkOptionMenu(
            scroll, values=type_values,
            font=ctk.CTkFont(size=13), height=36,
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            button_color=COLORS["accent_blue"],
            button_hover_color="#0077cc",
            dropdown_fg_color=COLORS["bg_card"],
            dropdown_text_color=COLORS["text"],
            dropdown_hover_color=COLORS["bg_input"],
            command=self._on_type_change,
        )
        self._type_menu.set(type_values[0])
        self._type_menu.pack(fill="x", pady=(4, 12))

        # Name
        ctk.CTkLabel(scroll, text="Rule Name *", font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(anchor="w")
        self._name_entry = ctk.CTkEntry(
            scroll, height=36, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_color=COLORS["border"],
            placeholder_text="e.g., No games on Sunday",
        )
        self._name_entry.pack(fill="x", pady=(4, 12))

        # Dynamic condition frame
        self._condition_frame = ctk.CTkFrame(scroll, fg_color="transparent")
        self._condition_frame.pack(fill="x", pady=(0, 12))
        self._condition_widgets = {}
        self._build_condition_ui("custom")

        # Description (optional)
        ctk.CTkLabel(scroll, text="Description (optional)",
                     font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(anchor="w")
        self._desc_box = ctk.CTkTextbox(
            scroll, height=60, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_width=1, border_color=COLORS["border"], corner_radius=8,
        )
        self._desc_box.pack(fill="x", pady=(4, 12))

        # Buttons
        btn_row = ctk.CTkFrame(scroll, fg_color="transparent")
        btn_row.pack(fill="x", pady=(8, 0))
        ctk.CTkButton(
            btn_row, text="Create Rule",
            font=ctk.CTkFont(size=14, weight="bold"), height=40,
            fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._create,
        ).pack(side="left", fill="x", expand=True, padx=(0, 6))
        ctk.CTkButton(
            btn_row, text="Cancel",
            font=ctk.CTkFont(size=14), height=40,
            fg_color="transparent", hover_color=COLORS["bg_input"],
            text_color=COLORS["text_dim"], border_width=1,
            border_color=COLORS["border"],
            command=self.destroy,
        ).pack(side="left", fill="x", expand=True)

    def _on_type_change(self, choice: str):
        rule_type = self._type_key_map.get(choice, "custom")
        self._build_condition_ui(rule_type)

    def _build_condition_ui(self, rule_type: str):
        for w in self._condition_frame.winfo_children():
            w.destroy()
        self._condition_widgets = {}

        if rule_type == "custom":
            return

        if rule_type == "no_play_day":
            ctk.CTkLabel(
                self._condition_frame, text="Select days:",
                font=ctk.CTkFont(size=13), text_color=COLORS["text_dim"],
            ).pack(anchor="w", pady=(0, 4))
            self._condition_widgets["day_vars"] = {}
            days_frame = ctk.CTkFrame(self._condition_frame, fg_color="transparent")
            days_frame.pack(fill="x")
            for day in _DAYS_OF_WEEK:
                var = ctk.BooleanVar(value=False)
                self._condition_widgets["day_vars"][day] = var
                ctk.CTkCheckBox(
                    days_frame, text=day[:3], variable=var,
                    font=ctk.CTkFont(size=12), text_color=COLORS["text"],
                    fg_color=COLORS["accent_blue"], hover_color="#0077cc",
                    checkbox_width=20, checkbox_height=20,
                ).pack(side="left", padx=(0, 8), pady=2)

        elif rule_type == "no_play_after":
            ctk.CTkLabel(
                self._condition_frame, text="Don't play after:",
                font=ctk.CTkFont(size=13), text_color=COLORS["text_dim"],
            ).pack(anchor="w", pady=(0, 4))
            hours = [f"{h}:00 {'AM' if h < 12 else 'PM'}" for h in range(12, 24)] + \
                    [f"{h}:00 AM" for h in range(0, 4)]
            self._condition_widgets["hour_menu"] = ctk.CTkOptionMenu(
                self._condition_frame, values=hours,
                font=ctk.CTkFont(size=13), height=36,
                fg_color=COLORS["bg_input"], text_color=COLORS["text"],
                button_color=COLORS["accent_blue"],
                dropdown_fg_color=COLORS["bg_card"],
                dropdown_text_color=COLORS["text"],
                dropdown_hover_color=COLORS["bg_input"],
            )
            self._condition_widgets["hour_menu"].set("23:00 PM")
            self._condition_widgets["hour_menu"].pack(fill="x")

        elif rule_type == "loss_streak":
            ctk.CTkLabel(
                self._condition_frame, text="Stop after how many consecutive losses?",
                font=ctk.CTkFont(size=13), text_color=COLORS["text_dim"],
            ).pack(anchor="w", pady=(0, 4))
            self._condition_widgets["entry"] = ctk.CTkEntry(
                self._condition_frame, height=36, font=ctk.CTkFont(size=13),
                fg_color=COLORS["bg_input"], text_color=COLORS["text"],
                border_color=COLORS["border"], placeholder_text="2",
            )
            self._condition_widgets["entry"].pack(fill="x")

        elif rule_type == "max_games":
            ctk.CTkLabel(
                self._condition_frame, text="Maximum games per day:",
                font=ctk.CTkFont(size=13), text_color=COLORS["text_dim"],
            ).pack(anchor="w", pady=(0, 4))
            self._condition_widgets["entry"] = ctk.CTkEntry(
                self._condition_frame, height=36, font=ctk.CTkFont(size=13),
                fg_color=COLORS["bg_input"], text_color=COLORS["text"],
                border_color=COLORS["border"], placeholder_text="5",
            )
            self._condition_widgets["entry"].pack(fill="x")

        elif rule_type == "min_mental":
            ctk.CTkLabel(
                self._condition_frame, text="Minimum mental rating to queue:",
                font=ctk.CTkFont(size=13), text_color=COLORS["text_dim"],
            ).pack(anchor="w", pady=(0, 4))
            self._condition_widgets["entry"] = ctk.CTkEntry(
                self._condition_frame, height=36, font=ctk.CTkFont(size=13),
                fg_color=COLORS["bg_input"], text_color=COLORS["text"],
                border_color=COLORS["border"], placeholder_text="5",
            )
            self._condition_widgets["entry"].pack(fill="x")

    def _get_condition_value(self, rule_type: str) -> str:
        if rule_type == "custom":
            return ""

        if rule_type == "no_play_day":
            day_vars = self._condition_widgets.get("day_vars", {})
            selected = [day for day, var in day_vars.items() if var.get()]
            return ",".join(selected)

        if rule_type == "no_play_after":
            menu = self._condition_widgets.get("hour_menu")
            if menu:
                text = menu.get()  # e.g., "23:00 PM"
                try:
                    return str(int(text.split(":")[0]))
                except (ValueError, IndexError):
                    return ""
            return ""

        # loss_streak, max_games, min_mental — all numeric entries
        entry = self._condition_widgets.get("entry")
        if entry:
            return entry.get().strip()
        return ""

    def _create(self):
        name = self._name_entry.get().strip()
        if not name:
            self._name_entry.configure(border_color=COLORS["loss_red"])
            return

        type_display = self._type_menu.get()
        rule_type = self._type_key_map.get(type_display, "custom")
        condition_value = self._get_condition_value(rule_type)

        # Validate numeric conditions
        if rule_type in ("loss_streak", "max_games", "min_mental"):
            try:
                val = int(condition_value)
                if val <= 0:
                    raise ValueError
            except ValueError:
                entry = self._condition_widgets.get("entry")
                if entry:
                    entry.configure(border_color=COLORS["loss_red"])
                return

        if rule_type == "no_play_day" and not condition_value:
            return  # No days selected

        self.db.rules.create(
            name=name,
            description=self._desc_box.get("1.0", "end-1c").strip(),
            rule_type=rule_type,
            condition_value=condition_value,
        )
        self.destroy()
        if self._on_created:
            self._on_created()


_DERIVED_EVENT_PRESET_COLORS = [
    ("#ff6b6b", "Red"),
    ("#fbbf24", "Gold"),
    ("#22c55e", "Green"),
    ("#0099ff", "Blue"),
    ("#8b5cf6", "Purple"),
    ("#ec4899", "Pink"),
    ("#06b6d4", "Cyan"),
    ("#f97316", "Orange"),
]


class _NewDerivedEventDialog(ctk.CTkToplevel):
    """Modal form for creating a new derived event definition."""

    def __init__(self, parent, db, on_created, **kw):
        super().__init__(parent, **kw)
        self.db = db
        self._on_created = on_created
        self._selected_color = _DERIVED_EVENT_PRESET_COLORS[0][0]
        self._color_buttons = []

        self.title("New Custom Event")
        self.geometry("500x580")
        self.configure(fg_color=COLORS["bg_dark"])
        self.resizable(False, False)
        self.lift()
        self.attributes("-topmost", True)
        self.after(100, lambda: self.attributes("-topmost", False))
        self.grab_set()

        scroll = ctk.CTkScrollableFrame(
            self, fg_color="transparent",
            scrollbar_button_color=COLORS["border"],
        )
        scroll.pack(fill="both", expand=True, padx=20, pady=16)

        ctk.CTkLabel(scroll, text="New Custom Event",
                     font=ctk.CTkFont(size=20, weight="bold"),
                     text_color=COLORS["text"]).pack(anchor="w", pady=(0, 16))

        # Name
        ctk.CTkLabel(scroll, text="Event Name *", font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(anchor="w")
        self._name_entry = ctk.CTkEntry(
            scroll, height=36, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_color=COLORS["border"],
            placeholder_text="e.g., Teamfight, Skirmish",
        )
        self._name_entry.pack(fill="x", pady=(4, 12))

        # Source types (checkboxes for event types from EVENT_STYLES)
        ctk.CTkLabel(scroll, text="Source Event Types *", font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 4))
        self._source_vars = {}
        source_frame = ctk.CTkFrame(scroll, fg_color="transparent")
        source_frame.pack(fill="x", pady=(0, 12))

        for etype, style in EVENT_STYLES.items():
            if etype == "LEVEL_UP":
                continue  # Skip level up — not useful for clustering
            var = ctk.BooleanVar(value=False)
            self._source_vars[etype] = var
            cb_frame = ctk.CTkFrame(source_frame, fg_color="transparent")
            cb_frame.pack(anchor="w", pady=1)
            ctk.CTkCheckBox(
                cb_frame, text=style.get("label", etype), variable=var,
                font=ctk.CTkFont(size=12), text_color=COLORS["text"],
                fg_color=style["color"], hover_color=style["color"],
                checkbox_width=18, checkbox_height=18,
            ).pack(side="left")

        # Min count
        ctk.CTkLabel(scroll, text="Minimum Event Count *", font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(anchor="w")
        self._min_count_entry = ctk.CTkEntry(
            scroll, height=36, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_color=COLORS["border"], placeholder_text="3",
        )
        self._min_count_entry.pack(fill="x", pady=(4, 12))

        # Window seconds
        ctk.CTkLabel(scroll, text="Time Window (seconds) *", font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(anchor="w")
        self._window_entry = ctk.CTkEntry(
            scroll, height=36, font=ctk.CTkFont(size=13),
            fg_color=COLORS["bg_input"], text_color=COLORS["text"],
            border_color=COLORS["border"], placeholder_text="30",
        )
        self._window_entry.pack(fill="x", pady=(4, 12))

        # Color picker (preset buttons)
        ctk.CTkLabel(scroll, text="Color", font=ctk.CTkFont(size=13),
                     text_color=COLORS["text_dim"]).pack(anchor="w", pady=(0, 4))
        color_frame = ctk.CTkFrame(scroll, fg_color="transparent")
        color_frame.pack(fill="x", pady=(0, 12))

        for hex_color, color_name in _DERIVED_EVENT_PRESET_COLORS:
            btn = ctk.CTkButton(
                color_frame, text="", width=32, height=32,
                fg_color=hex_color, hover_color=hex_color,
                corner_radius=6,
                border_width=3,
                border_color="#ffffff" if hex_color == self._selected_color else "transparent",
                command=lambda c=hex_color: self._select_color(c),
            )
            btn.pack(side="left", padx=(0, 6))
            btn._color_hex = hex_color
            self._color_buttons.append(btn)

        # Buttons
        btn_row = ctk.CTkFrame(scroll, fg_color="transparent")
        btn_row.pack(fill="x", pady=(8, 0))
        ctk.CTkButton(
            btn_row, text="Create Event",
            font=ctk.CTkFont(size=14, weight="bold"), height=40,
            fg_color=COLORS["accent_blue"], hover_color="#0077cc",
            command=self._create,
        ).pack(side="left", fill="x", expand=True, padx=(0, 6))
        ctk.CTkButton(
            btn_row, text="Cancel",
            font=ctk.CTkFont(size=14), height=40,
            fg_color="transparent", hover_color=COLORS["bg_input"],
            text_color=COLORS["text_dim"], border_width=1,
            border_color=COLORS["border"],
            command=self.destroy,
        ).pack(side="left", fill="x", expand=True)

    def _select_color(self, hex_color: str):
        self._selected_color = hex_color
        for btn in self._color_buttons:
            if btn._color_hex == hex_color:
                btn.configure(border_color="#ffffff")
            else:
                btn.configure(border_color="transparent")

    def _create(self):
        name = self._name_entry.get().strip()
        if not name:
            self._name_entry.configure(border_color=COLORS["loss_red"])
            return

        # Gather selected source types
        source_types = [etype for etype, var in self._source_vars.items() if var.get()]
        if not source_types:
            return  # Must select at least one source type

        # Validate min count
        try:
            min_count = int(self._min_count_entry.get().strip())
            if min_count <= 0:
                raise ValueError
        except ValueError:
            self._min_count_entry.configure(border_color=COLORS["loss_red"])
            return

        # Validate window seconds
        try:
            window_seconds = int(self._window_entry.get().strip())
            if window_seconds <= 0:
                raise ValueError
        except ValueError:
            self._window_entry.configure(border_color=COLORS["loss_red"])
            return

        self.db.derived_events.create(
            name=name,
            source_types=source_types,
            min_count=min_count,
            window_seconds=window_seconds,
            color=self._selected_color,
        )
        self.destroy()
        if self._on_created:
            self._on_created()


# ══════════════════════════════════════════════════════════
# Wrapper Pages: Review & VOD (dynamic inline pages)
# ══════════════════════════════════════════════════════════

class ReviewPage(ctk.CTkFrame):
    """Wrapper that hosts a ReviewPanel inline."""

    def __init__(self, parent, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self._panel = None

    def show_review(self, panel_class, kwargs: dict):
        """Destroy old panel and create a new one."""
        if self._panel:
            self._panel.destroy()
            self._panel = None
        self._panel = panel_class(self, **kwargs)
        self._panel.pack(fill="both", expand=True)

    def clear(self):
        """Clean up the current panel."""
        if self._panel:
            self._panel.destroy()
            self._panel = None


class VodPage(ctk.CTkFrame):
    """Wrapper that hosts a VodPlayerPanel inline."""

    def __init__(self, parent, **kw):
        super().__init__(parent, fg_color=COLORS["bg_dark"], **kw)
        self._panel = None

    def show_vod(self, kwargs: dict):
        """Destroy old panel, create new VodPlayerPanel, activate it."""
        self.clear()
        self._panel = VodPlayerPanel(self, **kwargs)
        self._panel.pack(fill="both", expand=True)
        # Delay activate so the widget is mapped and has a valid HWND
        self.after(50, self._panel.activate)

    def clear(self):
        """Deactivate and destroy the VOD panel (stops mpv, unbinds keys)."""
        if self._panel:
            self._panel.deactivate()
            self._panel.destroy()
            self._panel = None


# ══════════════════════════════════════════════════════════
# AppWindow — root window
# ══════════════════════════════════════════════════════════


class AppWindow(ctk.CTk):
    """Single-window app with sidebar navigation."""

    # Sidebar page names (have nav items)
    _SIDEBAR_PAGES = {"home", "session", "objectives", "rules", "history", "losses", "stats", "settings"}

    def __init__(self, db, on_minimize, on_open_vod, on_open_manual_entry,
                 on_settings_saved=None,
                 on_add_bookmark=None, on_update_bookmark=None,
                 on_delete_bookmark=None):
        super().__init__()

        self.db = db
        self._on_minimize = on_minimize
        self._on_open_vod = on_open_vod
        self._on_open_manual_entry = on_open_manual_entry
        self._on_settings_saved = on_settings_saved
        self._on_add_bookmark = on_add_bookmark
        self._on_update_bookmark = on_update_bookmark
        self._on_delete_bookmark = on_delete_bookmark
        self._current_page = "home"
        self._nav_stack: list[str] = []
        self._pages: dict[str, ctk.CTkFrame] = {}
        self._nav_items: dict[str, dict] = {}
        self._update_banner_slot = None
        self._update_label = None

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

        # Slot for update banners (above content)
        self._update_banner_slot = ctk.CTkFrame(right_panel, fg_color="transparent",
                                                 corner_radius=0, height=0)
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
            ("🎯", "Objectives", "objectives"),
            ("📏", "Rules", "rules"),
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
            if self._current_page != p and p not in ("review", "vod"):
                btn_frame.configure(fg_color=COLORS["sidebar_hover"])

        def on_leave(e, p=page_name):
            if self._current_page != p and p not in ("review", "vod"):
                btn_frame.configure(fg_color="transparent")

        for widget in (container, btn_frame, lbl):
            widget.bind("<Enter>", on_enter)
            widget.bind("<Leave>", on_leave)

    def _build_pages(self):
        self._pages["home"] = HomePage(
            self._content, db=self.db,
            on_open_vod=self._navigate_to_vod,
            on_open_review=self._navigate_to_review,
        )
        self._pages["session"] = SessionPage(
            self._content, db=self.db,
            on_open_vod=self._navigate_to_vod,
            on_open_review=self._navigate_to_review,
        )
        self._pages["objectives"] = ObjectivesPage(
            self._content, db=self.db,
        )
        self._pages["rules"] = RulesPage(
            self._content, db=self.db,
        )
        self._pages["history"] = HistoryPage(
            self._content, db=self.db,
            on_open_vod=self._navigate_to_vod,
            on_open_review=self._navigate_to_review,
        )
        self._pages["losses"] = LossesPage(
            self._content, db=self.db,
            on_open_vod=self._navigate_to_vod,
            on_open_review=self._navigate_to_review,
        )
        self._pages["stats"] = StatsPage(
            self._content, db=self.db,
        )
        self._pages["settings"] = SettingsPage(
            self._content,
            on_save=self._on_settings_saved,
            app_window=self,
            db=self.db,
        )

        # Dynamic pages (not in sidebar)
        self._pages["review"] = ReviewPage(self._content)
        self._pages["vod"] = VodPage(self._content)

    def _navigate(self, page_name: str, push_stack: bool = True):
        if page_name not in self._pages:
            return

        # Clean up dynamic pages when navigating away
        old_page = self._pages.get(self._current_page)
        if old_page and hasattr(old_page, "clear"):
            # Only clear when navigating to a sidebar page (not review→vod)
            if page_name in self._SIDEBAR_PAGES:
                old_page.clear()

        # Push current page to nav stack
        if push_stack and self._current_page != page_name:
            self._nav_stack.append(self._current_page)

        # Hide all pages
        for p in self._pages.values():
            p.pack_forget()

        # Update sidebar highlighting
        # For dynamic pages (review/vod), keep the originating sidebar item highlighted
        sidebar_highlight = page_name if page_name in self._SIDEBAR_PAGES else None
        if sidebar_highlight is None:
            # Find the last sidebar page in the stack
            for prev in reversed(self._nav_stack):
                if prev in self._SIDEBAR_PAGES:
                    sidebar_highlight = prev
                    break

        for name, items in self._nav_items.items():
            if name == sidebar_highlight:
                items["accent"].configure(fg_color=COLORS["sidebar_active"])
                items["btn_frame"].configure(fg_color=COLORS["sidebar_hover"])
                items["label"].configure(text_color=COLORS["text"])
            else:
                items["accent"].configure(fg_color="transparent")
                items["btn_frame"].configure(fg_color="transparent")
                items["label"].configure(text_color=COLORS["text_dim"])

        self._pages[page_name].pack(fill="both", expand=True)
        self._current_page = page_name

        # Clear stack when navigating to a sidebar page via sidebar click
        if push_stack and page_name in self._SIDEBAR_PAGES:
            self._nav_stack.clear()

    def _navigate_back(self):
        """Pop nav stack and navigate to the previous page."""
        if self._nav_stack:
            prev = self._nav_stack.pop()
            self._navigate(prev, push_stack=False)
        else:
            self._navigate("home", push_stack=False)

    def _save_session_game_review(self, review_data: dict, after_save=None):
        """Save review data from the unified ReviewPanel for an edited game.

        Mirrors the save logic in main._save_review so that objectives,
        concept tags, mental_handled, and matchup data are all persisted.
        """
        game_id = review_data["game_id"]
        win = review_data.pop("win", None)
        mental_handled = review_data.pop("mental_handled", "")
        concept_tag_ids = review_data.pop("concept_tag_ids", [])
        objectives_data = review_data.pop("objectives_data", [])
        matchup_helpful = review_data.pop("matchup_helpful", [])
        matchup_note = review_data.pop("matchup_note", None)
        enemy_laner = review_data.pop("enemy_laner", "")
        prompt_answers = review_data.pop("prompt_answers", [])

        self.db.update_review(**review_data)

        if mental_handled:
            self.db.update_mental_handled(game_id, mental_handled)

        if concept_tag_ids is not None:
            self.db.concept_tags.set_for_game(game_id, concept_tag_ids)

        for od in objectives_data:
            obj_id = od.get("objective_id")
            practiced = od.get("practiced", True)
            execution_note = od.get("execution_note", "")
            if obj_id:
                self.db.objectives.record_game(game_id, obj_id, practiced, execution_note)
                if practiced:
                    self.db.objectives.update_score(obj_id, win=bool(win))

        # Save matchup helpful ratings
        for mh in matchup_helpful:
            note_id = mh.get("note_id")
            helpful = mh.get("helpful")
            if note_id is not None and helpful is not None:
                self.db.matchup_notes.update_helpful(note_id, helpful)

        # Save new matchup note if provided
        if matchup_note:
            game = self.db.get_game(game_id)
            champion = game.get("champion_name", "") if game else ""
            self.db.matchup_notes.create(
                champion=champion,
                enemy=matchup_note["enemy"],
                note=matchup_note["note"],
                game_id=game_id,
            )

        # Update enemy_laner on the game record
        if enemy_laner:
            self.db.games.update_enemy_laner(game_id, enemy_laner)

        # Save prompt answers
        for pa in prompt_answers:
            self.db.prompts.save_answer(
                game_id=game_id,
                prompt_id=pa["prompt_id"],
                answer_value=pa["answer_value"],
                event_instance_id=pa.get("event_instance_id"),
                event_time_s=pa.get("event_time_s"),
            )

        logger.info(f"Session game review saved for game {game_id}")

        if after_save:
            after_save()

    def _navigate_to_review(self, review_type: str, **kwargs):
        """Navigate to an inline review page.

        review_type: "session_game" or "post_game"
        """
        try:
            if review_type == "session_game":
                game = kwargs.get("game", {})
                session_entry = kwargs.get("session_entry")
                on_save_cb = kwargs.get("on_save", lambda: None)

                # Build session_entry if not provided
                if session_entry is None:
                    game_id = game.get("game_id")
                    session_entry = {
                        "game_id": game_id,
                        "champion_name": game.get("champion_name"),
                        "win": game.get("win", 0),
                        "mental_rating": 5,
                    }

                game_id = session_entry.get("game_id")

                # Construct GameStats from the game dict for the full ReviewPanel
                stats = _game_dict_to_stats(game)

                # Gather VOD, bookmark, objective, and concept tag data
                vod_info = self.db.get_vod(game_id) if game_id else None
                has_vod = vod_info is not None
                bookmarks = self.db.get_bookmarks(game_id) if has_vod else []
                bookmark_count = len(bookmarks)

                pregame_intention = session_entry.get("pregame_intention", "") if session_entry else ""
                existing_mental_handled = session_entry.get("mental_handled", "") if session_entry else ""

                active_objectives = self.db.objectives.get_active()
                existing_game_objectives = self.db.objectives.get_game_objectives(game_id) if game_id else []
                concept_tags = self.db.concept_tags.get_all()
                existing_concept_tag_ids = self.db.concept_tags.get_ids_for_game(game_id) if game_id else []

                existing_review = self.db.get_game(game_id)

                # Look up enemy laner and matchup notes
                enemy_laner = game.get("enemy_laner", "") or ""
                matchup_notes_shown = []
                if not enemy_laner and game.get("raw_stats"):
                    import json as _json
                    raw = game["raw_stats"]
                    if isinstance(raw, str):
                        try:
                            raw = _json.loads(raw)
                        except (ValueError, TypeError):
                            raw = {}
                    enemy_champions = raw.get("_enemy_champions", []) if isinstance(raw, dict) else []
                    if enemy_champions:
                        enemy_laner = enemy_champions[0]
                champion_name = game.get("champion_name", "")
                if enemy_laner and champion_name:
                    matchup_notes_shown = self.db.matchup_notes.get_for_matchup(
                        champion_name, enemy_laner
                    )

                # Fetch objective prompts, game events, and derived events
                objective_prompts = {}
                for obj in active_objectives:
                    prompts = self.db.prompts.get_prompts_for_objective(obj["id"])
                    if prompts:
                        objective_prompts[obj["id"]] = prompts
                game_events = self.db.get_game_events(game_id) if game_id else []
                derived_event_instances = self.db.derived_events.get_instances(game_id) if game_id else []
                existing_prompt_answers = self.db.prompts.get_answers_for_game(game_id) if game_id else []

                review_page = self._pages["review"]
                review_page.show_review(
                    ReviewPanel,
                    {
                        "stats": stats,
                        "existing_review": existing_review,
                        "on_save": lambda rd: self._save_session_game_review(rd, after_save=on_save_cb),
                        "on_open_vod": self._navigate_to_vod,
                        "on_back": self._navigate_back,
                        "has_vod": has_vod,
                        "bookmark_count": bookmark_count,
                        "bookmarks": bookmarks,
                        "pregame_intention": pregame_intention,
                        "existing_mental_handled": existing_mental_handled,
                        "concept_tags": concept_tags,
                        "existing_concept_tag_ids": existing_concept_tag_ids,
                        "active_objectives": active_objectives,
                        "existing_game_objectives": existing_game_objectives,
                        "matchup_notes_shown": matchup_notes_shown,
                        "enemy_laner": enemy_laner,
                        "game_events": game_events,
                        "derived_event_instances": derived_event_instances,
                        "objective_prompts": objective_prompts,
                        "existing_prompt_answers": existing_prompt_answers,
                    },
                )
                self._navigate("review")

            elif review_type == "post_game":
                # Full post-game review with stats
                review_page = self._pages["review"]
                review_kwargs = dict(kwargs)
                review_kwargs["on_back"] = self._navigate_back
                review_kwargs["on_open_vod"] = self._navigate_to_vod
                review_page.show_review(ReviewPanel, review_kwargs)
                self._navigate("review")
        except Exception as e:
            logger.error(f"Failed to open review ({review_type}): {e}", exc_info=True)

    def _navigate_to_vod(self, game_id: int):
        """Navigate to an inline VOD player for a specific game."""
        vod_info = self.db.get_vod(game_id)
        if not vod_info:
            logger.warning(f"No VOD found for game {game_id}")
            return

        game = self.db.get_game(game_id)
        if not game:
            logger.warning(f"No game record found for game {game_id}")
            return

        bookmarks = self.db.get_bookmarks(game_id)
        tags = []
        game_events = self.db.get_game_events(game_id)
        derived_events = self.db.derived_events.get_instances(game_id)

        try:
            vod_page = self._pages["vod"]
            vod_page.show_vod({
                "game_id": game_id,
                "vod_path": vod_info["file_path"],
                "game_duration": game.get("game_duration", 0),
                "champion_name": game.get("champion_name", "Unknown"),
                "bookmarks": bookmarks,
                "tags": tags,
                "game_events": game_events,
                "derived_events": derived_events,
                "on_add_bookmark": self._on_add_bookmark,
                "on_update_bookmark": self._on_update_bookmark,
                "on_delete_bookmark": self._on_delete_bookmark,
                "on_back": self._navigate_back,
            })
            self._navigate("vod")
        except Exception as e:
            logger.error(f"Failed to open VOD player for game {game_id}: {e}", exc_info=True)

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

    def show_just_updated_banner(self):
        """Show a green 'Updated successfully' banner that auto-dismisses after 8s."""
        try:
            banner = ctk.CTkFrame(
                self._update_banner_slot,
                fg_color="#14332a", corner_radius=0,
            )
            banner.pack(fill="x")
            ctk.CTkLabel(
                banner,
                text=f"Updated to v{__version__} successfully",
                font=ctk.CTkFont(size=12, weight="bold"),
                text_color="#4ade80",
            ).pack(padx=16, pady=8)
            self.after(8000, banner.destroy)
        except Exception:
            pass

    def refresh(self):
        page = self._pages.get(self._current_page)
        if page and hasattr(page, "refresh"):
            page.refresh()

    def navigate_to(self, page: str):
        self._navigate(page)

    def navigate_to_review(self, review_type: str, **kwargs):
        """Public API for navigating to an inline review."""
        self._navigate_to_review(review_type, **kwargs)

    def navigate_to_vod(self, game_id: int):
        """Public API for navigating to an inline VOD player."""
        self._navigate_to_vod(game_id)
