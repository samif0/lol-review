"""Objective review prompts — per-objective questions asked during post-game review."""

import logging

from .connection import ConnectionManager

logger = logging.getLogger(__name__)


class PromptsRepository:
    """CRUD for objective_prompts and prompt_answers tables."""

    def __init__(self, conn_mgr: ConnectionManager):
        self._conn_mgr = conn_mgr

    def create_prompt(self, objective_id: int, question_text: str,
                      event_tag: str = "", answer_type: str = "yes_no",
                      sort_order: int = 0) -> int:
        conn = self._conn_mgr.get_conn()
        cur = conn.execute(
            """INSERT INTO objective_prompts
               (objective_id, question_text, event_tag, answer_type, sort_order)
               VALUES (?, ?, ?, ?, ?)""",
            (objective_id, question_text, event_tag, answer_type, sort_order),
        )
        conn.commit()
        return cur.lastrowid

    def get_prompts_for_objective(self, objective_id: int) -> list[dict]:
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            """SELECT * FROM objective_prompts
               WHERE objective_id = ?
               ORDER BY sort_order ASC, id ASC""",
            (objective_id,),
        ).fetchall()
        return [dict(r) for r in rows]

    def delete_prompts_for_objective(self, objective_id: int):
        conn = self._conn_mgr.get_conn()
        conn.execute(
            "DELETE FROM prompt_answers WHERE prompt_id IN (SELECT id FROM objective_prompts WHERE objective_id = ?)",
            (objective_id,),
        )
        conn.execute("DELETE FROM objective_prompts WHERE objective_id = ?", (objective_id,))
        conn.commit()

    def save_answer(self, game_id: int, prompt_id: int, answer_value: int,
                    event_instance_id: int = None, event_time_s: int = None):
        conn = self._conn_mgr.get_conn()
        conn.execute(
            """INSERT OR REPLACE INTO prompt_answers
               (game_id, prompt_id, event_instance_id, event_time_s, answer_value)
               VALUES (?, ?, ?, ?, ?)""",
            (game_id, prompt_id, event_instance_id, event_time_s, answer_value),
        )
        conn.commit()

    def get_answers_for_game(self, game_id: int) -> list[dict]:
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            "SELECT * FROM prompt_answers WHERE game_id = ?",
            (game_id,),
        ).fetchall()
        return [dict(r) for r in rows]

    def get_progression_data(self, objective_id: int) -> list[dict]:
        """Get answer scores over time for an objective's prompts."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            """SELECT pa.game_id, pa.answer_value, pa.prompt_id, g.timestamp
               FROM prompt_answers pa
               JOIN objective_prompts op ON op.id = pa.prompt_id
               JOIN games g ON g.game_id = pa.game_id
               WHERE op.objective_id = ?
               ORDER BY g.timestamp ASC""",
            (objective_id,),
        ).fetchall()
        return [dict(r) for r in rows]
