#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

TEST_SUFFIX="${GITHUB_RUN_ID:-local}-$$"
TEST_SUFFIX="${TEST_SUFFIX//[^A-Za-z0-9_.-]/-}"
OLD_IMAGE="gymbeam-managed-deploy-test:${TEST_SUFFIX}-old"
NEW_IMAGE="gymbeam-managed-deploy-test:${TEST_SUFFIX}-latest"
NETWORK_NAME="gymbeam-managed-deploy-test-${TEST_SUFFIX}"
RUN_LABEL="com.gymbeam.deploy-test=${TEST_SUFFIX}"
IHOR_CONTAINER="gymbeam-shifts-bot-ihor-${TEST_SUFFIX}"
ANDRIANA_CONTAINER="gymbeam-shifts-bot-andriana-${TEST_SUFFIX}"
TEST_ROOT="$(mktemp -d)"
SNAPSHOT_DIR="${TEST_ROOT}/snapshot"
MOUNT_DIR="${TEST_ROOT}/instance-bot-ihor"

cleanup() {
  local ids
  ids="$(docker ps -aq --filter "label=${RUN_LABEL}" || true)"
  if [[ -n "$ids" ]]; then
    docker rm -f $ids >/dev/null 2>&1 || true
  fi
  docker network rm "$NETWORK_NAME" >/dev/null 2>&1 || true
  docker image rm "$OLD_IMAGE" "$NEW_IMAGE" >/dev/null 2>&1 || true
  rm -rf -- "$TEST_ROOT"
}
trap cleanup EXIT

mkdir -p "$MOUNT_DIR" "$SNAPSHOT_DIR"
printf '%s\n' 'preserved' >"${MOUNT_DIR}/marker.txt"

docker build \
  --build-arg FIXTURE_VERSION=old \
  --tag "$OLD_IMAGE" \
  scripts/tests/fixtures/managed-bot >/dev/null
docker build \
  --build-arg FIXTURE_VERSION=new \
  --tag "$NEW_IMAGE" \
  scripts/tests/fixtures/managed-bot >/dev/null
docker network create "$NETWORK_NAME" >/dev/null

create_fixture_bot() {
  local name="$1"
  local bot_id="$2"
  docker create \
    --name "$name" \
    --label com.gymbeam.managed=true \
    --label com.gymbeam.role=bot \
    --label "com.gymbeam.bot-id=${bot_id}" \
    --label "$RUN_LABEL" \
    --env PRESERVED_ENV=preserved-value \
    --mount "type=bind,source=${MOUNT_DIR},target=/app/instance" \
    --network "$NETWORK_NAME" \
    --memory 64m \
    --shm-size 32m \
    --restart unless-stopped \
    "$OLD_IMAGE"
}

IHOR_OLD_ID="$(create_fixture_bot "$IHOR_CONTAINER" bot-ihor)"
ANDRIANA_OLD_ID="$(create_fixture_bot "$ANDRIANA_CONTAINER" bot-andriana)"
docker start "$IHOR_CONTAINER" >/dev/null

for attempt in {1..20}; do
  [[ "$(docker inspect --format '{{.State.Health.Status}}' "$IHOR_CONTAINER")" == "healthy" ]] && break
  sleep 1
done
[[ "$(docker inspect --format '{{.State.Health.Status}}' "$IHOR_CONTAINER")" == "healthy" ]]

export MANAGED_BOT_TARGET_IMAGE="$NEW_IMAGE"
export MANAGED_BOT_HEALTH_ATTEMPTS=20
export MANAGED_BOT_HEALTH_DELAY_SECONDS=1
. scripts/managed-bot-deploy.sh

snapshot_managed_bots "$SNAPSHOT_DIR"
recreate_managed_bots "$SNAPSHOT_DIR" "test-${TEST_SUFFIX}"

NEW_IMAGE_ID="$(docker image inspect --format '{{.Id}}' "$NEW_IMAGE")"
IHOR_NEW_ID="$(docker inspect --format '{{.Id}}' "$IHOR_CONTAINER")"
ANDRIANA_NEW_ID="$(docker inspect --format '{{.Id}}' "$ANDRIANA_CONTAINER")"

[[ "$IHOR_NEW_ID" != "$IHOR_OLD_ID" ]]
[[ "$ANDRIANA_NEW_ID" != "$ANDRIANA_OLD_ID" ]]
[[ "$(docker inspect --format '{{.Image}}' "$IHOR_CONTAINER")" == "$NEW_IMAGE_ID" ]]
[[ "$(docker inspect --format '{{.Image}}' "$ANDRIANA_CONTAINER")" == "$NEW_IMAGE_ID" ]]
[[ "$(docker inspect --format '{{.State.Running}}' "$IHOR_CONTAINER")" == "true" ]]
[[ "$(docker inspect --format '{{.State.Health.Status}}' "$IHOR_CONTAINER")" == "healthy" ]]
[[ "$(docker inspect --format '{{.State.Running}}' "$ANDRIANA_CONTAINER")" == "false" ]]
[[ "$(docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$IHOR_CONTAINER")" == *"PRESERVED_ENV=preserved-value"* ]]
[[ "$(docker inspect --format '{{index .Config.Labels "com.gymbeam.bot-id"}}' "$IHOR_CONTAINER")" == "bot-ihor" ]]
[[ "$(docker inspect --format '{{.HostConfig.Memory}}' "$IHOR_CONTAINER")" == "805306368" ]]
[[ "$(docker inspect --format '{{.HostConfig.ShmSize}}' "$IHOR_CONTAINER")" == "33554432" ]]
[[ "$(docker inspect --format '{{.HostConfig.RestartPolicy.Name}}' "$IHOR_CONTAINER")" == "unless-stopped" ]]
[[ "$(docker inspect --format '{{.HostConfig.NetworkMode}}' "$IHOR_CONTAINER")" == "$NETWORK_NAME" ]]
[[ "$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "/app/instance"}}{{.Source}}{{end}}{{end}}' "$IHOR_CONTAINER")" == "$MOUNT_DIR" ]]
docker exec "$IHOR_CONTAINER" curl --fail --silent http://127.0.0.1:8080/healthz >/dev/null

remove_managed_bot_backups "$SNAPSHOT_DIR"
! docker inspect "$IHOR_OLD_ID" >/dev/null 2>&1
! docker inspect "$ANDRIANA_OLD_ID" >/dev/null 2>&1

echo "Managed bot deployment test passed for bot-ihor and bot-andriana."
