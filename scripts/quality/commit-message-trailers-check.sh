#!/usr/bin/env bash
# Гейт commit-message-trailers: каждый коммит origin/master..HEAD проверяется
# тем же правилом, что commit-msg хук (scripts/git-hooks/commit-msg) — одна
# реализация, не две. Зелёный на master без новых коммитов.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HOOK="$ROOT/scripts/git-hooks/commit-msg"

if ! git rev-parse --verify -q origin/master >/dev/null; then
  echo "commit-message-trailers: ref origin/master не найден (git fetch origin master?)" >&2
  exit 1
fi

commits="$(git rev-list origin/master..HEAD)"
if [[ -z "$commits" ]]; then
  exit 0
fi

status=0
tmp_msg="$(mktemp)"
trap 'rm -f "$tmp_msg"' EXIT

while IFS= read -r sha; do
  git log -n1 --format=%B "$sha" > "$tmp_msg"
  # pipefail (set above) makes the pipeline's exit code the hook's, not sed's.
  if ! "$HOOK" "$tmp_msg" 2>&1 | sed "s/^/commit-message-trailers: $sha: /" >&2; then
    status=1
  fi
done <<< "$commits"

exit "$status"
