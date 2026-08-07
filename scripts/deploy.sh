#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

BRANCH="${DEPLOY_BRANCH:-main}"
BOT_SERVICES=(gymbeam-bot-1 gymbeam-bot-2)
REQUIRED_ENV_KEYS=(
  GYMBEAM_AUTH_LOGIN
  GYMBEAM_AUTH_PASSWORD
  GYMBEAM_TELEGRAM_BOT_TOKEN
  GYMBEAM_TELEGRAM_CHAT_ID
  GYMBEAM_ADMIN_USER
  GYMBEAM_ADMIN_PASSWORD
  GYMBEAM_ADMIN_TOKEN_SECRET
)

require_instance_files() {
  local instance="$1"
  local env_path="instances/${instance}/.env"
  local config_path="instances/${instance}/appconfig.json"

  if [[ ! -s "$env_path" ]]; then
    echo "ERROR: ${env_path} is missing or empty."
    exit 1
  fi

  if [[ ! -s "$config_path" ]]; then
    echo "ERROR: ${config_path} is missing or empty."
    exit 1
  fi

  local key
  for key in "${REQUIRED_ENV_KEYS[@]}"; do
    if ! grep -Eq "^${key}=.+$" "$env_path"; then
      echo "ERROR: ${key} is missing or empty in ${env_path}."
      exit 1
    fi
  done
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
      echo "${service} is healthy."
      return 0
    fi

    if [[ "$status" == "unhealthy" || "$status" == "exited" || "$status" == "dead" ]]; then
      echo "ERROR: ${service} entered state ${status}."
      docker compose logs --tail=100 "$service"
      return 1
    fi

    sleep 5
  done

  echo "ERROR: ${service} did not become healthy in time."
  docker compose logs --tail=100 "$service"
  return 1
}

remove_legacy_container() {
  local container_name="$1"
  if docker container inspect "$container_name" >/dev/null 2>&1; then
    echo "Removing legacy container: ${container_name}"
    docker rm -f "$container_name" >/dev/null
  fi
}

echo "Deploying branch: ${BRANCH}"
git fetch origin "$BRANCH"
git checkout "$BRANCH"
git pull --ff-only origin "$BRANCH"

require_instance_files bot1
require_instance_files bot2

mkdir -p instances/bot1/runtime-data instances/bot2/runtime-data backups

BACKUP_DIR="backups/$(date -u +%Y%m%dT%H%M%SZ)"
install -d -m 700 "$BACKUP_DIR"
cp instances/bot1/.env "$BACKUP_DIR/bot1.env"
cp instances/bot1/appconfig.json "$BACKUP_DIR/bot1.appconfig.json"
cp instances/bot2/.env "$BACKUP_DIR/bot2.env"
cp instances/bot2/appconfig.json "$BACKUP_DIR/bot2.appconfig.json"
chmod 600 "$BACKUP_DIR"/*
echo "Runtime configuration backup created: ${BACKUP_DIR}"

docker compose config --quiet
docker compose build gymbeam-bot-1

# These names belong to the previous single-bot/Nginx deployment only.
remove_legacy_container gymbeam-shifts-bot
remove_legacy_container gymbeam-nginx

for service in "${BOT_SERVICES[@]}"; do
  docker compose up -d --no-deps "$service"
  wait_for_healthy "$service"
done

docker compose up -d --remove-orphans caddy

curl --fail --silent --show-error --retry 12 --retry-delay 5 --retry-all-errors \
  https://bot1.mapa-svietidiel.sk/healthz >/dev/null
curl --fail --silent --show-error --retry 12 --retry-delay 5 --retry-all-errors \
  https://bot2.mapa-svietidiel.sk/healthz >/dev/null

docker image prune -f

echo "Deploy finished successfully."
docker compose ps
