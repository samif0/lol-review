"""Post-game coach mode (Phase 5a).

Context assembly per plan §7 Phase 5a task 1. Output: JSON mistakes/went_well/focus_next.
"""

from __future__ import annotations

import json
import logging
import time
from pathlib import Path
from typing import Any

try:
    import Levenshtein  # python-Levenshtein
except Exception:  # pragma: no cover
    Levenshtein = None  # type: ignore

from coach.config import load_config
from coach.db import fetch_game_row, fetch_matchup_note, fetch_session_log_row, read_core, write_coach
from coach.providers import get_provider
from coach.schemas import CoachResponse, LLMMessage, LLMRequest
from coach.summaries.compactor import load_summary

logger = logging.getLogger(__name__)

_PROMPT_PATH = Path(__file__).parent.parent / "prompts" / "post_game.md"
_PROMPT_CACHE: str | None = None


def _load_prompt() -> str:
    global _PROMPT_CACHE
    if _PROMPT_CACHE is None:
        _PROMPT_CACHE = _PROMPT_PATH.read_text(encoding="utf-8")
    return _PROMPT_CACHE


async def run_post_game(game_id: int) -> CoachResponse:
    ctx = _assemble_context(game_id)
    prompt = _fill_prompt(ctx)

    cfg = load_config()
    provider = get_provider()
    model = _pick_model(cfg)

    response = await provider.complete(
        LLMRequest(
            messages=[LLMMessage(role="user", content=prompt)],
            model=model,
            temperature=0.2,
            max_tokens=1200,
            response_format="json" if provider.supports_json_mode() else None,
        )
    )

    response_json = _parse_response_json(response.text)

    coach_session_id = _persist_session(
        mode="post_game",
        scope={"game_id": game_id},
        context=ctx,
        response_text=response.text,
        response_json=response_json,
        model_name=response.model,
        provider=response.provider,
        latency_ms=response.latency_ms,
    )

    return CoachResponse(
        coach_session_id=coach_session_id,
        mode="post_game",
        response_text=response.text,
        response_json=response_json,
        model=response.model,
        provider=response.provider,
        latency_ms=response.latency_ms,
    )


def _assemble_context(game_id: int) -> dict[str, Any]:
    game = fetch_game_row(game_id)
    summary = load_summary(game_id)
    summary_dict = summary.model_dump() if summary is not None else {}
    session = fetch_session_log_row(game_id) or {}

    # Top 15 concepts
    with read_core() as conn:
        concept_rows = conn.execute(
            """
            SELECT concept_canonical, frequency, positive_count, negative_count,
                   neutral_count, rank
            FROM user_concept_profile
            ORDER BY rank ASC LIMIT 15
            """
        ).fetchall()
        top_concepts = [dict(r) for r in concept_rows]

        # Top 10 signals with this game's values
        signal_rows = conn.execute(
            """
            SELECT usr.feature_name, usr.rank, usr.user_baseline_win_avg,
                   usr.user_baseline_loss_avg, usr.spearman_rho,
                   usr.partial_rho_mental_controlled,
                   fv.value AS current_value
            FROM user_signal_ranking usr
            LEFT JOIN feature_values fv ON fv.feature_name = usr.feature_name
                                         AND fv.game_id = ?
            WHERE usr.stable = 1
            ORDER BY usr.rank ASC LIMIT 10
            """,
            (game_id,),
        ).fetchall()
        top_signals = []
        for r in signal_rows:
            row = dict(r)
            cur = row.get("current_value")
            win_avg = row.get("user_baseline_win_avg")
            loss_avg = row.get("user_baseline_loss_avg")
            annotation = None
            if cur is not None and win_avg is not None and loss_avg is not None:
                # Annotate whether this value is closer to winning or losing baseline.
                closer_to_win = abs(cur - win_avg) < abs(cur - loss_avg)
                annotation = "closer_to_win_baseline" if closer_to_win else "closer_to_loss_baseline"
            row["annotation"] = annotation
            top_signals.append(row)

        # Previous focus_next
        prev_row = conn.execute(
            """
            SELECT focus_next FROM games
            WHERE id < ? AND focus_next IS NOT NULL AND focus_next != ''
            ORDER BY id DESC LIMIT 1
            """,
            (game_id,),
        ).fetchone()
        previous_focus_next = prev_row["focus_next"] if prev_row else ""

    matchup_note = None
    if game:
        enemy = summary_dict.get("compacted_json", {}).get("match_overview", {}).get("enemy_lane_champion")
        champion = game.get("champion_name")
        if champion and enemy:
            row = fetch_matchup_note(champion, enemy)
            if row:
                matchup_note = row.get("note")

    return {
        "game_id": game_id,
        "match_summary": summary_dict.get("compacted_json", {"error": "no summary built yet"}),
        "session_log": {
            k: session.get(k) for k in ("mental_rating", "improvement_note", "rule_broken", "date")
        },
        "top_concepts": top_concepts,
        "top_signals": top_signals,
        "matchup_note": matchup_note or "",
        "previous_focus_next": previous_focus_next,
    }


def _fill_prompt(ctx: dict[str, Any]) -> str:
    tpl = _load_prompt()
    return (
        tpl.replace("{{match_summary}}", json.dumps(ctx["match_summary"], indent=2))
        .replace("{{session_log}}", json.dumps(ctx["session_log"], indent=2))
        .replace("{{top_concepts}}", json.dumps(ctx["top_concepts"], indent=2))
        .replace("{{top_signals}}", json.dumps(ctx["top_signals"], indent=2))
        .replace("{{matchup_note}}", ctx["matchup_note"])
        .replace("{{previous_focus_next}}", ctx["previous_focus_next"])
    )


def _pick_model(cfg) -> str:
    if cfg.provider == "ollama":
        return cfg.ollama.model
    if cfg.provider == "google_ai":
        return cfg.google_ai.model
    return cfg.openrouter.model


def _parse_response_json(text: str) -> dict[str, Any] | None:
    if not text:
        return None
    stripped = text.strip()
    if stripped.startswith("```"):
        lines = [ln for ln in stripped.splitlines() if not ln.startswith("```")]
        stripped = "\n".join(lines).strip()
    try:
        parsed = json.loads(stripped)
    except Exception:
        try:
            from json_repair import repair_json

            repaired = repair_json(stripped)
            parsed = json.loads(repaired) if isinstance(repaired, str) else repaired
        except Exception:
            return None
    if isinstance(parsed, dict):
        return parsed
    return None


def _persist_session(
    *,
    mode: str,
    scope: dict[str, Any],
    context: dict[str, Any],
    response_text: str,
    response_json: dict[str, Any] | None,
    model_name: str,
    provider: str,
    latency_ms: int,
) -> int:
    with write_coach() as conn:
        cursor = conn.execute(
            """
            INSERT INTO coach_sessions
                (mode, scope_json, context_json, response_text, response_json,
                 model_name, provider, latency_ms, created_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                mode,
                json.dumps(scope),
                json.dumps(context),
                response_text,
                json.dumps(response_json) if response_json else None,
                model_name,
                provider,
                latency_ms,
                int(time.time()),
            ),
        )
        return int(cursor.lastrowid)


def log_edit(coach_session_id: int, edited_text: str) -> int:
    """Compute Levenshtein distance vs original response_text, persist."""
    with read_core() as conn:
        row = conn.execute(
            "SELECT response_text FROM coach_sessions WHERE id = ?", (coach_session_id,)
        ).fetchone()
    if row is None:
        raise ValueError(f"coach_session {coach_session_id} not found")

    original = row["response_text"] or ""
    if Levenshtein is not None:
        distance = int(Levenshtein.distance(original, edited_text))
    else:
        # Fallback approximation: absolute char count delta
        distance = abs(len(original) - len(edited_text))

    with write_coach() as conn:
        conn.execute(
            """
            INSERT INTO coach_response_edits (coach_session_id, edited_text, edit_distance, created_at)
            VALUES (?, ?, ?, strftime('%s','now'))
            ON CONFLICT(coach_session_id) DO UPDATE SET
                edited_text = excluded.edited_text,
                edit_distance = excluded.edit_distance,
                created_at = excluded.created_at
            """,
            (coach_session_id, edited_text, distance),
        )
    return distance
