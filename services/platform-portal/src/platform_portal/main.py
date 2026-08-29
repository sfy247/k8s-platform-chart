"""HTTP surface for the platform portal.

    GET /            the page
    GET /api/apps    the same data as JSON, for scripts
    GET /healthz     liveness
    GET /readyz      readiness — false until the first discovery succeeds
    GET /metrics     Prometheus

The portal never blocks a page load on probing the cluster. A background
task refreshes state on an interval and the handlers read a cache, so a slow
or unreachable app cannot make the portal itself slow.
"""

from __future__ import annotations

import asyncio
import json
import logging
import socket
import sys
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from fastapi import FastAPI, Request
from fastapi.responses import HTMLResponse, JSONResponse, Response
from fastapi.templating import Jinja2Templates
from prometheus_client import CONTENT_TYPE_LATEST, Counter, Gauge, generate_latest

from platform_portal import __version__
from platform_portal.config import Settings
from platform_portal.discovery import (
    App, apps_from_ingresses, apps_from_services, service_port_index,
)
from platform_portal.health import probe_all
from platform_portal.kube import KubeClient, NotInCluster

HOSTNAME = socket.gethostname()

APPS_DISCOVERED = Gauge("portal_apps_discovered", "Applications discovered from Ingresses.")
APPS_BY_STATUS = Gauge("portal_apps_by_status", "Applications by health status.", ["status"])
REFRESH_FAILURES = Counter("portal_refresh_failures_total", "Failed discovery refreshes.")
REFRESHES = Counter("portal_refreshes_total", "Completed discovery refreshes.")


class JsonFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        payload: dict[str, Any] = {
            "timestamp": self.formatTime(record, "%Y-%m-%dT%H:%M:%S%z"),
            "severity": record.levelname,
            "service": "platform-portal",
            "message": record.getMessage(),
            "hostname": HOSTNAME,
        }
        if record.exc_info:
            payload["error"] = self.formatException(record.exc_info)
        return json.dumps(payload)


def configure_logging(level: str = "INFO") -> None:
    handler = logging.StreamHandler(sys.stdout)
    handler.setFormatter(JsonFormatter())
    logging.basicConfig(level=level, handlers=[handler], force=True)
    for name in ("uvicorn", "uvicorn.error", "uvicorn.access"):
        uvicorn_logger = logging.getLogger(name)
        uvicorn_logger.handlers.clear()
        uvicorn_logger.propagate = True


settings = Settings.from_env()
configure_logging()
logger = logging.getLogger(__name__)

TEMPLATES = Jinja2Templates(directory=str(Path(__file__).parent / "templates"))


class State:
    """Cache of the last successful discovery."""

    def __init__(self) -> None:
        self.apps: list[App] = []
        self.last_refresh: datetime | None = None
        self.last_error: str = ""
        self.ready = False


state = State()


async def refresh(client: KubeClient) -> None:
    namespaces = settings.namespaces or None
    ingresses = await client.list_ingresses(namespaces)
    services = await client.list_services(namespaces)
    apps = apps_from_ingresses(
        ingresses,
        url_suffix=settings.url_suffix,
        default_health_path=settings.default_health_path,
        platform_namespaces=settings.platform_namespaces,
        port_index=service_port_index(services),
    )

    # Apps that serve no HTTP still belong on an overview of what is running.
    linked = {(a.namespace, a.service) for a in apps}
    apps += apps_from_services(
        services,
        already_seen=linked,
        label_selector=settings.internal_app_label,
        default_health_path=settings.default_health_path,
        platform_namespaces=settings.platform_namespaces,
    )
    apps.sort(key=lambda a: (a.is_platform, a.internal, a.namespace, a.name))

    apps = await probe_all(apps, settings.probe_timeout_seconds)

    state.apps = apps
    state.last_refresh = datetime.now(UTC)
    state.last_error = ""
    state.ready = True

    APPS_DISCOVERED.set(len(apps))
    counts = {"healthy": 0, "unhealthy": 0, "unreachable": 0, "unknown": 0}
    for app in apps:
        counts[app.status] = counts.get(app.status, 0) + 1
    for status, count in counts.items():
        APPS_BY_STATUS.labels(status).set(count)
    REFRESHES.inc()
    logger.info("refreshed: %d apps, %d healthy", len(apps), counts["healthy"])


async def refresh_loop(client: KubeClient) -> None:
    while True:
        try:
            await refresh(client)
        except asyncio.CancelledError:
            raise
        except Exception as exc:  # keep the loop alive; the portal is a dashboard
            REFRESH_FAILURES.inc()
            state.last_error = f"{type(exc).__name__}: {exc}"
            logger.exception("refresh failed")
        await asyncio.sleep(settings.refresh_seconds)


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    logger.info("starting up, version %s", __version__)
    try:
        client = KubeClient()
    except NotInCluster as exc:
        logger.error("no Kubernetes credentials: %s", exc)
        yield
        return

    task = asyncio.create_task(refresh_loop(client))
    try:
        yield
    finally:
        state.ready = False
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
        await client.close()
        logger.info("shut down cleanly")


app = FastAPI(title="platform-portal", version=__version__, lifespan=lifespan)


@app.get("/", response_class=HTMLResponse)
async def index(request: Request) -> Response:
    return TEMPLATES.TemplateResponse(
        request,
        "index.html",
        {
            "title": settings.title,
            "apps": [a for a in state.apps if not a.is_platform],
            "platform": [a for a in state.apps if a.is_platform],
            "last_refresh": state.last_refresh,
            "last_error": state.last_error,
            "refresh_seconds": settings.refresh_seconds,
            "version": __version__,
        },
    )


@app.get("/api/apps")
async def api_apps() -> JSONResponse:
    return JSONResponse(
        {
            "last_refresh": state.last_refresh.isoformat() if state.last_refresh else None,
            "last_error": state.last_error,
            "apps": [
                {
                    "name": a.name,
                    "namespace": a.namespace,
                    "url": a.url,
                    "status": a.status,
                    "status_code": a.status_code,
                    "latency_ms": a.latency_ms,
                    "detail": a.detail,
                    "platform": a.is_platform,
                    "internal": a.internal,
                }
                for a in state.apps
            ],
        }
    )


@app.get("/healthz")
async def healthz() -> dict[str, str]:
    """Liveness — the process is up. Deliberately independent of the cluster."""
    return {"status": "ok"}


@app.get("/readyz")
async def readyz() -> JSONResponse:
    """Readiness — false until the first discovery has succeeded."""
    if not state.ready:
        return JSONResponse({"status": "not ready", "reason": state.last_error}, status_code=503)
    return JSONResponse({"status": "ready"})


@app.get("/metrics")
async def metrics() -> Response:
    return Response(generate_latest(), media_type=CONTENT_TYPE_LATEST)
