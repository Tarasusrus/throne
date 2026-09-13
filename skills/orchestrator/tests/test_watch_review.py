"""Ревью-интенты в `watch`.

Ревьюер после вердикта паркуется в `awaiting_operator` и без этих правил висит в
`watch` как исполнитель навсегда — по строке на каждый круг ревью. Свойства:
ревью-интент с записанным `## Вердикт` из строк исполнителей уходит, всё остальное
остаётся как было.

Запуск: python3 -m pytest skills/orchestrator/tests -q
"""

from __future__ import annotations

import importlib.util
from pathlib import Path

from hypothesis import given, strategies as st

BIN_DIR = Path(__file__).resolve().parents[1] / "bin"

_spec = importlib.util.spec_from_file_location("_orchestrator", BIN_DIR / "_orchestrator.py")
orchestrator = importlib.util.module_from_spec(_spec)
assert _spec.loader is not None
_spec.loader.exec_module(orchestrator)

STATUSES = ("draft", "interview", "ready_for_work", "work", "awaiting_operator", "done", "reject", "fridge")

intent_id = st.text(alphabet="abcdef0123456789", min_size=4, max_size=8)
plain_title = st.text(
    alphabet=st.characters(blacklist_categories=("Cs",), blacklist_characters="\r\n[]"),
    min_size=1, max_size=30,
).filter(lambda s: s.strip())

VERDICT_BODY = "## Вердикт\nпринято\n\n## Definition of Done\n- a — проверено\n"
NO_VERDICT_BODY = "## Ветка\nfeat/x\n\n## Definition of Done\n- a\n"
FENCED_VERDICT_BODY = "## Ветка\nfeat/x\n\n```\n## Вердикт\n```\n"


@st.composite
def intents(draw):
    """Интент тега: обычный или `[REVIEW]`; у ревью — тело с вердиктом или без."""
    is_review = draw(st.booleans())
    title = draw(plain_title)
    if is_review:
        title = "[REVIEW] " + title
        body = draw(st.sampled_from([VERDICT_BODY, NO_VERDICT_BODY, FENCED_VERDICT_BODY]))
    else:
        body = draw(st.sampled_from([VERDICT_BODY, NO_VERDICT_BODY]))
    return {
        "id": draw(intent_id),
        "status": draw(st.sampled_from(STATUSES)),
        "live": draw(st.booleans()),
        "title": title,
        "body": title + "\n\n" + body,
    }


tag_pages = st.lists(intents(), max_size=6, unique_by=lambda i: i["id"])


def _snapshot(items):
    running = {i["id"] for i in items if i["live"]}
    page = [{"id": i["id"], "status": i["status"], "text_short": i["title"]} for i in items]
    bodies = {i["id"]: {"id": i["id"], "text": i["body"]} for i in items}
    return orchestrator.build_snapshot(running, page, bodies)


def _has_verdict(item) -> bool:
    return item["title"].startswith("[REVIEW]") and "## Вердикт\nпринято" in item["body"]


class TestReviewIntentsInWatch:
    @given(tag_pages)
    def test_review_with_verdict_is_not_an_executor(self, items):
        rows = dict(orchestrator._executors(_snapshot(items)))
        for item in items:
            if _has_verdict(item) and not item["live"]:
                assert item["id"] not in rows

    @given(tag_pages)
    def test_everything_else_is_listed_exactly_as_before(self, items):
        """Живая сессия или `awaiting_operator` — строка исполнителя, если это не
        ревью с вердиктом. Ревью без вердикта — обычный исполнитель: ему ещё работать."""
        rows = dict(orchestrator._executors(_snapshot(items)))
        for item in items:
            expected = (item["live"] or item["status"] == "awaiting_operator") and not (
                _has_verdict(item) and not item["live"]
            )
            assert (item["id"] in rows) == expected

    @given(tag_pages)
    def test_snapshot_marks_verdict_only_on_review_intents(self, items):
        snapshot = _snapshot(items)
        for item in items:
            assert snapshot[item["id"]]["verdict"] == _has_verdict(item)

    @given(tag_pages)
    def test_verdict_candidates_are_parked_reviews_without_session(self, items):
        """Тела тянутся только для тех, у кого вердикт вообще может быть: `[REVIEW]`,
        встал в `awaiting_operator`, сессии нет. Остальным лишний GET не нужен."""
        running = {i["id"] for i in items if i["live"]}
        page = [{"id": i["id"], "status": i["status"], "text_short": i["title"]} for i in items]
        candidates = set(orchestrator.verdict_candidates(running, page))
        for item in items:
            expected = (
                item["title"].startswith("[REVIEW]")
                and item["status"] == "awaiting_operator"
                and not item["live"]
            )
            assert (item["id"] in candidates) == expected

    @given(tag_pages)
    def test_missing_body_means_no_verdict(self, items):
        """Тело не скачано (сервер не отдал) — вердикта нет, строка остаётся."""
        running = {i["id"] for i in items if i["live"]}
        page = [{"id": i["id"], "status": i["status"], "text_short": i["title"]} for i in items]
        snapshot = orchestrator.build_snapshot(running, page, {})
        assert not any(state["verdict"] for state in snapshot.values())
