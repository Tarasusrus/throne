#!/usr/bin/env python3
"""Разбор ответов Throne для throne-orchestrator.

Живёт отдельным файлом, а не heredoc'ами внутри bash: кавычки в python-коде
внутри одинарных кавычек bash не выражаются, и попытка их протащить ломается
молча — на кавычках, а не на логике.
"""

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


def run_payload(vendor: str, model: str) -> None:
    preview = json.load(sys.stdin)
    payload = {
        "mode": "work",
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
    print(json.dumps(payload, ensure_ascii=False))


def run_result(target: str) -> None:
    body = json.load(sys.stdin)
    print(target + ": " + body["session_state"] + " (" + body["session_name"] + ")")
    blocking = body.get("blocking_bindings") or []
    if blocking:
        print("  не склонировано: " + ", ".join(blocking))


def watch_snapshot() -> None:
    """Срез: статус каждого интента тега + признак живой сессии.

    Печатается отсортированным JSON, чтобы сравнение двух срезов было
    посимвольным — дельта не зависит от порядка, в котором сервер отдал страницу.
    """
    running = {item["id"] for item in json.loads(sys.stdin.readline())["items"]}
    page = json.load(sys.stdin)
    snapshot = {
        item["id"]: {
            "status": item["status"],
            "live": item["id"] in running,
            "title": _title(item),
        }
        for item in page["items"]
    }
    print(json.dumps(snapshot, ensure_ascii=False, sort_keys=True))


def _executors(snapshot: dict) -> list[tuple[str, dict]]:
    return [(k, v) for k, v in snapshot.items() if v["live"] or v["status"] == "awaiting_operator"]


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
        run_payload(sys.argv[2], sys.argv[3])
    elif command == "run-result":
        run_result(sys.argv[2])
    elif command == "watch-snapshot":
        watch_snapshot()
    elif command == "watch-render":
        watch_render()
    elif command == "watch-delta":
        watch_delta()
    else:
        sys.exit("throne-orchestrator: unknown helper command " + command)


if __name__ == "__main__":
    main()
