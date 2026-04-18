"""LLM provider interface (plan §6)."""

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import AsyncIterator

from coach.schemas import LLMRequest, LLMResponse


class LLMProvider(ABC):
    @property
    @abstractmethod
    def name(self) -> str:
        ...

    @abstractmethod
    async def complete(self, req: LLMRequest) -> LLMResponse:
        ...

    async def complete_stream(self, req: LLMRequest) -> AsyncIterator[str]:
        """Yield text chunks as they arrive. Default falls back to non-streaming."""
        response = await self.complete(req)
        yield response.text

    @abstractmethod
    async def embed(self, texts: list[str]) -> list[list[float]]:
        ...

    @abstractmethod
    def supports_vision(self) -> bool:
        ...

    @abstractmethod
    def supports_json_mode(self) -> bool:
        ...

    def supports_streaming(self) -> bool:
        """True if complete_stream is a real streaming implementation."""
        return False

    @abstractmethod
    async def available(self) -> bool:
        """Lightweight reachability check — used by /health."""
