from __future__ import annotations

import asyncio
from dataclasses import dataclass
import os
import time
from typing import Any

from .dependencies import import_or_install


@dataclass
class AiEndpoint:
    api_key: str
    proxy: str | None = None
    cooldown_until: float = 0

    @property
    def available(self) -> bool:
        return time.monotonic() >= self.cooldown_until

    def cool_down(self, seconds: int) -> None:
        self.cooldown_until = time.monotonic() + seconds


@dataclass(frozen=True)
class AiConfig:
    base_url: str
    model: str
    endpoints: tuple[AiEndpoint, ...]
    timeout_seconds: int = 120
    cooldown_seconds: int = 60
    max_attempts: int = 0

    @classmethod
    def from_env(cls) -> "AiConfig":
        base_url = os.environ.get("TRANSLATE_AI_BASE_URL", "").rstrip("/")
        model = os.environ.get("TRANSLATE_AI_MODEL", "")
        keys = _split_secret_list(os.environ.get("TRANSLATE_AI_KEYS", ""))
        proxies = _split_secret_list(os.environ.get("TRANSLATE_AI_PROXIES", ""))

        if not base_url:
            raise ValueError("TRANSLATE_AI_BASE_URL is required for AI translation.")
        if not model:
            raise ValueError("TRANSLATE_AI_MODEL is required for AI translation.")
        if not keys:
            raise ValueError("TRANSLATE_AI_KEYS must contain at least one key.")

        endpoints = tuple(
            AiEndpoint(api_key=key, proxy=proxies[index % len(proxies)] if proxies else None)
            for index, key in enumerate(keys)
        )

        return cls(
            base_url=base_url,
            model=model,
            endpoints=endpoints,
            timeout_seconds=int(os.environ.get("TRANSLATE_AI_TIMEOUT_SECONDS", "120")),
            cooldown_seconds=int(os.environ.get("TRANSLATE_AI_COOLDOWN_SECONDS", "60")),
            max_attempts=int(os.environ.get("TRANSLATE_AI_MAX_ATTEMPTS", "0")),
        )


class OpenAICompatibleClient:
    def __init__(self, config: AiConfig):
        self._config = config
        self._endpoint_index = 0
        self._httpx = import_or_install("httpx", "httpx>=0.27,<1")

    async def chat(self, messages: list[dict[str, str]], temperature: float = 0.1) -> str:
        attempts = 0
        last_error: Exception | None = None

        while self._config.max_attempts <= 0 or attempts < self._config.max_attempts:
            attempts += 1
            endpoint = await self._next_endpoint()

            try:
                return await self._send(endpoint, messages, temperature)
            except RateLimitedError as error:
                endpoint.cool_down(self._config.cooldown_seconds)
                last_error = error
            except TransientAiError as error:
                endpoint.cool_down(self._config.cooldown_seconds)
                last_error = error

        raise RuntimeError("AI translation failed after configured retry attempts.") from last_error

    async def _next_endpoint(self) -> AiEndpoint:
        while True:
            for _ in range(len(self._config.endpoints)):
                endpoint = self._config.endpoints[self._endpoint_index]
                self._endpoint_index = (self._endpoint_index + 1) % len(self._config.endpoints)
                if endpoint.available:
                    return endpoint

            await asyncio.sleep(1)

    async def _send(self, endpoint: AiEndpoint, messages: list[dict[str, str]], temperature: float) -> str:
        headers = {
            "Authorization": f"Bearer {endpoint.api_key}",
            "Content-Type": "application/json",
        }
        payload: dict[str, Any] = {
            "model": self._config.model,
            "messages": messages,
            "temperature": temperature,
        }
        url = f"{self._config.base_url}/chat/completions"

        try:
            async with self._httpx.AsyncClient(
                timeout=self._config.timeout_seconds,
                proxy=endpoint.proxy,
            ) as client:
                response = await client.post(url, headers=headers, json=payload)
        except self._httpx.HTTPError as error:
            raise TransientAiError(f"AI provider request failed: {error.__class__.__name__}") from None

        if response.status_code == 429:
            raise RateLimitedError("AI provider returned rate limit.")

        if response.status_code >= 500:
            raise TransientAiError(f"AI provider returned {response.status_code}.")

        if response.status_code >= 400:
            raise RuntimeError(f"AI provider returned {response.status_code}.")

        data = response.json()
        return data["choices"][0]["message"]["content"]


class RateLimitedError(RuntimeError):
    pass


class TransientAiError(RuntimeError):
    pass


def _split_secret_list(value: str) -> list[str]:
    normalized = value.replace("\r", "\n").replace(",", "\n")
    return [item.strip() for item in normalized.split("\n") if item.strip()]
