"""Probe each discovered app and record whether it is actually serving.

Probes go to the in-cluster Service address, not through the ingress. That
tests the application rather than the ingress path, and it keeps working if
DNS or the load balancer is the thing that is broken.
"""

from __future__ import annotations

import asyncio
import logging
import time

import httpx

from platform_portal.discovery import App

logger = logging.getLogger(__name__)

# Bound concurrency: a cluster with 200 apps should not open 200 sockets at
# once and look like a denial of service to itself.
MAX_CONCURRENT_PROBES = 10


async def probe(app: App, client: httpx.AsyncClient) -> App:
    started = time.perf_counter()
    try:
        response = await client.get(app.probe_url)
        app.latency_ms = round((time.perf_counter() - started) * 1000, 1)
        app.status_code = response.status_code
        if response.is_success:
            app.status, app.detail = "healthy", ""
        else:
            app.status = "unhealthy"
            app.detail = f"HTTP {response.status_code}"
    except httpx.TimeoutException:
        app.latency_ms = round((time.perf_counter() - started) * 1000, 1)
        app.status, app.detail = "unhealthy", "timed out"
    except httpx.HTTPError as exc:
        app.latency_ms = None
        app.status = "unreachable"
        # Connection errors are the normal case for an app that is down;
        # keep the message short enough to render in a tile.
        app.detail = type(exc).__name__
    return app


async def probe_all(apps: list[App], timeout_seconds: float) -> list[App]:
    semaphore = asyncio.Semaphore(MAX_CONCURRENT_PROBES)
    limits = httpx.Limits(max_connections=MAX_CONCURRENT_PROBES)
    async with httpx.AsyncClient(
        timeout=httpx.Timeout(timeout_seconds), limits=limits, follow_redirects=False
    ) as client:

        async def guarded(app: App) -> App:
            async with semaphore:
                return await probe(app, client)

        return list(await asyncio.gather(*(guarded(app) for app in apps)))
