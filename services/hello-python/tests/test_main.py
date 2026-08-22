"""Behaviour tests — no cluster, no network, no container required."""

from fastapi.testclient import TestClient

from hello_python.main import app


def test_hello_returns_greeting_and_hostname() -> None:
    with TestClient(app) as client:
        response = client.get("/")
    assert response.status_code == 200
    body = response.json()
    assert body["message"].endswith("from hello-python")
    assert body["hostname"]


def test_liveness_is_always_ok_while_the_process_runs() -> None:
    with TestClient(app) as client:
        assert client.get("/healthz").status_code == 200


def test_readiness_is_ready_inside_the_lifespan() -> None:
    with TestClient(app) as client:
        response = client.get("/readyz")
    assert response.status_code == 200
    assert response.json()["status"] == "ready"


def test_readiness_reports_503_before_startup_completes() -> None:
    # No TestClient context manager => lifespan never ran => not ready.
    client = TestClient(app)
    assert client.get("/readyz").status_code == 503
