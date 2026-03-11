"""Game event storage — timestamped kills, deaths, objectives, etc."""

import json
import logging
from typing import Optional

from .connection import ConnectionManager

logger = logging.getLogger(__name__)

# Event type constants
EVENT_KILL = "KILL"
EVENT_DEATH = "DEATH"
EVENT_ASSIST = "ASSIST"
EVENT_DRAGON = "DRAGON"
EVENT_BARON = "BARON"
EVENT_HERALD = "HERALD"
EVENT_TURRET = "TURRET"
EVENT_INHIBITOR = "INHIBITOR"
EVENT_FIRST_BLOOD = "FIRST_BLOOD"
EVENT_MULTI_KILL = "MULTI_KILL"
EVENT_LEVEL_UP = "LEVEL_UP"

# Visual styling for each event type (color, symbol, label)
EVENT_STYLES = {
    EVENT_KILL:       {"color": "#28c76f", "symbol": "▲", "label": "Kill"},
    EVENT_DEATH:      {"color": "#ea5455", "symbol": "▼", "label": "Death"},
    EVENT_ASSIST:     {"color": "#0099ff", "symbol": "●", "label": "Assist"},
    EVENT_DRAGON:     {"color": "#c89b3c", "symbol": "◆", "label": "Dragon"},
    EVENT_BARON:      {"color": "#8b5cf6", "symbol": "◆", "label": "Baron"},
    EVENT_HERALD:     {"color": "#06b6d4", "symbol": "◆", "label": "Herald"},
    EVENT_TURRET:     {"color": "#f97316", "symbol": "■", "label": "Turret"},
    EVENT_INHIBITOR:  {"color": "#ec4899", "symbol": "■", "label": "Inhibitor"},
    EVENT_FIRST_BLOOD: {"color": "#ef4444", "symbol": "★", "label": "First Blood"},
    EVENT_MULTI_KILL: {"color": "#fbbf24", "symbol": "★", "label": "Multi Kill"},
    EVENT_LEVEL_UP:   {"color": "#6366f1", "symbol": "↑", "label": "Level Up"},
}


class GameEventsRepository:
    """CRUD for game_events table — timestamped in-game events."""

    def __init__(self, conn_mgr: ConnectionManager):
        self._conn_mgr = conn_mgr

    def save_events(self, game_id: int, events: list[dict]):
        """Bulk-insert events for a game. Each event needs:
        event_type, game_time_s, and optionally details (dict).
        Clears any existing events for the game first.
        """
        conn = self._conn_mgr.get_conn()
        conn.execute("DELETE FROM game_events WHERE game_id = ?", (game_id,))
        conn.executemany(
            """INSERT INTO game_events (game_id, event_type, game_time_s, details)
               VALUES (?, ?, ?, ?)""",
            [
                (
                    game_id,
                    e["event_type"],
                    e["game_time_s"],
                    json.dumps(e.get("details", {})),
                )
                for e in events
            ],
        )
        conn.commit()
        logger.info(f"Saved {len(events)} events for game {game_id}")

    def get_events(self, game_id: int) -> list[dict]:
        """Get all events for a game, sorted by timestamp."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            """SELECT * FROM game_events
               WHERE game_id = ?
               ORDER BY game_time_s ASC""",
            (game_id,),
        ).fetchall()
        result = []
        for r in rows:
            d = dict(r)
            try:
                d["details"] = json.loads(d.get("details", "{}"))
            except (json.JSONDecodeError, TypeError):
                d["details"] = {}
            result.append(d)
        return result

    def has_events(self, game_id: int) -> bool:
        """Check if events exist for a game."""
        conn = self._conn_mgr.get_conn()
        row = conn.execute(
            "SELECT COUNT(*) FROM game_events WHERE game_id = ?", (game_id,)
        ).fetchone()
        return (row[0] if row else 0) > 0

    def get_event_count(self, game_id: int) -> int:
        """Count events for a game."""
        conn = self._conn_mgr.get_conn()
        row = conn.execute(
            "SELECT COUNT(*) FROM game_events WHERE game_id = ?", (game_id,)
        ).fetchone()
        return row[0] if row else 0

    def delete_events(self, game_id: int):
        """Delete all events for a game."""
        conn = self._conn_mgr.get_conn()
        conn.execute("DELETE FROM game_events WHERE game_id = ?", (game_id,))
        conn.commit()
