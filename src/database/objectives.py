"""Learning objectives repository."""

import time

from .connection import ConnectionManager

# Progression thresholds: (min_score, level_name)
_LEVELS = [
    (0,  "Exploring"),
    (15, "Drilling"),
    (30, "Ingraining"),
    (50, "Ready"),
]


class ObjectivesRepository:
    """CRUD + scoring for the objectives and game_objectives tables."""

    def __init__(self, conn_mgr: ConnectionManager):
        self._conn_mgr = conn_mgr

    def create(
        self,
        title: str,
        skill_area: str = "",
        obj_type: str = "primary",
        completion_criteria: str = "",
        description: str = "",
    ) -> int:
        """Create a new objective. Returns the new objective ID."""
        conn = self._conn_mgr.get_conn()
        cur = conn.execute(
            """INSERT INTO objectives
               (title, skill_area, type, completion_criteria, description,
                status, score, game_count, created_at)
               VALUES (?, ?, ?, ?, ?, 'active', 0, 0, ?)""",
            (title, skill_area, obj_type, completion_criteria, description,
             int(time.time())),
        )
        conn.commit()
        return cur.lastrowid

    def get_all(self) -> list[dict]:
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            "SELECT * FROM objectives ORDER BY status ASC, type ASC, created_at DESC"
        ).fetchall()
        return [dict(r) for r in rows]

    def get_active(self) -> list[dict]:
        """Return active objectives, primary first."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            "SELECT * FROM objectives WHERE status = 'active' ORDER BY type ASC, created_at ASC"
        ).fetchall()
        return [dict(r) for r in rows]

    def get(self, obj_id: int) -> dict | None:
        conn = self._conn_mgr.get_conn()
        row = conn.execute("SELECT * FROM objectives WHERE id = ?", (obj_id,)).fetchone()
        return dict(row) if row else None

    def update_score(self, obj_id: int, win: bool):
        """Apply win (+2) or loss (-1) to objective score. Score floors at 0."""
        delta = 2 if win else -1
        conn = self._conn_mgr.get_conn()
        conn.execute(
            "UPDATE objectives SET score = MAX(0, score + ?) WHERE id = ?",
            (delta, obj_id),
        )
        conn.commit()

    def mark_complete(self, obj_id: int):
        conn = self._conn_mgr.get_conn()
        conn.execute(
            "UPDATE objectives SET status = 'completed', completed_at = ? WHERE id = ?",
            (int(time.time()), obj_id),
        )
        conn.commit()

    def delete(self, obj_id: int):
        conn = self._conn_mgr.get_conn()
        conn.execute("DELETE FROM game_objectives WHERE objective_id = ?", (obj_id,))
        conn.execute("DELETE FROM objectives WHERE id = ?", (obj_id,))
        conn.commit()

    def record_game(
        self,
        game_id: int,
        objective_id: int,
        practiced: bool,
        execution_note: str = "",
    ):
        """Log that a game was played under this objective."""
        conn = self._conn_mgr.get_conn()
        conn.execute(
            """INSERT OR REPLACE INTO game_objectives
               (game_id, objective_id, practiced, execution_note)
               VALUES (?, ?, ?, ?)""",
            (game_id, objective_id, 1 if practiced else 0, execution_note),
        )
        conn.commit()

    def get_game_objectives(self, game_id: int) -> list[dict]:
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            """SELECT go.*, o.title, o.completion_criteria, o.type
               FROM game_objectives go
               JOIN objectives o ON o.id = go.objective_id
               WHERE go.game_id = ?""",
            (game_id,),
        ).fetchall()
        return [dict(r) for r in rows]

    @staticmethod
    def get_level_info(score: int, game_count: int) -> dict:
        """Return progression display info for a score + game_count."""
        level_idx = 0
        for i, (threshold, _) in enumerate(_LEVELS):
            if score >= threshold:
                level_idx = i

        level_name = _LEVELS[level_idx][1]
        level_start = _LEVELS[level_idx][0]

        if level_idx + 1 < len(_LEVELS):
            next_threshold = _LEVELS[level_idx + 1][0]
            progress = min(1.0, (score - level_start) / (next_threshold - level_start))
        else:
            next_threshold = None
            progress = 1.0

        return {
            "level_name": level_name,
            "level_index": level_idx,
            "score": score,
            "game_count": game_count,
            "progress": progress,
            "next_threshold": next_threshold,
            "can_complete": game_count >= 30,
            "suggest_complete": score >= 50 and game_count >= 30,
        }
