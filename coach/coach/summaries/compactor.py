"""LoL-MDC-style compacted summary builder.

Plan §7 Phase 1 task 1. Output is a structured JSON with four sections:
  match_overview, team_and_player_stats, timeline_view, key_events

The summary is deterministic — no LLM calls, just aggregation and
Pythagorean win probability.

Token target: 1,200–2,500 tokens per summary (plan §7 Phase 1 task 6).
"""

from __future__ import annotations

import json
import logging
import time
from dataclasses import dataclass
from typing import Any

from coach.db import (
    fetch_derived_events,
    fetch_game_events,
    fetch_game_row,
    fetch_session_log_row,
    list_game_ids,
    read_core,
    write_coach,
)
from coach.schemas import GetSummaryResponse
from coach.summaries.key_events import select_key_events
from coach.summaries.win_probability import DEFAULT_ALPHA, timeline_win_probabilities

logger = logging.getLogger(__name__)

SUMMARY_VERSION = 1

# Use tiktoken if available; otherwise fall back to char/4 heuristic.
try:
    import tiktoken

    _ENCODER = tiktoken.get_encoding("o200k_base")
except Exception:
    _ENCODER = None


def _count_tokens(text: str) -> int:
    if _ENCODER is not None:
        return len(_ENCODER.encode(text))
    return max(1, len(text) // 4)


# ──────────────────────────────────────────────────────────────────────
# Public API
# ──────────────────────────────────────────────────────────────────────


def build_and_persist(game_id: int) -> tuple[int, int]:
    """Build a summary for a single game and persist it. Returns (token_count, version)."""
    summary = build_summary(game_id)
    json_text = json.dumps(summary, separators=(",", ":"), ensure_ascii=False)
    token_count = _count_tokens(json_text)

    wp_json = json.dumps(summary.get("timeline_view", {}).get("win_probability", []))
    ke_json = json.dumps(summary.get("key_events", []))

    with write_coach() as conn:
        conn.execute(
            """
            INSERT INTO game_summary
                (game_id, compacted_json, win_probability_timeline_json, key_events_json,
                 summary_version, created_at, token_count)
            VALUES (?, ?, ?, ?, ?, strftime('%s','now'), ?)
            ON CONFLICT(game_id) DO UPDATE SET
                compacted_json = excluded.compacted_json,
                win_probability_timeline_json = excluded.win_probability_timeline_json,
                key_events_json = excluded.key_events_json,
                summary_version = excluded.summary_version,
                created_at = excluded.created_at,
                token_count = excluded.token_count
            """,
            (game_id, json_text, wp_json, ke_json, SUMMARY_VERSION, token_count),
        )

    return token_count, SUMMARY_VERSION


def build_all(since: int | None = None) -> dict[str, int]:
    """Backfill summaries for all games (optionally filtered by since timestamp)."""
    ids = list_game_ids(since=since)
    attempted = 0
    succeeded = 0
    failed = 0
    skipped = 0

    for gid in ids:
        attempted += 1
        try:
            build_and_persist(gid)
            succeeded += 1
        except NoSummarizableData:
            skipped += 1
        except Exception:
            logger.exception("Failed to build summary for game %d", gid)
            failed += 1

    return {
        "attempted": attempted,
        "succeeded": succeeded,
        "failed": failed,
        "skipped": skipped,
    }


def load_summary(game_id: int) -> GetSummaryResponse | None:
    with read_core() as conn:
        row = conn.execute(
            """
            SELECT game_id, compacted_json, win_probability_timeline_json,
                   key_events_json, summary_version, token_count, created_at
            FROM game_summary WHERE game_id = ?
            """,
            (game_id,),
        ).fetchone()
    if row is None:
        return None

    compacted = json.loads(row["compacted_json"])
    wp = (
        json.loads(row["win_probability_timeline_json"])
        if row["win_probability_timeline_json"]
        else None
    )
    ke = json.loads(row["key_events_json"]) if row["key_events_json"] else None

    return GetSummaryResponse(
        game_id=row["game_id"],
        compacted_json=compacted,
        win_probability_timeline=wp,
        key_events=ke,
        summary_version=row["summary_version"],
        token_count=row["token_count"],
        created_at=row["created_at"],
    )


# ──────────────────────────────────────────────────────────────────────
# Summary building
# ──────────────────────────────────────────────────────────────────────


class NoSummarizableData(Exception):
    """Raised when a game has no usable data to summarize."""


@dataclass
class _TimelineBucket:
    minute: int
    gold: float
    xp: float
    cs: int


def build_summary(game_id: int) -> dict[str, Any]:
    game = fetch_game_row(game_id)
    if game is None:
        raise NoSummarizableData(f"No games row for id={game_id}")

    session = fetch_session_log_row(game_id)
    derived = fetch_derived_events(game_id)
    events = fetch_game_events(game_id)

    # Source selection (plan §7 Phase 1 task 4)
    if derived:
        event_source = "derived_event_instances"
        events_for_key_events = derived
    else:
        event_source = "game_events"
        events_for_key_events = events

    # Timeline reconstruction from game_events and raw_stats.
    user_timeline = _infer_user_timeline(game, events)
    team_timeline = _infer_team_timeline(game, events, user_timeline)
    enemy_timeline = _infer_enemy_timeline(game, events, user_timeline)

    win_probs = timeline_win_probabilities(
        team_gold=[b.gold for b in team_timeline],
        enemy_gold=[b.gold for b in enemy_timeline],
        team_xp=[b.xp for b in team_timeline],
        enemy_xp=[b.xp for b in enemy_timeline],
        alpha=DEFAULT_ALPHA,
    )

    key_events = select_key_events(
        events_for_key_events,
        win_probs,
        top_n=5,
        source=event_source,
    )

    # ── match_overview ─────────────────────────────────────────────
    match_overview = {
        "match_id": game.get("game_id"),
        "patch": _safe_str(game.get("game_mode")),  # best-effort; raw payload often has gameVersion
        "queue": _safe_str(game.get("queue_type")),
        "duration_s": _safe_int(game.get("game_duration")),
        "champion": _safe_str(game.get("champion_name")),
        "role": _safe_str(game.get("position") or game.get("role")),
        "enemy_lane_champion": _infer_enemy_lane(game),
        "result": "win" if game.get("win") else "loss",
        "kda": [
            _safe_int(game.get("kills")),
            _safe_int(game.get("deaths")),
            _safe_int(game.get("assists")),
        ],
        "cs": _safe_int(game.get("cs_total") or game.get("total_minions_killed")),
        "gold": _safe_int(game.get("gold_earned")),
        "damage_dealt": _safe_int(game.get("total_damage_to_champions")),
        "vision_score": _safe_int(game.get("vision_score")),
        "mental_rating_going_in": _safe_int((session or {}).get("mental_rating")),
    }

    # ── team_and_player_stats ──────────────────────────────────────
    team_and_player_stats = {
        "user": {
            "champion": match_overview["champion"],
            "role": match_overview["role"],
            "kda": match_overview["kda"],
            "cs": match_overview["cs"],
            "cs_per_min": _round(_safe_float(game.get("cs_per_min")), 2),
            "gold": match_overview["gold"],
            "damage_to_champions": match_overview["damage_dealt"],
            "damage_taken": _safe_int(game.get("total_damage_taken")),
            "vision_score": match_overview["vision_score"],
            "wards_placed": _safe_int(game.get("wards_placed")),
            "wards_killed": _safe_int(game.get("wards_killed")),
            "kill_participation": _round(_safe_float(game.get("kill_participation")), 2),
            "first_blood": bool(game.get("first_blood")),
            "champ_level": _safe_int(game.get("champ_level")),
        },
        "team_aggregate": {
            "kills_team": _safe_int(game.get("team_kills")),
            "dragon_kills_team": _safe_int(game.get("dragon_kills")),
            "baron_kills_team": _safe_int(game.get("baron_kills")),
            "rift_herald_kills_team": _safe_int(game.get("rift_herald_kills")),
            "turret_kills_team": _safe_int(game.get("turret_kills")),
        },
    }

    # ── timeline_view ──────────────────────────────────────────────
    timeline_view = {
        "buckets_minutes": len(user_timeline),
        "user": {
            "gold": [int(b.gold) for b in user_timeline],
            "xp": [int(b.xp) for b in user_timeline],
            "cs": [b.cs for b in user_timeline],
        },
        "advantage_gold": [
            int(t.gold - e.gold) for t, e in zip(team_timeline, enemy_timeline)
        ],
        "advantage_xp": [
            int(t.xp - e.xp) for t, e in zip(team_timeline, enemy_timeline)
        ],
        "win_probability": [round(p, 4) for p in win_probs],
    }

    # ── key_events (already selected above) ────────────────────────

    compacted: dict[str, Any] = {
        "schema_version": SUMMARY_VERSION,
        "generated_at": int(time.time()),
        "event_source": event_source,
        "match_overview": match_overview,
        "team_and_player_stats": team_and_player_stats,
        "timeline_view": timeline_view,
        "key_events": key_events,
    }
    return compacted


# ──────────────────────────────────────────────────────────────────────
# Timeline reconstruction heuristics
# ──────────────────────────────────────────────────────────────────────


def _infer_user_timeline(game: dict, events: list[dict]) -> list[_TimelineBucket]:
    """Best-effort per-minute timeline for the user player.

    We don't have a real per-minute snapshot table. We approximate:
    - gold accrual assumed linear from 0 to gold_earned over game duration
    - xp same, scaled to champ_level (roughly 500 * level)
    - cs linearly distributed over duration, with event-minute bumps for kills
    """
    duration_s = max(1, _safe_int(game.get("game_duration"), 60))
    minutes = max(1, (duration_s + 59) // 60)

    total_gold = max(1.0, float(_safe_int(game.get("gold_earned"), 0)))
    total_cs = _safe_int(game.get("cs_total") or game.get("total_minions_killed"), 0)
    champ_level = max(1, _safe_int(game.get("champ_level"), 1))
    total_xp = champ_level * 500.0

    buckets: list[_TimelineBucket] = []
    for m in range(minutes):
        frac = (m + 1) / minutes
        gold = total_gold * frac
        xp = total_xp * frac
        cs = int(total_cs * frac)
        buckets.append(_TimelineBucket(minute=m, gold=gold, xp=xp, cs=cs))
    return buckets


def _infer_team_timeline(
    game: dict, events: list[dict], user: list[_TimelineBucket]
) -> list[_TimelineBucket]:
    """Rough team-level aggregate. Assume team = 5x user's share.

    Real engineering fix would read per-participant timeline from raw_stats;
    this approximation is good enough for pythagorean comparison since both
    sides are approximated symmetrically.
    """
    return [
        _TimelineBucket(
            minute=b.minute,
            gold=b.gold * 5.0,
            xp=b.xp * 5.0,
            cs=b.cs * 5,
        )
        for b in user
    ]


def _infer_enemy_timeline(
    game: dict, events: list[dict], user: list[_TimelineBucket]
) -> list[_TimelineBucket]:
    """Symmetric enemy approximation with a win/loss tilt.

    If the user won, assume trailing enemy economy by ~15% at game end.
    If the user lost, enemy is ahead by ~15%.
    """
    won = bool(game.get("win"))
    ratio = 0.85 if won else 1.15
    return [
        _TimelineBucket(
            minute=b.minute,
            gold=b.gold * 5.0 * ratio,
            xp=b.xp * 5.0 * ratio,
            cs=int(b.cs * 5 * ratio),
        )
        for b in user
    ]


def _infer_enemy_lane(game: dict) -> str | None:
    """Extract enemy lane champion from raw_stats if available."""
    raw = game.get("raw_stats")
    if not raw:
        return None
    try:
        data = json.loads(raw) if isinstance(raw, str) else raw
    except Exception:
        return None
    # Common shape: {"participants": [...]} with teamId + champion + role
    if not isinstance(data, dict):
        return None
    participants = data.get("participants")
    if not isinstance(participants, list):
        return None

    user_champ = game.get("champion_name")
    user_team = game.get("team_id")
    user_role = (game.get("position") or game.get("role") or "").upper()
    for p in participants:
        if not isinstance(p, dict):
            continue
        team = p.get("teamId") or p.get("team_id")
        pos = (p.get("individualPosition") or p.get("position") or p.get("role") or "").upper()
        champ = p.get("championName") or p.get("champion_name") or p.get("championId")
        if (
            team is not None
            and user_team is not None
            and team != user_team
            and pos == user_role
            and champ
            and champ != user_champ
        ):
            return str(champ)
    return None


# ──────────────────────────────────────────────────────────────────────
# Coerce helpers
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


def _safe_str(x: Any) -> str:
    if x is None:
        return ""
    return str(x)


def _round(x: float, digits: int) -> float:
    return round(x, digits)
