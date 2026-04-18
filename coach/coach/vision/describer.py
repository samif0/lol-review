"""Call the vision provider on extracted frames and persist descriptions."""

from __future__ import annotations

import logging
import time
from pathlib import Path

from coach.config import load_config
from coach.db import write_coach
from coach.providers import get_vision_provider
from coach.schemas import LLMMessage, LLMRequest
from coach.vision.frame_sampler import VodUnavailableError, sample_frames

logger = logging.getLogger(__name__)

_PROMPT_PATH = Path(__file__).parent.parent / "prompts" / "frame_description.md"
_PROMPT_CACHE: str | None = None


def _load_prompt() -> str:
    global _PROMPT_CACHE
    if _PROMPT_CACHE is None:
        _PROMPT_CACHE = _PROMPT_PATH.read_text(encoding="utf-8")
    return _PROMPT_CACHE


async def describe_bookmark(bookmark_id: int) -> int:
    """Sample frames, run vision describer on each, persist. Returns count."""
    try:
        frames = sample_frames(bookmark_id)
    except VodUnavailableError as exc:
        logger.warning("Vision describe skipped for bookmark %d: %s", bookmark_id, exc)
        return 0

    if not frames:
        return 0

    cfg = load_config()
    provider = get_vision_provider()
    if not provider.supports_vision():
        logger.warning("Configured vision provider does not support vision; skipping")
        return 0

    model = (
        cfg.ollama.vision_model
        if provider.name == "ollama"
        else cfg.google_ai.model
        if provider.name == "google_ai"
        else cfg.openrouter.model
    )

    prompt_template = _load_prompt()
    count = 0

    for frame_path, ts_ms in frames:
        try:
            img_bytes = frame_path.read_bytes()
        except Exception:
            logger.exception("Failed to read frame %s", frame_path)
            continue

        prompt = prompt_template.replace("{{timestamp_s}}", f"{ts_ms / 1000:.1f}")

        try:
            response = await provider.complete(
                LLMRequest(
                    messages=[LLMMessage(role="user", content=prompt)],
                    model=model,
                    temperature=0.1,
                    max_tokens=600,
                    response_format="json" if provider.supports_json_mode() else None,
                    images=[img_bytes],
                )
            )
        except Exception:
            logger.exception("Vision call failed for frame %s", frame_path)
            continue

        with write_coach() as conn:
            conn.execute(
                """
                INSERT INTO clip_frame_descriptions
                    (bookmark_id, frame_timestamp_ms, frame_path, description_text,
                     model_name, created_at)
                VALUES (?, ?, ?, ?, ?, ?)
                ON CONFLICT(bookmark_id, frame_timestamp_ms) DO UPDATE SET
                    description_text = excluded.description_text,
                    model_name = excluded.model_name,
                    created_at = excluded.created_at
                """,
                (
                    bookmark_id,
                    ts_ms,
                    str(frame_path),
                    response.text,
                    response.model,
                    int(time.time()),
                ),
            )
        count += 1

    return count
