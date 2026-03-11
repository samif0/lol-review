"""Game history and statistics window."""

import json

import customtkinter as ctk

from ..constants import COLORS, format_duration, format_number
from .widgets import StatCard
from .game_review import SessionGameReviewWindow


class HistoryWindow(ctk.CTkToplevel):
    """Window showing game history and stats overview."""

    def __init__(
        self, games: list[dict], overall: dict, champion_stats: list[dict],
        db=None, on_open_vod=None,
        *args, **kwargs,
    ):
        super().__init__(*args, **kwargs)

        self.db = db
        self._on_open_vod = on_open_vod
        self._review_popup = None

        self.title("LoL Review — Game History")
        self.geometry("900x700")
        self.configure(fg_color=COLORS["bg_dark"])
        self.minsize(800, 600)

        # Bring to front
        self.lift()
        self.attributes("-topmost", True)
        self.after(100, lambda: self.attributes("-topmost", False))

        notebook = ctk.CTkTabview(self, fg_color=COLORS["bg_dark"])
        notebook.pack(fill="both", expand=True, padx=12, pady=12)

        # Tab 1: Recent Games
        tab_games = notebook.add("Recent Games")
        self._build_games_list(tab_games, games)

        # Tab 2: Stats Overview
        tab_stats = notebook.add("Stats Overview")
        self._build_stats_overview(tab_stats, overall)

        # Tab 3: Champion Stats
        tab_champs = notebook.add("By Champion")
        self._build_champion_stats(tab_champs, champion_stats)

    def _build_games_list(self, parent, games: list[dict]):
        """Scrollable list of recent games."""
        scroll = ctk.CTkScrollableFrame(parent, fg_color="transparent")
        scroll.pack(fill="both", expand=True)

        if not games:
            ctk.CTkLabel(
                scroll,
                text="No games recorded yet.\nPlay a game with the app running to start tracking!",
                font=ctk.CTkFont(size=14),
                text_color=COLORS["text_dim"],
            ).pack(pady=40)
            return

        for game in games:
            self._build_game_row(scroll, game)

    def _build_game_row(self, parent, game: dict):
        """A single game entry in the history list."""
        is_win = bool(game.get("win"))
        border_color = COLORS["win_green"] if is_win else COLORS["loss_red"]

        row = ctk.CTkFrame(
            parent,
            fg_color=COLORS["bg_card"],
            corner_radius=8,
            border_width=2,
            border_color=border_color,
        )
        row.pack(fill="x", pady=4, padx=4)

        inner = ctk.CTkFrame(row, fg_color="transparent")
        inner.pack(fill="x", padx=12, pady=10)

        # Left: champion + result
        left = ctk.CTkFrame(inner, fg_color="transparent")
        left.pack(side="left")

        result = "W" if is_win else "L"
        result_color = COLORS["win_green"] if is_win else COLORS["loss_red"]

        ctk.CTkLabel(
            left,
            text=f"{result}  {game.get('champion_name', '?')}",
            font=ctk.CTkFont(size=15, weight="bold"),
            text_color=result_color,
        ).pack(anchor="w")

        ctk.CTkLabel(
            left,
            text=f"{game.get('date_played', '')}  •  {format_duration(game.get('game_duration', 0))}  •  {game.get('game_mode', '')}",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        ).pack(anchor="w")

        # Show tags if present
        tags = json.loads(game.get("tags", "[]")) if isinstance(game.get("tags"), str) else game.get("tags", [])
        if tags:
            tag_text = " ".join(f"[{t}]" for t in tags)
            ctk.CTkLabel(
                left,
                text=tag_text,
                font=ctk.CTkFont(size=10),
                text_color=COLORS["accent_blue"],
            ).pack(anchor="w")

        # Right: KDA + stats
        right = ctk.CTkFrame(inner, fg_color="transparent")
        right.pack(side="right")

        k, d, a = game.get("kills", 0), game.get("deaths", 0), game.get("assists", 0)
        kda = game.get("kda_ratio", 0)

        ctk.CTkLabel(
            right,
            text=f"{k}/{d}/{a}  ({kda:.1f} KDA)",
            font=ctk.CTkFont(size=14, weight="bold"),
            text_color=COLORS["text"],
        ).pack(anchor="e")

        ctk.CTkLabel(
            right,
            text=f"CS {game.get('cs_total', 0)} ({game.get('cs_per_min', 0)}/m)  •  Vision {game.get('vision_score', 0)}  •  {format_number(game.get('total_damage_to_champions', 0))} dmg",
            font=ctk.CTkFont(size=11),
            text_color=COLORS["text_dim"],
        ).pack(anchor="e")

        # Show rating stars if reviewed
        rating = game.get("rating", 0)
        if rating > 0:
            stars = "★" * rating + "☆" * (5 - rating)
            ctk.CTkLabel(
                right,
                text=stars,
                font=ctk.CTkFont(size=12),
                text_color=COLORS["star_active"],
            ).pack(anchor="e")

        # Action buttons row
        if self.db:
            btn_row = ctk.CTkFrame(right, fg_color="transparent")
            btn_row.pack(anchor="e", pady=(4, 0))

            has_review = bool(
                game.get("mistakes", "").strip()
                or game.get("went_well", "").strip()
                or game.get("focus_next", "").strip()
                or (game.get("rating") or 0) > 0
            )
            review_text = "Edit Review" if has_review else "Review"
            review_color = COLORS["tag_bg"] if has_review else COLORS["accent_blue"]

            ctk.CTkButton(
                btn_row,
                text=review_text,
                font=ctk.CTkFont(size=11),
                height=26, width=100, corner_radius=6,
                fg_color=review_color,
                hover_color="#0077cc",
                command=lambda g=game: self._open_review(g),
            ).pack(side="left", padx=(0, 6))

            game_id = game.get("game_id")
            if game_id and self.db.get_vod(game_id):
                ctk.CTkButton(
                    btn_row,
                    text="Watch VOD",
                    font=ctk.CTkFont(size=11, weight="bold"),
                    height=26, width=100, corner_radius=6,
                    fg_color=COLORS["accent_gold"], hover_color="#a88432",
                    text_color="#0a0a0f",
                    command=lambda gid=game_id: self._on_open_vod(gid) if self._on_open_vod else None,
                ).pack(side="left")

    def _build_stats_overview(self, parent, overall: dict):
        """Aggregate stats summary."""
        scroll = ctk.CTkScrollableFrame(parent, fg_color="transparent")
        scroll.pack(fill="both", expand=True)

        if not overall or overall.get("total_games", 0) == 0:
            ctk.CTkLabel(
                scroll,
                text="No stats yet — play some games!",
                font=ctk.CTkFont(size=14),
                text_color=COLORS["text_dim"],
            ).pack(pady=40)
            return

        total = overall.get("total_games", 0)
        wins = overall.get("total_wins", 0)
        losses = total - wins

        # Big winrate display
        wr = overall.get("winrate", 0)
        wr_color = COLORS["win_green"] if wr >= 50 else COLORS["loss_red"]

        ctk.CTkLabel(
            scroll,
            text=f"{wr:.1f}%",
            font=ctk.CTkFont(size=48, weight="bold"),
            text_color=wr_color,
        ).pack(pady=(20, 0))

        ctk.CTkLabel(
            scroll,
            text=f"Win Rate  •  {wins}W {losses}L ({total} games)",
            font=ctk.CTkFont(size=14),
            text_color=COLORS["text_dim"],
        ).pack(pady=(0, 20))

        # Stat grid
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

        cols = 5
        for i, (label, value) in enumerate(stats_data):
            row, col = divmod(i, cols)
            card = StatCard(grid, label, value)
            card.grid(row=row, column=col, padx=4, pady=4, sticky="nsew")

        for col in range(cols):
            grid.columnconfigure(col, weight=1)

    def _build_champion_stats(self, parent, champ_stats: list[dict]):
        """Per-champion stats table."""
        scroll = ctk.CTkScrollableFrame(parent, fg_color="transparent")
        scroll.pack(fill="both", expand=True)

        if not champ_stats:
            ctk.CTkLabel(
                scroll,
                text="No champion data yet.",
                font=ctk.CTkFont(size=14),
                text_color=COLORS["text_dim"],
            ).pack(pady=40)
            return

        # Header
        header = ctk.CTkFrame(scroll, fg_color=COLORS["bg_input"], corner_radius=0)
        header.pack(fill="x", pady=(0, 4))

        headers = ["Champion", "Games", "WR%", "Avg KDA", "Avg CS/m", "Avg Dmg"]
        widths = [140, 60, 60, 80, 80, 90]
        for text, w in zip(headers, widths):
            ctk.CTkLabel(
                header,
                text=text,
                font=ctk.CTkFont(size=11, weight="bold"),
                text_color=COLORS["text_dim"],
                width=w,
            ).pack(side="left", padx=6, pady=6)

        # Rows
        for champ in champ_stats:
            row = ctk.CTkFrame(scroll, fg_color="transparent")
            row.pack(fill="x")

            wr = champ.get("winrate", 0)
            wr_color = COLORS["win_green"] if wr >= 50 else COLORS["loss_red"]

            values = [
                (champ.get("champion_name", "?"), COLORS["accent_gold"], 140),
                (str(champ.get("games_played", 0)), COLORS["text"], 60),
                (f"{wr:.1f}%", wr_color, 60),
                (f"{champ.get('avg_kda', 0):.2f}", COLORS["text"], 80),
                (f"{champ.get('avg_cs_min', 0):.1f}", COLORS["text"], 80),
                (format_number(int(champ.get("avg_damage", 0))), COLORS["text"], 90),
            ]

            for text, color, w in values:
                ctk.CTkLabel(
                    row,
                    text=text,
                    font=ctk.CTkFont(size=12),
                    text_color=color,
                    width=w,
                ).pack(side="left", padx=6, pady=4)

    def _open_review(self, game: dict):
        """Open a review popup for a game from the history list."""
        if self._review_popup and self._review_popup.winfo_exists():
            self._review_popup.destroy()

        game_id = game.get("game_id")
        session_entry = {
            "game_id": game_id,
            "champion_name": game.get("champion_name"),
            "win": game.get("win", 0),
            "mental_rating": 5,
        }

        vod_info = self.db.get_vod(game_id) if game_id else None
        has_vod = vod_info is not None
        bookmark_count = self.db.get_bookmark_count(game_id) if has_vod else 0

        self._review_popup = SessionGameReviewWindow(
            db=self.db,
            session_entry=session_entry,
            game_data=game,
            on_save=None,
            on_open_vod=self._on_open_vod,
            has_vod=has_vod,
            bookmark_count=bookmark_count,
        )
