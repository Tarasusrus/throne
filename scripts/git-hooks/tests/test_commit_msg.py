"""commit-msg хук: сообщение обязано заканчиваться трейлерами Problem/Decision/Task.

Красный → зелёный: до появления scripts/git-hooks/commit-msg этот файл падает —
скрипта ещё нет. Property-based тесты гоняют инвариант («последние три непустые
строки — трейлеры в этом порядке, ≤160 символов каждая») через широкий спектр
случайных тел сообщения, а не один фиксированный пример. Merge- и
Revert-коммиты — вне проверки независимо от содержимого.

Запуск: python3 -m pytest scripts/git-hooks/tests -q
"""

from __future__ import annotations

import itertools
import subprocess
from pathlib import Path

from hypothesis import HealthCheck, given, settings, strategies as st

settings.register_profile("tmp_path_fixture", suppress_health_check=[HealthCheck.function_scoped_fixture])
settings.load_profile("tmp_path_fixture")

HOOK = Path(__file__).resolve().parents[1] / "commit-msg"
LIMIT = 160

# Однострочный, непустой после strip, без '\n'/'\r'/'#' (комментарий) текст —
# годится и как заголовок, и как значение трейлера.
line_text = (
    st.text(
        alphabet=st.characters(
            blacklist_characters="\n\r#",
            blacklist_categories=("Cs",),
            min_codepoint=0x20,
            max_codepoint=0x2FA1D,
        ),
        min_size=1,
        max_size=40,
    )
    .map(lambda s: s.strip())
    .filter(lambda s: s != "")
)


def run_hook(tmp_path: Path, message: str) -> subprocess.CompletedProcess:
    msg_file = tmp_path / "MSG"
    msg_file.write_text(message, encoding="utf-8")
    return subprocess.run(
        [str(HOOK), str(msg_file)],
        capture_output=True,
        text=True,
        check=False,
    )


def trailers(problem: str, decision: str, task: str) -> str:
    return f"Problem: {problem}\nDecision: {decision}\nTask: {task}"


class TestWellFormedMessagePasses:
    @given(header=line_text, body=line_text, problem=line_text, decision=line_text, task=line_text)
    @settings(max_examples=60)
    def test_header_body_and_trailers_pass(self, tmp_path, header, body, problem, decision, task):
        msg = f"fix: {header}\n\n{body}\n\n{trailers(problem, decision, task)}\n"
        result = run_hook(tmp_path, msg)
        assert result.returncode == 0, result.stderr

    @given(problem=line_text, decision=line_text, task=line_text)
    @settings(max_examples=30)
    def test_trailers_alone_pass(self, tmp_path, problem, decision, task):
        result = run_hook(tmp_path, trailers(problem, decision, task) + "\n")
        assert result.returncode == 0, result.stderr

    @given(problem=line_text, decision=line_text, task=line_text)
    @settings(max_examples=20)
    def test_trailing_blank_lines_after_task_are_ignored(self, tmp_path, problem, decision, task):
        msg = trailers(problem, decision, task) + "\n\n\n"
        result = run_hook(tmp_path, msg)
        assert result.returncode == 0, result.stderr

    @given(problem=line_text, decision=line_text, task=line_text)
    @settings(max_examples=20)
    def test_git_comment_lines_are_stripped(self, tmp_path, problem, decision, task):
        msg = (
            trailers(problem, decision, task)
            + "\n\n# Please enter the commit message for your changes.\n"
            + "# On branch feat/x\n"
        )
        result = run_hook(tmp_path, msg)
        assert result.returncode == 0, result.stderr


class TestMissingTrailerFails:
    @given(
        missing=st.sampled_from(["problem", "decision", "task"]),
        a=line_text,
        b=line_text,
    )
    @settings(max_examples=30)
    def test_exactly_one_missing_trailer_fails(self, tmp_path, missing, a, b):
        present = {"problem": "Problem", "decision": "Decision", "task": "Task"}
        del present[missing]
        lines = [f"{label}: {value}" for label, value in zip(present.values(), (a, b))]
        result = run_hook(tmp_path, "\n".join(lines) + "\n")
        assert result.returncode != 0

    def test_empty_message_fails(self, tmp_path):
        result = run_hook(tmp_path, "\n")
        assert result.returncode != 0

    @given(header=line_text)
    @settings(max_examples=10)
    def test_header_only_no_trailers_fails(self, tmp_path, header):
        result = run_hook(tmp_path, f"fix: {header}\n")
        assert result.returncode != 0


class TestWrongOrderFails:
    @given(problem=line_text, decision=line_text, task=line_text)
    @settings(max_examples=30)
    def test_every_non_identity_permutation_fails(self, tmp_path, problem, decision, task):
        labeled = [("Problem", problem), ("Decision", decision), ("Task", task)]
        for perm in itertools.permutations(labeled):
            if perm == tuple(labeled):
                continue
            msg = "\n".join(f"{label}: {value}" for label, value in perm) + "\n"
            result = run_hook(tmp_path, msg)
            assert result.returncode != 0, f"permutation {perm} unexpectedly passed"


class TestLineLengthLimit:
    @given(
        problem=line_text,
        decision=line_text,
        overflow_len=st.integers(min_value=LIMIT + 1, max_value=LIMIT + 60),
    )
    @settings(max_examples=20)
    def test_task_line_over_160_chars_fails(self, tmp_path, problem, decision, overflow_len):
        task_value = "x" * (overflow_len - len("Task: "))
        msg = trailers(problem, decision, task_value) + "\n"
        assert len(f"Task: {task_value}") > LIMIT
        result = run_hook(tmp_path, msg)
        assert result.returncode != 0

    def test_exactly_160_chars_passes(self, tmp_path):
        task_value = "x" * (LIMIT - len("Task: "))
        line = f"Task: {task_value}"
        assert len(line) == LIMIT
        msg = trailers("p", "d", task_value) + "\n"
        result = run_hook(tmp_path, msg)
        assert result.returncode == 0, result.stderr


class TestMergeAndRevertBypass:
    @given(garbage=st.text(max_size=200))
    @settings(max_examples=20)
    def test_merge_commit_is_exempt(self, tmp_path, garbage):
        msg = "Merge branch 'x' into y\n\n" + garbage
        result = run_hook(tmp_path, msg)
        assert result.returncode == 0, result.stderr

    @given(subject=line_text, garbage=st.text(max_size=200))
    @settings(max_examples=20)
    def test_revert_commit_is_exempt(self, tmp_path, subject, garbage):
        msg = f'Revert "{subject}"\n\n' + garbage
        result = run_hook(tmp_path, msg)
        assert result.returncode == 0, result.stderr
