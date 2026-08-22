# hello-python

Minimal FastAPI service used to demonstrate the path from source code to a
running workload in the local lab.

| Endpoint | Purpose |
|---|---|
| `GET /` | the greeting, plus the pod hostname and namespace |
| `GET /healthz` | liveness — process is alive |
| `GET /readyz` | readiness — 503 until startup completes, 503 again while draining |

## Configuration

| Variable | Default | Notes |
|---|---|---|
| `GREETING` | `hello` | text in the response |
| `LOG_LEVEL` | `INFO` | standard logging levels |
| `PORT` | `8000` | validated at startup; a bad value fails fast |
| `POD_NAMESPACE` | `unknown` | injected by the chart via the downward API |

## Local development

```bash
uv sync                 # create .venv from uv.lock
uv run pytest           # tests, no cluster needed
uv run uvicorn hello_python.main:app --reload
```

## Container

```bash
docker build -t hello-python:0.1.0 .
docker run --rm -p 8000:8000 -u 1000:1000 --read-only hello-python:0.1.0
```

Runs as uid 1000 with a read-only root filesystem, matching what the
`generic-app` chart enforces in the cluster.

## Deploy to the lab

```bash
docker build -t hello-python:0.1.0 .
make image-import IMAGE=hello-python:0.1.0     # from the repo root
make deploy APP=hello-python
curl http://hello-python.localtest.me:8090
```

Deployment values live in `apps/hello-python/`, not here — this directory is
only the application.
