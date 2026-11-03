# Docker Compose environment

The checked-in `docker-compose.yml` describes a Linux operations deployment, not an isolated developer demo. It starts two Selenium bot containers, the Admin Manager, and Caddy. Caddy binds host ports 80 and 443; the manager has access to the Docker socket and the configured instance directories.

Do not run this stack on a machine with valuable Docker workloads, real local instance data, or another service using ports 80/443. Use a disposable Linux host or dedicated isolated Docker environment. Do not point it at a production Docker socket.

## Requirements

- Linux with Docker Engine and Docker Compose v2.
- Prepared `instances/bot1/` and `instances/bot2/` directories.
- Placeholder-derived `.env` and `appconfig.json` files with local credentials.
- Admin Manager environment variables and a Caddy configuration whose hostnames match the test environment.
- Ports 80 and 443 available, or a separately prepared Compose override for isolated ports.

See [Deployment and recovery](DEPLOY.md) for required server settings and secrets. `deploy/examples/` contains safe placeholders. Never copy real credentials into tracked files.

## Validate and build without starting containers

After preparing all required environment variables and directories, validate the configuration and build only:

```bash
docker compose config --quiet
docker compose build gymbeam-bot-1 gymbeam-admin-manager
```

These commands do not start the services. CI performs this validation using temporary example files.

## Start and stop a prepared isolated environment

Only after confirming the target Docker daemon, bind mounts, and host ports are isolated:

```bash
docker compose up -d
docker compose ps
docker compose logs --tail=100 gymbeam-admin-manager caddy
```

To stop the stack:

```bash
docker compose down
```

`docker compose down` stops and removes the Compose containers and network, but named volumes persist. Do not use `docker compose down --volumes` unless intentionally discarding the Admin Manager's registry/session data and Caddy state in that disposable environment.

For the safe local code and test workflow, see [Development and verification](docs/DEVELOPMENT.md); it does not require launching this stack.
