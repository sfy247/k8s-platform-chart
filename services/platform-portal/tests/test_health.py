"""Probing behaviour, with a stubbed transport — no cluster, no network."""

import httpx

from platform_portal.discovery import App
from platform_portal.health import probe


def make_app() -> App:
    return App(
        name="api", namespace="demo", url="http://api.example.com",
        host="api.example.com", service="api", service_port=8080,
        health_path="/healthz",
    )


async def _probe_with(handler) -> App:
    transport = httpx.MockTransport(handler)
    async with httpx.AsyncClient(transport=transport) as client:
        return await probe(make_app(), client)


async def test_2xx_is_healthy() -> None:
    app = await _probe_with(lambda r: httpx.Response(200))
    assert app.status == "healthy"
    assert app.status_code == 200
    assert app.latency_ms is not None


async def test_500_is_unhealthy_not_unreachable() -> None:
    app = await _probe_with(lambda r: httpx.Response(500))
    assert app.status == "unhealthy"
    assert app.detail == "HTTP 500"


async def test_503_from_readiness_is_unhealthy() -> None:
    app = await _probe_with(lambda r: httpx.Response(503))
    assert app.status == "unhealthy"


def _refuse(request: httpx.Request) -> httpx.Response:
    raise httpx.ConnectError("connection refused", request=request)


async def test_connection_error_is_unreachable() -> None:
    app = await _probe_with(_refuse)
    assert app.status == "unreachable"
    assert app.detail == "ConnectError"


def _timeout(request: httpx.Request) -> httpx.Response:
    raise httpx.ReadTimeout("too slow", request=request)


async def test_timeout_is_reported_as_such() -> None:
    app = await _probe_with(_timeout)
    assert app.status == "unhealthy"
    assert app.detail == "timed out"


async def test_probe_targets_the_cluster_service_not_the_ingress() -> None:
    seen = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["url"] = str(request.url)
        return httpx.Response(200)

    await _probe_with(handler)
    assert seen["url"] == "http://api.demo.svc.cluster.local:8080/healthz"
