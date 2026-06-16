#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

BRANCH="${DEPLOY_BRANCH:-main}"
CONFIG_PATH="GymBeamShiftsControllerX/appconfig.json"

echo "Deploying branch: ${BRANCH}"
git fetch origin "${BRANCH}"
git checkout "${BRANCH}"
# Always discard server-local app config and use GitHub version.
if [[ -f "${CONFIG_PATH}" ]]; then
  git checkout -- "${CONFIG_PATH}"
fi
git pull --ff-only origin "${BRANCH}"

mkdir -p runtime-data

if [[ ! -f "GymBeamShiftsControllerX/.env" ]]; then
  echo "ERROR: GymBeamShiftsControllerX/.env not found on server."
  echo "Create it once manually before the first deploy."
  exit 1
fi

if [[ ! -f "nginx/.htpasswd" ]]; then
  echo "ERROR: nginx/.htpasswd not found on server."
  echo "Create it once manually before the first deploy."
  exit 1
fi

docker compose up -d --build --remove-orphans
docker image prune -f

echo "Deploy finished successfully."
docker compose ps
