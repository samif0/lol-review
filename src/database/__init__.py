"""SQLite database for storing game stats and review notes.

All game data and player notes are stored locally in a single .db file.
The schema auto-creates on first run and migrates gracefully if columns
are added in future versions.
"""

from pathlib import Path
from typing import Optional

from .connection import ConnectionManager, DEFAULT_DB_PATH
from .context import generate_claude_context
from .games import GameRepository
from .notes import NotesRepository
from .session_log import SessionLogRepository
from .tags import TagRepository

__all__ = ["Database", "DEFAULT_DB_PATH"]


class Database:
    """Interface to the local SQLite database.

    Composes domain-specific repositories and delegates to them.
    All existing call-sites (db.save_game, db.get_game, etc.) continue
    to work via thin wrapper methods.
    """

    def __init__(self, db_path: Optional[Path] = None):
        self._conn_mgr = ConnectionManager(db_path)
        self.db_path = self._conn_mgr.db_path  # Expose for callers like main.py
        self.games = GameRepository(self._conn_mgr)
        self.session_log = SessionLogRepository(self._conn_mgr)
        self.tags = TagRepository(self._conn_mgr)
        self.notes = NotesRepository(self._conn_mgr)

        # One-time cleanup
        self.session_log.cleanup_mismatched_entries()

    # ── Game delegates ───────────────────────────────────────────────

    def save_game(self, stats) -> int:
        return self.games.save(stats)

    def update_review(self, game_id, **kwargs):
        return self.games.update_review(game_id, **kwargs)

    def get_game(self, game_id):
        return self.games.get(game_id)

    def get_recent_games(self, limit=50):
        return self.games.get_recent(limit)

    def get_champion_stats(self):
        return self.games.get_champion_stats()

    def get_overall_stats(self):
        return self.games.get_overall_stats()

    def get_last_review_focus(self):
        return self.games.get_last_review_focus()

    def get_win_streak(self):
        return self.games.get_win_streak()

    def get_losses(self, champion=None):
        return self.games.get_losses(champion)

    def get_todays_games(self):
        return self.games.get_todays_games()

    def get_unique_champions(self, losses_only=False):
        return self.games.get_unique_champions(losses_only)

    def save_manual_game(self, **kwargs):
        return self.games.save_manual(**kwargs)

    def get_unreviewed_games(self, days=3):
        return self.games.get_unreviewed_games(days)

    def get_games_for_date(self, date_str):
        return self.games.get_games_for_date(date_str)

    # ── Session log delegates ────────────────────────────────────────

    def log_session_game(self, game_id, champion_name, win, **kwargs):
        return self.session_log.log_game(game_id, champion_name, win, **kwargs)

    def has_session_log_entry(self, game_id):
        return self.session_log.has_entry(game_id)

    def cleanup_mismatched_session_entries(self):
        return self.session_log.cleanup_mismatched_entries()

    def get_session_log_today(self):
        return self.session_log.get_today()

    def get_session_log_for_date(self, date_str):
        return self.session_log.get_for_date(date_str)

    def get_session_stats_today(self):
        return self.session_log.get_stats_today()

    def get_session_stats_for_date(self, date_str):
        return self.session_log.get_stats_for_date(date_str)

    def get_session_dates_with_games(self):
        return self.session_log.get_dates_with_games()

    def get_session_log_range(self, days=7):
        return self.session_log.get_range(days)

    def get_daily_summaries(self, days=7):
        return self.session_log.get_daily_summaries(days)

    def get_adherence_streak(self):
        return self.session_log.get_adherence_streak()

    def get_mental_winrate_correlation(self):
        return self.session_log.get_mental_winrate_correlation()

    # ── Tag delegates ────────────────────────────────────────────────

    def get_all_tags(self):
        return self.tags.get_all()

    def add_tag(self, name, color="#3b82f6"):
        return self.tags.add(name, color)

    # ── Notes delegates ──────────────────────────────────────────────

    def get_persistent_notes(self):
        return self.notes.get()

    def save_persistent_notes(self, content):
        return self.notes.save(content)

    # ── Context generation ───────────────────────────────────────────

    def generate_claude_context(self):
        return generate_claude_context(
            self._conn_mgr, self.games, self.session_log, self.notes
        )

    # ── Lifecycle ────────────────────────────────────────────────────

    def close(self):
        self._conn_mgr.close()
