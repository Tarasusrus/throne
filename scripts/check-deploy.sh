#!/usr/bin/env bash
# Сверить развёрнутый инстанс (~/.throne/app) с этим репозиторием: манифесты
# промптов и SKILL.md — то, что install-local.sh копирует в бандл напрямую из
# репо, в обход publish. Молчаливый дрейф (инстанс собран из старого коммита,
# но жив и отвечает на /health) иначе не виден — см. intent 13.09.
#
#   ./scripts/check-deploy.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
THRONE_HOME="${THRONE_HOME:-$HOME/.throne}"
APP_DIR="$THRONE_HOME/app"

if [[ ! -d "$APP_DIR" ]]; then
  echo "нет развёрнутого инстанса в $APP_DIR — нечего сравнивать" >&2
  exit 1
fi

drift=0
for rel in specs/manifest skills; do
  if [[ ! -e "$APP_DIR/$rel" ]]; then
    echo "отсутствует в инстансе: $rel" >&2
    drift=1
    continue
  fi
  diff -rq --exclude=.DS_Store "$REPO_ROOT/$rel" "$APP_DIR/$rel" || drift=1
done

if [[ $drift -ne 0 ]]; then
  echo "инстанс разошёлся с репозиторием — переустанови: ./scripts/install-local.sh" >&2
  exit 1
fi

echo "инстанс соответствует репозиторию ($(git -C "$REPO_ROOT" rev-parse --short HEAD))"
