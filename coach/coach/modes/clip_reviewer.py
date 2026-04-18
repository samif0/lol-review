"""Clip review mode (Phase 5b)."""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

import numpy as np

from coach.concepts.embedder import embed
from coach.config import load_config
from coach.db import fetch_game_row, fetch_matchup_note, fetch_vod_bookmark, read_core
from coach.modes.post_game import _persist_session
from coach.providers import get_provider
from coach.schemas import CoachResponse, LLMMessage, LLMRequest
from coach.summaries.compactor import load_summary

logger = logging.getLogger(__name__)

_PROMPT_PATH = Path(__file__).parent.parent / "prompts" / "clip_reviewer.md"
_PROMPT_CACHE: str | None = None


def _load_prompt() -> str:
    global _PROMPT_CACHE
    if _PROMPT_CACHE is None:
        _PROMPT_CACHE = _PROMPT_PATH.read_text(encoding="utf-8")
    return _PROMPT_CACHE


async def run_clip_review(bookmark_id: int) -> CoachResponse:
    bookmark = fetch_vod_bookmark(bookmark_id)
    if bookmark is None:
        raise ValueError(f"No bookmark {bookmark_id}")

    game_id = int(bookmark["game_id"])
    game = fetch_game_row(game_id) or {}

    # Frame descriptions
    with read_core() as conn:
        frame_rows = conn.execute(
            """
            SELECT frame_timestamp_ms, description_text
            FROM clip_frame_descriptions
            WHERE bookmark_id = ?
            ORDER BY frame_timestamp_ms
            """,
            (bookmark_id,),
        ).fetchall()
    frames_section = [
        {
            "t_ms": r["frame_timestamp_ms"],
            "desc": _maybe_json(r["description_text"]),
        }
        for r in frame_rows
    ]

    # Narrow match summary to the minute window around the bookmark
    summary_full = load_summary(game_id)
    summary_dict = summary_full.model_dump() if summary_full else {}
    summary_window = _narrow_summary(summary_dict.get("compacted_json", {}), bookmark)

    # Matchup note
    enemy = summary_dict.get("compacted_json", {}).get("match_overview", {}).get("enemy_lane_champion")
    matchup_note = ""
    if game.get("champion_name") and enemy:
        m = fetch_matchup_note(game["champion_name"], enemy)
        if m:
            matchup_note = m.get("note") or ""

    # Relevant concepts: top 10 by semantic similarity to frame descriptions
    relevant_concepts = _top_relevant_concepts(frames_section)

    prompt = (
        _load_prompt()
        .replace("{{match_summary_window}}", json.dumps(summary_window, indent=2))
        .replace("{{frame_descriptions}}", json.dumps(frames_section, indent=2))
        .replace("{{matchup_note}}", matchup_note)
        .replace("{{relevant_concepts}}", json.dumps(relevant_concepts, indent=2))
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
            max_tokens=500,
        )
    )

    context = {
        "bookmark_id": bookmark_id,
        "game_id": game_id,
        "frames": frames_section,
        "summary_window": summary_window,
        "matchup_note": matchup_note,
        "relevant_concepts": relevant_concepts,
    }

    coach_session_id = _persist_session(
        mode="clip_review",
        scope={"bookmark_id": bookmark_id},
        context=context,
        response_text=response.text,
        response_json=None,
        model_name=response.model,
        provider=response.provider,
        latency_ms=response.latency_ms,
    )

    return CoachResponse(
        coach_session_id=coach_session_id,
        mode="clip_review",
        response_text=response.text,
        response_json=None,
        model=response.model,
        provider=response.provider,
        latency_ms=response.latency_ms,
    )


def _maybe_json(text: str) -> Any:
    try:
        return json.loads(text)
    except Exception:
        return text


def _narrow_summary(compacted: dict[str, Any], bookmark: dict[str, Any]) -> dict[str, Any]:
    """Trim the summary to minute buckets around the bookmark timestamp."""
    start = int(bookmark.get("clip_start_s") or bookmark.get("game_time_s") or 0)
    end = int(bookmark.get("clip_end_s") or start + 10)
    center_min = (start + end) // 2 // 60

    narrow = dict(compacted)
    timeline = narrow.get("timeline_view", {})
    buckets = timeline.get("buckets_minutes", 0)
    if buckets:
        low = max(0, center_min - 2)
        high = min(buckets, center_min + 3)

        def _slice(seq):
            return seq[low:high] if isinstance(seq, list) else seq

        user = timeline.get("user", {})
        narrow["timeline_view"] = {
            "window_minutes": [low, high],
            "user": {k: _slice(v) for k, v in user.items()},
            "advantage_gold": _slice(timeline.get("advantage_gold", [])),
            "advantage_xp": _slice(timeline.get("advantage_xp", [])),
            "win_probability": _slice(timeline.get("win_probability", [])),
        }

    return narrow


def _top_relevant_concepts(frames_section: list[dict]) -> list[dict]:
    with read_core() as conn:
        rows = conn.execute(
            """
            SELECT concept_canonical, frequency, positive_count, negative_count, rank
            FROM user_concept_profile ORDER BY rank ASC LIMIT 50
            """
        ).fetchall()
    concepts = [dict(r) for r in rows]
    if not concepts or not frames_section:
        return concepts[:10]

    # Embed concepts + a concatenated frame descriptor, rank by cosine sim.
    frame_summary = " | ".join(
        json.dumps(f["desc"]) if isinstance(f["desc"], dict) else str(f["desc"])
        for f in frames_section
    )
    try:
        concept_vecs = embed([c["concept_canonical"] for c in concepts])
        frame_vec = embed([frame_summary])[0]
        sims = concept_vecs @ frame_vec  # normalized, so dot == cosine
        for c, s in zip(concepts, sims):
            c["_sim"] = float(s)
        concepts.sort(key=lambda c: c["_sim"], reverse=True)
        for c in concepts:
            c.pop("_sim", None)
    except Exception:
        logger.exception("Concept similarity ranking failed; using rank-order")

    return concepts[:10]
