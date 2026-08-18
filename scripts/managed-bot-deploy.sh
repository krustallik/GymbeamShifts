#!/usr/bin/env bash

# This file is sourced by deploy.sh and its integration test.

MANAGED_BOT_TARGET_IMAGE="${MANAGED_BOT_TARGET_IMAGE:-gymbeam-shifts-bot:latest}"
MANAGED_BOT_HEALTH_ATTEMPTS="${MANAGED_BOT_HEALTH_ATTEMPTS:-36}"
MANAGED_BOT_HEALTH_DELAY_SECONDS="${MANAGED_BOT_HEALTH_DELAY_SECONDS:-5}"

validate_managed_bot_name() {
  local name="$1"
  [[ "$name" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*$ ]] \
    && [[ "$name" != *".deploy-backup-"* ]]
}

discover_managed_bot_ids() {
  local id name managed role
  while IFS= read -r id; do
    [[ -n "$id" ]] || continue
    name="$(docker inspect --format '{{.Name}}' "$id")"
    name="${name#/}"
    validate_managed_bot_name "$name" || {
      echo "ERROR: invalid managed bot container name: ${name}" >&2
      return 1
    }
    managed="$(docker inspect --format '{{index .Config.Labels "com.gymbeam.managed"}}' "$id")"
    role="$(docker inspect --format '{{index .Config.Labels "com.gymbeam.role"}}' "$id")"
    if [[ "$managed" != "true" || "$role" != "bot" ]]; then
      echo "ERROR: managed bot identity changed during discovery: ${name}" >&2
      return 1
    fi
    printf '%s\n' "$id"
  done < <(docker ps -aq \
    --filter label=com.gymbeam.managed=true \
    --filter label=com.gymbeam.role=bot)
}

snapshot_managed_bots() {
  local snapshot_dir="$1"
  local bots_dir="${snapshot_dir}/managed-bots"
  install -d -m 700 "$bots_dir"

  local discovered id name running
  discovered="$(discover_managed_bot_ids)" || return 1
  while IFS= read -r id; do
    [[ -n "$id" ]] || continue
    name="$(docker inspect --format '{{.Name}}' "$id")"
    name="${name#/}"
    running="$(docker inspect --format '{{.State.Running}}' "$id")"
    docker inspect "$id" >"${bots_dir}/${name}.inspect.json"
    printf '%s\n' "$id" >"${bots_dir}/${name}.id"
    printf '%s\n' "$running" >"${bots_dir}/${name}.running"
    chmod 600 "${bots_dir}/${name}.inspect.json" \
      "${bots_dir}/${name}.id" "${bots_dir}/${name}.running"
  done <<<"$discovered"
}

managed_bot_names_from_snapshot() {
  local snapshot_dir="$1"
  local inspect_file name
  shopt -s nullglob
  for inspect_file in "${snapshot_dir}/managed-bots/"*.inspect.json; do
    name="$(basename "$inspect_file" .inspect.json)"
    validate_managed_bot_name "$name" || {
      echo "ERROR: invalid managed bot snapshot name: ${name}" >&2
      shopt -u nullglob
      return 1
    }
    printf '%s\n' "$name"
  done
  shopt -u nullglob
}

wait_for_managed_bot_healthy() {
  local name="$1"
  local attempt status
  for ((attempt = 1; attempt <= MANAGED_BOT_HEALTH_ATTEMPTS; attempt++)); do
    status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$name")"
    if [[ "$status" == "healthy" ]]; then
      docker exec "$name" curl --fail --silent --show-error \
        http://127.0.0.1:8080/healthz >/dev/null
      return 0
    fi
    if [[ "$status" == "unhealthy" || "$status" == "exited" || "$status" == "dead" ]]; then
      docker logs --tail=100 "$name" || true
      return 1
    fi
    sleep "$MANAGED_BOT_HEALTH_DELAY_SECONDS"
  done
  docker logs --tail=100 "$name" || true
  echo "ERROR: managed bot did not become healthy: ${name}" >&2
  return 1
}

create_managed_bot_from_snapshot() {
  local snapshot_dir="$1"
  local name="$2"
  local inspect_file="${snapshot_dir}/managed-bots/${name}.inspect.json"
  local payload_file="${snapshot_dir}/managed-bots/${name}.create.json"
  local response_file="${snapshot_dir}/managed-bots/${name}.create-response.json"
  local api_version http_status

  command -v python3 >/dev/null 2>&1 || {
    echo "ERROR: python3 is required to recreate managed bot containers." >&2
    return 1
  }
  python3 scripts/build-managed-bot-payload.py \
    "$inspect_file" "$MANAGED_BOT_TARGET_IMAGE" "$payload_file"
  chmod 600 "$payload_file"
  api_version="$(docker version --format '{{.Server.APIVersion}}')"
  [[ "$api_version" =~ ^[0-9]+\.[0-9]+$ ]] || {
    echo "ERROR: invalid Docker API version: ${api_version}" >&2
    return 1
  }
  http_status="$(curl --silent --show-error \
    --unix-socket /var/run/docker.sock \
    --output "$response_file" \
    --write-out '%{http_code}' \
    --header 'Content-Type: application/json' \
    --request POST \
    --data-binary "@${payload_file}" \
    "http://localhost/v${api_version}/containers/create?name=${name}")"
  chmod 600 "$response_file"
  if [[ "$http_status" != "201" ]]; then
    echo "ERROR: Docker failed to recreate ${name} (HTTP ${http_status})." >&2
    sed -n '1,20p' "$response_file" >&2
    return 1
  fi
}

recreate_managed_bots() {
  local snapshot_dir="$1"
  local deploy_id="$2"
  local snapshot_names name old_id was_running backup_name expected_image actual_image
  snapshot_names="$(managed_bot_names_from_snapshot "$snapshot_dir")" || return 1
  expected_image="$(docker image inspect --format '{{.Id}}' "$MANAGED_BOT_TARGET_IMAGE")"

  while IFS= read -r name; do
    [[ -n "$name" ]] || continue
    old_id="$(<"${snapshot_dir}/managed-bots/${name}.id")"
    was_running="$(<"${snapshot_dir}/managed-bots/${name}.running")"
    backup_name="${name}.deploy-backup-${deploy_id}"

    if [[ "$was_running" == "true" ]]; then
      docker stop "$old_id" >/dev/null
    fi
    docker rename "$old_id" "$backup_name"
    create_managed_bot_from_snapshot "$snapshot_dir" "$name"
    actual_image="$(docker inspect --format '{{.Image}}' "$name")"
    if [[ "$actual_image" != "$expected_image" ]]; then
      echo "ERROR: ${name} was recreated with unexpected image ${actual_image}." >&2
      return 1
    fi

    if [[ "$was_running" == "true" ]]; then
      docker start "$name" >/dev/null
      wait_for_managed_bot_healthy "$name"
    fi
  done <<<"$snapshot_names"
}

restore_managed_bots() {
  local snapshot_dir="$1"
  local snapshot_names name old_id was_running current_id
  snapshot_names="$(managed_bot_names_from_snapshot "$snapshot_dir")" || return 1

  while IFS= read -r name; do
    [[ -n "$name" ]] || continue
    old_id="$(<"${snapshot_dir}/managed-bots/${name}.id")"
    was_running="$(<"${snapshot_dir}/managed-bots/${name}.running")"
    current_id="$(docker inspect --format '{{.Id}}' "$name" 2>/dev/null || true)"
    if [[ -n "$current_id" && "$current_id" != "$old_id" ]]; then
      docker rm -f "$current_id" >/dev/null
    fi
    if docker inspect "$old_id" >/dev/null 2>&1; then
      current_id="$(docker inspect --format '{{.Name}}' "$old_id")"
      if [[ "$current_id" != "/${name}" ]]; then
        docker rename "$old_id" "$name"
      fi
      if [[ "$was_running" == "true" ]]; then
        docker start "$old_id" >/dev/null
      fi
    fi
  done <<<"$snapshot_names"
}

remove_managed_bot_backups() {
  local snapshot_dir="$1"
  local snapshot_names name old_id
  snapshot_names="$(managed_bot_names_from_snapshot "$snapshot_dir")" || return 1
  while IFS= read -r name; do
    [[ -n "$name" ]] || continue
    old_id="$(<"${snapshot_dir}/managed-bots/${name}.id")"
    docker rm "$old_id" >/dev/null
  done <<<"$snapshot_names"
}
