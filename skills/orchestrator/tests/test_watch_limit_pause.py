"""Пауза по лимиту вендора в `watch` / `list` (ADR-0055).

Сессия исполнителя жива в tmux, но ждёт сброса лимита. Свойства: в срезе у такой
строки есть `pause` с `resume_at`; рендер пишет «пауза до HH:MM», а не «работает»;
ключ сравнения для `--wait` не зависит от паузы — уход в паузу и выход из неё не
считаются изменением; всё остальное — как раньше.

Запуск: python3 -m pytest skills/orchestrator/tests -q
"""

from __future__ import annotations

import importlib.util
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path

from hypothesis import given, strategies as st

BIN_DIR = Path(__file__).resolve().parents[1] / "bin"

_spec = importlib.util.spec_from_file_location("_orchestrator", BIN_DIR / "_orchestrator.py")
orchestrator = importlib.util.module_from_spec(_spec)
assert _spec.loader is not None
_spec.loader.exec_module(orchestrator)

STATUSES = ("draft", "work", "awaiting_operator", "done")
intent_id = st.text(alphabet="abcdef0123456789", min_size=4, max_size=8)
NOW = datetime(2026, 9, 13, 12, 0, tzinfo=timezone.utc)


@st.composite
def sessions(draw):
    """Ответ GET /terminal/session для живого исполнителя: работает или в паузе."""
    paused = draw(st.booleans())
    minutes = draw(st.integers(min_value=-5, max_value=36 * 60))
    resume_at = (NOW + timedelta(minutes=minutes)).isoformat()
    body = {"session_state": "paused_by_limit" if paused else "running"}
    if paused:
        body["limit_pause"] = {
            "resume_at": resume_at,
            "attempts": draw(st.integers(min_value=1, max_value=3)),
            "message": "You've hit your monthly spend limit",
        }
    return body


@st.composite
def executors(draw):
    return {
        "id": draw(intent_id),
        "status": draw(st.sampled_from(STATUSES)),
        "live": draw(st.booleans()),
        "session": draw(sessions()),
    }


tag_pages = st.lists(executors(), max_size=6, unique_by=lambda i: i["id"])


def _snapshot(items):
    running = {i["id"] for i in items if i["live"]}
    page = [{"id": i["id"], "status": i["status"], "text_short": "задача " + i["id"]} for i in items]
    sessions_by_id = {i["id"]: i["session"] for i in items if i["live"]}
    return orchestrator.build_snapshot(running, page, {}, sessions_by_id)


class TestPauseInSnapshot:
    @given(tag_pages)
    def test_live_paused_session_carries_pause_others_none(self, items):
        snap = _snapshot(items)
        for item in items:
            row = snap[item["id"]]
            paused = item["live"] and item["session"]["session_state"] == "paused_by_limit"
            if paused:
                assert row["pause"] == {
                    "resume_at": item["session"]["limit_pause"]["resume_at"],
                    "attempts": item["session"]["limit_pause"]["attempts"],
                }
                assert row["live"] is True, "пауза — живая сессия, не исчезновение"
            else:
                assert row["pause"] is None

    @given(tag_pages)
    def test_watch_key_ignores_pause(self, items):
        snap = _snapshot(items)
        stripped = {
            k: {**v, "pause": None} for k, v in snap.items()
        }
        assert orchestrator.watch_key(snap) == orchestrator.watch_key(stripped)
        assert "pause" not in json.loads(orchestrator.watch_key(snap)).get(next(iter(snap), ""), {})

    @given(tag_pages, tag_pages)
    def test_watch_key_still_sees_status_and_liveness(self, before, after):
        a, b = _snapshot(before), _snapshot(after)
        same = {k: (v["status"], v["live"], v["title"], v["verdict"]) for k, v in a.items()} == {
            k: (v["status"], v["live"], v["title"], v["verdict"]) for k, v in b.items()
        }
        assert (orchestrator.watch_key(a) == orchestrator.watch_key(b)) == same


class TestPauseRendering:
    def test_pause_label_today_and_tomorrow(self):
        today = orchestrator._pause_label({"resume_at": (NOW + timedelta(hours=1)).isoformat(), "attempts": 1}, NOW)
        assert today.startswith("пауза до ")
        assert "лимит вендора" in today
        tomorrow = orchestrator._pause_label({"resume_at": (NOW + timedelta(hours=20)).isoformat(), "attempts": 2}, NOW)
        assert "попытка 2" in tomorrow
        assert tomorrow != today

    @given(tag_pages)
    def test_render_rows_mark_pause_instead_of_working(self, items):
        snap = _snapshot(items)
        for intent_id_, row in orchestrator._executors(snap):
            line = orchestrator._watch_row(intent_id_, row, NOW)
            if row["pause"]:
                assert "пауза до" in line and "работает" not in line
            elif row["live"]:
                assert "работает" in line
            else:
                assert "сессии нет" in line
