"""Provider factory. Selects a provider based on current config."""

from __future__ import annotations

import threading

from coach.config import CoachConfig, load_config
from coach.providers.base import LLMProvider
from coach.providers.google_ai import GoogleAIProvider
from coach.providers.ollama import OllamaProvider
from coach.providers.openrouter import OpenRouterProvider

_lock = threading.Lock()
_cached_provider: LLMProvider | None = None
_cached_vision_provider: LLMProvider | None = None
_cached_config_key: tuple | None = None


def _config_key(cfg: CoachConfig) -> tuple:
    return (
        cfg.provider,
        cfg.vision_override_provider,
        cfg.ollama.base_url,
        cfg.ollama.model,
        cfg.ollama.vision_model,
        cfg.google_ai.model,
        cfg.google_ai.api_key,
        cfg.openrouter.model,
        cfg.openrouter.api_key,
    )


def _build(name: str, cfg: CoachConfig) -> LLMProvider:
    if name == "ollama":
        return OllamaProvider(
            base_url=cfg.ollama.base_url,
            model=cfg.ollama.model,
            vision_model=cfg.ollama.vision_model,
        )
    if name == "google_ai":
        return GoogleAIProvider(model=cfg.google_ai.model, api_key=cfg.google_ai.api_key)
    if name == "openrouter":
        return OpenRouterProvider(
            model=cfg.openrouter.model, api_key=cfg.openrouter.api_key
        )
    raise ValueError(f"Unknown provider: {name}")


def get_provider() -> LLMProvider:
    """Return the currently-configured text provider (cached)."""
    global _cached_provider, _cached_vision_provider, _cached_config_key
    cfg = load_config()
    key = _config_key(cfg)

    with _lock:
        if _cached_config_key != key:
            _cached_provider = None
            _cached_vision_provider = None
            _cached_config_key = key

        if _cached_provider is None:
            _cached_provider = _build(cfg.provider, cfg)

        return _cached_provider


def get_vision_provider() -> LLMProvider:
    """Return the provider to use for vision calls. Falls back to Ollama if
    the text provider doesn't support vision (plan §7 Phase 4 task 6)."""
    global _cached_provider, _cached_vision_provider, _cached_config_key
    cfg = load_config()
    key = _config_key(cfg)

    with _lock:
        if _cached_config_key != key:
            _cached_provider = None
            _cached_vision_provider = None
            _cached_config_key = key

        if _cached_vision_provider is None:
            override = cfg.vision_override_provider
            if override:
                _cached_vision_provider = _build(override, cfg)
            else:
                text_provider = _cached_provider or _build(cfg.provider, cfg)
                if text_provider.supports_vision():
                    _cached_vision_provider = text_provider
                else:
                    _cached_vision_provider = _build("ollama", cfg)

        return _cached_vision_provider
