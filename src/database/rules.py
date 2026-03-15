"""User-defined gaming rules repository."""

import time
from datetime import datetime

from .connection import ConnectionManager


class RulesRepository:
    """CRUD + violation checking for user-defined gaming rules."""

    def __init__(self, conn_mgr: ConnectionManager):
        self._conn_mgr = conn_mgr

    def create(
        self,
        name: str,
        description: str = "",
        rule_type: str = "custom",
        condition_value: str = "",
    ) -> int:
        """Create a new rule. Returns the new rule ID."""
        conn = self._conn_mgr.get_conn()
        cur = conn.execute(
            """INSERT INTO rules
               (name, description, rule_type, condition_value, is_active, created_at)
               VALUES (?, ?, ?, ?, 1, ?)""",
            (name, description, rule_type, condition_value, int(time.time())),
        )
        conn.commit()
        return cur.lastrowid

    def get_all(self) -> list[dict]:
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            "SELECT * FROM rules ORDER BY is_active DESC, created_at DESC"
        ).fetchall()
        return [dict(r) for r in rows]

    def get_active(self) -> list[dict]:
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            "SELECT * FROM rules WHERE is_active = 1 ORDER BY created_at ASC"
        ).fetchall()
        return [dict(r) for r in rows]

    def get(self, rule_id: int) -> dict | None:
        conn = self._conn_mgr.get_conn()
        row = conn.execute(
            "SELECT * FROM rules WHERE id = ?", (rule_id,)
        ).fetchone()
        return dict(row) if row else None

    def toggle(self, rule_id: int):
        """Toggle a rule's active state."""
        conn = self._conn_mgr.get_conn()
        conn.execute(
            "UPDATE rules SET is_active = 1 - is_active WHERE id = ?", (rule_id,)
        )
        conn.commit()

    def delete(self, rule_id: int):
        conn = self._conn_mgr.get_conn()
        conn.execute("DELETE FROM rules WHERE id = ?", (rule_id,))
        conn.commit()

    def check_violations(
        self,
        todays_games: list[dict] | None = None,
        mental_rating: int | None = None,
    ) -> list[dict]:
        """Check all active rules, return list of {rule, violated, reason}."""
        rules = self.get_active()
        results = []
        now = datetime.now()

        for rule in rules:
            rt = rule["rule_type"]
            cv = rule["condition_value"]
            violated = False
            reason = ""

            if rt == "no_play_day":
                days = [d.strip().lower() for d in cv.split(",")]
                today_name = now.strftime("%A").lower()
                if today_name in days:
                    violated = True
                    reason = f"Today is {now.strftime('%A')}"

            elif rt == "no_play_after":
                try:
                    hour = int(cv)
                    if now.hour >= hour:
                        violated = True
                        reason = f"It's past {hour}:00"
                except ValueError:
                    pass

            elif rt == "loss_streak" and todays_games is not None:
                try:
                    threshold = int(cv)
                    consecutive = 0
                    for g in reversed(todays_games):
                        if not g.get("win"):
                            consecutive += 1
                        else:
                            break
                    if consecutive >= threshold:
                        violated = True
                        reason = f"{consecutive} consecutive losses"
                except ValueError:
                    pass

            elif rt == "max_games" and todays_games is not None:
                try:
                    max_g = int(cv)
                    if len(todays_games) >= max_g:
                        violated = True
                        reason = f"{len(todays_games)}/{max_g} games played"
                except ValueError:
                    pass

            elif rt == "min_mental" and mental_rating is not None:
                try:
                    min_m = int(cv)
                    if mental_rating < min_m:
                        violated = True
                        reason = f"Mental at {mental_rating}, minimum is {min_m}"
                except ValueError:
                    pass

            # custom rules can't be auto-checked
            results.append({"rule": rule, "violated": violated, "reason": reason})

        return results
