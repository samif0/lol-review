"""Concept tag management for categorizing game moments."""

from .connection import ConnectionManager


class ConceptTagRepository:
    """CRUD for concept_tags and game_concept_tags tables."""

    def __init__(self, conn_mgr: ConnectionManager):
        self._conn_mgr = conn_mgr

    def get_all(self) -> list[dict]:
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            "SELECT * FROM concept_tags ORDER BY polarity DESC, name ASC"
        ).fetchall()
        return [dict(r) for r in rows]

    def create(self, name: str, polarity: str = "neutral", color: str = "") -> int:
        """Create a concept tag. Auto-assigns a color if not specified."""
        if not color:
            color = (
                "#22c55e" if polarity == "positive"
                else "#ef4444" if polarity == "negative"
                else "#3b82f6"
            )
        conn = self._conn_mgr.get_conn()
        cur = conn.execute(
            "INSERT OR IGNORE INTO concept_tags (name, polarity, color) VALUES (?, ?, ?)",
            (name, polarity, color),
        )
        conn.commit()
        return cur.lastrowid

    def get_ids_for_game(self, game_id: int) -> list[int]:
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            "SELECT tag_id FROM game_concept_tags WHERE game_id = ?", (game_id,)
        ).fetchall()
        return [r[0] for r in rows]

    def set_for_game(self, game_id: int, tag_ids: list[int]):
        """Replace the concept tags for a game."""
        conn = self._conn_mgr.get_conn()
        conn.execute("DELETE FROM game_concept_tags WHERE game_id = ?", (game_id,))
        for tid in tag_ids:
            conn.execute(
                "INSERT OR IGNORE INTO game_concept_tags (game_id, tag_id) VALUES (?, ?)",
                (game_id, tid),
            )
        conn.commit()
