"""Гигиена оркестратора: за собой он закрывает принятое и гасит отработавшие сессии.

- `accept` на коде 0 переводит задачу и её незакрытые ревью-интенты в done (сервер
  на done сам гасит сессию и чистит воркспейс); ненулевой код ничего не закрывает.
- `review` новым раундом закрывает прошлые ревью-интенты задачи до создания нового.
- `run` на вернувшемся исполнителе (awaiting_operator) гасит прошлую сессию.

curl подменяется заглушкой на PATH, ответы берутся из routes.json; git — настоящий.

Запуск: python3 -m pytest skills/orchestrator/tests -q
"""

from __future__ import annotations

import importlib.util
import json
import stat
import subprocess
from pathlib import Path

import pytest

BIN_DIR = Path(__file__).resolve().parents[1] / "bin"
SCRIPT_PATH = BIN_DIR / "throne-orchestrator"

_spec = importlib.util.spec_from_file_location("_orchestrator", BIN_DIR / "_orchestrator.py")
orchestrator = importlib.util.module_from_spec(_spec)
assert _spec.loader is not None
_spec.loader.exec_module(orchestrator)

FAKE_CURL = r'''#!/usr/bin/env python3
import json, os, sys
args = sys.argv[1:]
out = args[args.index("-o") + 1]
url = [a for a in args if a.startswith("http")][0]
path = url.split("://", 1)[1].split("/", 1)[1]
method = "POST" if "-X" in args else "GET"
body = sys.stdin.read() if method == "POST" else ""
with open(os.environ["FAKE_CURL_LOG"], "a") as log:
    log.write(json.dumps([method, "/" + path, body], ensure_ascii=False) + "\n")
routes = json.load(open(os.environ["FAKE_ROUTES"]))
key = method + " /" + path
if key in routes:
    status, reply = routes[key]
elif method == "POST" and path.endswith("/status"):
    status, reply = 200, {"status": json.loads(body)["status"]}
elif method == "POST" and path.endswith("/terminal/kill"):
    status, reply = 200, {}
else:
    status, reply = 404, {"error": "unexpected " + key}
with open(out, "w") as handle:
    json.dump(reply, handle, ensure_ascii=False)
sys.stdout.write(str(status))
'''

CHILD_TEXT = "[TASK] x\n\n## Ветка\nfeat/x\n\n## Definition of Done\n- a\n"


def _review_link(review_id: str, status: str) -> dict:
    return {
        "direction": "incoming",
        "peer": {"id": review_id, "status": status,
                 "text_short": "[REVIEW] независимое ревью ветки feat/x"},
    }


def _child(status: str = "awaiting_operator", links: list | None = None) -> dict:
    parent = {"direction": "incoming",
              "peer": {"id": "orch", "status": "work", "text_short": "[ORCH] зона"}}
    return {"id": "child", "status": status, "tags": [{"name": "t"}], "text": CHILD_TEXT,
            "links": [parent, *(links or [])]}


class Env:
    def __init__(self, tmp_path: Path, child: dict, extra_routes: dict | None = None):
        bin_dir = tmp_path / "bin"
        bin_dir.mkdir()
        curl = bin_dir / "curl"
        curl.write_text(FAKE_CURL)
        curl.chmod(curl.stat().st_mode | stat.S_IXUSR)
        routes = {
            "GET /api/v1/intents/orch": [200, {"id": "orch", "tags": [{"name": "t"}]}],
            "GET /api/v1/intents/child": [200, child],
            "GET /api/v1/settings/terminal": [200, {"default_vendor": "claude"}],
            "POST /api/v1/intents/child/terminal/preview": [200, {
                "system_prompt": "s", "user_prompt": "u", "selected_part_ids": [],
                "available_skills_for_mode": []}],
            "POST /api/v1/intents/child/terminal/run": [200, {
                "session_state": "started", "session_name": "throne-child"}],
            **(extra_routes or {}),
        }
        (tmp_path / "routes.json").write_text(json.dumps(routes, ensure_ascii=False))
        self.log = tmp_path / "calls.log"
        self.env = {
            "PATH": str(bin_dir) + ":/usr/bin:/bin:/usr/local/bin:/opt/homebrew/bin",
            "THRONE_INTENT_ID": "orch",
            "THRONE_API_BASE": "http://fake",
            "FAKE_ROUTES": str(tmp_path / "routes.json"),
            "FAKE_CURL_LOG": str(self.log),
            "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t",
            "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t",
            "HOME": str(tmp_path),
        }

    def run(self, *args: str) -> subprocess.CompletedProcess:
        return subprocess.run([str(SCRIPT_PATH), *args], capture_output=True, text=True,
                              env=self.env, timeout=60)

    def calls(self) -> list[tuple[str, str, str]]:
        if not self.log.exists():
            return []
        return [tuple(json.loads(line)) for line in self.log.read_text().splitlines()]

    def closed(self) -> list[str]:
        return [path.split("/")[4] for method, path, body in self.calls()
                if method == "POST" and path.endswith("/status")
                and json.loads(body)["status"] == "done"]


def _git(cwd: Path, *args: str) -> None:
    subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True,
                   env={"PATH": "/usr/bin:/bin:/opt/homebrew/bin", "HOME": str(cwd),
                        "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t",
                        "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t"})


@pytest.fixture
def repo(tmp_path: Path) -> Path:
    """Клон оркестратора; на origin — main и ветка исполнителя feat/x."""
    origin = tmp_path / "origin.git"
    seed = tmp_path / "seed"
    _git(tmp_path, "init", "-q", "--bare", "-b", "main", str(origin))
    _git(tmp_path, "clone", "-q", str(origin), str(seed))
    (seed / "a.txt").write_text("a\n")
    _git(seed, "add", ".")
    _git(seed, "commit", "-q", "-m", "init")
    _git(seed, "push", "-q", "origin", "main")
    _git(seed, "checkout", "-q", "-b", "feat/x")
    (seed / "b.txt").write_text("b\n")
    _git(seed, "add", ".")
    _git(seed, "commit", "-q", "-m", "feat")
    _git(seed, "push", "-q", "origin", "feat/x")
    clone = tmp_path / "clone"
    _git(tmp_path, "clone", "-q", str(origin), str(clone))
    return clone


class TestAcceptCloses:
    def test_merged_branch_closes_task_and_its_open_reviews(self, tmp_path, repo):
        env = Env(tmp_path, _child(links=[_review_link("rev-old", "awaiting_operator"),
                                          _review_link("rev-done", "done")]))
        result = env.run("accept", "--intent", "child", "--repo", str(repo))
        assert result.returncode == 0, result.stderr
        assert "влито" in result.stdout
        # Закрытое повторно не трогаем; ревью — раньше задачи.
        assert env.closed() == ["rev-old", "child"]

    def test_already_merged_still_closes(self, tmp_path, repo):
        _git(repo, "merge", "-q", "--no-ff", "-m", "m", "origin/feat/x")
        _git(repo, "push", "-q", "origin", "main")
        env = Env(tmp_path, _child())
        result = env.run("accept", "--intent", "child", "--repo", str(repo))
        assert result.returncode == 0, result.stderr
        assert "уже влито" in result.stdout
        assert env.closed() == ["child"]

    def test_failed_accept_closes_nothing(self, tmp_path, repo):
        env = Env(tmp_path, _child(links=[_review_link("rev-old", "awaiting_operator")]))
        result = env.run("accept", "--intent", "child", "--repo", str(repo), "--check", "false")
        assert result.returncode == 68
        assert env.closed() == []

    def test_branch_comes_from_task_body(self, tmp_path, repo):
        child = _child()
        child["text"] = CHILD_TEXT.replace("feat/x", "feat/absent")
        env = Env(tmp_path, child)
        result = env.run("accept", "--intent", "child", "--repo", str(repo))
        assert result.returncode == 66
        assert "feat/absent" in result.stderr
        assert env.closed() == []


    def test_merge_message_passes_repo_commit_msg_hook(self, tmp_path, repo):
        # Как scripts/git-hooks/commit-msg Throne: всё, что не «Merge …», без трейлеров — отказ.
        hook = repo / ".git" / "hooks" / "commit-msg"
        hook.write_text('#!/bin/sh\ncase "$(head -1 "$1")" in "Merge "*) exit 0 ;; esac\nexit 1\n')
        hook.chmod(0o755)
        env = Env(tmp_path, _child())
        result = env.run("accept", "--intent", "child", "--repo", str(repo))
        assert result.returncode == 0, result.stderr
        assert env.closed() == ["child"]

    def test_closing_kills_the_session_itself(self, tmp_path, repo):
        env = Env(tmp_path, _child())
        result = env.run("accept", "--intent", "child", "--repo", str(repo))
        assert result.returncode == 0, result.stderr
        paths = [f"{method} {path}" for method, path, _ in env.calls()]
        # Сервер гасит сессию на done лишь при флаге очистки — не полагаемся на него.
        assert paths.index("POST /api/v1/intents/child/terminal/kill") < paths.index(
            "POST /api/v1/intents/child/status")

    def test_empty_branch_is_not_accepted(self, tmp_path, repo):
        _git(repo, "push", "-q", "origin", "origin/main:refs/heads/feat/empty")
        child = _child()
        child["text"] = CHILD_TEXT.replace("feat/x", "feat/empty")
        env = Env(tmp_path, child)
        result = env.run("accept", "--intent", "child", "--repo", str(repo))
        assert result.returncode == 66
        assert "нет своих коммитов" in result.stderr
        assert env.closed() == []

    def test_close_failure_after_push_has_its_own_code(self, tmp_path, repo):
        env = Env(tmp_path, _child(), {"POST /api/v1/intents/child/status": [500, {"error": "boom"}]})
        result = env.run("accept", "--intent", "child", "--repo", str(repo))
        assert result.returncode == 70
        # Ветка при этом влита — повтор accept будет no-op слиянием.
        _git(repo, "fetch", "-q", "origin")
        merged = subprocess.run(["git", "merge-base", "--is-ancestor", "origin/feat/x", "origin/main"],
                                cwd=repo, capture_output=True)
        assert merged.returncode == 0

    def test_bare_branch_mode_merges_without_throne(self, tmp_path, repo):
        env = Env(tmp_path, _child())
        result = env.run("accept", "--branch", "feat/x", "--repo", str(repo))
        assert result.returncode == 0, result.stderr
        assert env.calls() == []

    def test_intent_and_branch_together_are_refused(self, tmp_path, repo):
        env = Env(tmp_path, _child())
        result = env.run("accept", "--intent", "child", "--branch", "feat/x", "--repo", str(repo))
        assert result.returncode == 64
        assert env.calls() == []


class TestReviewClosesPreviousRound:
    def test_prior_reviews_closed_before_new_one_is_created(self, tmp_path):
        env = Env(tmp_path, _child(links=[_review_link("rev-old", "awaiting_operator")]),
                  {"POST /api/v1/intents": [201, {"id": "rev-new"}],
                   "POST /api/v1/intents/rev-new/links": [500, {"error": "stop here"}]})
        result = env.run("review", "--intent", "child")
        assert result.returncode != 0
        paths = [f"{method} {path}" for method, path, _ in env.calls()]
        assert env.closed() == ["rev-old"]
        assert paths.index("POST /api/v1/intents/rev-old/status") < paths.index("POST /api/v1/intents")


class TestRunRestartsReturnedExecutor:
    def test_returned_executor_session_is_killed_before_preview(self, tmp_path):
        env = Env(tmp_path, _child(status="awaiting_operator"))
        result = env.run("run", "--intent", "child")
        assert result.returncode == 0, result.stderr
        paths = [f"{method} {path}" for method, path, _ in env.calls()]
        kill = paths.index("POST /api/v1/intents/child/terminal/kill")
        assert kill < paths.index("POST /api/v1/intents/child/terminal/preview")

    @pytest.mark.parametrize("status", ["draft", "work"])
    def test_other_statuses_are_not_killed(self, tmp_path, status):
        env = Env(tmp_path, _child(status=status))
        result = env.run("run", "--intent", "child")
        assert result.returncode == 0, result.stderr
        assert not any(path.endswith("/terminal/kill") for _, path, _ in env.calls())


class TestHelpers:
    def test_branch_of_refuses_body_without_branch(self):
        with pytest.raises(ValueError):
            orchestrator.branch_of("[TASK] x\n\n## Definition of Done\n- a\n")

    def test_open_reviews_skip_closed_and_non_review_links(self):
        intent = _child(links=[_review_link("a", "awaiting_operator"), _review_link("b", "reject")])
        assert orchestrator.open_reviews(intent) == ["a"]
