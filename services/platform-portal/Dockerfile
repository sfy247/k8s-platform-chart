# ─────────────────────────────────────────────────────────────
# Multi-stage build. Same shape as services/hello-python.
#
# For EKS, build for the node architecture — Graviton nodes need arm64:
#   docker buildx build --platform linux/amd64,linux/arm64 --push \
#     -t <acct>.dkr.ecr.<region>.amazonaws.com/platform-portal:0.1.0 .
# ─────────────────────────────────────────────────────────────
FROM python:3.13-slim AS builder

COPY --from=ghcr.io/astral-sh/uv:0.12.5 /uv /usr/local/bin/uv

ENV UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    UV_PYTHON_DOWNLOADS=never

WORKDIR /app

COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-dev --no-install-project --no-editable

COPY src ./src
RUN uv sync --frozen --no-dev --no-editable

# ─────────────────────────────────────────────────────────────
FROM python:3.13-slim AS runtime

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PATH="/app/.venv/bin:$PATH"

WORKDIR /app

COPY --from=builder /app/.venv /app/.venv

USER 1000:1000

EXPOSE 8080

CMD ["uvicorn", "platform_portal.main:app", "--host", "0.0.0.0", "--port", "8080"]
