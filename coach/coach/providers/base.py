"""LLM provider interface (plan §6)."""

from __future__ import annotations

from abc import ABC, abstractmethod

from coach.schemas import LLMRequest, LLMResponse


class LLMProvider(ABC):
    @property
    @abstractmethod
    def name(self) -> str:
        ...

    @abstractmethod
    async def complete(self, req: LLMRequest) -> LLMResponse:
        ...

    @abstractmethod
    async def embed(self, texts: list[str]) -> list[list[float]]:
        ...

    @abstractmethod
    def supports_vision(self) -> bool:
        ...

    @abstractmethod
    def supports_json_mode(self) -> bool:
        ...

    @abstractmethod
    async def available(self) -> bool:
        """Lightweight reachability check — used by /health."""
