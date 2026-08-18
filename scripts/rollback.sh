#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEPLOYMENTS_DIR="$(realpath -m "${ROOT_DIR}/runtime/deployments")"
if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <deployment-snapshot-directory>" >&2
  exit 2
fi

SNAPSHOT_DIR="$(realpath -e -- "$1")"
case "$SNAPSHOT_DIR" in
  "${DEPLOYMENTS_DIR}"/*) ;;
  *) echo "ERROR: snapshot must be inside ${DEPLOYMENTS_DIR}." >&2; exit 2 ;;
esac
if [[ ! -d "$SNAPSHOT_DIR" || -L "$SNAPSHOT_DIR" ]]; then
  echo "ERROR: invalid deployment snapshot." >&2
  exit 2
fi

DEPLOY_ID="$(basename "$SNAPSHOT_DIR")"
if [[ ! "$DEPLOY_ID" =~ ^[0-9]{8}T[0-9]{6}Z-[0-9]+$ ]]; then
  echo "ERROR: invalid deployment snapshot identifier." >&2
  exit 2
fi

COMPOSE=(docker compose --project-directory "$ROOT_DIR" -f "${SNAPSHOT_DIR}/docker-compose.yml")
cd "$ROOT_DIR"
. "${ROOT_DIR}/scripts/managed-bot-deploy.sh"
export ADMIN_MANAGER_PROVISIONING_DOCKER_INSTANCES_PATH="${ROOT_DIR}/instances"
export DOCKER_SOCKET_GID="${DOCKER_SOCKET_GID:-$(stat -c '%g' /var/run/docker.sock)}"
if [[ ! "$DOCKER_SOCKET_GID" =~ ^[0-9]+$ ]]; then
  echo "ERROR: Docker socket group identifier is invalid." >&2
  exit 2
fi

"${COMPOSE[@]}" stop gymbeam-admin-manager caddy || true

validate_archive() {
  local archive="$1"
  local entry
  while IFS= read -r entry; do
    case "$entry" in
      /*|../*|*/../*|*/..) echo "ERROR: unsafe archive entry: ${entry}" >&2; return 1 ;;
    esac
  done < <(tar -tf "$archive")
  if tar -tvf "$archive" | awk 'substr($1,1,1) == "l" || substr($1,1,1) == "h" { found=1 } END { exit found ? 0 : 1 }'; then
    echo "ERROR: archive contains symbolic or hard links." >&2
    return 1
  fi
}

if [[ -f "${SNAPSHOT_DIR}/admin-manager-data.tar" && -f "${SNAPSHOT_DIR}/admin-volume-name" ]]; then
  validate_archive "${SNAPSHOT_DIR}/admin-manager-data.tar"
  admin_volume="$(<"${SNAPSHOT_DIR}/admin-volume-name")"
  if [[ ! "$admin_volume" =~ ^[A-Za-z0-9_.-]+$ ]] || ! docker volume inspect "$admin_volume" >/dev/null 2>&1; then
    echo "ERROR: invalid Admin Manager data volume in snapshot." >&2
    exit 1
  fi
  docker run --rm -v "${admin_volume}:/target" caddy:2-alpine \
    sh -eu -c 'find /target -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +'
  docker run --rm \
    -v "${admin_volume}:/target" \
    -v "${SNAPSHOT_DIR}:/backup:ro" \
    caddy:2-alpine \
    tar -C /target -xf /backup/admin-manager-data.tar
fi

if [[ -f "${SNAPSHOT_DIR}/Caddyfile" && ! -L "${ROOT_DIR}/runtime/caddy" ]]; then
  install -d -m 755 "${ROOT_DIR}/runtime/caddy"
  cp "${SNAPSHOT_DIR}/Caddyfile" "${ROOT_DIR}/runtime/caddy/.Caddyfile.rollback"
  chmod 644 "${ROOT_DIR}/runtime/caddy/.Caddyfile.rollback"
  mv -f "${ROOT_DIR}/runtime/caddy/.Caddyfile.rollback" "${ROOT_DIR}/runtime/caddy/Caddyfile"
fi

if [[ -f "${SNAPSHOT_DIR}/caddy-routes.tar" ]]; then
  routes_path="${ROOT_DIR}/runtime/caddy-dynamic"
  if [[ -L "$routes_path" ]]; then
    echo "ERROR: refusing to restore routes through a symbolic link." >&2
    exit 1
  fi
  validate_archive "${SNAPSHOT_DIR}/caddy-routes.tar"
  install -d -m 755 "$routes_path"
  docker run --rm \
    -v "${routes_path}:/target" \
    -v "${SNAPSHOT_DIR}:/backup:ro" \
    caddy:2-alpine \
    sh -eu -c 'find /target -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +; tar -C /target -xf /backup/caddy-routes.tar; chmod 755 /target'
fi

if [[ -f "${SNAPSHOT_DIR}/instances-config.tar" ]]; then
  validate_archive "${SNAPSHOT_DIR}/instances-config.tar"
  docker run --rm \
    -v "${ROOT_DIR}/instances:/target" \
    -v "${SNAPSHOT_DIR}:/backup:ro" \
    caddy:2-alpine \
    tar -C /target -xf /backup/instances-config.tar
fi

if docker image inspect "gymbeam-shifts-bot:rollback-${DEPLOY_ID}" >/dev/null 2>&1; then
  docker image tag "gymbeam-shifts-bot:rollback-${DEPLOY_ID}" gymbeam-shifts-bot:latest
fi
if docker image inspect "gymbeam-admin-manager:rollback-${DEPLOY_ID}" >/dev/null 2>&1; then
  docker image tag "gymbeam-admin-manager:rollback-${DEPLOY_ID}" gymbeam-admin-manager:latest
fi

if [[ "${ROLLBACK_SKIP_MANAGED_BOTS:-0}" != "1" ]]; then
  ROLLBACK_BOTS_SNAPSHOT="${SNAPSHOT_DIR}/rollback-managed-bots-$$"
  snapshot_managed_bots "$ROLLBACK_BOTS_SNAPSHOT"
  restore_current_bots_on_error() {
    local exit_code=$?
    trap - ERR
    restore_managed_bots "$ROLLBACK_BOTS_SNAPSHOT" || true
    exit "$exit_code"
  }
  trap restore_current_bots_on_error ERR
  recreate_managed_bots "$ROLLBACK_BOTS_SNAPSHOT" "rollback-${DEPLOY_ID}"
  remove_managed_bot_backups "$ROLLBACK_BOTS_SNAPSHOT"
  trap - ERR
fi

docker run --rm --user 0 \
  -v "${ROOT_DIR}/runtime/caddy-dynamic:/target" \
  --entrypoint /bin/sh \
  gymbeam-admin-manager:latest \
  -c 'chown app:app /target && chmod 755 /target'

"${COMPOSE[@]}" up -d --no-deps --force-recreate gymbeam-admin-manager
"${COMPOSE[@]}" up -d caddy

printf '%s\n' "rolled_back" >"${SNAPSHOT_DIR}/status"
chmod 600 "${SNAPSHOT_DIR}/status"
echo "Rollback ${DEPLOY_ID} completed. Managed bot containers now use the rollback image."
