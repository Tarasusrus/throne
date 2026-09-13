"""Свойства вырезки тела ревью-интента и payload запуска ревьюера
(`throne-orchestrator review --intent <id>`).

Красный → зелёный: до реализации `review_body`/`build_run_payload(mode=…)` в
`_orchestrator.py` файл падает на импорте. Инварианты гоняются по сгенерированным
телам исполнителя, а не по одному фиксированному примеру.

Запуск: python3 -m pytest skills/orchestrator/tests -q
"""

from __future__ import annotations

import importlib.util
import subprocess
from pathlib import Path

import pytest
from hypothesis import given, strategies as st

BIN_DIR = Path(__file__).resolve().parents[1] / "bin"
SCRIPT_PATH = BIN_DIR / "throne-orchestrator"

_spec = importlib.util.spec_from_file_location("_orchestrator", BIN_DIR / "_orchestrator.py")
orchestrator = importlib.util.module_from_spec(_spec)
assert _spec.loader is not None
_spec.loader.exec_module(orchestrator)

KEPT = ("Ветка", "Definition of Done", "Для человека")
DROPPED = ("Для агента", "Отчёт", "Порядок сдачи", "Решения", "В работе")


def _sections(text: str) -> dict[str, str]:
    """Разбор тела на секции по `## ` — независимая от реализации проверка."""
    result: dict[str, str] = {}
    name = None
    for line in text.split("\n"):
        if line.startswith("## "):
            name = line[3:].strip()
            result[name] = ""
        elif name is not None:
            result[name] += line + "\n"
    return result


# Строка содержимого секции: любой текст, кроме нового заголовка второго уровня —
# он начал бы другую секцию и сломал бы соответствие «секция → содержимое».
content_line = st.text(
    alphabet=st.characters(blacklist_categories=("Cs",), blacklist_characters="\r\n"),
    max_size=40,
).filter(lambda s: not s.startswith("## "))

section_body = st.lists(content_line, min_size=0, max_size=5).map("\n".join)

# DoD без единого пункта — пожелание, а не задача: вырезка такое тело отвергает,
# поэтому генератор даёт DoD хотя бы с одной непустой строкой.
dod_body = section_body.filter(lambda s: s.strip() != "")

branch_name = st.text(
    alphabet="abcdefghijklmnopqrstuvwxyz0123456789-/_", min_size=1, max_size=20
).filter(lambda s: s.strip("/-") == s)


@st.composite
def executor_bodies(draw):
    """Тело исполнителя: заголовок + перемешанные секции, часть — лишние."""
    title = draw(content_line)
    names = draw(st.permutations(list(KEPT) + list(DROPPED)))
    present = [n for n in names if n in ("Ветка", "Definition of Done") or draw(st.booleans())]
    sections = {}
    for name in present:
        if name == "Ветка":
            body = draw(branch_name)
        elif name == "Definition of Done":
            body = draw(dod_body)
        else:
            body = draw(section_body)
        sections[name] = body
    text = title + "\n\n" + "\n\n".join("## " + n + "\n" + sections[n] for n in present)
    return text, sections


class TestReviewBodyInvariants:
    @given(executor_bodies())
    def test_only_the_three_sections_survive(self, case):
        text, _ = case
        cut = orchestrator.review_body(text)
        assert set(_sections(cut)) <= set(KEPT)

    @given(executor_bodies())
    def test_present_kept_sections_survive_verbatim(self, case):
        text, sections = case
        cut = _sections(orchestrator.review_body(text))
        for name in KEPT:
            if name in sections:
                assert name in cut
                assert cut[name].strip() == sections[name].strip()

    @given(executor_bodies())
    def test_dropped_sections_leave_no_trace(self, case):
        text, sections = case
        cut = orchestrator.review_body(text)
        for name in DROPPED:
            assert "## " + name not in cut
            body = sections.get(name, "").strip()
            if body and body not in "".join(sections[k] for k in KEPT if k in sections):
                assert body not in cut

    @given(executor_bodies())
    def test_nothing_before_the_first_section_except_own_header(self, case):
        text, sections = case
        cut = orchestrator.review_body(text)
        head = cut.split("\n## ", 1)[0].rstrip("\n")
        # Единственная строка до секций — заголовок ревью с именем ветки.
        assert head.count("\n") == 0
        assert sections["Ветка"].strip() in head

    def test_body_with_exactly_the_five_sections_keeps_three(self):
        text = (
            "Задача\n\n## Ветка\nfeat/x\n\n## Definition of Done\n- a\n\n"
            "## Для человека\nпочему\n\n## Для агента\nточки\n\n## Отчёт\nсделано"
        )
        cut = _sections(orchestrator.review_body(text))
        assert list(cut) == ["Ветка", "Definition of Done", "Для человека"]

    @pytest.mark.parametrize("missing", ["Ветка", "Definition of Done"])
    def test_missing_branch_or_dod_is_refused(self, missing):
        sections = {"Ветка": "feat/x", "Definition of Done": "- a", "Для человека": "why"}
        del sections[missing]
        text = "\n\n".join("## " + n + "\n" + b for n, b in sections.items())
        with pytest.raises(ValueError):
            orchestrator.review_body(text)

    def test_fenced_heading_is_not_a_section(self):
        text = (
            "## Ветка\nfeat/x\n\n## Definition of Done\n```\n## Отчёт\n```\n\n## Отчёт\nreal"
        )
        cut = orchestrator.review_body(text)
        assert "real" not in cut
        assert "```\n## Отчёт\n```" in cut


preview_strategy = st.fixed_dictionaries(
    {
        "system_prompt": st.text(max_size=100),
        "user_prompt": st.text(max_size=100),
        "selected_part_ids": st.lists(st.text(min_size=1, max_size=8), max_size=4),
        "available_skills_for_mode": st.lists(
            st.fixed_dictionaries(
                {
                    "skill_id": st.text(min_size=1, max_size=8),
                    "selected": st.booleans(),
                    "materializable": st.booleans(),
                }
            ),
            max_size=4,
        ),
    }
)


class TestReviewRunPayload:
    @given(preview=preview_strategy)
    def test_review_payload_runs_verify_mode(self, preview):
        payload = orchestrator.build_run_payload(preview, "", "", "", mode="verify")
        assert payload["mode"] == "verify"

    @given(preview=preview_strategy)
    def test_default_mode_is_still_work(self, preview):
        assert orchestrator.build_run_payload(preview, "", "", "")["mode"] == "work"

    @given(preview=preview_strategy, vendor=st.text(max_size=6), model=st.text(max_size=6),
           effort=st.sampled_from(["", "low", "high"]))
    def test_mode_is_the_only_difference_between_work_and_verify(self, preview, vendor, model, effort):
        work = orchestrator.build_run_payload(preview, vendor, model, effort)
        verify = orchestrator.build_run_payload(preview, vendor, model, effort, mode="verify")
        work.pop("mode")
        verify.pop("mode")
        assert work == verify


class TestCliReviewOrdering:
    def test_bad_effort_fails_before_intent_lookup(self):
        result = subprocess.run(
            [str(SCRIPT_PATH), "review", "--intent", "x", "--effort", "bogus"],
            capture_output=True, text=True, env={"PATH": "/usr/bin:/bin:/usr/local/bin"}, timeout=10,
        )
        assert result.returncode == 64
        assert "effort" in result.stderr
        assert "THRONE_INTENT_ID" not in result.stderr

    def test_review_requires_intent_flag(self):
        result = subprocess.run(
            [str(SCRIPT_PATH), "review"],
            capture_output=True, text=True, env={"PATH": "/usr/bin:/bin:/usr/local/bin"}, timeout=10,
        )
        assert result.returncode == 64
        assert "--intent" in result.stderr
