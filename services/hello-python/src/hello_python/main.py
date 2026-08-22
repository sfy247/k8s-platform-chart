"""HTTP surface for the hello-python service.

Three endpoints, and the split between the last two is the point:

    GET /          the actual work
    GET /healthz   liveness  — is this process broken beyond recovery?
    GET /readyz    readiness — should this pod receive traffic right now?

Kubernetes restarts a pod that fails liveness, but only removes it from the
Service endpoints when readiness fails. Conflating them is how a slow
dependency turns into a cluster-wide restart loop.
"""

from __future__ import annotations

import json
import logging
import os
import socket
import sys
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from typing import Any

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, Response
from prometheus_client import CONTENT_TYPE_LATEST, Counter, Histogram, generate_latest

from hello_python import __version__
from hello_python.config import Settings

HOSTNAME = socket.gethostname()

# ── Metrics ───────────────────────────────────────────────────────────────
# The ingress already reports request rate, errors and latency for this app
# without any code. These add what only the app knows: its own view of the
# request, including the matched route rather than the raw URL.
#
# Keep label values BOUNDED. `path` is the route template, never the raw
# path — /users/12345 as a label value would create a new time series per
# user and eventually take Prometheus down.
REQUESTS = Counter(
    "hello_python_requests_total",
    "Total HTTP requests handled.",
    ["method", "path", "status"],
)
REQUEST_DURATION = Histogram(
    "hello_python_request_duration_seconds",
    "Request duration in seconds.",
    ["method", "path"],
)


class JsonFormatter(logging.Formatter):
    """Structured logs — one JSON object per line, which is what Loki wants."""

    def format(self, record: logging.LogRecord) -> str:
        payload: dict[str, Any] = {
            "timestamp": self.formatTime(record, "%Y-%m-%dT%H:%M:%S%z"),
            "severity": record.levelname,
            "service": "hello-python",
            "message": record.getMessage(),
            "hostname": HOSTNAME,
        }
        if record.exc_info:
            payload["error"] = self.formatException(record.exc_info)
        return json.dumps(payload)


def configure_logging(level: str) -> None:
    handler = logging.StreamHandler(sys.stdout)
    handler.setFormatter(JsonFormatter())
    logging.basicConfig(level=level, handlers=[handler], force=True)

    # uvicorn attaches its own plain-text handlers. Strip them and let its
    # records propagate to the root handler, so every line the process emits
    # is JSON — mixed formats break log parsing at the collector.
    for name in ("uvicorn", "uvicorn.error", "uvicorn.access"):
        uvicorn_logger = logging.getLogger(name)
        uvicorn_logger.handlers.clear()
        uvicorn_logger.propagate = True


settings = Settings.from_env()  # fails fast at import if the env is wrong
configure_logging(settings.log_level)
logger = logging.getLogger(__name__)

# Flipped by the lifespan handler. Readiness reads it, so Kubernetes stops
# sending traffic the moment shutdown begins — connections drain instead of
# being cut mid-request.
_ready = False


@asynccontextmanager
async def lifespan(_: FastAPI) -> AsyncIterator[None]:
    global _ready
    logger.info("starting up")
    # Real services connect to their dependencies here before going ready.
    _ready = True
    logger.info("ready to serve traffic")
    yield
    _ready = False
    logger.info("shutting down, draining traffic")


app = FastAPI(title="hello-python", version=__version__, lifespan=lifespan)


@app.middleware("http")
async def record_metrics(request: Request, call_next):  # type: ignore[no-untyped-def]
    """Time every request and count it by route, method and status."""
    route = request.url.path
    with REQUEST_DURATION.labels(request.method, route).time():
        response = await call_next(request)
    REQUESTS.labels(request.method, route, str(response.status_code)).inc()
    return response


@app.get("/metrics")
async def metrics() -> Response:
    """Prometheus scrape endpoint — see metrics.enabled in the app's values."""
    return Response(generate_latest(), media_type=CONTENT_TYPE_LATEST)


@app.get("/")
async def hello() -> dict[str, str]:
    """Say hello, and report which pod answered."""
    logger.info("handled request")
    return {
        "message": f"{settings.greeting} from hello-python",
        "version": __version__,
        "hostname": HOSTNAME,
        "namespace": os.environ.get("POD_NAMESPACE", "unknown"),
    }


@app.get("/healthz")
async def healthz() -> dict[str, str]:
    """Liveness: the process is running and the event loop responds."""
    return {"status": "ok"}


@app.get("/readyz")
async def readyz() -> JSONResponse:
    """Readiness: this pod is willing to receive traffic."""
    if not _ready:
        return JSONResponse({"status": "not ready"}, status_code=503)
    return JSONResponse({"status": "ready"})
