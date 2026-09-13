#!/usr/bin/env python3
"""Разбор ответов Throne для throne-orchestrator.

Живёт отдельным файлом, а не heredoc'ами внутри bash: кавычки в python-коде
внутри одинарных кавычек bash не выражаются, и попытка их протащить ломается
молча — на кавычках, а не на логике.
"""

from __future__ import annotations

import json
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


def render(cards_dir: str, as_json: bool) -> None:
    page = json.load(sys.stdin)
    items = page["items"]
    if as_json:
        for item in items:
            item["cards"] = _cards(cards_dir, item["id"])
        print(json.dumps(page, ensure_ascii=False, indent=2))
        return

    if not items:
        print("интентов с этим тегом нет")
        return
    for item in items:
        line = item["id"] + "  " + item["status"].ljust(18) + "  " + _title(item)
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


def validate_effort(value: str) -> None:
    """Пустая строка — флаг --effort не передан, это валидное состояние."""
    if value and value not in KNOWN_EFFORTS:
        raise ValueError(
            "неизвестный effort '" + value + "' — допустимо: " + ", ".join(KNOWN_EFFORTS)
        )


def build_run_payload(
    preview: dict, vendor: str, model: str, effort: str, mode: str = "work"
) -> dict:
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
    }
    if vendor:
        payload["vendor"] = vendor
    if model:
        payload["model"] = model
    if effort:
        payload["effort"] = effort
    return payload


def run_payload(vendor: str, model: str, effort: str, mode: str) -> None:
    preview = json.load(sys.stdin)
    print(json.dumps(build_run_payload(preview, vendor, model, effort, mode), ensure_ascii=False))


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


def build_snapshot(running: set[str], page: list[dict], bodies: dict[str, dict]) -> dict:
    return {
        item["id"]: {
            "status": item["status"],
            "live": item["id"] in running,
            "title": _title(item),
            "verdict": _is_review(item) and _has_verdict(bodies.get(item["id"])),
        }
        for item in page
    }


def _read_bodies(bodies_dir: str, ids: list[str]) -> dict[str, dict]:
    bodies = {}
    for intent_id in ids:
        path = os.path.join(bodies_dir, intent_id + ".json")
        if os.path.exists(path):
            with open(path, encoding="utf-8") as handle:
                bodies[intent_id] = json.load(handle)
    return bodies


def verdict_candidates_cmd() -> None:
    running = {item["id"] for item in json.loads(sys.stdin.readline())["items"]}
    page = json.load(sys.stdin)["items"]
    print(" ".join(verdict_candidates(running, page)))


def watch_snapshot(bodies_dir: str) -> None:
    """Срез: статус каждого интента тега + признак живой сессии + есть ли вердикт
    у ревью-интента (тела кандидатов лежат в bodies_dir как <id>.json).

    Печатается отсортированным JSON, чтобы сравнение двух срезов было
    посимвольным — дельта не зависит от порядка, в котором сервер отдал страницу.
    """
    running = {item["id"] for item in json.loads(sys.stdin.readline())["items"]}
    page = json.load(sys.stdin)["items"]
    bodies = _read_bodies(bodies_dir, verdict_candidates(running, page))
    print(json.dumps(build_snapshot(running, page, bodies), ensure_ascii=False, sort_keys=True))


def _executors(snapshot: dict) -> list[tuple[str, dict]]:
    """Строки исполнителей. Ревью-интент с записанным вердиктом свою работу сделал:
    он паркуется в awaiting_operator навсегда, и без этого фильтра каждый круг ревью
    оставлял бы в watch мёртвую строку."""
    return [
        (k, v)
        for k, v in snapshot.items()
        if v["live"] or (v["status"] == "awaiting_operator" and not v.get("verdict"))
    ]


def watch_render() -> None:
    snapshot = json.load(sys.stdin)
    rows = _executors(snapshot)
    if not rows:
        print("исполнителей нет: ни живых сессий, ни ожидающих ответа")
        return
    for intent_id, state in rows:
        mark = "работает" if state["live"] else "сессии нет"
        print(intent_id + "  " + state["status"].ljust(18) + "  " + mark + "  " + state["title"])


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
        render(sys.argv[2], as_json=False)
    elif command == "render-json":
        render(sys.argv[2], as_json=True)
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
    elif command == "validate-effort":
        try:
            validate_effort(sys.argv[2] if len(sys.argv) > 2 else "")
        except ValueError as exc:
            sys.exit(str(exc))
    elif command == "run-result":
        run_result(sys.argv[2])
    elif command == "verdict-candidates":
        verdict_candidates_cmd()
    elif command == "watch-snapshot":
        watch_snapshot(sys.argv[2])
    elif command == "watch-render":
        watch_render()
    elif command == "watch-delta":
        watch_delta()
    else:
        sys.exit("throne-orchestrator: unknown helper command " + command)


if __name__ == "__main__":
    main()
