"""Configuration, read from the environment once at startup.

Kept in one place so no other module reaches into os.environ — that is what
makes the service testable and its failure modes obvious.
"""

from __future__ import annotations

import os
from dataclasses import dataclass


class ConfigError(RuntimeError):
    """Raised when the environment cannot produce a valid configuration."""


@dataclass(frozen=True, slots=True)
class Settings:
    greeting: str
    log_level: str
    port: int

    @classmethod
    def from_env(cls) -> Settings:
        port_raw = os.environ.get("PORT", "8000")
        try:
            port = int(port_raw)
        except ValueError as exc:
            raise ConfigError(f"PORT must be an integer, got {port_raw!r}") from exc
        if not 1 <= port <= 65535:
            raise ConfigError(f"PORT out of range: {port}")

        return cls(
            greeting=os.environ.get("GREETING", "hello"),
            log_level=os.environ.get("LOG_LEVEL", "INFO").upper(),
            port=port,
        )
