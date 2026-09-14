#!/usr/bin/env bash
# Идемпотентная установка commit-msg хука: копирует scripts/git-hooks/commit-msg
# в .git/hooks/commit-msg этого чек-аута, только если файла нет или содержимое
# отличается. Вызывается из scripts/install-local.sh и напрямую исполнителем
# перед первым коммитом (клоны исполнителей install-local.sh не проходят).
set -euo pipefail

SOURCE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/commit-msg"
GIT_DIR="$(git rev-parse --git-dir)"
TARGET="$GIT_DIR/hooks/commit-msg"

if [[ -f "$TARGET" ]] && cmp -s "$SOURCE" "$TARGET"; then
  exit 0
fi

mkdir -p "$GIT_DIR/hooks"
cp "$SOURCE" "$TARGET"
chmod +x "$TARGET"
echo "commit-msg хук установлен: $TARGET"
