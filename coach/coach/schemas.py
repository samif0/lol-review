"""Pydantic request/response models for the FastAPI sidecar."""

from __future__ import annotations

from typing import Any, Literal

from pydantic import BaseModel, Field


# ──────────────────────────────────────────────────────────────────
# Health and config
# ──────────────────────────────────────────────────────────────────


class HealthResponse(BaseModel):
    status: Literal["ok", "degraded", "error"]
    version: str
    provider: str
    provider_available: bool
    sidecar_uptime_seconds: float


class TestPromptRequest(BaseModel):
    prompt: str


class TestPromptResponse(BaseModel):
    text: str
    model: str
    provider: str
    latency_ms: int


class ConfigUpdateRequest(BaseModel):
    """Partial config update. Any field may be omitted."""

    provider: str | None = None
    port: int | None = None
    vision_override_provider: str | None = None
    ollama: dict[str, Any] | None = None
    google_ai: dict[str, Any] | None = None
    openrouter: dict[str, Any] | None = None


# ──────────────────────────────────────────────────────────────────
# Phase 1: summaries
# ──────────────────────────────────────────────────────────────────


class BuildSummaryResponse(BaseModel):
    game_id: int
    summary_version: int
    token_count: int | None
    ok: bool
    error: str | None = None


class BuildAllSummariesResponse(BaseModel):
    attempted: int
    succeeded: int
    failed: int
    skipped: int


class GetSummaryResponse(BaseModel):
    game_id: int
    compacted_json: dict[str, Any]
    win_probability_timeline: list[float] | None = None
    key_events: list[dict[str, Any]] | None = None
    summary_version: int
    token_count: int | None
    created_at: int


# ──────────────────────────────────────────────────────────────────
# Phase 2: concepts
# ──────────────────────────────────────────────────────────────────


class ExtractConceptsResponse(BaseModel):
    game_id: int
    concepts_extracted: int
    ok: bool
    error: str | None = None


class ConceptProfileEntry(BaseModel):
    concept_canonical: str
    frequency: int
    recency_weighted_frequency: float
    positive_count: int
    negative_count: int
    neutral_count: int
    win_correlation: float | None
    rank: int
    last_seen_at: int


class ConceptProfileResponse(BaseModel):
    entries: list[ConceptProfileEntry]
    total: int


# ──────────────────────────────────────────────────────────────────
# Phase 3: signals
# ──────────────────────────────────────────────────────────────────


class ComputeFeaturesResponse(BaseModel):
    game_id: int
    features_computed: int
    ok: bool
    error: str | None = None


class SignalRankingEntry(BaseModel):
    feature_name: str
    rank: int
    spearman_rho: float
    partial_rho_mental_controlled: float | None
    ci_low: float
    ci_high: float
    sample_size: int
    stable: bool
    drift_flag: bool
    user_baseline_win_avg: float | None
    user_baseline_loss_avg: float | None


class SignalRankingResponse(BaseModel):
    entries: list[SignalRankingEntry]
    computed_at: int


# ──────────────────────────────────────────────────────────────────
# Phase 4: vision
# ──────────────────────────────────────────────────────────────────


class VisionDescribeResponse(BaseModel):
    bookmark_id: int
    frames_processed: int
    ok: bool
    error: str | None = None


# ──────────────────────────────────────────────────────────────────
# Phase 5: coach modes
# ──────────────────────────────────────────────────────────────────


class PostGameCoachRequest(BaseModel):
    game_id: int


class ClipReviewCoachRequest(BaseModel):
    bookmark_id: int


class SessionCoachRequest(BaseModel):
    since: int | None = None
    until: int | None = None


class WeeklyCoachRequest(BaseModel):
    since: int | None = None
    until: int | None = None


class CoachResponse(BaseModel):
    coach_session_id: int
    mode: str
    response_text: str
    response_json: dict[str, Any] | None = None
    model: str
    provider: str
    latency_ms: int


class LogEditRequest(BaseModel):
    coach_session_id: int
    edited_text: str


class LogEditResponse(BaseModel):
    coach_session_id: int
    edit_distance: int
    ok: bool


# ──────────────────────────────────────────────────────────────────
# Chat (phase-2-reshape, 2026-04-18)
# ──────────────────────────────────────────────────────────────────


class AskRequest(BaseModel):
    question: str
    thread_id: int | None = None  # None = new thread
    scope: dict[str, Any] | None = None  # optional pinned scope (e.g. {"game_id": 123})


class ChatMessage(BaseModel):
    id: int
    thread_id: int
    role: Literal["user", "assistant"]
    content: str
    model: str | None = None
    provider: str | None = None
    latency_ms: int | None = None
    created_at: int


class AskResponse(BaseModel):
    thread_id: int
    user_message: ChatMessage
    assistant_message: ChatMessage
    coach_visible_totals: dict[str, int]


class ChatThread(BaseModel):
    id: int
    title: str | None
    scope: dict[str, Any] | None
    created_at: int
    updated_at: int
    messages: list[ChatMessage]


class GenerateObjectiveRequest(BaseModel):
    # Optional: limit analysis to recent N games (default: all games with data)
    since: int | None = None


class ObjectiveProposal(BaseModel):
    title: str
    rationale: str
    # New in v2.8.2: specific in-game cue the player uses to
    # recognize the trigger, and how they'll self-verify success.
    # Both optional; older clients and older prompt versions can
    # still return proposals without them.
    trigger: str | None = None
    success_criteria: str | None = None
    replaces_objective_id: int | None = None
    confidence: float


class GenerateObjectiveResponse(BaseModel):
    proposals: list[ObjectiveProposal]
    model: str
    provider: str
    latency_ms: int


# ──────────────────────────────────────────────────────────────────
# Provider-internal (used by coach/providers/*)
# ──────────────────────────────────────────────────────────────────


class LLMMessage(BaseModel):
    role: Literal["system", "user", "assistant"]
    content: str | list[Any]


class LLMRequest(BaseModel):
    messages: list[LLMMessage]
    model: str
    temperature: float = 0.3
    max_tokens: int = 2000
    response_format: str | None = None
    images: list[bytes] | None = None

    model_config = {"arbitrary_types_allowed": True}


class LLMResponse(BaseModel):
    text: str
    model: str
    provider: str
    input_tokens: int | None = None
    output_tokens: int | None = None
    latency_ms: int
