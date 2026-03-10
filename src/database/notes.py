"""Persistent notes for Claude context."""

import time

from .connection import ConnectionManager


class NotesRepository:
    """CRUD operations for the persistent_notes table."""

    def __init__(self, conn_mgr: ConnectionManager):
        self._conn_mgr = conn_mgr

    def get(self) -> str:
        """Get the persistent notes content."""
        conn = self._conn_mgr.get_conn()
        row = conn.execute(
            "SELECT content FROM persistent_notes ORDER BY id LIMIT 1"
        ).fetchone()
        return row["content"] if row else ""

    def save(self, content: str):
        """Save updated persistent notes."""
        conn = self._conn_mgr.get_conn()
        conn.execute(
            """UPDATE persistent_notes SET content = ?, updated_at = ?
            WHERE id = (SELECT MIN(id) FROM persistent_notes)""",
            (content, int(time.time())),
        )
        conn.commit()
