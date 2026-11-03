# Deployment and recovery runbook

This document describes the checked-in deployment workflow. It deliberately contains no deployment IP addresses, private hostnames, or SSH key paths. Set the values for your own environment in the protected GitHub Environment and server-local configuration.

## Deployment layout

The Compose deployment includes a shift bot image, the Admin Manager, and Caddy. Each bot runs in an isolated container with a separate `.env`, `appconfig.json`, and `runtime-data/` directory. The manager stores its registry, sessions, and audit log in a Docker volume. Caddy routes HTTPS traffic to the manager and bot administration UIs.

The repository also supports bots provisioned dynamically through the Admin Manager. Deployment scripts identify managed bot containers by the labels `com.gymbeam.managed=true` and `com.gymbeam.role=bot`, rather than relying on fixed container names.

## Host prerequisites

- Linux host with Docker Engine and Docker Compose v2.
- Git checkout of this repository on the deployment host; the host needs read access to the Git remote for `git fetch`.
- `curl`, `python3`, and `bash` for validation and deployment scripts.
- DNS and firewall rules for the operator-selected HTTPS hostnames. Expose the reverse proxy on ports 80 and 443; keep the bot and manager application ports private.
- Adequate memory and disk for the configured number of Chromium-based bots, Docker images, and deployment snapshots.

Before deploying, verify that the public hostnames in the server-local environment, Caddy configuration, and generated bot routes agree. The checked-in Compose and Caddy files have defaults for the original setup; they are not a source of truth for a different deployment.

## Server-local files

Create per-instance files from the placeholder examples only when the destination files do not already exist. Do not overwrite existing credentials or runtime data:

```bash
mkdir -p instances/bot1/runtime-data instances/bot2/runtime-data
cp -n deploy/examples/bot.env.example instances/bot1/.env
cp -n deploy/examples/bot.env.example instances/bot2/.env
cp -n deploy/examples/appconfig.example.json instances/bot1/appconfig.json
cp -n deploy/examples/appconfig.example.json instances/bot2/appconfig.json
```

Replace placeholders on the server with each bot's own portal, Telegram, and local administration credentials. Do not copy real values to GitHub Actions or tracked files. Protect the files and directories, for example:

```bash
chmod 700 instances/bot1 instances/bot2
chmod 600 instances/bot1/.env instances/bot1/appconfig.json
chmod 600 instances/bot2/.env instances/bot2/appconfig.json
```

The Admin Manager container runs as UID/GID `1654` and updates credential files atomically. On a Linux host, make the instance directories writable only by that account while keeping the files private:

```bash
sudo chown -R 1654:1654 instances
sudo find instances -type d -exec chmod 700 {} \;
sudo find instances -type f \( -name .env -o -name appconfig.json \) -exec chmod 600 {} \;
```

### Moving an existing bot into instance storage

1. Identify the current bot's bind-mount source directories. Review only mount paths; avoid printing the full container environment because it can contain credentials.
2. Back up its `.env`, `appconfig.json`, and runtime data to an access-controlled location outside the Git checkout.
3. Copy the bot's files into one server-local `instances/<id>/` directory without overwriting an existing instance. Keep the saved backup until the new container passes health checks.
4. If adding another bot, prepare a separate credential set and separate runtime directory for it. Do not reuse accounts or session-signing secrets unintentionally.
5. Confirm the manager can access and update the instance directory, and that each bot's bind mounts point to its own directory.

The existing-bot importer recognizes the preconfigured `bot1` and `bot2` directories only when both `.env` and `appconfig.json` exist. Bots provisioned from the dashboard are registered through the manager instead.

The Admin Manager requires its own settings through environment variables, including:

- `ADMIN_MANAGER_ADMIN_USERNAME`
- `ADMIN_MANAGER_ADMIN_PASSWORD_HASH` (PBKDF2-SHA256 format)
- `ADMIN_MANAGER_SESSION_SIGNING_KEY`
- `ADMIN_MANAGER_PUBLIC_HOST`
- `ADMIN_MANAGER_PROVISIONING_DOCKER_INSTANCES_PATH`

The Compose file also validates additional storage, Docker, and Caddy paths. Set them to absolute paths that match the host's directory layout. Set `DOCKER_SOCKET_GID` to the Docker socket's group ID when needed. Do not reuse a production secret in a development environment.

## GitHub Actions configuration

The protected GitHub Environment named `production` is used by the deploy job. Configure these environment secrets:

| Secret | Purpose |
|---|---|
| `SSH_HOST` | Deployment host address |
| `SSH_USER` | SSH account used by the workflow |
| `SSH_PRIVATE_KEY` | Private key corresponding to a server-authorized public key |
| `DEPLOY_PATH` | Absolute path to the repository checkout on the host |

The workflow runs only for a push to `main` after the validation job passes. The deployment job fetches and fast-forward pulls `main`, then runs `scripts/deploy.sh` with the previous commit recorded for snapshot and rollback.

## What CI validates

The `test` job in `.github/workflows/ci.yml`:

1. Restores and builds the .NET solution using .NET 8 and .NET 9.
2. Runs unit, integration, security, deployment, and full test passes.
3. Verifies formatting.
4. Creates temporary deployment fixtures from the safe examples.
5. Validates Compose and deployment shell/Python scripts.
6. Builds the bot and Admin Manager images.
7. Runs the managed-bot deployment fixture and validates the Caddy configuration.

## Deployment and automatic rollback

On the deployment host, `scripts/deploy.sh`:

1. Validates the current/previous commit and runtime directory boundaries.
2. Creates a timestamped directory under `runtime/deployments/` containing the prior Compose file, Caddy configuration and routes, relevant images, and snapshots of managed containers.
3. Validates Compose and Caddy configuration, then builds the bot and manager images.
4. Recreates managed bot containers from the new image while preserving their managed settings and running/stopped state.
5. Starts and health-checks the manager, reloads Caddy, and checks the configured public manager `/healthz` endpoint.
6. Removes temporary rollback resources only after the deployment succeeds.

An error invokes `scripts/rollback.sh` for that deployment snapshot. Runtime instance files remain server-local and are not replaced by a Git checkout. Maintain a separate backup policy for instance credentials and user data; deployment snapshots are for release recovery, not a substitute for off-host backups.

The Compose bot services set a 768 MiB memory limit and a 1536 MiB combined memory-plus-swap limit, with a 256 MiB shared-memory area. The deployment payload also normalizes the memory and memory-plus-swap limits of managed bot containers to those values. To inspect the resulting container limits after deployment:

```bash
docker ps -aq \
  --filter label=com.gymbeam.managed=true \
  --filter label=com.gymbeam.role=bot \
  | xargs -r docker inspect \
      --format '{{.Name}} RAM={{.HostConfig.Memory}} RAM+SWAP={{.HostConfig.MemorySwap}}'
```

Expected values are `RAM=805306368` and `RAM+SWAP=1610612736` for each managed bot.

## Manual health checks

On the host:

```bash
docker compose ps
docker compose logs --tail=100 gymbeam-admin-manager
docker compose logs --tail=100 caddy
curl -fsS "https://${ADMIN_MANAGER_PUBLIC_HOST}/healthz"
```

For each bot, check its container health and internal endpoint:

```bash
docker ps --filter label=com.gymbeam.managed=true --filter label=com.gymbeam.role=bot
docker exec CONTAINER_NAME curl --fail --silent http://127.0.0.1:8080/healthz
```

The health response should be `{"status":"ok"}`. Then sign in through the configured HTTPS hostnames and verify that each instance shows its own settings.

## Manual rollback

List available snapshots and choose the release to restore:

```bash
ls -1 runtime/deployments
bash scripts/rollback.sh "runtime/deployments/SNAPSHOT_ID"
```

The script validates that the selected snapshot is inside `runtime/deployments/`, restores the captured Compose/Caddy state and images, and recreates the managed containers. Review the command and selected snapshot ID before running it; do not use a path outside the deployment snapshot directory.

## Operational notes

- The manager requires access to the Docker socket; treat its host role as privileged even though the container drops Linux capabilities and runs with a read-only root filesystem.
- Do not publish container port 8080 directly to the internet. Route traffic through the configured HTTPS reverse proxy.
- Do not run the Compose stack on a workstation with valuable Docker workloads or existing `instances/` data. It can start containers, bind ports 80/443, and manage Docker resources.
- Keep GitHub SSH credentials in the protected `production` environment. Keep portal, Telegram, and bot UI credentials in each server-local instance directory.
