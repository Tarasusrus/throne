#!/usr/bin/env bash
# Собрать Throne из этого репозитория и поставить в рабочий инстанс (~/.throne/app).
# Данные (throne.db, workspaces/) не трогаются — меняется только код приложения.
#
#   ./scripts/install-local.sh            # сборка UI + бинаря, стоп демона, подмена, старт
#   ./scripts/install-local.sh --no-web   # переиспользовать уже собранный UI
#   ./scripts/install-local.sh --no-restart
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
THRONE_HOME="${THRONE_HOME:-$HOME/.throne}"
APP_DIR="$THRONE_HOME/app"
RID="${RID:-osx-arm64}"
PUBLISH_DIR="$REPO_ROOT/apps/api/src/Throne.Api/bin/Release/net10.0/$RID/publish"

build_web=1
restart=1
for arg in "$@"; do
  case "$arg" in
    --no-web) build_web=0 ;;
    --no-restart) restart=0 ;;
    *) echo "unknown arg: $arg" >&2; exit 2 ;;
  esac
done

# SDK стоит в ~/.dotnet (устанавливался без sudo через dotnet-install.sh).
export PATH="$HOME/.dotnet:$PATH"
command -v dotnet >/dev/null || { echo "dotnet SDK не найден" >&2; exit 1; }

if [[ $build_web -eq 1 ]]; then
  pnpm -C "$REPO_ROOT/apps/web" build
fi
# Инкрементальный publish теряет wwwroot: Content-линк apps/web/dist пересчитывается
# только на чистом выходе, иначе в publish/wwwroot остаётся пустой assets и UI отдаёт 404.
rm -rf "$REPO_ROOT/apps/api/src/Throne.Api/bin/Release/net10.0/$RID"

# Версией помечаем коммит, из которого собрано, — иначе `throne status` покажет 0.0.0-dev.
VERSION="0.0.0-local+$(git -C "$REPO_ROOT" rev-parse --short HEAD)"
dotnet publish "$REPO_ROOT/apps/api/src/Throne.Api/Throne.Api.csproj" -c Release -r "$RID" -p:Version="$VERSION"

[[ -x "$PUBLISH_DIR/throne" ]] || { echo "нет бинаря в $PUBLISH_DIR" >&2; exit 1; }

was_running=0
if "$APP_DIR/throne" status >/dev/null 2>&1; then
  was_running=1
  "$APP_DIR/throne" stop || true
fi

# Прошлый install-каталог остаётся откатом до следующей установки.
BACKUP_DIR="$THRONE_HOME/app.prev"
rm -rf "$BACKUP_DIR"
[[ -d "$APP_DIR" ]] && mv "$APP_DIR" "$BACKUP_DIR"

mkdir -p "$APP_DIR"
# pdb в рантайме не нужны, но и не мешают; копируем публикацию как есть.
cp -R "$PUBLISH_DIR/." "$APP_DIR/"

# publish кладёт specs/skills пустыми каркасами: yaml-манифесты и SKILL.md/bin в него
# не попадают, а без specs/manifest/*.yaml хост падает на старте
# (SkillManifestException: User prompt seed not found). Берём их из репозитория.
rm -rf "$APP_DIR/skills" "$APP_DIR/specs/manifest"
cp -R "$REPO_ROOT/skills" "$APP_DIR/skills"
mkdir -p "$APP_DIR/specs/manifest"
cp "$REPO_ROOT"/specs/manifest/*.yaml "$APP_DIR/specs/manifest/"

if [[ $restart -eq 1 && $was_running -eq 1 ]]; then
  "$APP_DIR/throne" --no-browser
fi

"$APP_DIR/throne" status || true
echo "Установлено из $(git -C "$REPO_ROOT" rev-parse --short HEAD) ($(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD)). Откат: $BACKUP_DIR"
