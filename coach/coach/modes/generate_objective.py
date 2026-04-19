"""Generate learning objective candidates from the user's actual patterns.

Uses the same grounding discipline as ask mode — proposals must trace to
concepts, signals, or specific games. No invented objectives if data is
too thin.
"""

from __future__ import annotations

import json
import logging
import time
from pathlib import Path
from typing import Any

try:
    from json_repair import repair_json
except Exception:
    repair_json = None  # type: ignore

from coach.config import load_config
from coach.db import read_core
from coach.modes.ask import (
    _fetch_active_objectives,
    _fetch_review_text,
    _fetch_top_concepts,
    _fetch_top_signals,
)
from coach.providers import get_provider
from coach.schemas import GenerateObjectiveResponse, LLMMessage, LLMRequest, ObjectiveProposal
from coach.text_sanitizer import sanitize

logger = logging.getLogger(__name__)

_PROMPT_PATH = Path(__file__).parent.parent / "prompts" / "generate_objective.md"
_PROMPT_CACHE: str | None = None


def _load_prompt() -> str:
    global _PROMPT_CACHE
    if _PROMPT_CACHE is None:
        _PROMPT_CACHE = _PROMPT_PATH.read_text(encoding="utf-8")
    return _PROMPT_CACHE


async def run_generate_objective(since: int | None = None) -> GenerateObjectiveResponse:
    context = _retrieve_context(since)

    cfg = load_config()
    provider = get_provider()
    model = _pick_model(cfg)

    prompt = _load_prompt().replace(
        "{{context}}", json.dumps(context, indent=2, default=str)[:60000]
    )

    response = await provider.complete(
        LLMRequest(
            messages=[LLMMessage(role="user", content=prompt)],
            model=model,
            temperature=0.2,
            # Enough headroom for 3 proposals with multi-sentence
            # rationales. Previously 1500, which silently truncated the
            # rationale mid-sentence on longer outputs. json_repair
            # couldn't recover the tail, so the UI showed garbage like
            # "very strong correlation (".
            # Big enough for 3 full proposals with multi-sentence
            # rationales even after the model self-validates against
            # the "no stats-as-goals" rubric.
            max_tokens=6000,
            response_format="json" if provider.supports_json_mode() else None,
        )
    )

    proposals = _parse_proposals(response.text)

    return GenerateObjectiveResponse(
        proposals=proposals,
        model=response.model,
        provider=response.provider,
        latency_ms=response.latency_ms,
    )


def _retrieve_context(since: int | None) -> dict[str, Any]:
    # Pull recent games' reviews (the noisiest, highest-signal data for
    # objective-worthy patterns). Limit to last 25.
    with read_core() as conn:
        if since is not None:
            rows = conn.execute(
                "SELECT id FROM games WHERE COALESCE(timestamp, 0) >= ? ORDER BY id DESC LIMIT 25",
                (since,),
            ).fetchall()
        else:
            rows = conn.execute(
                "SELECT id FROM games ORDER BY id DESC LIMIT 25"
            ).fetchall()
        game_ids = [int(r["id"]) for r in rows]

    return {
        "recent_reviews": _fetch_review_text(game_ids),
        "concepts": _fetch_top_concepts(limit=30),
        "signals": _fetch_top_signals(limit=15),
        "current_objectives": _fetch_active_objectives(),
    }


def _parse_proposals(text: str) -> list[ObjectiveProposal]:
    if not text:
        return []
    stripped = text.strip()
    if stripped.startswith("```"):
        lines = [ln for ln in stripped.splitlines() if not ln.startswith("```")]
        stripped = "\n".join(lines).strip()

    try:
        parsed = json.loads(stripped)
    except Exception:
        if repair_json is None:
            return []
        try:
            repaired = repair_json(stripped)
            parsed = json.loads(repaired) if isinstance(repaired, str) else repaired
        except Exception:
            return []

    if not isinstance(parsed, dict):
        return []

    raw_proposals = parsed.get("proposals", [])
    if not isinstance(raw_proposals, list):
        return []

    out: list[ObjectiveProposal] = []
    for p in raw_proposals:
        if not isinstance(p, dict):
            continue
        title = str(p.get("title", "")).strip()
        if not title:
            continue
        rationale = str(p.get("rationale", "")).strip()
        trigger_raw = p.get("trigger")
        trigger = str(trigger_raw).strip() if trigger_raw else None
        success_raw = p.get("success_criteria")
        success_criteria = str(success_raw).strip() if success_raw else None
        confidence = float(p.get("confidence", 0.0))
        replaces = p.get("replaces_objective_id")
        replaces_id = int(replaces) if isinstance(replaces, int) else None
        out.append(
            ObjectiveProposal(
                # Sanitize every user-visible string: strips quoted
                # phrases like 'good mental' and stale [game #N]
                # references the prompt rules keep leaking.
                title=sanitize(title),
                rationale=sanitize(rationale),
                trigger=sanitize(trigger) if trigger else None,
                success_criteria=sanitize(success_criteria) if success_criteria else None,
                replaces_objective_id=replaces_id,
                confidence=max(0.0, min(1.0, confidence)),
            )
        )
    return out


def _pick_model(cfg) -> str:
    if cfg.provider == "ollama":
        return cfg.ollama.model
    if cfg.provider == "google_ai":
        return cfg.google_ai.model
    return cfg.openrouter.model
