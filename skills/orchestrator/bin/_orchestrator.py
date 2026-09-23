#!/usr/bin/env python3
"""Разбор ответов Throne для throne-orchestrator.

Живёт отдельным файлом, а не heredoc'ами внутри bash: кавычки в python-коде
внутри одинарных кавычек bash не выражаются, и попытка их протащить ломается
молча — на кавычках, а не на логике.
"""

from __future__ import annotations

import json
from datetime import datetime, timezone
import os
import sys
from urllib.parse import urlencode


def own_tag() -> None:
    tags = [t["name"] for t in json.load(sys.stdin).get("tags", [])]
    if len(tags) != 1:
        found = ", ".join(tags) or "ни одного"
        sys.exit(
            "throne-orchestrator: интент-оркестратор обязан нести ровно один тег, найдено: "
            + found
        )
    print(tags[0])


def list_query(argv: list[str]) -> None:
    pairs = [("tag", argv[0]), ("limit", "100")]
    pairs += [("status", s) for s in argv[1:]]
    print(urlencode(pairs))


def ids() -> None:
    print(" ".join(item["id"] for item in json.load(sys.stdin)["items"]))


def _cards(cards_dir: str, intent_id: str) -> list[str]:
    path = os.path.join(cards_dir, intent_id + ".json")
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8") as handle:
        attachments = json.load(handle)
    labels = []
    for card in attachments:
        # Карточка не несёт отдельного title — берём первую непустую строку снапшота.
        head = next(
            (line.strip() for line in card["text"].splitlines() if line.strip()),
            card["card_id"],
        )
        availability = card["availability"]
        labels.append(head if availability == "available" else head + " (" + availability + ")")
    return labels


def _title(item: dict) -> str:
    text = item.get("text_short") or ""
    for line in text.splitlines():
        if line.strip():
            return line.strip()
    return "(пусто)"


def render(cards_dir: str, as_json: bool, sessions_dir: str | None = None) -> None:
    page = json.load(sys.stdin)
    items = page["items"]
    sessions = _read_sessions(sessions_dir, {item["id"] for item in items})
    if as_json:
        for item in items:
            item["cards"] = _cards(cards_dir, item["id"])
            item["limit_pause"] = _pause_of(sessions.get(item["id"]))
        print(json.dumps(page, ensure_ascii=False, indent=2))
        return

    if not items:
        print("интентов с этим тегом нет")
        return
    for item in items:
        line = item["id"] + "  " + item["status"].ljust(18) + "  " + _title(item)
        pause = _pause_of(sessions.get(item["id"]))
        if pause:
            line += "  ⏸ " + _pause_label(pause)
        attached = _cards(cards_dir, item["id"])
        if attached:
            line += "  [" + "; ".join(attached) + "]"
        print(line)
    if page.get("next_cursor"):
        print("… страница обрезана, есть продолжение")


def assert_tag(expected: str, target: str) -> None:
    tags = [t["name"] for t in json.load(sys.stdin).get("tags", [])]
    if expected not in tags:
        found = ", ".join(tags) or "нет тегов"
        sys.exit(
            "throne-orchestrator: интент " + target + " вне твоего тега " + expected
            + " (на нём: " + found + ")"
        )


# Тот же словарь, что и TerminalReasoningEffort на сервере (low/medium/high/xhigh) —
# claude-only "max" сюда намеренно не входит, ось запуска общая для всех вендоров.
KNOWN_EFFORTS = ("low", "medium", "high", "xhigh")

# Политика запуска исполнителей и ревьюеров — единственный источник правды.
# Потолок effort один для обеих ролей: задача, которой нужен high, недодекомпозирована —
# оркестратор дробит её, а не поднимает effort. Ревью вдобавок не идёт на сильнейшей модели.
ALLOWED_EFFORTS = ("low", "medium")
DEFAULT_EFFORT = "medium"
# вендор → (модель по умолчанию, сильнейшая модель). Сильнейшая стоит первой в каталоге
# сервера (TerminalVendorDescriptors) и она же серверный дефолт вместе с effort=high —
# поэтому модель и effort в payload шлются всегда, серверному дефолту не доверяем.
VENDOR_MODELS = {
    "claude": ("sonnet", "opus"),
    "codex": ("gpt-5.6-terra", "gpt-5.6-sol"),
}
# У opencode нет оси effort, а модель локальная — таблица к нему неприменима.
UNPOLICED_VENDORS = ("opencode",)
REVIEW_MODE = "verify"


def check_launch(mode: str, vendor: str, model: str, effort: str) -> None:
    """Отказ на запрещённой паре. Не ходит в сеть: зовётся до первого HTTP-вызова.

    Пустая строка — флаг не передан. Вендор ещё может быть неизвестен (возьмётся из
    настроек сервера) — тогда сильнейшей считается сильнейшая модель любого вендора.
    """
    if effort and effort not in KNOWN_EFFORTS:
        raise ValueError(
            "неизвестный effort '" + effort + "' — допустимо: " + ", ".join(ALLOWED_EFFORTS)
        )
    if effort and effort not in ALLOWED_EFFORTS:
        if mode == REVIEW_MODE:
            raise ValueError(
                "effort '" + effort + "' для ревью запрещён — допустимо: "
                + ", ".join(ALLOWED_EFFORTS)
            )
        raise ValueError(
            "effort '" + effort + "' для задачи запрещён: high effort — признак неверной "
            "декомпозиции, разбей задачу. Допустимо: " + ", ".join(ALLOWED_EFFORTS)
        )
    if vendor and vendor not in VENDOR_MODELS and vendor not in UNPOLICED_VENDORS:
        raise ValueError(
            "вендор '" + vendor + "' не описан в политике запуска — допустимо: "
            + ", ".join(tuple(VENDOR_MODELS) + UNPOLICED_VENDORS)
        )
    if mode == REVIEW_MODE and model:
        strongest = (
            {VENDOR_MODELS[vendor][1]} if vendor in VENDOR_MODELS
            else {pair[1] for pair in VENDOR_MODELS.values()}
        )
        if model in strongest:
            raise ValueError(
                "модель '" + model + "' для ревью запрещена: ревью не идёт на сильнейшей модели"
            )


def build_run_payload(
    preview: dict, vendor: str, model: str, effort: str, mode: str = "work"
) -> dict:
    """vendor обязателен: без него не выбрать модель по умолчанию из политики."""
    check_launch(mode, vendor, model, effort)
    if vendor in VENDOR_MODELS:
        model = model or VENDOR_MODELS[vendor][0]
        effort = effort or DEFAULT_EFFORT
    payload = {
        "mode": mode,
        # Сервер не пересобирает промпт из id частей — везём собранный текст.
        "system_prompt": preview["system_prompt"],
        "user_prompt": preview["user_prompt"],
        "selected_part_ids": preview["selected_part_ids"],
        "selected_skill_ids": [
            skill["skill_id"]
            for skill in preview["available_skills_for_mode"]
            if skill["selected"] and skill["materializable"]
        ],
        "vendor": vendor,
    }
    if model:
        payload["model"] = model
    if effort:
        payload["effort"] = effort
    return payload


def run_payload(vendor: str, model: str, effort: str, mode: str) -> None:
    preview = json.load(sys.stdin)
    try:
        payload = build_run_payload(preview, vendor, model, effort, mode)
    except ValueError as exc:
        sys.exit("throne-orchestrator: " + str(exc))
    print(json.dumps(payload, ensure_ascii=False))


def default_vendor() -> None:
    print(json.load(sys.stdin)["default_vendor"])


# Ревьюер знает ровно три вещи из постановки исполнителя (ADR-0054 §8): ветку,
# DoD и проблему. Всё остальное — `## Для агента`, `## Отчёт`, заголовок задачи —
# вырезается, чтобы вердикт не опирался на самооценку исполнителя.
REVIEW_SECTIONS = ("Ветка", "Definition of Done", "Для человека")
REVIEW_REQUIRED = ("Ветка", "Definition of Done")


def _fence_marker(line: str) -> tuple[str, int] | None:
    """Строка — code fence по CommonMark: три и больше одинаковых ` или ~ подряд."""
    stripped = line.lstrip()
    for char in "`~":
        if stripped.startswith(char * 3):
            return char, len(stripped) - len(stripped.lstrip(char))
    return None


def _scan_sections(text: str) -> tuple[dict[str, list[str]], int | None]:
    """Секции по заголовкам `## `; заголовок внутри code fence секцию не открывает.

    Fence закрывается только тем же символом и не короче открывающего (CommonMark),
    иначе любой ``` в тексте переключал бы состояние и прятал секции. Второй
    элемент — номер строки fence, который так и не закрылся, либо None.
    """
    sections: dict[str, list[str]] = {}
    current = None
    fence: tuple[str, int] | None = None
    fence_line = None
    for number, line in enumerate(text.split("\n"), start=1):
        marker = _fence_marker(line)
        if fence is None and marker is not None:
            fence, fence_line = marker, number
        elif fence is not None and marker is not None and marker[0] == fence[0] and marker[1] >= fence[1]:
            fence = None
        elif fence is None and line.startswith("## "):
            current = line[3:].strip()
            sections[current] = []
            continue
        if current is not None:
            sections[current].append(line)
    return sections, fence_line if fence is not None else None


def _split_sections(text: str) -> dict[str, list[str]]:
    return _scan_sections(text)[0]


def _branch_name(section: list[str]) -> str:
    """Имя ветки — первая непустая строка секции без fence и инлайновых бэктиков;
    остальные строки — примечания исполнителю, в заголовок ревью им не место."""
    for line in section:
        if line.strip() and _fence_marker(line) is None:
            return line.strip().strip("`").strip()
    return ""


def review_body(executor_text: str) -> str:
    sections, open_fence = _scan_sections(executor_text)
    missing = [name for name in REVIEW_REQUIRED if not "\n".join(sections.get(name, [])).strip()]
    if missing and open_fence is not None:
        raise ValueError(
            "в теле исполнителя не закрыт code fence (строка " + str(open_fence)
            + "), за ним не видно " + " и ".join("## " + m for m in missing)
            + " — закрой fence и повтори"
        )
    if missing:
        raise ValueError(
            "в теле исполнителя нет секции " + " и ".join("## " + m for m in missing)
            + " — без неё ревьюеру нечего проверять"
        )
    branch = _branch_name(sections["Ветка"])
    if not branch:
        raise ValueError("в секции ## Ветка нет имени ветки — только fence или пустые строки")
    parts = ["[REVIEW] независимое ревью ветки " + branch]
    for name in REVIEW_SECTIONS:
        if name in sections:
            parts.append("## " + name + "\n" + "\n".join(sections[name]).strip())
    return "\n\n".join(parts) + "\n"


def create_payload(body_file: str, tag: str) -> None:
    with open(body_file, encoding="utf-8") as handle:
        text = handle.read()
    print(json.dumps({"text": text, "tag_names": [tag]}, ensure_ascii=False))


def review_body_cmd() -> None:
    intent = json.load(sys.stdin)
    try:
        print(review_body(intent["text"]), end="")
    except ValueError as exc:
        sys.exit("throne-orchestrator: " + str(exc))


def run_result(target: str) -> None:
    body = json.load(sys.stdin)
    print(target + ": " + body["session_state"] + " (" + body["session_name"] + ")")
    blocking = body.get("blocking_bindings") or []
    if blocking:
        print("  не склонировано: " + ", ".join(blocking))


REVIEW_PREFIX = "[REVIEW]"


def _is_review(item: dict) -> bool:
    return _title(item).startswith(REVIEW_PREFIX)


def verdict_candidates(running: set[str], page: list[dict]) -> list[str]:
    """У кого вердикт вообще может быть: ревью-интент встал в awaiting_operator без
    сессии. Только им watch тянет полное тело — в списке лежит обрезок в 140 символов."""
    return [
        item["id"]
        for item in page
        if _is_review(item) and item["status"] == "awaiting_operator" and item["id"] not in running
    ]


def _has_verdict(body: dict | None) -> bool:
    if body is None:
        return False
    return bool("\n".join(_split_sections(body.get("text") or "").get("Вердикт", [])).strip())


def _pause_of(session: dict | None) -> dict | None:
    """Пауза по лимиту вендора из ответа /terminal/session (ADR-0055): сессия ждёт
    сброс. Живая — paused_by_limit; умершая в паузе до перезапуска сторожем — exited,
    но с limit_pause: это тоже пауза, а не исчезновение. Без сообщения — оно длинное
    и в срез не нужно."""
    if not session or session.get("session_state") not in ("paused_by_limit", "exited"):
        return None
    pause = session.get("limit_pause") or {}
    if "resume_at" not in pause:
        return None
    return {"resume_at": pause["resume_at"], "attempts": pause.get("attempts", 1)}


def build_snapshot(
    running: set[str],
    page: list[dict],
    bodies: dict[str, dict],
    sessions: dict[str, dict] | None = None,
) -> dict:
    sessions = sessions or {}
    snapshot = {}
    for item in page:
        pause = _pause_of(sessions.get(item["id"]))
        snapshot[item["id"]] = {
            "status": item["status"],
            # Умершая в паузе сессия — ещё исполнитель: Throne перезапустит её сам,
            # для оркестратора и --wait это то же ожидание, а не «сессии нет».
            "live": item["id"] in running or pause is not None,
            "title": _title(item),
            "verdict": _is_review(item) and _has_verdict(bodies.get(item["id"])),
            "pause": pause,
        }
    return snapshot


def watch_key(snapshot: dict) -> str:
    """Ключ сравнения для `watch --wait`: пауза по лимиту — расписание, а не событие.
    Уход в паузу и выход из неё изменением не считаются; статус, живость, вердикт — считаются."""
    return json.dumps(
        {k: {f: v for f, v in row.items() if f != "pause"} for k, row in snapshot.items()},
        ensure_ascii=False,
        sort_keys=True,
    )


def _pause_label(pause: dict, now: datetime | None = None) -> str:
    now = now or datetime.now(timezone.utc)
    try:
        at = datetime.fromisoformat(pause["resume_at"].replace("Z", "+00:00"))
    except (ValueError, KeyError, AttributeError):
        return "пауза до " + str(pause.get("resume_at")) + " (лимит вендора)"
    local, local_now = at.astimezone(), now.astimezone()
    clock = local.strftime("%H:%M") if local.date() == local_now.date() else local.strftime("%d.%m %H:%M")
    label = "пауза до " + clock + " (лимит вендора"
    attempts = pause.get("attempts", 1)
    if attempts > 1:
        label += ", попытка " + str(attempts)
    return label + ")"


def _read_bodies(bodies_dir: str, ids: list[str]) -> dict[str, dict]:
    bodies = {}
    for intent_id in ids:
        path = os.path.join(bodies_dir, intent_id + ".json")
        if os.path.exists(path):
            with open(path, encoding="utf-8") as handle:
                bodies[intent_id] = json.load(handle)
    return bodies


def _read_sessions(sessions_dir: str | None, ids: set[str]) -> dict[str, dict]:
    """Ответы /terminal/session живых исполнителей, сложенные bash-обёрткой как <id>.json.
    Битый или отсутствующий файл — «нет данных о паузе», не ошибка watch."""
    if not sessions_dir:
        return {}
    sessions = {}
    for intent_id in ids:
        path = os.path.join(sessions_dir, intent_id + ".json")
        if not os.path.exists(path):
            continue
        try:
            with open(path, encoding="utf-8") as handle:
                sessions[intent_id] = json.load(handle)
        except (OSError, ValueError):
            continue
    return sessions


def session_candidates(running: set[str], page: list[dict]) -> list[str]:
    """У кого пробник может показать паузу: живые сессии и интенты в work без
    сессии — последние либо правда без исполнителя, либо умерли в паузе и ждут
    перезапуска сторожем."""
    return [
        item["id"]
        for item in page
        if item["id"] in running or item["status"] == "work"
    ] + [id_ for id_ in sorted(running) if not any(item["id"] == id_ for item in page)]


def session_candidates_cmd() -> None:
    running = {item["id"] for item in json.loads(sys.stdin.readline())["items"]}
    page = json.load(sys.stdin)["items"]
    print(" ".join(session_candidates(running, page)))


def verdict_candidates_cmd() -> None:
    running = {item["id"] for item in json.loads(sys.stdin.readline())["items"]}
    page = json.load(sys.stdin)["items"]
    print(" ".join(verdict_candidates(running, page)))


def watch_snapshot(bodies_dir: str, sessions_dir: str | None = None) -> None:
    """Срез: статус каждого интента тега + признак живой сессии + есть ли вердикт
    у ревью-интента (тела кандидатов лежат в bodies_dir как <id>.json) + пауза по
    лимиту вендора у живых (ответы /terminal/session лежат в sessions_dir).

    Печатается отсортированным JSON, чтобы сравнение двух срезов было
    посимвольным — дельта не зависит от порядка, в котором сервер отдал страницу.
    """
    running = {item["id"] for item in json.loads(sys.stdin.readline())["items"]}
    page = json.load(sys.stdin)["items"]
    bodies = _read_bodies(bodies_dir, verdict_candidates(running, page))
    sessions = _read_sessions(sessions_dir, set(session_candidates(running, page)))
    print(json.dumps(build_snapshot(running, page, bodies, sessions), ensure_ascii=False, sort_keys=True))


def watch_key_cmd() -> None:
    print(watch_key(json.load(sys.stdin)))


def _executors(snapshot: dict) -> list[tuple[str, dict]]:
    """Строки исполнителей. Ревью-интент с записанным вердиктом свою работу сделал:
    он паркуется в awaiting_operator навсегда, и без этого фильтра каждый круг ревью
    оставлял бы в watch мёртвую строку."""
    return [
        (k, v)
        for k, v in snapshot.items()
        if v["live"] or (v["status"] == "awaiting_operator" and not v.get("verdict"))
    ]


def _watch_row(intent_id: str, state: dict, now: datetime | None = None) -> str:
    if state.get("pause"):
        mark = _pause_label(state["pause"], now)
    elif state["live"]:
        mark = "работает"
    else:
        mark = "сессии нет"
    return intent_id + "  " + state["status"].ljust(18) + "  " + mark + "  " + state["title"]


def watch_render() -> None:
    snapshot = json.load(sys.stdin)
    rows = _executors(snapshot)
    if not rows:
        print("исполнителей нет: ни живых сессий, ни ожидающих ответа")
        return
    for intent_id, state in rows:
        print(_watch_row(intent_id, state))


def watch_delta() -> None:
    before = json.loads(sys.stdin.readline())
    after = json.load(sys.stdin)
    changes = []
    for intent_id, state in after.items():
        was = before.get(intent_id)
        if was is None:
            changes.append(intent_id + ": новый интент — " + state["status"])
            continue
        if was["status"] != state["status"]:
            changes.append(intent_id + ": " + was["status"] + " → " + state["status"] + "  " + state["title"])
        if was["live"] and not state["live"] and was["status"] == state["status"]:
            # Сессия исчезла, а статус не двигался — исполнитель умер, не доложив.
            changes.append(intent_id + ": сессия пропала без смены статуса  " + state["title"])
    for intent_id in before.keys() - after.keys():
        changes.append(intent_id + ": исчез из тега")
    for line in changes or ["изменения есть, но не в статусах исполнителей"]:
        print(line)


def main() -> None:
    command = sys.argv[1]
    if command == "own-tag":
        own_tag()
    elif command == "list-query":
        list_query(sys.argv[2:])
    elif command == "ids":
        ids()
    elif command == "render":
        render(sys.argv[2], as_json=False, sessions_dir=sys.argv[3] if len(sys.argv) > 3 else None)
    elif command == "render-json":
        render(sys.argv[2], as_json=True, sessions_dir=sys.argv[3] if len(sys.argv) > 3 else None)
    elif command == "assert-tag":
        assert_tag(sys.argv[2], sys.argv[3])
    elif command == "run-payload":
        effort = sys.argv[4] if len(sys.argv) > 4 else ""
        mode = sys.argv[5] if len(sys.argv) > 5 else "work"
        run_payload(sys.argv[2], sys.argv[3], effort, mode)
    elif command == "review-body":
        review_body_cmd()
    elif command == "create-payload":
        create_payload(sys.argv[2], sys.argv[3])
    elif command == "check-launch":
        try:
            check_launch(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5])
        except ValueError as exc:
            sys.exit(str(exc))
    elif command == "default-vendor":
        default_vendor()
    elif command == "run-result":
        run_result(sys.argv[2])
    elif command == "verdict-candidates":
        verdict_candidates_cmd()
    elif command == "session-candidates":
        session_candidates_cmd()
    elif command == "watch-snapshot":
        watch_snapshot(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
    elif command == "watch-key":
        watch_key_cmd()
    elif command == "watch-render":
        watch_render()
    elif command == "watch-delta":
        watch_delta()
    else:
        sys.exit("throne-orchestrator: unknown helper command " + command)


if __name__ == "__main__":
    main()
