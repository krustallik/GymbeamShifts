#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"
export ADMIN_MANAGER_PROVISIONING_DOCKER_INSTANCES_PATH="${ROOT_DIR}/instances"
export DOCKER_SOCKET_GID="${DOCKER_SOCKET_GID:-$(stat -c '%g' /var/run/docker.sock)}"
if [[ ! "$DOCKER_SOCKET_GID" =~ ^[0-9]+$ ]]; then
  echo "ERROR: Docker socket group identifier is invalid." >&2
  exit 2
fi

BRANCH="${DEPLOY_BRANCH:-main}"
BOT_SERVICES=(gymbeam-bot-1 gymbeam-bot-2)
DEPLOYMENTS_DIR="${ROOT_DIR}/runtime/deployments"
DEPLOY_ID="$(date -u +%Y%m%dT%H%M%SZ)-$$"
SNAPSHOT_DIR="${DEPLOYMENTS_DIR}/${DEPLOY_ID}"
ROLLBACK_ARMED=0
MANAGER_STOPPED_BY_DEPLOY=0

rollback_on_error() {
  local exit_code=$?
  trap - ERR
  if [[ "$ROLLBACK_ARMED" == "1" ]]; then
    echo "ERROR: deployment failed; restoring snapshot ${SNAPSHOT_DIR}."
    if ! bash scripts/rollback.sh "$SNAPSHOT_DIR"; then
      echo "ERROR: automatic rollback failed; snapshot remains at ${SNAPSHOT_DIR}."
    fi
  elif [[ "$MANAGER_STOPPED_BY_DEPLOY" == "1" ]]; then
    docker compose start gymbeam-admin-manager || true
  fi
  exit "$exit_code"
}
trap rollback_on_error ERR

require_instance_files() {
  local instance="$1"
  docker run --rm \
    -v "${ROOT_DIR}/instances:/instances:ro" \
    caddy:2-alpine \
    sh -eu -c '
      instance="$1"
      env_path="/instances/${instance}/.env"
      config_path="/instances/${instance}/appconfig.json"
      test -s "$env_path" && test -s "$config_path"
      for key in GYMBEAM_AUTH_LOGIN GYMBEAM_AUTH_PASSWORD GYMBEAM_TELEGRAM_BOT_TOKEN GYMBEAM_TELEGRAM_CHAT_ID GYMBEAM_ADMIN_USER GYMBEAM_ADMIN_PASSWORD GYMBEAM_ADMIN_TOKEN_SECRET; do
        grep -Eq "^${key}=.+$" "$env_path" || exit 1
      done
      test -z "$(find "/instances/${instance}" -type l -print -quit)"
    ' sh "$instance" || {
      echo "ERROR: required managed files for ${instance} are invalid."
      return 1
    }
}

wait_for_healthy() {
  local service="$1"
  local container_id
  container_id="$(docker compose ps -q "$service")"
  if [[ -z "$container_id" ]]; then
    echo "ERROR: ${service} container was not created."
    return 1
  fi

  local attempt status
  for attempt in {1..36}; do
    status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container_id")"
    if [[ "$status" == "healthy" ]]; then
      return 0
    fi
    if [[ "$status" == "unhealthy" || "$status" == "exited" || "$status" == "dead" ]]; then
      docker compose logs --tail=100 "$service"
      return 1
    fi
    sleep 5
  done
  docker compose logs --tail=100 "$service"
  return 1
}

snapshot_image() {
  local image="$1"
  local rollback_tag="$2"
  if docker image inspect "$image" >/dev/null 2>&1; then
    docker image tag "$image" "$rollback_tag"
  fi
}

create_snapshot() {
  install -d -m 700 "$SNAPSHOT_DIR"
  git show "${DEPLOY_PREVIOUS_COMMIT}:docker-compose.yml" >"${SNAPSHOT_DIR}/docker-compose.yml"
  cp runtime/caddy/Caddyfile "${SNAPSHOT_DIR}/Caddyfile"
  printf '%s\n' "${DEPLOY_PREVIOUS_COMMIT:-unknown}" >"${SNAPSHOT_DIR}/previous-commit"

  tar -C runtime/caddy-dynamic -cf "${SNAPSHOT_DIR}/caddy-routes.tar" .
  docker run --rm \
    -v "${ROOT_DIR}/instances:/source:ro" \
    -v "${SNAPSHOT_DIR}:/backup" \
    caddy:2-alpine \
    sh -eu -c 'test -z "$(find /source -type l -print -quit)"; tar --exclude="./*/runtime-data" -C /source -cf /backup/instances-config.tar .'
  chmod 600 "${SNAPSHOT_DIR}"/*.tar "${SNAPSHOT_DIR}/previous-commit"

  local admin_volume
  admin_volume="$(docker inspect gymbeam-admin-manager --format '{{range .Mounts}}{{if eq .Destination "/app/data"}}{{.Name}}{{end}}{{end}}' 2>/dev/null || true)"
  if [[ -n "$admin_volume" ]]; then
    printf '%s\n' "$admin_volume" >"${SNAPSHOT_DIR}/admin-volume-name"
    docker run --rm \
      -v "${admin_volume}:/source:ro" \
      -v "${SNAPSHOT_DIR}:/backup" \
      caddy:2-alpine \
      tar -C /source -cf /backup/admin-manager-data.tar .
    chmod 600 "${SNAPSHOT_DIR}/admin-manager-data.tar" "${SNAPSHOT_DIR}/admin-volume-name"
  fi

  snapshot_image gymbeam-shifts-bot:latest "gymbeam-shifts-bot:rollback-${DEPLOY_ID}"
  snapshot_image gymbeam-admin-manager:latest "gymbeam-admin-manager:rollback-${DEPLOY_ID}"
}

if [[ "${DEPLOY_SKIP_UPDATE:-0}" != "1" ]]; then
  DEPLOY_PREVIOUS_COMMIT="$(git rev-parse HEAD)"
  git fetch origin "$BRANCH"
  git checkout "$BRANCH"
  git pull --ff-only origin "$BRANCH"
fi
if [[ ! "${DEPLOY_PREVIOUS_COMMIT:-}" =~ ^[0-9a-fA-F]{40}$ ]]; then
  echo "ERROR: DEPLOY_PREVIOUS_COMMIT must be a full Git commit hash." >&2
  exit 2
fi

require_instance_files bot1
require_instance_files bot2
mkdir -p instances/bot1/runtime-data instances/bot2/runtime-data backups

if [[ -L instances || -L runtime/caddy-dynamic || -L runtime/caddy ]]; then
  echo "ERROR: managed runtime roots must not be symbolic links."
  exit 1
fi

if [[ ! -e runtime/caddy-dynamic/.initialized ]]; then
  install -d -m 755 runtime/caddy-dynamic
  cp deploy/examples/caddy-dynamic/*.caddy runtime/caddy-dynamic/
  touch runtime/caddy-dynamic/.initialized
fi
if [[ ! -f runtime/caddy/Caddyfile ]]; then
  install -d -m 755 runtime/caddy
  cp Caddyfile runtime/caddy/Caddyfile
  chmod 644 runtime/caddy/Caddyfile
fi
if [[ -n "$(find runtime/caddy-dynamic -type l -print -quit)" ]]; then
  echo "ERROR: dynamic Caddy routes must not contain symbolic links."
  exit 1
fi

if docker inspect gymbeam-admin-manager >/dev/null 2>&1; then
  docker compose stop gymbeam-admin-manager
  MANAGER_STOPPED_BY_DEPLOY=1
fi
create_snapshot
ROLLBACK_ARMED=1

docker compose config --quiet
docker run --rm \
  -v "${ROOT_DIR}/Caddyfile:/etc/caddy/Caddyfile:ro" \
  -v "${ROOT_DIR}/runtime/caddy-dynamic:/etc/caddy/dynamic:ro" \
  caddy:2-alpine \
  caddy validate --config /etc/caddy/Caddyfile
docker compose build gymbeam-bot-1 gymbeam-admin-manager
cp Caddyfile runtime/caddy/.Caddyfile.new
chmod 644 runtime/caddy/.Caddyfile.new
mv -f runtime/caddy/.Caddyfile.new runtime/caddy/Caddyfile
docker run --rm --user 0 \
  -v "${ROOT_DIR}/runtime/caddy-dynamic:/target" \
  --entrypoint /bin/sh \
  gymbeam-admin-manager:latest \
  -c 'chown app:app /target && chmod 755 /target'

for service in "${BOT_SERVICES[@]}"; do
  docker compose up -d --no-deps "$service"
  wait_for_healthy "$service"
done

docker compose up -d --no-deps gymbeam-admin-manager
wait_for_healthy gymbeam-admin-manager
MANAGER_STOPPED_BY_DEPLOY=0

docker compose up -d caddy
docker compose exec -T caddy caddy reload \
  --address unix//run/caddy-admin/admin.sock \
  --config /etc/caddy/Caddyfile

curl --fail --silent --show-error --retry 12 --retry-delay 5 --retry-all-errors \
  https://bot1.mapa-svietidiel.sk/healthz >/dev/null
curl --fail --silent --show-error --retry 12 --retry-delay 5 --retry-all-errors \
  https://bot2.mapa-svietidiel.sk/healthz >/dev/null
curl --fail --silent --show-error --retry 12 --retry-delay 5 --retry-all-errors \
  "https://${ADMIN_MANAGER_PUBLIC_HOST:-admin.mapa-svietidiel.sk}/healthz" >/dev/null

ROLLBACK_ARMED=0
trap - ERR
printf '%s\n' "succeeded" >"${SNAPSHOT_DIR}/status"
chmod 600 "${SNAPSHOT_DIR}/status"
echo "Deploy ${DEPLOY_ID} finished successfully. Rollback snapshot: ${SNAPSHOT_DIR}"
docker compose ps
