r"""Configuration loader for the coach sidecar.

Config file: %LOCALAPPDATA%\LoLReviewData\coach_config.json.

API keys are NOT stored here. C# injects them at runtime via POST /config after
sidecar health is green (plan section 6, UNCLEAR-003 resolved option (b)).
"""

from __future__ import annotations

import json
import logging
import os
import threading
from pathlib import Path
from typing import Any, Literal

from pydantic import BaseModel, Field

logger = logging.getLogger(__name__)

ProviderName = Literal["ollama", "google_ai", "openrouter"]


class OllamaConfig(BaseModel):
    base_url: str = "http://localhost:11434"
    # Gemma 4 E4B: 4.5B effective params, multimodal (text+image+audio),
    # 128K context, Apache 2.0. Default "latest" on Ollama.
    # Pull with: ollama pull gemma4:e4b
    model: str = "gemma4:e4b"
    vision_model: str = "gemma4:e4b"


class GoogleAIConfig(BaseModel):
    # Gemini 2.5 Flash on Google AI Studio.
    # Chosen as default after 2026-04-18 user test:
    #   - Gemma 4 was doing 40-60s uncapped internal reasoning per ask
    #     with no way to bound it (no thinkingConfig support, no penalty
    #     support). Resulting in 40s+ latency per chat turn, which is
    #     unusable as a chat UX.
    #   - Gemini 2.5 Flash accepts thinkingConfig.thinkingBudget=2048,
    #     keeps reasoning invisible, and returns in 1-3 seconds for
    #     the same kind of grounded coaching question.
    # Free of charge on Google AI for text I/O; same API key works.
    # Power users can swap to `gemma-4-26b-a4b-it` in Settings if they
    # want pure-Gemma output and don't mind the latency.
    model: str = "gemini-2.5-flash"
    api_key: str | None = None  # injected by C# at runtime


class OpenRouterConfig(BaseModel):
    # OpenRouter model IDs trail Google AI Studio by a few days on new
    # Gemma releases; update to google/gemma-4-* when available.
    model: str = "google/gemma-3-27b-it"
    api_key: str | None = None  # injected by C# at runtime


class CoachConfig(BaseModel):
    # Hosted-first default (2026-04-18): Google AI Studio is the recommended
    # provider for both @samif0 and shared users — no local GPU required,
    # no Ollama install, no model-pull friction. Users paste an API key once.
    # Switch provider to "ollama" in settings for local inference.
    provider: ProviderName = "google_ai"
    port: int = 5577  # UNCLEAR-002: port is configurable; if taken, sidecar picks next free port
    vision_override_provider: ProviderName | None = None
    ollama: OllamaConfig = Field(default_factory=OllamaConfig)
    google_ai: GoogleAIConfig = Field(default_factory=GoogleAIConfig)
    openrouter: OpenRouterConfig = Field(default_factory=OpenRouterConfig)


_config_lock = threading.Lock()
_current_config: CoachConfig | None = None


def user_data_root() -> Path:
    """%LOCALAPPDATA%\\LoLReviewData on Windows."""
    local_appdata = os.environ.get("LOCALAPPDATA")
    if local_appdata:
        return Path(local_appdata) / "LoLReviewData"
    return Path.home() / "AppData" / "Local" / "LoLReviewData"


def config_path() -> Path:
    return user_data_root() / "coach_config.json"


def db_path() -> Path:
    # v2.10.0 rename: previously "lol_review.db". The C# AppDataMigrator
    # renames the file in place on first launch; the coach never sees the
    # legacy name on a current install.
    return user_data_root() / "revu.db"


def coach_data_root() -> Path:
    return user_data_root() / "coach"


def embeddings_dir() -> Path:
    return coach_data_root() / "embeddings"


def frames_dir(bookmark_id: int) -> Path:
    return user_data_root() / "coach_frames" / str(bookmark_id)


def backups_dir() -> Path:
    return user_data_root() / "backups"


def log_path() -> Path:
    install_root = os.environ.get("LOCALAPPDATA")
    if install_root:
        return Path(install_root) / "LoLReview" / "coach.log"
    return Path.home() / "AppData" / "Local" / "LoLReview" / "coach.log"


def load_config() -> CoachConfig:
    global _current_config
    with _config_lock:
        if _current_config is not None:
            return _current_config

        path = config_path()
        if path.exists():
            try:
                raw: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
                _current_config = CoachConfig.model_validate(raw)
                logger.info("Loaded coach config from %s", path)
            except Exception:
                logger.exception("Failed to parse coach config; falling back to defaults")
                _current_config = CoachConfig()
        else:
            _current_config = CoachConfig()
            _write_config_to_disk(_current_config)  # materialize defaults so user can edit

        return _current_config


def _write_config_to_disk(cfg: CoachConfig) -> None:
    """Serialize cfg and write to disk. Caller must hold _config_lock (or not
    care about races). Does NOT mutate _current_config — that's the caller's
    responsibility so ordering stays visible."""
    path = config_path()
    path.parent.mkdir(parents=True, exist_ok=True)

    serializable = cfg.model_dump()
    # Strip API keys before writing to disk; they live in Windows Credential Manager.
    serializable.get("google_ai", {}).pop("api_key", None)
    serializable.get("openrouter", {}).pop("api_key", None)

    path.write_text(json.dumps(serializable, indent=2), encoding="utf-8")
    logger.info("Saved coach config to %s", path)


def save_config(cfg: CoachConfig) -> None:
    """Persist config without API keys (keys are C#-injected runtime state)."""
    global _current_config
    with _config_lock:
        _write_config_to_disk(cfg)
        _current_config = cfg


def update_config(partial: dict[str, Any]) -> CoachConfig:
    """Apply a partial update (from C# POST /config). Deep-merges into current."""
    global _current_config
    # Ensure config is loaded BEFORE acquiring the lock (load_config locks too).
    base = load_config()
    with _config_lock:
        existing = (_current_config or base).model_dump()
        _deep_merge(existing, partial)
        new_cfg = CoachConfig.model_validate(existing)
        _write_config_to_disk(new_cfg)
        _current_config = new_cfg
    return new_cfg


def _deep_merge(dst: dict[str, Any], src: dict[str, Any]) -> None:
    for key, value in src.items():
        if isinstance(value, dict) and isinstance(dst.get(key), dict):
            _deep_merge(dst[key], value)
        else:
            dst[key] = value
