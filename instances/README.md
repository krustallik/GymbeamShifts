# Runtime instances

Each bot uses its own server-local files:

- `bot1/.env` and `bot1/appconfig.json`
- `bot2/.env` and `bot2/appconfig.json`
- a separate `runtime-data/` directory per bot

The real files are intentionally ignored by Git. Initialize them from
`deploy/examples/` and keep backups outside the repository before deployment.

The Admin Manager runs as UID/GID `1654` and atomically replaces each `.env`.
On Linux, make the instance directories writable only by that account before deployment:

```sh
sudo chown -R 1654:1654 instances
sudo find instances -type d -exec chmod 700 {} \;
sudo find instances -name .env -exec chmod 600 {} \;
```

Bots mount their instance directory read-only and load credentials through
`GYMBEAM_ENV_PATH`, so a container restart observes the atomic replacement.
