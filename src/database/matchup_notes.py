"""Matchup notes — user notes about specific champion matchups."""

import logging
import time

from .connection import ConnectionManager

logger = logging.getLogger(__name__)


class MatchupNotesRepository:
    """CRUD for matchup_notes table."""

    def __init__(self, conn_mgr: ConnectionManager):
        self._conn_mgr = conn_mgr

    def create(self, champion: str, enemy: str, note: str = "",
               helpful: int = None, game_id: int = None) -> int:
        conn = self._conn_mgr.get_conn()
        cur = conn.execute(
            """INSERT INTO matchup_notes
               (champion, enemy, note, helpful, game_id, created_at)
               VALUES (?, ?, ?, ?, ?, ?)""",
            (champion, enemy, note, helpful, game_id, int(time.time())),
        )
        conn.commit()
        return cur.lastrowid

    def get_for_matchup(self, champion: str, enemy: str) -> list[dict]:
        """Get notes for an exact champion vs enemy matchup."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            """SELECT * FROM matchup_notes
               WHERE champion = ? AND enemy = ?
               ORDER BY created_at DESC""",
            (champion, enemy),
        ).fetchall()
        return [dict(r) for r in rows]

    def get_all(self) -> list[dict]:
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            "SELECT * FROM matchup_notes ORDER BY created_at DESC"
        ).fetchall()
        return [dict(r) for r in rows]

    def update_helpful(self, note_id: int, helpful: int):
        conn = self._conn_mgr.get_conn()
        conn.execute(
            "UPDATE matchup_notes SET helpful = ? WHERE id = ?",
            (helpful, note_id),
        )
        conn.commit()

    def get_helpfulness_stats(self) -> dict:
        """Get aggregate stats on matchup note helpfulness."""
        conn = self._conn_mgr.get_conn()
        row = conn.execute(
            """SELECT
                COUNT(*) as total,
                SUM(CASE WHEN helpful = 1 THEN 1 ELSE 0 END) as helpful_count,
                SUM(CASE WHEN helpful = 0 THEN 1 ELSE 0 END) as unhelpful_count,
                SUM(CASE WHEN helpful IS NULL THEN 1 ELSE 0 END) as unrated_count
            FROM matchup_notes"""
        ).fetchone()
        return dict(row) if row else {"total": 0, "helpful_count": 0, "unhelpful_count": 0, "unrated_count": 0}
