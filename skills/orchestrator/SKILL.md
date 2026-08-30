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

## Driving executors

```bash
skills/orchestrator/bin/throne-orchestrator run --intent <id>
skills/orchestrator/bin/throne-orchestrator run --intent <id> --vendor claude --model opus
skills/orchestrator/bin/throne-orchestrator stop --intent <id>
```

`run` starts an ordinary `work` session on a child intent of your tag: it previews the composition
first and passes the assembled `system_prompt`/`user_prompt` into the spawn — the server does not
assemble them on `run`, so skipping the preview would boot an executor with no rules and no task
while still looking like success.

An intent outside your tag is refused before any HTTP call. A live session is not a failure: `run`
prints «уже работает» and exits 0. `stop` kills the session and is idempotent.

Start executors **one at a time** and wait for each to come back. The spawn is synchronous (it waits
for repository clones, up to five minutes) and the vendor trust file is shared, so parallel launches
race. Every executor also gets its own workspace and its own clones — a pack of them costs disk and
minutes, not just tokens.

## Watching them

```bash
skills/orchestrator/bin/throne-orchestrator watch
skills/orchestrator/bin/throne-orchestrator watch --wait --timeout 600
```

Plain `watch` is an instant picture: who has a live session, who is parked in `awaiting_operator`.
`--wait` blocks until the first change and reports the delta — a status move, or a session that
vanished without one (an executor that died silently). It never waits past `--timeout` (default 600
seconds, hard cap 900), and a timeout is not an error: it prints that nothing moved.

There is no background watching in this contour. When your turn ends, Throne parks you in
`awaiting_operator` and nothing wakes you up — so either wait inside the turn with `--wait`, or take
the snapshot, report to the operator, and let them re-prompt you. Do not promise the operator that
you will «keep an eye on it» after the turn ends.

`watch` only reads. It never changes a status or touches a session.

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
