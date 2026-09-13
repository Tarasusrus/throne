#!/usr/bin/env bash
# Сверить развёрнутый инстанс (~/.throne/app) с этим репозиторием: манифесты
# промптов, весь skills/ и sha сборки (`throne status` → version: X+sha против
# git rev-parse --short HEAD) — то, что install-local.sh копирует в бандл
# напрямую из репо, в обход publish. Молчаливый дрейф (инстанс собран из
# старого коммита, но жив и отвечает на /health) иначе не виден — см. intent
# 13.09.
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

repo_sha="$(git -C "$REPO_ROOT" rev-parse --short HEAD)"
instance_version=""
if [[ -x "$APP_DIR/throne" ]]; then
  instance_version="$("$APP_DIR/throne" status 2>/dev/null | sed -n 's/^version: *//p' || true)"
fi
if [[ -z "$instance_version" ]]; then
  echo "инстанс не запущен — не могу сверить sha сборки (запусти его или сравни вручную)" >&2
  drift=1
elif [[ "$instance_version" != *+* ]]; then
  echo "версия инстанса без sha сборки ($instance_version) — старая сборка, это дрейф" >&2
  drift=1
else
  instance_sha="${instance_version##*+}"
  if [[ "$instance_sha" != "$repo_sha"* && "$repo_sha" != "$instance_sha"* ]]; then
    echo "sha инстанса ($instance_sha) не совпадает с sha репозитория ($repo_sha)" >&2
    drift=1
  fi
fi

if [[ $drift -ne 0 ]]; then
  echo "инстанс разошёлся с репозиторием — переустанови: ./scripts/install-local.sh" >&2
  exit 1
fi

echo "инстанс соответствует репозиторию ($repo_sha)"
