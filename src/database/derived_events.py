"""Derived events — user-defined event clustering (teamfights, skirmishes, etc.)."""

import json
import logging
import time

from .connection import ConnectionManager

logger = logging.getLogger(__name__)


class DerivedEventsRepository:
    """CRUD + computation for derived event definitions and instances."""

    def __init__(self, conn_mgr: ConnectionManager):
        self._conn_mgr = conn_mgr

    def create(self, name: str, source_types: list[str], min_count: int,
               window_seconds: int, color: str = "#ff6b6b") -> int:
        conn = self._conn_mgr.get_conn()
        cur = conn.execute(
            """INSERT INTO derived_event_definitions
               (name, source_types, min_count, window_seconds, color, created_at)
               VALUES (?, ?, ?, ?, ?, ?)""",
            (name, json.dumps(source_types), min_count, window_seconds, color,
             int(time.time())),
        )
        conn.commit()
        return cur.lastrowid

    def get_all_definitions(self) -> list[dict]:
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            "SELECT * FROM derived_event_definitions ORDER BY is_default DESC, name ASC"
        ).fetchall()
        result = []
        for r in rows:
            d = dict(r)
            d["source_types"] = json.loads(d.get("source_types", "[]"))
            result.append(d)
        return result

    def delete_definition(self, def_id: int):
        conn = self._conn_mgr.get_conn()
        conn.execute("DELETE FROM derived_event_instances WHERE definition_id = ?", (def_id,))
        conn.execute("DELETE FROM derived_event_definitions WHERE id = ? AND is_default = 0", (def_id,))
        conn.commit()

    def compute_instances(self, game_id: int, events: list[dict]) -> list[dict]:
        """Compute derived event instances from raw game events.

        For each definition, filter events to matching source_types, sort by time.
        Sliding window: for each event, look forward within window_seconds, count matches.
        When count >= min_count, emit instance. Advance past cluster (greedy non-overlapping).
        """
        definitions = self.get_all_definitions()
        all_instances = []

        for defn in definitions:
            source_types = set(defn["source_types"])
            min_count = defn["min_count"]
            window = defn["window_seconds"]

            # Filter and sort matching events
            matching = sorted(
                [e for e in events if e.get("event_type") in source_types],
                key=lambda e: e.get("game_time_s", 0),
            )

            if len(matching) < min_count:
                continue

            i = 0
            while i < len(matching):
                start_time = matching[i].get("game_time_s", 0)
                # Look forward within window
                cluster = [matching[i]]
                j = i + 1
                while j < len(matching):
                    t = matching[j].get("game_time_s", 0)
                    if t - start_time <= window:
                        cluster.append(matching[j])
                        j += 1
                    else:
                        break

                if len(cluster) >= min_count:
                    end_time = cluster[-1].get("game_time_s", 0)
                    source_ids = [e.get("id", 0) for e in cluster if e.get("id")]
                    all_instances.append({
                        "game_id": game_id,
                        "definition_id": defn["id"],
                        "start_time_s": start_time,
                        "end_time_s": end_time,
                        "event_count": len(cluster),
                        "source_event_ids": source_ids,
                        "definition_name": defn["name"],
                        "color": defn["color"],
                    })
                    i = j  # Advance past cluster (greedy non-overlapping)
                else:
                    i += 1

        return all_instances

    def save_instances(self, game_id: int, instances: list[dict]):
        """Save computed instances for a game, replacing any existing ones."""
        conn = self._conn_mgr.get_conn()
        conn.execute("DELETE FROM derived_event_instances WHERE game_id = ?", (game_id,))
        for inst in instances:
            conn.execute(
                """INSERT INTO derived_event_instances
                   (game_id, definition_id, start_time_s, end_time_s, event_count, source_event_ids)
                   VALUES (?, ?, ?, ?, ?, ?)""",
                (game_id, inst["definition_id"], inst["start_time_s"],
                 inst["end_time_s"], inst["event_count"],
                 json.dumps(inst.get("source_event_ids", []))),
            )
        conn.commit()

    def get_instances(self, game_id: int) -> list[dict]:
        """Get all derived event instances for a game, with definition info."""
        conn = self._conn_mgr.get_conn()
        rows = conn.execute(
            """SELECT di.*, dd.name as definition_name, dd.color, dd.source_types
               FROM derived_event_instances di
               JOIN derived_event_definitions dd ON dd.id = di.definition_id
               WHERE di.game_id = ?
               ORDER BY di.start_time_s ASC""",
            (game_id,),
        ).fetchall()
        result = []
        for r in rows:
            d = dict(r)
            d["source_event_ids"] = json.loads(d.get("source_event_ids", "[]"))
            d["source_types"] = json.loads(d.get("source_types", "[]"))
            result.append(d)
        return result
