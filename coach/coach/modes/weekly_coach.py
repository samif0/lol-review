"""Weekly coach mode (Phase 5b, GROW frame)."""

from __future__ import annotations

import json
import logging
import time
from pathlib import Path

from coach.config import load_config
from coach.db import read_core
from coach.modes.post_game import _persist_session
from coach.providers import get_provider
from coach.schemas import CoachResponse, LLMMessage, LLMRequest

logger = logging.getLogger(__name__)

_PROMPT_PATH = Path(__file__).parent.parent / "prompts" / "weekly_coach.md"
_PROMPT_CACHE: str | None = None


def _load_prompt() -> str:
    global _PROMPT_CACHE
    if _PROMPT_CACHE is None:
        _PROMPT_CACHE = _PROMPT_PATH.read_text(encoding="utf-8")
    return _PROMPT_CACHE


async def run_weekly(since: int | None, until: int | None) -> CoachResponse:
    if until is None:
        until = int(time.time())
    if since is None:
        since = until - 7 * 86400

    with read_core() as conn:
        week_games = conn.execute(
            """
            SELECT champion_name, win FROM games
            WHERE COALESCE(timestamp, 0) BETWEEN ? AND ? AND win IS NOT NULL
            """,
            (since, until),
        ).fetchall()

        objectives_rows = conn.execute(
            """
            SELECT * FROM objectives
            """
        ).fetchall() if _table_exists(conn, "objectives") else []

        adherence_rows = conn.execute(
            """
            SELECT date, SUM(rule_broken) AS broken FROM session_log
            GROUP BY date ORDER BY date DESC LIMIT 30
            """
        ).fetchall() if _table_exists(conn, "session_log") else []

        concepts = conn.execute(
            "SELECT * FROM user_concept_profile ORDER BY rank ASC LIMIT 50"
        ).fetchall()
        signals = conn.execute(
            "SELECT * FROM user_signal_ranking ORDER BY rank ASC"
        ).fetchall()

    # Aggregate week stats
    total = len(week_games)
    wins = sum(1 for r in week_games if int(r["win"]) == 1)
    champ_counts: dict[str, int] = {}
    champ_wins: dict[str, int] = {}
    for r in week_games:
        name = r["champion_name"] or "Unknown"
        champ_counts[name] = champ_counts.get(name, 0) + 1
        if int(r["win"]) == 1:
            champ_wins[name] = champ_wins.get(name, 0) + 1

    week_stats = {
        "games": total,
        "wins": wins,
        "losses": total - wins,
        "per_champion": [
            {
                "champion": name,
                "games": count,
                "winrate": round(champ_wins.get(name, 0) / count, 3) if count else 0.0,
            }
            for name, count in sorted(champ_counts.items(), key=lambda x: -x[1])
        ],
    }

    # Adherence streak: count days descending from today until first broken day.
    adherence_streak = 0
    for row in adherence_rows:
        if int(row["broken"]) == 0:
            adherence_streak += 1
        else:
            break

    prompt = (
        _load_prompt()
        .replace("{{week_stats}}", json.dumps(week_stats, indent=2))
        .replace("{{objectives_progress}}", json.dumps([dict(r) for r in objectives_rows], indent=2, default=str))
        .replace("{{rules_adherence_streak}}", str(adherence_streak))
        .replace("{{concept_profile}}", json.dumps([dict(r) for r in concepts], indent=2, default=str))
        .replace("{{signal_ranking}}", json.dumps([dict(r) for r in signals], indent=2, default=str))
    )

    cfg = load_config()
    provider = get_provider()
    model = (
        cfg.ollama.model if cfg.provider == "ollama"
        else cfg.google_ai.model if cfg.provider == "google_ai"
        else cfg.openrouter.model
    )

    response = await provider.complete(
        LLMRequest(
            messages=[LLMMessage(role="user", content=prompt)],
            model=model,
            temperature=0.2,
            max_tokens=2000,
        )
    )

    coach_session_id = _persist_session(
        mode="weekly",
        scope={"since": since, "until": until},
        context={
            "week_stats": week_stats,
            "adherence_streak": adherence_streak,
            "objective_count": len(objectives_rows),
        },
        response_text=response.text,
        response_json=None,
        model_name=response.model,
        provider=response.provider,
        latency_ms=response.latency_ms,
    )

    return CoachResponse(
        coach_session_id=coach_session_id,
        mode="weekly",
        response_text=response.text,
        response_json=None,
        model=response.model,
        provider=response.provider,
        latency_ms=response.latency_ms,
    )


def _table_exists(conn, name: str) -> bool:
    r = conn.execute(
        "SELECT 1 FROM sqlite_master WHERE type='table' AND name = ?", (name,)
    ).fetchone()
    return r is not None
