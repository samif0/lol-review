"""Repository for tilt check exercises (guided mental reset between games)."""

import logging
import time
from typing import Optional

logger = logging.getLogger(__name__)


class TiltCheckRepository:
    """Stores and queries tilt check exercise results."""

    def __init__(self, conn_mgr):
        self._conn = conn_mgr

    def save(
        self,
        emotion: str,
        intensity_before: int,
        intensity_after: Optional[int] = None,
        reframe_thought: str = "",
        reframe_response: str = "",
        thought_type: str = "",
        cue_word: str = "",
        focus_intention: str = "",
    ) -> int:
        """Save a completed tilt check exercise. Returns the row id."""
        conn = self._conn.get_conn()
        cursor = conn.execute(
            """INSERT INTO tilt_checks
               (emotion, intensity_before, intensity_after,
                reframe_thought, reframe_response, thought_type,
                cue_word, focus_intention, created_at)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            (
                emotion,
                intensity_before,
                intensity_after,
                reframe_thought,
                reframe_response,
                thought_type,
                cue_word,
                focus_intention,
                int(time.time()),
            ),
        )
        conn.commit()
        return cursor.lastrowid

    def get_recent(self, limit: int = 20) -> list[dict]:
        """Get recent tilt checks, newest first."""
        conn = self._conn.get_conn()
        rows = conn.execute(
            """SELECT * FROM tilt_checks
               ORDER BY created_at DESC LIMIT ?""",
            (limit,),
        ).fetchall()
        return [dict(r) for r in rows]

    def get_stats(self) -> dict:
        """Get aggregate tilt check stats."""
        conn = self._conn.get_conn()

        row = conn.execute(
            """SELECT
                COUNT(*) as total,
                AVG(intensity_before) as avg_before,
                AVG(intensity_after) as avg_after,
                AVG(intensity_before - intensity_after) as avg_reduction
               FROM tilt_checks
               WHERE intensity_after IS NOT NULL"""
        ).fetchone()

        emotions = conn.execute(
            """SELECT emotion, COUNT(*) as cnt
               FROM tilt_checks
               GROUP BY emotion
               ORDER BY cnt DESC"""
        ).fetchall()

        return {
            "total": row["total"] if row else 0,
            "avg_before": round(row["avg_before"], 1) if row and row["avg_before"] else 0,
            "avg_after": round(row["avg_after"], 1) if row and row["avg_after"] else 0,
            "avg_reduction": round(row["avg_reduction"], 1) if row and row["avg_reduction"] else 0,
            "top_emotions": [{"emotion": e["emotion"], "count": e["cnt"]} for e in emotions],
        }
