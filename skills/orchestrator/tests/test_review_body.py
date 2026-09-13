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


# Строка примечания к ветке: не пустая, не fence и не похожа на имя ветки —
# иначе не отличить «взяли первую строку» от «взяли примечание».
note_line = content_line.filter(
    lambda s: s.strip() and not s.lstrip().startswith(("```", "~~~")) and " " in s.strip()
)


@st.composite
def branch_sections(draw):
    """Секция `## Ветка`: имя ветки — первой непустой строкой, возможно в code fence
    или в инлайновых бэктиках, дальше — примечания. Возвращает (текст секции, имя)."""
    name = draw(branch_name)
    shown = draw(st.sampled_from([name, "`" + name + "`"]))
    lines = draw(st.lists(st.just(""), max_size=2))
    if draw(st.booleans()):
        lines += ["```", shown, "```"]
    else:
        lines.append(shown)
    lines += draw(st.lists(note_line, max_size=3))
    return "\n".join(lines), name


@st.composite
def executor_bodies(draw):
    """Тело исполнителя: заголовок + перемешанные секции, часть — лишние.

    Возвращает (текст, секции, имя ветки) — имя отдельно, потому что секция ветки
    может быть многострочной, а в заголовок ревью должно попасть только имя.
    """
    title = draw(content_line)
    names = draw(st.permutations(list(KEPT) + list(DROPPED)))
    present = [n for n in names if n in ("Ветка", "Definition of Done") or draw(st.booleans())]
    sections = {}
    branch = ""
    for name in present:
        if name == "Ветка":
            body, branch = draw(branch_sections())
        elif name == "Definition of Done":
            body = draw(dod_body)
        else:
            body = draw(section_body)
        sections[name] = body
    text = title + "\n\n" + "\n\n".join("## " + n + "\n" + sections[n] for n in present)
    return text, sections, branch


class TestReviewBodyInvariants:
    @given(executor_bodies())
    def test_only_the_three_sections_survive(self, case):
        text, _, _ = case
        cut = orchestrator.review_body(text)
        assert set(_sections(cut)) <= set(KEPT)

    @given(executor_bodies())
    def test_present_kept_sections_survive_verbatim(self, case):
        text, sections, _ = case
        cut = _sections(orchestrator.review_body(text))
        for name in KEPT:
            if name in sections:
                assert name in cut
                assert cut[name].strip() == sections[name].strip()

    @given(executor_bodies())
    def test_dropped_sections_leave_no_trace(self, case):
        text, sections, _ = case
        cut = orchestrator.review_body(text)
        # Сравниваем с содержимым секций, а не со всем текстом: однобуквенное тело
        # лишней секции найдётся внутри заголовка «Definition of Done» и даст ложный красный.
        kept_text = "".join(_sections(cut).values())
        for name in DROPPED:
            assert "## " + name not in cut
            body = sections.get(name, "").strip()
            if body and body not in "".join(sections[k] for k in KEPT if k in sections):
                assert body not in kept_text

    @given(executor_bodies())
    def test_nothing_before_the_first_section_except_own_header(self, case):
        text, sections, branch = case
        cut = orchestrator.review_body(text)
        head = cut.split("\n## ", 1)[0].rstrip("\n")
        # Единственная строка до секций — заголовок ревью с именем ветки: имя без
        # бэктиков, без fence и без примечаний из остальных строк секции.
        assert head.count("\n") == 0
        assert head.endswith(" " + branch)
        assert "`" not in head
        for note in sections["Ветка"].splitlines():
            if note.strip() and note.strip().strip("`") != branch:
                assert note.strip() not in head

    def test_branch_header_takes_first_nonempty_line_not_the_note(self):
        text = (
            "## Ветка\n\n```\nfeat/x\n```\nпуш после каждого куска\n\n"
            "## Definition of Done\n- a"
        )
        cut = orchestrator.review_body(text)
        assert cut.split("\n", 1)[0] == "[REVIEW] независимое ревью ветки feat/x"

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

    @given(
        opening=st.integers(min_value=3, max_value=6),
        closing=st.integers(min_value=1, max_value=6),
        char=st.sampled_from("`~"),
        body=dod_body,
    )
    def test_fence_closes_only_with_a_marker_at_least_as_long(self, opening, closing, char, body):
        """CommonMark: закрывающий fence — тот же символ и не короче открывающего.
        Короче — fence не закрыт, DoD спрятан, и ошибка обязана сказать про fence,
        а не про «нет секции»."""
        text = (
            "## Ветка\nfeat/x\n\n## Для человека\n" + char * opening + "\nпример\n"
            + char * closing + "\n\n## Definition of Done\n" + body
        )
        if closing >= opening:
            cut = _sections(orchestrator.review_body(text))
            assert cut["Definition of Done"].strip() == body.strip()
        else:
            with pytest.raises(ValueError, match="fence"):
                orchestrator.review_body(text)

    @given(opening=st.sampled_from(["```", "~~~"]), closing=st.sampled_from(["```", "~~~"]))
    def test_fence_of_the_other_character_does_not_close(self, opening, closing):
        text = (
            "## Ветка\nfeat/x\n\n## Для человека\n" + opening + "\nпример\n" + closing
            + "\n\n## Definition of Done\n- a"
        )
        if opening == closing:
            assert "Definition of Done" in _sections(orchestrator.review_body(text))
        else:
            with pytest.raises(ValueError, match="fence"):
                orchestrator.review_body(text)

    def test_unclosed_fence_error_names_its_line(self):
        text = "## Для человека\n```\nоборвано\n\n## Ветка\nfeat/x\n\n## Definition of Done\n- a"
        with pytest.raises(ValueError) as exc:
            orchestrator.review_body(text)
        message = str(exc.value)
        assert "fence" in message and "строк" in message and "2" in message
        assert "нет секции" not in message


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
