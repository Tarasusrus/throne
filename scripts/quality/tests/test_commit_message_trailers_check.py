"""Гейт commit-message-trailers: коммиты origin/master..HEAD проверяются тем
же правилом, что commit-msg хук (одна реализация, не две — оба вызывают
scripts/git-hooks/commit-msg).

Красный → зелёный: до появления scripts/quality/commit-message-trailers-check.sh
этот файл падает — скрипта ещё нет.

Запуск: python3 -m pytest scripts/quality/tests -q
"""

from __future__ import annotations

import subprocess
import uuid
from pathlib import Path

from hypothesis import HealthCheck, given, settings, strategies as st

SCRIPT = Path(__file__).resolve().parents[1] / "commit-message-trailers-check.sh"

settings.register_profile("tmp_repo", suppress_health_check=[HealthCheck.function_scoped_fixture])
settings.load_profile("tmp_repo")

VALID_TRAILERS = "Problem: p\nDecision: d\nTask: t\n"
INVALID_TRAILERS = "no trailers here\n"


def git(repo: Path, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["git", "-C", str(repo), *args],
        capture_output=True,
        text=True,
        check=True,
        env=_env(),
    )


def _env() -> dict:
    import os

    env = os.environ.copy()
    env.update(
        {
            "GIT_AUTHOR_NAME": "test",
            "GIT_AUTHOR_EMAIL": "test@example.com",
            "GIT_COMMITTER_NAME": "test",
            "GIT_COMMITTER_EMAIL": "test@example.com",
        }
    )
    return env


def init_repo(tmp_path: Path) -> Path:
    # Fresh subdir per call: hypothesis reuses the same tmp_path fixture instance
    # across @given examples, so a shared path would re-init the same repo.
    path = tmp_path / f"repo-{uuid.uuid4().hex}"
    subprocess.run(["git", "init", "-q", "-b", "master", str(path)], check=True, env=_env())
    git(path, "config", "commit.gpgsign", "false")
    (path / "README.md").write_text("seed\n", encoding="utf-8")
    git(path, "add", "README.md")
    git(path, "commit", "-q", "-m", "chore: seed\n\nProblem: p\nDecision: d\nTask: t\n")
    return path


def freeze_origin_master(repo: Path) -> None:
    sha = git(repo, "rev-parse", "HEAD").stdout.strip()
    git(repo, "update-ref", "refs/remotes/origin/master", sha)


def commit(repo: Path, message: str, filename: str) -> None:
    (repo / filename).write_text(message, encoding="utf-8")
    git(repo, "add", filename)
    git(repo, "commit", "-q", "-m", message)


def run_gate(repo: Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        [str(SCRIPT)],
        cwd=repo,
        capture_output=True,
        text=True,
        check=False,
    )


class TestGreenOnNoNewCommits:
    def test_head_equals_origin_master(self, tmp_path):
        repo = init_repo(tmp_path)
        freeze_origin_master(repo)

        result = run_gate(repo)

        assert result.returncode == 0, result.stderr


class TestOriginMasterMissing:
    def test_fails_loudly_without_origin_master(self, tmp_path):
        repo = init_repo(tmp_path)
        # no freeze_origin_master call: refs/remotes/origin/master does not exist

        result = run_gate(repo)

        assert result.returncode != 0
        assert "origin/master" in result.stderr


class TestNewCommitsChecked:
    def test_all_valid_new_commits_pass(self, tmp_path):
        repo = init_repo(tmp_path)
        freeze_origin_master(repo)
        commit(repo, "feat: a\n\n" + VALID_TRAILERS, "a.txt")
        commit(repo, "feat: b\n\n" + VALID_TRAILERS, "b.txt")

        result = run_gate(repo)

        assert result.returncode == 0, result.stderr

    def test_one_invalid_new_commit_fails(self, tmp_path):
        repo = init_repo(tmp_path)
        freeze_origin_master(repo)
        commit(repo, "feat: a\n\n" + VALID_TRAILERS, "a.txt")
        commit(repo, "feat: b\n\n" + INVALID_TRAILERS, "b.txt")

        result = run_gate(repo)

        assert result.returncode != 0

    def test_merge_commit_without_trailers_is_exempt(self, tmp_path):
        repo = init_repo(tmp_path)
        freeze_origin_master(repo)
        git(repo, "checkout", "-q", "-b", "side")
        commit(repo, "feat: side\n\n" + VALID_TRAILERS, "side.txt")
        git(repo, "checkout", "-q", "master")
        commit(repo, "feat: main\n\n" + VALID_TRAILERS, "main.txt")
        git(repo, "merge", "-q", "--no-ff", "-m", "Merge branch 'side'", "side")

        result = run_gate(repo)

        assert result.returncode == 0, result.stderr


class TestPropertyAcrossCommitSequences:
    @given(valid_flags=st.lists(st.booleans(), min_size=1, max_size=5))
    @settings(max_examples=15, deadline=None)
    def test_gate_exit_matches_all_commits_valid(self, tmp_path, valid_flags):
        repo = init_repo(tmp_path)
        freeze_origin_master(repo)
        for i, is_valid in enumerate(valid_flags):
            body = VALID_TRAILERS if is_valid else INVALID_TRAILERS
            commit(repo, f"feat: c{i}\n\n{body}", f"f{i}.txt")

        result = run_gate(repo)

        expected_ok = all(valid_flags)
        assert (result.returncode == 0) == expected_ok, result.stderr
