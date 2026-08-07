# Local Docker run

The Compose stack contains two bot instances and Caddy:

- `gymbeam-bot-1` uses `instances/bot1/`
- `gymbeam-bot-2` uses `instances/bot2/`
- Caddy publishes ports 80 and 443

Initialize missing local runtime files from `deploy/examples/`, then provide valid
credentials in both `.env` files:

```bash
mkdir -p instances/bot1/runtime-data instances/bot2/runtime-data
cp deploy/examples/bot.env.example instances/bot1/.env
cp deploy/examples/bot.env.example instances/bot2/.env
cp deploy/examples/appconfig.example.json instances/bot1/appconfig.json
cp deploy/examples/appconfig.example.json instances/bot2/appconfig.json
```

Validate and build:

```bash
docker compose config --quiet
docker compose build gymbeam-bot-1
```

The production hostnames in `Caddyfile` resolve to the production server, so do
not start Caddy locally unless DNS or local hosts routing is intentionally set up.

Runtime files under `instances/` are ignored by Git.
