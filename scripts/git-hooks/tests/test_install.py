"""scripts/git-hooks/install.sh: идемпотентная установка commit-msg хука.

Красный → зелёный: до появления install.sh этот файл падает — скрипта ещё
нет. Копирует scripts/git-hooks/commit-msg в .git/hooks/commit-msg только
если файла нет или содержимое отличается.

Запуск: python3 -m pytest scripts/git-hooks/tests -q
"""

from __future__ import annotations

import subprocess
from pathlib import Path

INSTALL = Path(__file__).resolve().parents[1] / "install.sh"
SOURCE = Path(__file__).resolve().parents[1] / "commit-msg"


def init_repo(path: Path) -> Path:
    subprocess.run(["git", "init", "-q", str(path)], check=True)
    return path


def run_install(repo: Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        [str(INSTALL)],
        cwd=repo,
        capture_output=True,
        text=True,
        check=False,
    )


class TestInstallsHook:
    def test_creates_hook_when_missing(self, tmp_path):
        repo = init_repo(tmp_path)
        target = repo / ".git" / "hooks" / "commit-msg"
        assert not target.exists()

        result = run_install(repo)

        assert result.returncode == 0, result.stderr
        assert target.exists()
        assert target.read_bytes() == SOURCE.read_bytes()
        assert target.stat().st_mode & 0o111, "hook must be executable"

    def test_noop_when_already_identical(self, tmp_path):
        repo = init_repo(tmp_path)
        run_install(repo)
        target = repo / ".git" / "hooks" / "commit-msg"
        before = target.stat().st_mtime_ns

        result = run_install(repo)

        assert result.returncode == 0, result.stderr
        assert target.stat().st_mtime_ns == before, "identical content must not be rewritten"

    def test_overwrites_when_different(self, tmp_path):
        repo = init_repo(tmp_path)
        target_dir = repo / ".git" / "hooks"
        target_dir.mkdir(parents=True, exist_ok=True)
        target = target_dir / "commit-msg"
        target.write_text("#!/usr/bin/env bash\necho stale\n", encoding="utf-8")

        result = run_install(repo)

        assert result.returncode == 0, result.stderr
        assert target.read_bytes() == SOURCE.read_bytes()
