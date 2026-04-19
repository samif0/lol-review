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
        gen_config: dict[str, Any] = {
            "temperature": req.temperature,
            "maxOutputTokens": req.max_tokens,
            "topP": 0.95,
            "topK": 64,
        }
        # Gemini-only: thinkingConfig caps reasoning tokens.
        # NOTE: frequencyPenalty/presencePenalty were tried and rejected by
        # both Gemma ('Penalty is not enabled for this model') AND
        # Gemini 2.5 Flash ('Penalty is not enabled for models/gemini-2.5-flash').
        # Don't send them.
        if self._is_gemini_family():
            gen_config["thinkingConfig"] = {"thinkingBudget": 2048}
        body: dict[str, Any] = {"contents": contents, "generationConfig": gen_config}
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

    async def complete_stream(self, req: LLMRequest) -> AsyncIterator[tuple[str, str]]:
        """Stream (kind, text) tuples from streamGenerateContent?alt=sse.

        `kind` is one of:
          - "thought": part of the model's internal reasoning (hide from UI,
            caller should just keep showing a thinking indicator).
          - "answer":  final user-facing text (stream into the bubble).

        Google AI indicates reasoning via `candidates[].content.parts[].thought = true`.
        """
        if not self._api_key:
            raise RuntimeError("Google AI provider is selected but no API key is configured.")

        url = f"{self.BASE_URL}/models/{self._model}:streamGenerateContent?alt=sse&key={self._api_key}"
        contents = self._build_contents(req)
        gen_config: dict[str, Any] = {
            "temperature": req.temperature,
            "maxOutputTokens": req.max_tokens,
            # topP + topK nudge Gemma 4 away from pathological token-repeat
            # loops ('I donleksya_ch_av_av_av_').
            "topP": 0.95,
            "topK": 64,
        }
        # Gemini-only: thinkingConfig caps reasoning.
        # frequency/presencePenalty were rejected by both Gemma and
        # Gemini 2.5 Flash — see comment in complete().
        if self._is_gemini_family():
            gen_config["thinkingConfig"] = {
                "includeThoughts": True,
                "thinkingBudget": 2048,
            }
        body: dict[str, Any] = {"contents": contents, "generationConfig": gen_config}
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
            any_answer = False
            last_finish_reason: str | None = None

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
                candidate = candidates[0]
                if "finishReason" in candidate:
                    last_finish_reason = candidate["finishReason"]

                parts = candidate.get("content", {}).get("parts", [])
                for p in parts:
                    text = p.get("text", "")
                    if not text:
                        continue
                    kind = "thought" if p.get("thought") else "answer"
                    if kind == "answer":
                        any_answer = True
                    yield (kind, text)

        # If the stream ended without any answer tokens, decide what to do.
        # STOP + empty is a model behavior bug (no user input was rejected);
        # transparently retry once. Other reasons (SAFETY, MAX_TOKENS,
        # RECITATION) are hard stops with real causes — surface them.
        if not any_answer:
            if last_finish_reason in (None, "STOP"):
                # Retry once without yielding anything to the caller.
                async for kind, chunk in self._retry_stream_once(req, body, url):
                    yield (kind, chunk)
                return
            if last_finish_reason == "SAFETY":
                yield ("answer",
                    "Response blocked by Google's safety filter. Try rephrasing — the filter sometimes trips on unrelated words.")
            elif last_finish_reason == "MAX_TOKENS":
                yield ("answer",
                    "Hit the token limit before finishing an answer. Try a shorter/simpler question.")
            elif last_finish_reason == "RECITATION":
                yield ("answer",
                    "Response blocked to avoid reciting copyrighted text. Try asking a different way.")
            else:
                yield ("answer",
                    "No response — the provider returned nothing. Try again.")

    async def _retry_stream_once(
        self, req: LLMRequest, body: dict[str, Any], url: str
    ) -> AsyncIterator[tuple[str, str]]:
        """Single silent retry for empty-STOP responses. Any outcome is final
        — we don't recurse. Signals final failure via a short note if the
        retry also returns nothing."""
        try:
            async with self._client.stream("POST", url, json=body) as response:
                if response.status_code >= 400:
                    yield ("answer", "No response after retry. Try again.")
                    return
                any_answer = False
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
                        if not text:
                            continue
                        kind = "thought" if p.get("thought") else "answer"
                        if kind == "answer":
                            any_answer = True
                        yield (kind, text)
                if not any_answer:
                    yield ("answer",
                        "Model returned no answer after retry. Try rephrasing.")
        except Exception as exc:
            logger.exception("retry stream failed")
            yield ("answer", f"Retry failed: {exc}")

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

    def _is_gemini_family(self) -> bool:
        """thinkingConfig + frequency/presencePenalty are Gemini-only.
        Gemma rejects them with 400 INVALID_ARGUMENT."""
        name = (self._model or "").lower()
        return name.startswith("gemini-") or "gemini" in name.split("/")

    async def aclose(self) -> None:
        await self._client.aclose()
