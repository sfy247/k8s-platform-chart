"""Configuration, read from the environment once at startup.

Nothing here is specific to the local lab. The same image runs on EKS with
different values — which is the point of reading config rather than baking
cluster assumptions into the code.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field


class ConfigError(RuntimeError):
    """Raised when the environment cannot produce a valid configuration."""


def _int(name: str, default: int) -> int:
    raw = os.environ.get(name, str(default))
    try:
        return int(raw)
    except ValueError as exc:
        raise ConfigError(f"{name} must be an integer, got {raw!r}") from exc


def _csv(name: str) -> list[str]:
    raw = os.environ.get(name, "").strip()
    return [item.strip() for item in raw.split(",") if item.strip()]


@dataclass(frozen=True, slots=True)
class Settings:
    port: int
    title: str
    # Empty means "every namespace the ServiceAccount can read".
    namespaces: list[str] = field(default_factory=list)
    # Appended to discovered hostnames. The local lab publishes on :8090;
    # on EKS behind a real load balancer this is empty.
    url_suffix: str = ""
    # Namespaces holding platform components rather than user apps. They are
    # shown in a separate section.
    platform_namespaces: list[str] = field(default_factory=list)
    refresh_seconds: int = 30
    probe_timeout_seconds: float = 3.0
    default_health_path: str = "/healthz"
    # Services carrying this label are treated as applications even when they
    # have no Ingress. Without it a worker — something that does real work but
    # serves no page — is invisible to the portal, which is the opposite of
    # what a platform overview is for.
    internal_app_label: str = "app.kubernetes.io/name=generic-app"

    @classmethod
    def from_env(cls) -> Settings:
        port = _int("PORT", 8080)
        if not 1 <= port <= 65535:
            raise ConfigError(f"PORT out of range: {port}")
        platform = _csv("PLATFORM_NAMESPACES") or [
            "argocd",
            "observability",
            "ingress-nginx",
            "kube-system",
        ]
        return cls(
            port=port,
            title=os.environ.get("PORTAL_TITLE", "Platform Portal"),
            namespaces=_csv("WATCH_NAMESPACES"),
            url_suffix=os.environ.get("URL_SUFFIX", ""),
            platform_namespaces=platform,
            refresh_seconds=_int("REFRESH_SECONDS", 30),
            probe_timeout_seconds=float(os.environ.get("PROBE_TIMEOUT_SECONDS", "3.0")),
            default_health_path=os.environ.get("DEFAULT_HEALTH_PATH", "/healthz"),
            internal_app_label=os.environ.get(
                "INTERNAL_APP_LABEL", "app.kubernetes.io/name=generic-app"),
        )
