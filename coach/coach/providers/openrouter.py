"""OpenRouter provider — model-flexible fallback for hosted users.

OpenRouter uses an OpenAI-compatible chat completions API. Full
implementation per plan §7 Phase 6.
"""

from __future__ import annotations

import asyncio
import base64
import logging
import time
from typing import Any

import httpx

from coach.providers.base import LLMProvider
from coach.schemas import LLMRequest, LLMResponse

logger = logging.getLogger(__name__)


class OpenRouterProvider(LLMProvider):
    BASE_URL = "https://openrouter.ai/api/v1"

    def __init__(self, model: str, api_key: str | None) -> None:
        self._model = model
        self._api_key = api_key
        self._client = httpx.AsyncClient(timeout=120.0)

    @property
    def name(self) -> str:
        return "openrouter"

    def set_api_key(self, api_key: str | None) -> None:
        self._api_key = api_key

    async def complete(self, req: LLMRequest) -> LLMResponse:
        if not self._api_key:
            raise RuntimeError("OpenRouter provider is selected but no API key is configured.")

        messages = self._build_messages(req)
        body: dict[str, Any] = {
            "model": self._model,
            "messages": messages,
            "temperature": req.temperature,
            "max_tokens": req.max_tokens,
        }
        if req.response_format == "json":
            body["response_format"] = {"type": "json_object"}

        headers = {
            "Authorization": f"Bearer {self._api_key}",
            "HTTP-Referer": "https://github.com/samif0/lol-review",
            "X-Title": "lol-review coach",
        }

        start = time.perf_counter()
        data = await self._post_with_retry(f"{self.BASE_URL}/chat/completions", body, headers)
        latency_ms = int((time.perf_counter() - start) * 1000)

        choice = data.get("choices", [{}])[0]
        text = choice.get("message", {}).get("content", "")
        usage = data.get("usage", {})
        return LLMResponse(
            text=text,
            model=self._model,
            provider=self.name,
            input_tokens=usage.get("prompt_tokens"),
            output_tokens=usage.get("completion_tokens"),
            latency_ms=latency_ms,
        )

    async def embed(self, texts: list[str]) -> list[list[float]]:
        raise NotImplementedError(
            "Use coach.concepts.embedder for embeddings (deterministic, local)."
        )

    def supports_vision(self) -> bool:
        # OpenRouter supports vision for many models; conservatively return True
        # and let the underlying model handle or reject.
        return True

    def supports_json_mode(self) -> bool:
        return True

    async def available(self) -> bool:
        if not self._api_key:
            return False
        try:
            r = await self._client.get(
                f"{self.BASE_URL}/models",
                headers={"Authorization": f"Bearer {self._api_key}"},
                timeout=5.0,
            )
            return r.status_code == 200
        except Exception:
            return False

    async def _post_with_retry(
        self,
        url: str,
        body: dict[str, Any],
        headers: dict[str, str],
        max_attempts: int = 4,
    ) -> dict[str, Any]:
        backoff = 1.0
        for attempt in range(1, max_attempts + 1):
            try:
                r = await self._client.post(url, json=body, headers=headers)
                if r.status_code == 429 or 500 <= r.status_code < 600:
                    if attempt == max_attempts:
                        r.raise_for_status()
                    await asyncio.sleep(backoff)
                    backoff *= 2
                    continue
                r.raise_for_status()
                return r.json()
            except httpx.TimeoutException:
                if attempt == max_attempts:
                    raise
                await asyncio.sleep(backoff)
                backoff *= 2
        raise RuntimeError("exhausted retries")

    @staticmethod
    def _build_messages(req: LLMRequest) -> list[dict[str, Any]]:
        messages: list[dict[str, Any]] = []
        for msg in req.messages:
            content: Any
            if isinstance(msg.content, str):
                if req.images and msg.role == "user":
                    parts = [{"type": "text", "text": msg.content}]
                    for img in req.images:
                        parts.append(
                            {
                                "type": "image_url",
                                "image_url": {
                                    "url": f"data:image/png;base64,{base64.b64encode(img).decode('ascii')}"
                                },
                            }
                        )
                    content = parts
                else:
                    content = msg.content
            else:
                content = msg.content
            messages.append({"role": msg.role, "content": content})
        return messages

    async def aclose(self) -> None:
        await self._client.aclose()
