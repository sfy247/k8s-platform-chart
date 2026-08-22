# ─────────────────────────────────────────────────────────────
# Multi-stage build: the toolchain never reaches the runtime image.
# ─────────────────────────────────────────────────────────────
FROM python:3.13-slim AS builder

# uv resolves and installs dependencies from the committed uv.lock.
COPY --from=ghcr.io/astral-sh/uv:0.12.5 /uv /usr/local/bin/uv

ENV UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    UV_PYTHON_DOWNLOADS=never

WORKDIR /app

# Dependencies first, in their own layer: source changes then rebuild in
# seconds instead of re-resolving the whole dependency tree.
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

# Only the virtualenv — no uv, no build tools, no source tree.
COPY --from=builder /app/.venv /app/.venv

# Numeric uid, matching the chart's podSecurityContext.runAsUser. The chart
# also enforces runAsNonRoot, so a root image would be rejected at admission.
USER 1000:1000

EXPOSE 8000

CMD ["uvicorn", "hello_python.main:app", "--host", "0.0.0.0", "--port", "8000"]
