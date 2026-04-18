"""FastAPI app for the coach sidecar.

Usage (dev):
    python -m coach.main

Wires up every endpoint documented in COACH_PLAN.md §5.
"""

from __future__ import annotations

import argparse
import logging
import socket
import sys
import time
from contextlib import asynccontextmanager
from typing import Any

import uvicorn
from fastapi import FastAPI, HTTPException
from fastapi.responses import JSONResponse

from coach import __version__
from coach.config import load_config, log_path, update_config
from coach.db import ensure_migrations_applied
from coach.providers import get_provider
from coach.schemas import (
    AskRequest,
    AskResponse,
    BuildAllSummariesResponse,
    BuildSummaryResponse,
    ClipReviewCoachRequest,
    CoachResponse,
    ComputeFeaturesResponse,
    ConceptProfileResponse,
    ConfigUpdateRequest,
    ExtractConceptsResponse,
    GenerateObjectiveRequest,
    GenerateObjectiveResponse,
    GetSummaryResponse,
    HealthResponse,
    LLMMessage,
    LLMRequest,
    LogEditRequest,
    LogEditResponse,
    PostGameCoachRequest,
    SessionCoachRequest,
    SignalRankingResponse,
    TestPromptRequest,
    TestPromptResponse,
    VisionDescribeResponse,
    WeeklyCoachRequest,
)

logger = logging.getLogger("coach")

_started_at: float = 0.0


def _configure_logging() -> None:
    log_file = log_path()
    log_file.parent.mkdir(parents=True, exist_ok=True)
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
        handlers=[
            logging.FileHandler(log_file, encoding="utf-8"),
            logging.StreamHandler(sys.stderr),
        ],
    )
    # httpx INFO logs the full request URL, which for Google AI includes
    # `?key=<API_KEY>` in plaintext. Demote to WARNING so keys never hit
    # disk or stderr even in a redirected-log scenario.
    logging.getLogger("httpx").setLevel(logging.WARNING)
    logging.getLogger("httpcore").setLevel(logging.WARNING)


@asynccontextmanager
async def lifespan(app: FastAPI):
    global _started_at
    _started_at = time.time()
    _configure_logging()
    logger.info("coach sidecar starting, version=%s", __version__)
    try:
        ensure_migrations_applied()
    except Exception:
        logger.exception("migration failure; sidecar will still start but DB ops may fail")
    yield
    logger.info("coach sidecar shutting down")


app = FastAPI(title="lol-review coach sidecar", version=__version__, lifespan=lifespan)


# ─────────────────────────────── Health & config ───────────────────────────────


@app.get("/health", response_model=HealthResponse)
async def health() -> HealthResponse:
    cfg = load_config()
    provider = get_provider()
    available = await provider.available()
    return HealthResponse(
        status="ok" if available else "degraded",
        version=__version__,
        provider=cfg.provider,
        provider_available=available,
        sidecar_uptime_seconds=max(0.0, time.time() - _started_at),
    )


@app.get("/config")
async def get_config() -> dict[str, Any]:
    cfg = load_config()
    data = cfg.model_dump()
    # Never expose API keys over HTTP.
    data.get("google_ai", {}).pop("api_key", None)
    data.get("openrouter", {}).pop("api_key", None)
    return data


@app.post("/config")
async def post_config(update: ConfigUpdateRequest) -> dict[str, Any]:
    partial = update.model_dump(exclude_none=True)
    cfg = update_config(partial)
    data = cfg.model_dump()
    data.get("google_ai", {}).pop("api_key", None)
    data.get("openrouter", {}).pop("api_key", None)
    return data


# ─────────────────────────────── Test prompt ───────────────────────────────


@app.post("/coach/test-prompt", response_model=TestPromptResponse)
async def test_prompt(req: TestPromptRequest) -> TestPromptResponse:
    cfg = load_config()
    provider = get_provider()
    model = (
        cfg.ollama.model
        if cfg.provider == "ollama"
        else cfg.google_ai.model if cfg.provider == "google_ai" else cfg.openrouter.model
    )
    resp = await provider.complete(
        LLMRequest(
            messages=[LLMMessage(role="user", content=req.prompt)],
            model=model,
            max_tokens=500,
        )
    )
    return TestPromptResponse(
        text=resp.text,
        model=resp.model,
        provider=resp.provider,
        latency_ms=resp.latency_ms,
    )


# ─────────────────────────────── Chat (phase-2-reshape) ───────────────────────────────


@app.post("/coach/ask", response_model=AskResponse)
async def coach_ask(req: AskRequest) -> AskResponse:
    from coach.modes.ask import run_ask

    return await run_ask(req.question, thread_id=req.thread_id, scope=req.scope)


@app.get("/coach/threads")
async def list_coach_threads(limit: int = 50) -> dict[str, Any]:
    from coach.modes.ask import list_threads

    return {"threads": list_threads(limit=limit)}


@app.get("/coach/threads/{thread_id}")
async def get_coach_thread(thread_id: int) -> dict[str, Any]:
    from coach.modes.ask import load_thread

    thread = load_thread(thread_id)
    if thread is None:
        raise HTTPException(status_code=404, detail=f"Thread {thread_id} not found")
    return thread


@app.post("/coach/generate-objective", response_model=GenerateObjectiveResponse)
async def coach_generate_objective(req: GenerateObjectiveRequest) -> GenerateObjectiveResponse:
    from coach.modes.generate_objective import run_generate_objective

    return await run_generate_objective(since=req.since)


# ─────────────────────────────── Summaries (Phase 1) ───────────────────────────────


@app.post("/summaries/build/{game_id}", response_model=BuildSummaryResponse)
async def build_summary(game_id: int) -> BuildSummaryResponse:
    from coach.summaries.compactor import build_and_persist

    try:
        token_count, version = build_and_persist(game_id)
        return BuildSummaryResponse(
            game_id=game_id, summary_version=version, token_count=token_count, ok=True
        )
    except Exception as e:
        logger.exception("summary build failed for game %d", game_id)
        return BuildSummaryResponse(
            game_id=game_id, summary_version=1, token_count=None, ok=False, error=str(e)
        )


@app.post("/summaries/build-all", response_model=BuildAllSummariesResponse)
async def build_all_summaries(since: int | None = None) -> BuildAllSummariesResponse:
    from coach.summaries.compactor import build_all

    result = build_all(since=since)
    return BuildAllSummariesResponse(**result)


@app.get("/summaries/{game_id}", response_model=GetSummaryResponse)
async def get_summary(game_id: int) -> GetSummaryResponse:
    from coach.summaries.compactor import load_summary

    row = load_summary(game_id)
    if row is None:
        raise HTTPException(status_code=404, detail=f"No summary for game {game_id}")
    return row


# ─────────────────────────────── Concepts (Phase 2) ───────────────────────────────


@app.post("/concepts/extract/{game_id}", response_model=ExtractConceptsResponse)
async def extract_concepts(game_id: int) -> ExtractConceptsResponse:
    from coach.concepts.extractor import extract_for_game

    try:
        count = await extract_for_game(game_id)
        return ExtractConceptsResponse(game_id=game_id, concepts_extracted=count, ok=True)
    except Exception as e:
        logger.exception("concept extraction failed for game %d", game_id)
        return ExtractConceptsResponse(
            game_id=game_id, concepts_extracted=0, ok=False, error=str(e)
        )


@app.post("/concepts/extract-all")
async def extract_all_concepts(since: int | None = None) -> dict[str, int]:
    from coach.concepts.extractor import extract_all

    return await extract_all(since=since)


@app.post("/concepts/recluster")
async def recluster_concepts() -> dict[str, Any]:
    from coach.concepts.clusterer import recluster

    return recluster()


@app.get("/concepts/profile", response_model=ConceptProfileResponse)
async def get_concept_profile() -> ConceptProfileResponse:
    from coach.concepts.profiler import load_profile

    return load_profile()


# ─────────────────────────────── Signals (Phase 3) ───────────────────────────────


@app.post("/signals/compute-features/{game_id}", response_model=ComputeFeaturesResponse)
async def compute_features_for_game(game_id: int) -> ComputeFeaturesResponse:
    from coach.signals.features import compute_and_persist

    try:
        count = compute_and_persist(game_id)
        return ComputeFeaturesResponse(game_id=game_id, features_computed=count, ok=True)
    except Exception as e:
        logger.exception("feature compute failed for game %d", game_id)
        return ComputeFeaturesResponse(
            game_id=game_id, features_computed=0, ok=False, error=str(e)
        )


@app.post("/signals/compute-features-all")
async def compute_features_all(since: int | None = None) -> dict[str, int]:
    from coach.signals.features import compute_all

    return compute_all(since=since)


@app.post("/signals/rerank")
async def rerank_signals() -> dict[str, Any]:
    from coach.signals.ranker import rerank

    return rerank()


@app.get("/signals/ranking", response_model=SignalRankingResponse)
async def get_signal_ranking() -> SignalRankingResponse:
    from coach.signals.ranker import load_ranking

    return load_ranking()


# ─────────────────────────────── Vision (Phase 4) ───────────────────────────────


@app.post("/vision/describe-bookmark/{bookmark_id}", response_model=VisionDescribeResponse)
async def describe_bookmark(bookmark_id: int) -> VisionDescribeResponse:
    from coach.vision.describer import describe_bookmark

    try:
        count = await describe_bookmark(bookmark_id)
        return VisionDescribeResponse(
            bookmark_id=bookmark_id, frames_processed=count, ok=True
        )
    except Exception as e:
        logger.exception("vision describe failed for bookmark %d", bookmark_id)
        return VisionDescribeResponse(
            bookmark_id=bookmark_id, frames_processed=0, ok=False, error=str(e)
        )


# ─────────────────────────────── Coach modes (Phase 5) ───────────────────────────────


@app.post("/coach/post-game", response_model=CoachResponse)
async def coach_post_game(req: PostGameCoachRequest) -> CoachResponse:
    from coach.modes.post_game import run_post_game

    return await run_post_game(req.game_id)


@app.post("/coach/clip-review", response_model=CoachResponse)
async def coach_clip_review(req: ClipReviewCoachRequest) -> CoachResponse:
    from coach.modes.clip_reviewer import run_clip_review

    return await run_clip_review(req.bookmark_id)


@app.post("/coach/session", response_model=CoachResponse)
async def coach_session(req: SessionCoachRequest) -> CoachResponse:
    from coach.modes.session_coach import run_session

    return await run_session(req.since, req.until)


@app.post("/coach/weekly", response_model=CoachResponse)
async def coach_weekly(req: WeeklyCoachRequest) -> CoachResponse:
    from coach.modes.weekly_coach import run_weekly

    return await run_weekly(req.since, req.until)


@app.post("/coach/log-edit", response_model=LogEditResponse)
async def log_edit(req: LogEditRequest) -> LogEditResponse:
    from coach.modes.post_game import log_edit as log_edit_impl

    try:
        distance = log_edit_impl(req.coach_session_id, req.edited_text)
        return LogEditResponse(
            coach_session_id=req.coach_session_id, edit_distance=distance, ok=True
        )
    except Exception as e:
        logger.exception("log-edit failed")
        return JSONResponse(
            status_code=500,
            content={"ok": False, "error": str(e), "coach_session_id": req.coach_session_id},
        )


# ─────────────────────────────── Runner ───────────────────────────────


def _pick_port(preferred: int) -> int:
    """Try the preferred port; if taken, bind to next free port."""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        try:
            s.bind(("127.0.0.1", preferred))
            return preferred
        except OSError:
            pass
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def run() -> None:
    parser = argparse.ArgumentParser(description="lol-review coach sidecar")
    parser.add_argument("--port", type=int, default=None, help="Override config port")
    parser.add_argument("--host", type=str, default="127.0.0.1")
    parser.add_argument("--log-level", type=str, default="info")
    args = parser.parse_args()

    cfg = load_config()
    preferred = args.port if args.port else cfg.port
    port = _pick_port(preferred)
    if port != preferred:
        logger.warning("Preferred port %d is taken; using %d instead", preferred, port)
        # Write the actual port to a handshake file so C# can discover it.
        from coach.config import user_data_root

        handshake = user_data_root() / "coach_port.txt"
        handshake.parent.mkdir(parents=True, exist_ok=True)
        handshake.write_text(str(port), encoding="utf-8")

    uvicorn.run(app, host=args.host, port=port, log_level=args.log_level)


if __name__ == "__main__":
    run()
