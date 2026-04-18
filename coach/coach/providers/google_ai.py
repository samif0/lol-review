"""Google AI Studio provider.

Full implementation per plan §7 Phase 6: streaming, rate-limit backoff,
5xx retry. Implemented here rather than stubbed so it's ready to go when
@samif0 pastes an API key.
"""

from __future__ import annotations

import asyncio
import base64
import json
import logging
import time
from typing import Any, AsyncIterator

import httpx

from coach.providers.base import LLMProvider
from coach.schemas import LLMRequest, LLMResponse

logger = logging.getLogger(__name__)


class GoogleAIProvider(LLMProvider):
    """Uses Google AI Studio's Generative Language API (v1beta)."""

    BASE_URL = "https://generativelanguage.googleapis.com/v1beta"

    def __init__(self, model: str, api_key: str | None) -> None:
        self._model = model
        self._api_key = api_key
        self._client = httpx.AsyncClient(timeout=120.0)

    @property
    def name(self) -> str:
        return "google_ai"

    def set_api_key(self, api_key: str | None) -> None:
        self._api_key = api_key

    async def complete(self, req: LLMRequest) -> LLMResponse:
        if not self._api_key:
            raise RuntimeError("Google AI provider is selected but no API key is configured.")

        url = f"{self.BASE_URL}/models/{self._model}:generateContent?key={self._api_key}"
        contents = self._build_contents(req)
        body: dict[str, Any] = {
            "contents": contents,
            "generationConfig": {
                "temperature": req.temperature,
                "maxOutputTokens": req.max_tokens,
            },
        }
        if req.response_format == "json":
            body["generationConfig"]["responseMimeType"] = "application/json"

        start = time.perf_counter()
        data = await self._post_with_retry(url, body)
        latency_ms = int((time.perf_counter() - start) * 1000)

        candidates = data.get("candidates", [])
        text = ""
        if candidates:
            parts = candidates[0].get("content", {}).get("parts", [])
            text = "".join(p.get("text", "") for p in parts)

        usage = data.get("usageMetadata", {})
        return LLMResponse(
            text=text,
            model=self._model,
            provider=self.name,
            input_tokens=usage.get("promptTokenCount"),
            output_tokens=usage.get("candidatesTokenCount"),
            latency_ms=latency_ms,
        )

    async def complete_stream(self, req: LLMRequest) -> AsyncIterator[str]:
        """Stream text chunks from streamGenerateContent?alt=sse."""
        if not self._api_key:
            raise RuntimeError("Google AI provider is selected but no API key is configured.")

        url = f"{self.BASE_URL}/models/{self._model}:streamGenerateContent?alt=sse&key={self._api_key}"
        contents = self._build_contents(req)
        body: dict[str, Any] = {
            "contents": contents,
            "generationConfig": {
                "temperature": req.temperature,
                "maxOutputTokens": req.max_tokens,
            },
        }
        if req.response_format == "json":
            body["generationConfig"]["responseMimeType"] = "application/json"

        async with self._client.stream("POST", url, json=body) as response:
            if response.status_code >= 400:
                body_text = await response.aread()
                raise httpx.HTTPStatusError(
                    f"Streaming error {response.status_code}: {body_text.decode('utf-8', errors='replace')[:500]}",
                    request=response.request,
                    response=response,
                )
            async for line in response.aiter_lines():
                if not line or not line.startswith("data: "):
                    continue
                data_str = line[len("data: "):]
                if data_str == "[DONE]":
                    break
                try:
                    event = json.loads(data_str)
                except Exception:
                    continue
                candidates = event.get("candidates", [])
                if not candidates:
                    continue
                parts = candidates[0].get("content", {}).get("parts", [])
                for p in parts:
                    text = p.get("text", "")
                    if text:
                        yield text

    async def embed(self, texts: list[str]) -> list[list[float]]:
        raise NotImplementedError(
            "Use coach.concepts.embedder for embeddings (deterministic, local)."
        )

    def supports_vision(self) -> bool:
        return True

    def supports_json_mode(self) -> bool:
        return True

    def supports_streaming(self) -> bool:
        return True

    async def available(self) -> bool:
        if not self._api_key:
            return False
        try:
            r = await self._client.get(
                f"{self.BASE_URL}/models?key={self._api_key}", timeout=5.0
            )
            return r.status_code == 200
        except Exception:
            return False

    async def _post_with_retry(
        self, url: str, body: dict[str, Any], max_attempts: int = 4
    ) -> dict[str, Any]:
        backoff = 1.0
        for attempt in range(1, max_attempts + 1):
            try:
                r = await self._client.post(url, json=body)
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
    def _build_contents(req: LLMRequest) -> list[dict[str, Any]]:
        contents: list[dict[str, Any]] = []
        for msg in req.messages:
            role = "user" if msg.role in ("user", "system") else "model"
            parts: list[dict[str, Any]]
            if isinstance(msg.content, str):
                parts = [{"text": msg.content}]
            else:
                parts = []
                for part in msg.content:
                    if isinstance(part, dict) and part.get("type") == "text":
                        parts.append({"text": str(part.get("text", ""))})
            if req.images and role == "user":
                for img in req.images:
                    parts.append(
                        {
                            "inline_data": {
                                "mime_type": "image/png",
                                "data": base64.b64encode(img).decode("ascii"),
                            }
                        }
                    )
            contents.append({"role": role, "parts": parts})
        return contents

    async def aclose(self) -> None:
        await self._client.aclose()
