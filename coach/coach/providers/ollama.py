"""Ollama provider — local default."""

from __future__ import annotations

import base64
import logging
import time
from typing import Any

import httpx

from coach.providers.base import LLMProvider
from coach.schemas import LLMRequest, LLMResponse

logger = logging.getLogger(__name__)


class OllamaProvider(LLMProvider):
    def __init__(self, base_url: str, model: str, vision_model: str) -> None:
        self._base_url = base_url.rstrip("/")
        self._model = model
        self._vision_model = vision_model
        self._client = httpx.AsyncClient(timeout=300.0)

    @property
    def name(self) -> str:
        return "ollama"

    async def complete(self, req: LLMRequest) -> LLMResponse:
        model = req.model or self._model
        payload: dict[str, Any] = {
            "model": model,
            "messages": [self._convert_message(m, req.images) for m in req.messages],
            "stream": False,
            "options": {
                "temperature": req.temperature,
                "num_predict": req.max_tokens,
            },
        }
        if req.response_format == "json":
            payload["format"] = "json"

        start = time.perf_counter()
        response = await self._client.post(f"{self._base_url}/api/chat", json=payload)
        response.raise_for_status()
        data = response.json()
        latency_ms = int((time.perf_counter() - start) * 1000)

        text = data.get("message", {}).get("content", "")
        # Ollama reports prompt_eval_count and eval_count for tokens.
        return LLMResponse(
            text=text,
            model=model,
            provider=self.name,
            input_tokens=data.get("prompt_eval_count"),
            output_tokens=data.get("eval_count"),
            latency_ms=latency_ms,
        )

    async def embed(self, texts: list[str]) -> list[list[float]]:
        """Use sentence-transformers locally, not Ollama's embed API, because
        we want deterministic, cache-friendly embeddings for clustering.

        Returning empty list here; the embedder module handles this via
        sentence-transformers directly.
        """
        raise NotImplementedError(
            "Ollama provider does not implement embed(); use coach.concepts.embedder."
        )

    def supports_vision(self) -> bool:
        return True

    def supports_json_mode(self) -> bool:
        return True

    async def available(self) -> bool:
        try:
            r = await self._client.get(f"{self._base_url}/api/tags", timeout=3.0)
            return r.status_code == 200
        except Exception:
            return False

    @staticmethod
    def _convert_message(msg: Any, images: list[bytes] | None) -> dict[str, Any]:
        """Convert a coach LLMMessage to the Ollama wire format.

        For vision, Ollama expects `images` as a list of base64 strings on the
        message itself.
        """
        content = msg.content
        out: dict[str, Any] = {"role": msg.role, "content": ""}

        if isinstance(content, str):
            out["content"] = content
        else:
            # multimodal list: join text parts, pass images separately
            text_parts: list[str] = []
            for part in content:
                if isinstance(part, dict) and part.get("type") == "text":
                    text_parts.append(str(part.get("text", "")))
            out["content"] = "\n".join(text_parts)

        if images:
            out["images"] = [base64.b64encode(img).decode("ascii") for img in images]

        return out

    async def aclose(self) -> None:
        await self._client.aclose()
