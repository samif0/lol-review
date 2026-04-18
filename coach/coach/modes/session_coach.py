"""Session coach mode (Phase 5b)."""

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

_PROMPT_PATH = Path(__file__).parent.parent / "prompts" / "session_coach.md"
_PROMPT_CACHE: str | None = None

SESSION_HOURS_DEFAULT = 6
SESSION_MAX_GAP_MIN = 60


def _load_prompt() -> str:
    global _PROMPT_CACHE
    if _PROMPT_CACHE is None:
        _PROMPT_CACHE = _PROMPT_PATH.read_text(encoding="utf-8")
    return _PROMPT_CACHE


async def run_session(since: int | None, until: int | None) -> CoachResponse:
    if until is None:
        until = int(time.time())
    if since is None:
        since = until - SESSION_HOURS_DEFAULT * 3600

    with read_core() as conn:
        summaries_rows = conn.execute(
            """
            SELECT gs.game_id, gs.compacted_json
            FROM game_summary gs
            JOIN games g ON g.id = gs.game_id
            WHERE COALESCE(g.timestamp, g.game_duration, 0) >= ?
              AND COALESCE(g.timestamp, g.game_duration, 0) <= ?
            ORDER BY g.timestamp
            """,
            (since, until),
        ).fetchall()

        session_rows = conn.execute(
            """
            SELECT game_id, mental_rating, improvement_note, rule_broken, timestamp
            FROM session_log
            WHERE timestamp BETWEEN ? AND ?
            ORDER BY timestamp
            """,
            (since, until),
        ).fetchall()

        concept_rows = conn.execute(
            "SELECT concept_canonical, frequency, positive_count, negative_count, rank "
            "FROM user_concept_profile ORDER BY rank ASC LIMIT 15"
        ).fetchall()

        signal_rows = conn.execute(
            """
            SELECT feature_name, rank, spearman_rho, partial_rho_mental_controlled,
                   user_baseline_win_avg, user_baseline_loss_avg
            FROM user_signal_ranking WHERE stable = 1 ORDER BY rank ASC LIMIT 10
            """
        ).fetchall()

    summaries = [
        {"game_id": r["game_id"], "summary": json.loads(r["compacted_json"])}
        for r in summaries_rows
    ]
    session_logs = [dict(r) for r in session_rows]
    top_concepts = [dict(r) for r in concept_rows]
    top_signals = [dict(r) for r in signal_rows]

    prompt = (
        _load_prompt()
        .replace("{{summaries}}", json.dumps(summaries, indent=2, default=str)[:20000])
        .replace("{{session_logs}}", json.dumps(session_logs, indent=2, default=str))
        .replace("{{top_concepts}}", json.dumps(top_concepts, indent=2))
        .replace("{{top_signals}}", json.dumps(top_signals, indent=2))
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
            max_tokens=1500,
        )
    )

    coach_session_id = _persist_session(
        mode="session",
        scope={"since": since, "until": until},
        context={"summaries_count": len(summaries), "session_logs_count": len(session_logs)},
        response_text=response.text,
        response_json=None,
        model_name=response.model,
        provider=response.provider,
        latency_ms=response.latency_ms,
    )

    return CoachResponse(
        coach_session_id=coach_session_id,
        mode="session",
        response_text=response.text,
        response_json=None,
        model=response.model,
        provider=response.provider,
        latency_ms=response.latency_ms,
    )
