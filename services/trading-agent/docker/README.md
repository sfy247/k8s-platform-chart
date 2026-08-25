# Docker runtime

The Compose configuration is designed for local paper-trading development.

## Safety defaults

- PostgreSQL binds to localhost only.
- The app receives credentials from `.env`, which is gitignored.
- The app container uses a read-only filesystem plus `/tmp` tmpfs.
- No restart loop is configured; unexpected failures should remain visible during testing.
- The app's broker endpoint is independently validated in C# and must resolve to `paper-api.alpaca.markets`.

## Start

```bash
cp .env.example .env
# Add PAPER credentials only.
docker compose -f docker/docker-compose.yml up --build
```

Keep `TRADING_ENABLED=false` until observation-mode validation is complete.
