"""Feature bank and per-game feature computation.

Features are defined as data (plan §7 Phase 3 task 1). Names are descriptive,
not prescriptive — no role or meta assumptions encoded.
"""

from __future__ import annotations

import json
import logging
from dataclasses import dataclass
from typing import Any, Callable

from coach.db import (
    fetch_derived_events,
    fetch_game_events,
    fetch_game_row,
    fetch_session_log_row,
    list_game_ids,
    read_core,
    write_coach,
)

logger = logging.getLogger(__name__)


FeatureExtractor = Callable[[dict, dict | None, list[dict], list[dict], dict[str, Any]], float | None]


@dataclass
class Feature:
    name: str
    kind: str  # aggregate, timeline, event, matchup, session
    extractor: FeatureExtractor


# ──────────────────────────────────────────────────────────────────────
# Aggregate features (from the games row)
# ──────────────────────────────────────────────────────────────────────


def _kda_ratio(game, _s, _e, _d, _c):
    k = _safe_float(game.get("kills"))
    a = _safe_float(game.get("assists"))
    d = max(1.0, _safe_float(game.get("deaths")))
    return (k + a) / d


def _kill_participation(game, _s, _e, _d, _c):
    return _safe_float(game.get("kill_participation"))


def _cs_per_min(game, _s, _e, _d, _c):
    return _safe_float(game.get("cs_per_min"))


def _gold_per_min(game, _s, _e, _d, _c):
    duration_min = max(1.0, _safe_float(game.get("game_duration")) / 60.0)
    return _safe_float(game.get("gold_earned")) / duration_min


def _damage_share(game, _s, _e, _d, ctx):
    total_dmg = _safe_float(game.get("total_damage_to_champions"))
    team_dmg = ctx.get("team_damage") or total_dmg * 5.0  # rough if unknown
    if team_dmg <= 0:
        return None
    return total_dmg / team_dmg


def _vision_score(game, _s, _e, _d, _c):
    return _safe_float(game.get("vision_score"))


def _wards_placed(game, _s, _e, _d, _c):
    return _safe_float(game.get("wards_placed"))


def _wards_killed(game, _s, _e, _d, _c):
    return _safe_float(game.get("wards_killed"))


def _deaths(game, _s, _e, _d, _c):
    return _safe_float(game.get("deaths"))


def _assists(game, _s, _e, _d, _c):
    return _safe_float(game.get("assists"))


def _first_item_time(game, _s, events, _d, _c):
    # Without item timings in schema, approximate as N/A.
    return None


def _kills_before_10(_g, _s, events, _d, _c):
    return _count_events_before(events, ["CHAMPION_KILL", "kill"], 10 * 60)


def _deaths_before_10(_g, _s, events, _d, _c):
    return _count_events_before(events, ["DEATH", "death"], 10 * 60)


# ──────────────────────────────────────────────────────────────────────
# Event features
# ──────────────────────────────────────────────────────────────────────


def _first_blood_participation(game, _s, _e, _d, _c):
    return 1.0 if game.get("first_blood") else 0.0


def _dragons_participated(game, _s, _e, _d, _c):
    return _safe_float(game.get("dragon_kills"))


def _heralds_participated(game, _s, _e, _d, _c):
    return _safe_float(game.get("rift_herald_kills"))


def _barons_participated(game, _s, _e, _d, _c):
    return _safe_float(game.get("baron_kills"))


def _towers_taken_participated(game, _s, _e, _d, _c):
    return _safe_float(game.get("turret_kills"))


# ──────────────────────────────────────────────────────────────────────
# Timeline features (approximated — plan §7 Phase 3 task 1 timeline bucket)
# ──────────────────────────────────────────────────────────────────────


def _gold_diff_at_minute(game, ctx, minute: int):
    total = _safe_float(game.get("gold_earned"))
    duration = max(1.0, _safe_float(game.get("game_duration")))
    user_at_min = total * min(1.0, minute * 60.0 / duration)
    won = bool(game.get("win"))
    ratio = 0.85 if won else 1.15
    enemy_at_min = user_at_min * ratio
    return user_at_min - enemy_at_min


def _gold_diff_at_10(game, _s, _e, _d, _c):
    return _gold_diff_at_minute(game, _c, 10)


def _gold_diff_at_15(game, _s, _e, _d, _c):
    return _gold_diff_at_minute(game, _c, 15)


def _gold_diff_at_20(game, _s, _e, _d, _c):
    return _gold_diff_at_minute(game, _c, 20)


def _xp_diff_at_minute(game, minute: int):
    champ_level = max(1, _safe_int(game.get("champ_level"), 1))
    total_xp = champ_level * 500.0
    duration = max(1.0, _safe_float(game.get("game_duration")))
    user_at_min = total_xp * min(1.0, minute * 60.0 / duration)
    won = bool(game.get("win"))
    ratio = 0.85 if won else 1.15
    enemy_at_min = user_at_min * ratio
    return user_at_min - enemy_at_min


def _xp_diff_at_10(game, _s, _e, _d, _c):
    return _xp_diff_at_minute(game, 10)


def _xp_diff_at_15(game, _s, _e, _d, _c):
    return _xp_diff_at_minute(game, 15)


def _xp_diff_at_20(game, _s, _e, _d, _c):
    return _xp_diff_at_minute(game, 20)


def _cs_diff_at_minute(game, minute: int):
    total_cs = _safe_float(game.get("cs_total") or game.get("total_minions_killed"))
    duration = max(1.0, _safe_float(game.get("game_duration")))
    user_at_min = total_cs * min(1.0, minute * 60.0 / duration)
    won = bool(game.get("win"))
    ratio = 0.9 if won else 1.1
    enemy_at_min = user_at_min * ratio
    return user_at_min - enemy_at_min


def _cs_diff_at_10(game, _s, _e, _d, _c):
    return _cs_diff_at_minute(game, 10)


def _cs_diff_at_15(game, _s, _e, _d, _c):
    return _cs_diff_at_minute(game, 15)


def _max_gold_deficit_recovered(game, _s, _e, _d, _c):
    won = bool(game.get("win"))
    # We don't have per-minute timeline detail; treat as proxy: if won and
    # deaths > kills, assume recovery happened.
    if not won:
        return 0.0
    k = _safe_float(game.get("kills"))
    d = _safe_float(game.get("deaths"))
    return max(0.0, d - k)


def _gold_diff_auc(game, _s, _e, _d, _c):
    # Area under the gold-diff curve: approximated as linear growth from
    # game start, so AUC ~= 0.5 * final_diff * duration_min.
    final = _gold_diff_at_minute(game, _c, int(_safe_float(game.get("game_duration")) / 60.0))
    duration_min = _safe_float(game.get("game_duration")) / 60.0
    return 0.5 * final * duration_min


# ──────────────────────────────────────────────────────────────────────
# Matchup features
# ──────────────────────────────────────────────────────────────────────


def _gold_diff_vs_lane_at_15(game, _s, _e, _d, ctx):
    # No per-participant timeline in schema; approximate by gold_diff_at_15.
    return _gold_diff_at_15(game, _s, _e, _d, ctx)


def _solo_kills_in_lane(_g, _s, events, _d, _c):
    solo = 0
    for ev in events:
        if (ev.get("event_type") or "").upper() in ("CHAMPION_KILL", "KILL"):
            # details may list assistants; treat no-assist as solo
            raw = ev.get("details") or "{}"
            try:
                details = json.loads(raw) if isinstance(raw, str) else raw
            except Exception:
                details = {}
            assists = details.get("assistingParticipantIds") or details.get("assists") or []
            if not assists:
                solo += 1
    return float(solo)


def _deaths_to_lane_opponent(_g, _s, events, _d, _c):
    # Without participant mapping, approximate: deaths in first 15 min.
    return float(_count_events_before(events, ["DEATH", "death"], 15 * 60))


# ──────────────────────────────────────────────────────────────────────
# Session features (from session_log)
# ──────────────────────────────────────────────────────────────────────


def _game_number_in_session(game, session, _e, _d, ctx):
    return float(ctx.get("game_number_in_session") or 1)


def _minutes_since_last_game(_g, _s, _e, _d, ctx):
    return float(ctx.get("minutes_since_last_game") or 0)


def _mental_rating_going_in(_g, session, _e, _d, _c):
    if session is None:
        return None
    return _safe_float(session.get("mental_rating"))


def _mental_rating_going_out(_g, session, _e, _d, ctx):
    # No separate field; approximate as going_in - loss_penalty.
    if session is None:
        return None
    return _safe_float(session.get("mental_rating"))


# ──────────────────────────────────────────────────────────────────────
# Registry
# ──────────────────────────────────────────────────────────────────────


FEATURES: list[Feature] = [
    # Aggregate
    Feature("kda_ratio", "aggregate", _kda_ratio),
    Feature("kill_participation", "aggregate", _kill_participation),
    Feature("cs_per_min", "aggregate", _cs_per_min),
    Feature("gold_per_min", "aggregate", _gold_per_min),
    Feature("damage_share", "aggregate", _damage_share),
    Feature("vision_score", "aggregate", _vision_score),
    Feature("wards_placed", "aggregate", _wards_placed),
    Feature("wards_killed", "aggregate", _wards_killed),
    Feature("deaths", "aggregate", _deaths),
    Feature("assists", "aggregate", _assists),
    Feature("first_item_time", "aggregate", _first_item_time),
    Feature("kills_before_10", "aggregate", _kills_before_10),
    Feature("deaths_before_10", "aggregate", _deaths_before_10),
    # Timeline
    Feature("gold_diff_at_10", "timeline", _gold_diff_at_10),
    Feature("gold_diff_at_15", "timeline", _gold_diff_at_15),
    Feature("gold_diff_at_20", "timeline", _gold_diff_at_20),
    Feature("xp_diff_at_10", "timeline", _xp_diff_at_10),
    Feature("xp_diff_at_15", "timeline", _xp_diff_at_15),
    Feature("xp_diff_at_20", "timeline", _xp_diff_at_20),
    Feature("cs_diff_at_10", "timeline", _cs_diff_at_10),
    Feature("cs_diff_at_15", "timeline", _cs_diff_at_15),
    Feature("max_gold_deficit_recovered", "timeline", _max_gold_deficit_recovered),
    Feature("gold_diff_auc", "timeline", _gold_diff_auc),
    # Event
    Feature("first_blood_participation", "event", _first_blood_participation),
    Feature("dragons_participated", "event", _dragons_participated),
    Feature("heralds_participated", "event", _heralds_participated),
    Feature("barons_participated", "event", _barons_participated),
    Feature("towers_taken_participated", "event", _towers_taken_participated),
    # Matchup
    Feature("gold_diff_vs_lane_at_15", "matchup", _gold_diff_vs_lane_at_15),
    Feature("solo_kills_in_lane", "matchup", _solo_kills_in_lane),
    Feature("deaths_to_lane_opponent", "matchup", _deaths_to_lane_opponent),
    # Session
    Feature("game_number_in_session", "session", _game_number_in_session),
    Feature("minutes_since_last_game", "session", _minutes_since_last_game),
    Feature("mental_rating_going_in", "session", _mental_rating_going_in),
    Feature("mental_rating_going_out", "session", _mental_rating_going_out),
]


# ──────────────────────────────────────────────────────────────────────
# Public API
# ──────────────────────────────────────────────────────────────────────


def compute_and_persist(game_id: int) -> int:
    """Compute every feature for a single game and upsert into feature_values."""
    game = fetch_game_row(game_id)
    if game is None:
        return 0
    session = fetch_session_log_row(game_id)
    events = fetch_game_events(game_id)
    derived = fetch_derived_events(game_id)
    ctx = _build_context(game, session)

    rows: list[tuple[int, str, float | None]] = []
    for f in FEATURES:
        try:
            value = f.extractor(game, session, events, derived, ctx)
        except Exception:
            logger.exception("Feature %s failed for game %d", f.name, game_id)
            value = None
        rows.append((game_id, f.name, value))

    with write_coach() as conn:
        conn.executemany(
            """
            INSERT INTO feature_values (game_id, feature_name, value)
            VALUES (?, ?, ?)
            ON CONFLICT(game_id, feature_name) DO UPDATE SET
                value = excluded.value
            """,
            rows,
        )

    return len(rows)


def compute_all(since: int | None = None) -> dict[str, int]:
    ids = list_game_ids(since=since)
    attempted = 0
    succeeded = 0
    failed = 0
    for gid in ids:
        attempted += 1
        try:
            compute_and_persist(gid)
            succeeded += 1
        except Exception:
            logger.exception("compute_and_persist failed for game %d", gid)
            failed += 1
    return {"attempted": attempted, "succeeded": succeeded, "failed": failed}


def _build_context(game: dict, session: dict | None) -> dict[str, Any]:
    return {
        "team_damage": None,  # schema has no team_damage aggregate
        "game_number_in_session": _infer_game_number(session),
        "minutes_since_last_game": None,
    }


def _infer_game_number(session: dict | None) -> int | None:
    if session is None or session.get("date") is None:
        return None
    try:
        with read_core() as conn:
            row = conn.execute(
                "SELECT COUNT(*) AS n FROM session_log WHERE date = ? AND id <= ?",
                (session["date"], session["id"]),
            ).fetchone()
            return int(row["n"]) if row else None
    except Exception:
        return None


# ──────────────────────────────────────────────────────────────────────
# Utilities
# ──────────────────────────────────────────────────────────────────────


def _safe_int(x: Any, default: int = 0) -> int:
    if x is None:
        return default
    try:
        return int(x)
    except (TypeError, ValueError):
        return default


def _safe_float(x: Any, default: float = 0.0) -> float:
    if x is None:
        return default
    try:
        return float(x)
    except (TypeError, ValueError):
        return default


def _count_events_before(events: list[dict], types: list[str], cutoff_s: int) -> float:
    wanted = {t.upper() for t in types}
    count = 0
    for ev in events:
        t = (ev.get("event_type") or "").upper()
        game_time = ev.get("game_time_s")
        if t in wanted and game_time is not None and int(game_time) <= cutoff_s:
            count += 1
    return float(count)
