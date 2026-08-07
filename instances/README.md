# Runtime instances

Each bot uses its own server-local files:

- `bot1/.env` and `bot1/appconfig.json`
- `bot2/.env` and `bot2/appconfig.json`
- a separate `runtime-data/` directory per bot

The real files are intentionally ignored by Git. Initialize them from
`deploy/examples/` and keep backups outside the repository before deployment.
