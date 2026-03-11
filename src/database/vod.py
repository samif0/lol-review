"""VOD file tracking and bookmark storage."""

import json
import logging
import time
from typing import Optional

from .connection import ConnectionManager

logger = logging.getLogger(__name__)


class VodRepository:
    """CRUD for vod_files and vod_bookmarks tables."""

    def __init__(self, conn_mgr: ConnectionManager):
        self._conn_mgr = conn_mgr

    # ── VOD file linking ─────────────────────────────────────────

    def link_vod(self, game_id: int, file_path: str,
                 file_size: int = 0, duration_s: int = 0):
        """Associate a VOD file with a game."""
        conn = self._conn_mgr.get_conn()
        conn.execute(
            """INSERT OR REPLACE INTO vod_files
               (game_id, file_path, file_size, duration_s, matched_at)
               VALUES (?, ?, ?, ?, ?)""",
            (game_id, file_path, file_size, duration_s, int(time.time())),
        )
        conn.commit()
        logger.info(f"Linked VOD for game {game_id}: {file_path}")

    def get_vod(self, game_id: int) -> Optional[dict]:
        """Get the VOD file info for a game, or None."""
        conn = self._conn_mgr.get_conn()
        row = conn.execute(
            "SELECT * FROM vod_files WHERE game_id = ?", (game_id,)
        ).fetchone()
        return dict(row) if row else None

    def unlink_vod(self, game_id: int):
        """Remove a VOD association (doesn't delete the file)."""
        conn = self._conn_mgr.get_conn()
        conn.execute("DELETE FROM vod_files WHERE game_id = ?", (game_id,))
        conn.commit()

    def get_all_vods(self) -> list[dict]:
        """Get all linked VODs."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            "SELECT * FROM vod_files ORDER BY matched_at DESC"
        ).fetchall()
        return [dict(r) for r in rows]

    # ── Bookmarks ────────────────────────────────────────────────

    def add_bookmark(self, game_id: int, game_time_s: int,
                     note: str = "", tags: list[str] = None) -> int:
        """Add a timestamp bookmark for a game. Returns the bookmark ID."""
        conn = self._conn_mgr.get_conn()
        cursor = conn.execute(
            """INSERT INTO vod_bookmarks (game_id, game_time_s, note, tags, created_at)
               VALUES (?, ?, ?, ?, ?)""",
            (game_id, game_time_s, note, json.dumps(tags or []), int(time.time())),
        )
        conn.commit()
        return cursor.lastrowid

    def update_bookmark(self, bookmark_id: int, note: str = None,
                        tags: list[str] = None, game_time_s: int = None):
        """Update an existing bookmark's note, tags, or timestamp."""
        conn = self._conn_mgr.get_conn()
        updates = []
        params = []

        if note is not None:
            updates.append("note = ?")
            params.append(note)
        if tags is not None:
            updates.append("tags = ?")
            params.append(json.dumps(tags))
        if game_time_s is not None:
            updates.append("game_time_s = ?")
            params.append(game_time_s)

        if not updates:
            return

        params.append(bookmark_id)
        conn.execute(
            f"UPDATE vod_bookmarks SET {', '.join(updates)} WHERE id = ?",
            params,
        )
        conn.commit()

    def delete_bookmark(self, bookmark_id: int):
        """Delete a single bookmark."""
        conn = self._conn_mgr.get_conn()
        conn.execute("DELETE FROM vod_bookmarks WHERE id = ?", (bookmark_id,))
        conn.commit()

    def get_bookmarks(self, game_id: int) -> list[dict]:
        """Get all bookmarks for a game, sorted by timestamp."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            """SELECT * FROM vod_bookmarks
               WHERE game_id = ?
               ORDER BY game_time_s ASC""",
            (game_id,),
        ).fetchall()
        return [dict(r) for r in rows]

    def get_bookmark_count(self, game_id: int) -> int:
        """Count bookmarks for a game."""
        conn = self._conn_mgr.get_conn()
        row = conn.execute(
            "SELECT COUNT(*) FROM vod_bookmarks WHERE game_id = ?", (game_id,)
        ).fetchone()
        return row[0] if row else 0

    def delete_all_bookmarks(self, game_id: int):
        """Delete all bookmarks for a game."""
        conn = self._conn_mgr.get_conn()
        conn.execute("DELETE FROM vod_bookmarks WHERE game_id = ?", (game_id,))
        conn.commit()
