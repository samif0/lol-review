"""Session log tracking — daily games, mental ratings, and rule breaks."""

import logging
from datetime import datetime, timedelta
from typing import Optional

from .connection import ConnectionManager

logger = logging.getLogger(__name__)


class SessionLogRepository:
    """CRUD operations for the session_log table."""

    def __init__(self, conn_mgr: ConnectionManager):
        self._conn_mgr = conn_mgr

    def log_game(
        self,
        game_id: int,
        champion_name: str,
        win: bool,
        mental_rating: int = 5,
        improvement_note: str = "",
    ):
        """Log a game in the session log with mental rating and notes."""
        conn = self._conn_mgr.get_conn()
        today = datetime.now().strftime("%Y-%m-%d")
        now = int(datetime.now().timestamp())

        existing = conn.execute(
            "SELECT id FROM session_log WHERE game_id = ?", (game_id,)
        ).fetchone()
        if existing:
            conn.execute(
                """UPDATE session_log SET mental_rating = ?, improvement_note = ?
                WHERE game_id = ?""",
                (mental_rating, improvement_note, game_id),
            )
            conn.commit()
            return

        rule_broken = self._check_rule_break(today)

        conn.execute(
            """INSERT INTO session_log
            (date, game_id, champion_name, win, mental_rating, improvement_note, rule_broken, timestamp)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)""",
            (today, game_id, champion_name, int(win), mental_rating,
             improvement_note, int(rule_broken), now),
        )
        conn.commit()
        logger.info(f"Session log: {champion_name} {'W' if win else 'L'} mental={mental_rating}")

    def _check_rule_break(self, today: str) -> bool:
        """Check if playing this game breaks the 2-loss stop rule."""
        conn = self._conn_mgr.get_conn()
        recent = conn.execute(
            """SELECT win FROM session_log
            WHERE date = ?
            ORDER BY id DESC LIMIT 2""",
            (today,),
        ).fetchall()

        if len(recent) < 2:
            return False

        return recent[0]["win"] == 0 and recent[1]["win"] == 0

    def has_entry(self, game_id: int) -> bool:
        """Check if a game already has a session_log entry."""
        conn = self._conn_mgr.get_conn()
        row = conn.execute(
            "SELECT 1 FROM session_log WHERE game_id = ?", (game_id,)
        ).fetchone()
        return row is not None

    def cleanup_mismatched_entries(self) -> int:
        """Remove session_log entries whose date doesn't match the game's actual date."""
        conn = self._conn_mgr.get_conn()
        deleted = conn.execute(
            """DELETE FROM session_log
            WHERE game_id IN (
                SELECT sl.game_id FROM session_log sl
                JOIN games g ON sl.game_id = g.game_id
                WHERE sl.date != SUBSTR(g.date_played, 1, 10)
            )"""
        ).rowcount
        conn.commit()
        if deleted:
            logger.info(f"Cleaned up {deleted} mismatched session_log entries")
        return deleted

    def get_today(self) -> list[dict]:
        """Get today's session log entries ordered chronologically."""
        return self.get_for_date(datetime.now().strftime("%Y-%m-%d"))

    def get_for_date(self, date_str: str) -> list[dict]:
        """Get session log entries for a specific date (YYYY-MM-DD)."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            """SELECT * FROM session_log
            WHERE date = ?
            ORDER BY timestamp ASC""",
            (date_str,),
        ).fetchall()
        return [dict(r) for r in rows]

    def get_stats_today(self) -> dict:
        """Get aggregate session stats for today."""
        return self.get_stats_for_date(datetime.now().strftime("%Y-%m-%d"))

    def get_stats_for_date(self, date_str: str) -> dict:
        """Get aggregate session stats for a specific date."""
        conn = self._conn_mgr.get_conn()
        row = conn.execute(
            """SELECT
                COUNT(*) as games,
                SUM(win) as wins,
                COUNT(*) - SUM(win) as losses,
                ROUND(AVG(mental_rating), 1) as avg_mental,
                SUM(rule_broken) as rule_breaks
            FROM session_log
            WHERE date = ?""",
            (date_str,),
        ).fetchone()

        if row and row["games"] > 0:
            return dict(row)
        return {"games": 0, "wins": 0, "losses": 0, "avg_mental": 0, "rule_breaks": 0}

    def get_dates_with_games(self) -> list[str]:
        """Get all dates that have session log entries, newest first."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            "SELECT DISTINCT date FROM session_log ORDER BY date DESC"
        ).fetchall()
        return [r["date"] for r in rows]

    def get_range(self, days: int = 7) -> list[dict]:
        """Get session log entries for the last N days."""
        conn = self._conn_mgr.get_conn()
        cutoff = datetime.now().replace(hour=0, minute=0, second=0, microsecond=0)
        cutoff = cutoff - timedelta(days=days - 1)
        cutoff_str = cutoff.strftime("%Y-%m-%d")

        rows = conn.execute(
            """SELECT * FROM session_log
            WHERE date >= ?
            ORDER BY date DESC, timestamp ASC""",
            (cutoff_str,),
        ).fetchall()
        return [dict(r) for r in rows]

    def get_daily_summaries(self, days: int = 7) -> list[dict]:
        """Get per-day summary stats for the last N days."""
        conn = self._conn_mgr.get_conn()
        cutoff = datetime.now().replace(hour=0, minute=0, second=0, microsecond=0)
        cutoff = cutoff - timedelta(days=days - 1)
        cutoff_str = cutoff.strftime("%Y-%m-%d")

        rows = conn.execute(
            """SELECT
                date,
                COUNT(*) as games,
                SUM(win) as wins,
                COUNT(*) - SUM(win) as losses,
                ROUND(AVG(mental_rating), 1) as avg_mental,
                SUM(rule_broken) as rule_breaks,
                GROUP_CONCAT(DISTINCT champion_name) as champions_played
            FROM session_log
            WHERE date >= ?
            GROUP BY date
            ORDER BY date DESC""",
            (cutoff_str,),
        ).fetchall()
        return [dict(r) for r in rows]

    def get_adherence_streak(self) -> int:
        """Count consecutive clean play-days (no rule breaks)."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            """SELECT date, SUM(rule_broken) as breaks
            FROM session_log
            GROUP BY date
            ORDER BY date DESC"""
        ).fetchall()

        streak = 0
        for row in rows:
            if row["breaks"] == 0:
                streak += 1
            else:
                break

        return streak

    def get_mental_winrate_correlation(self) -> list[dict]:
        """Analyze winrate by mental rating bracket."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            """SELECT
                CASE
                    WHEN mental_rating <= 3 THEN '1-3 (Low)'
                    WHEN mental_rating <= 6 THEN '4-6 (Mid)'
                    ELSE '7-10 (High)'
                END as bracket,
                COUNT(*) as games,
                SUM(win) as wins,
                ROUND(AVG(win) * 100, 1) as winrate
            FROM session_log
            GROUP BY bracket
            ORDER BY bracket"""
        ).fetchall()
        return [dict(r) for r in rows]
