---
name: orchestrator
description: Available for driving a whole tag from the orchestrator intent — listing the intents of your own tag with their statuses and attached tracker cards through skills/orchestrator/bin/throne-orchestrator. Routine operation in the orchestrator run mode (ADR-0054).
---

# Throne Orchestrator Operations

Use `skills/orchestrator/bin/throne-orchestrator` to see the field you are responsible for. The
skill ships in this repo; run it from the workspace root with the relative path shown below.

## Before anything else

Check that your own `Intent.text` starts with `[ORCH]`. If it does not, **stop and report to the
operator** — the mode is offered on every intent, and running it on a foreign one turns your
commands into work on someone else's tag. Do not «adapt» to a non-orchestrator body.

Your field is the tag of your own intent, resolved by the CLI itself. It is a discipline, not a
permission boundary: the server will happily act on any intent, so the guard is yours to keep.

## Commands

```bash
skills/orchestrator/bin/throne-orchestrator list
skills/orchestrator/bin/throne-orchestrator list --status draft --status ready_for_work
skills/orchestrator/bin/throne-orchestrator list --json
```

`list` prints one line per intent of your tag: id, status, first line of the body, and the attached
tracker cards in brackets. `--status` narrows the page (repeatable); `--json` gives the raw page
plus a `cards` array per intent when you need to compute rather than read.

The intent body itself is not in that output — read a specific intent with the `intent` skill when
you need its full text.

## Editing your own body

Your memory lives in your own `Intent.text` (ADR-0054): `## Зона`, `## Решения`, `## В работе`,
`## Открытые вопросы`. Append decisions to `## Решения` — newest at the bottom, never rewrite past
entries. Write through `skills/intent/bin/throne-intent replace-text` with a **fragment**, never the
whole body: the pre-flight modal edits the same text, and a full rewrite loses the race on version
conflict.

Keep the journal bounded. The body is injected as the task zone of every next orchestrator session,
so recent entries stay verbatim and older ones get folded into a summary line.

## Environment

The script reads two variables from the environment. A Throne-spawned session has them set.

- `THRONE_API_BASE` — optional. Defaults to the local backend `http://localhost:5008`.
- `THRONE_INTENT_ID` — required. It is the orchestrator intent itself; without it there is no tag
  to resolve and the command correctly refuses to guess.

## Rules

- Do not write intent status from the agent. Throne derives status from session hooks.
- An orchestrator intent carries exactly one tag. Several tags is a broken setup — the CLI refuses
  instead of picking one.
- You do not do the task yourself. Your output is a statement of work, a launched executor, a
  decision recorded in your body — not a code change.
