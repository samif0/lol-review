"""Extract structured concepts from review text using the configured LLM.

Plan §7 Phase 2 task 2. Parses JSON with a json-repair fallback so malformed
LLM output is tolerated (plan §9 risk mitigation).
"""

from __future__ import annotations

import json
import logging
import time
from pathlib import Path
from typing import Any

try:
    from json_repair import repair_json
except Exception:  # pragma: no cover
    repair_json = None  # type: ignore

from coach.config import load_config
from coach.db import (
    fetch_game_row,
    fetch_session_log_row,
    list_game_ids,
    read_core,
    write_coach,
)
from coach.providers import get_provider
from coach.schemas import LLMMessage, LLMRequest

logger = logging.getLogger(__name__)

_PROMPT_PATH = Path(__file__).parent.parent / "prompts" / "concept_extraction.md"
_PROMPT_CACHE: str | None = None


def _load_prompt() -> str:
    global _PROMPT_CACHE
    if _PROMPT_CACHE is None:
        _PROMPT_CACHE = _PROMPT_PATH.read_text(encoding="utf-8")
    return _PROMPT_CACHE


async def extract_for_game(game_id: int) -> int:
    """Extract concepts from every review field for one game. Returns count."""
    game = fetch_game_row(game_id)
    if game is None:
        return 0

    fields: list[tuple[str, str]] = []
    for field_name in ("mistakes", "went_well", "focus_next", "review_notes", "spotted_problems"):
        value = game.get(field_name)
        if value and str(value).strip():
            fields.append((field_name, str(value).strip()))

    session = fetch_session_log_row(game_id)
    if session and session.get("improvement_note"):
        fields.append(("improvement_note", str(session["improvement_note"]).strip()))

    matchup_notes = _fetch_matchup_notes_for_game(game_id)
    for note in matchup_notes:
        fields.append(("matchup_note", note))

    if not fields:
        return 0

    provider = get_provider()
    cfg = load_config()
    model = (
        cfg.ollama.model
        if cfg.provider == "ollama"
        else cfg.google_ai.model
        if cfg.provider == "google_ai"
        else cfg.openrouter.model
    )

    rows: list[tuple] = []
    now_ts = int(time.time())
    prompt_template = _load_prompt()

    for source_field, text in fields:
        filled = prompt_template.replace("{{field}}", source_field).replace("{{text}}", text)
        try:
            response = await provider.complete(
                LLMRequest(
                    messages=[LLMMessage(role="user", content=filled)],
                    model=model,
                    temperature=0.2,
                    max_tokens=600,
                    response_format="json" if provider.supports_json_mode() else None,
                )
            )
        except Exception:
            logger.exception("Provider call failed for concept extraction game=%d field=%s",
                             game_id, source_field)
            continue

        concepts = _parse_concepts_json(response.text)
        for c in concepts:
            concept_raw = str(c.get("concept", "")).strip()
            if not concept_raw:
                continue
            polarity = str(c.get("polarity", "neutral")).strip().lower()
            if polarity not in ("positive", "negative", "neutral"):
                polarity = "neutral"
            span = str(c.get("span", "")).strip() or text[:120]
            rows.append(
                (
                    game_id,
                    source_field,
                    concept_raw,
                    None,  # concept_canonical — filled by clusterer
                    polarity,
                    span,
                    None,  # cluster_id — filled by clusterer
                    now_ts,
                )
            )

    if not rows:
        return 0

    with write_coach() as conn:
        # Remove prior extractions for this game to keep row counts stable on re-run.
        conn.execute("DELETE FROM review_concepts WHERE game_id = ?", (game_id,))
        conn.executemany(
            """
            INSERT INTO review_concepts
                (game_id, source_field, concept_raw, concept_canonical,
                 polarity, span, cluster_id, created_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            rows,
        )

    return len(rows)


async def extract_all(since: int | None = None) -> dict[str, int]:
    ids = list_game_ids(since=since)
    attempted = 0
    succeeded = 0
    skipped = 0
    failed = 0
    for gid in ids:
        attempted += 1
        try:
            n = await extract_for_game(gid)
            if n == 0:
                skipped += 1
            else:
                succeeded += 1
        except Exception:
            logger.exception("extract_for_game failed for game %d", gid)
            failed += 1
    return {"attempted": attempted, "succeeded": succeeded, "skipped": skipped, "failed": failed}


def _parse_concepts_json(text: str) -> list[dict[str, Any]]:
    """Lenient JSON parse. Accepts text with wrapping code fences."""
    if not text:
        return []
    stripped = text.strip()
    if stripped.startswith("```"):
        # Strip ```json fences
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

    if isinstance(parsed, dict):
        # Some models wrap in { "concepts": [...] }
        for key in ("concepts", "items", "data"):
            if isinstance(parsed.get(key), list):
                parsed = parsed[key]
                break

    if not isinstance(parsed, list):
        return []
    return [p for p in parsed if isinstance(p, dict)]


def _fetch_matchup_notes_for_game(game_id: int) -> list[str]:
    game = fetch_game_row(game_id)
    if game is None:
        return []
    champion = game.get("champion_name")
    if not champion:
        return []

    with read_core() as conn:
        rows = conn.execute(
            """
            SELECT note FROM matchup_notes
            WHERE champion = ? AND (enemy IS NULL OR enemy = '' OR enemy = enemy)
            ORDER BY id DESC LIMIT 5
            """,
            (champion,),
        ).fetchall()
    return [r["note"] for r in rows if r["note"] and str(r["note"]).strip()]
