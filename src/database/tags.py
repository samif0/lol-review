"""Tag management."""

from .connection import ConnectionManager


class TagRepository:
    """CRUD operations for the tags table."""

    def __init__(self, conn_mgr: ConnectionManager):
        self._conn_mgr = conn_mgr

    def get_all(self) -> list[dict]:
        """Get all available tags."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute("SELECT * FROM tags ORDER BY name").fetchall()
        return [dict(r) for r in rows]

    def add(self, name: str, color: str = "#3b82f6") -> int:
        """Add a new tag. Returns the tag ID."""
        conn = self._conn_mgr.get_conn()
        cursor = conn.execute(
            "INSERT OR IGNORE INTO tags (name, color) VALUES (?, ?)",
            (name, color),
        )
        conn.commit()
        return cursor.lastrowid
