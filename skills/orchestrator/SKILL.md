---
name: orchestrator
description: Available for driving a whole tag from the orchestrator intent — listing the intents of your own tag, launching, watching, independently reviewing and accepting executors (merge into the main branch) through skills/orchestrator/bin/throne-orchestrator. Routine operation in the orchestrator run mode (ADR-0054).
---

# Throne Orchestrator Operations

Use `skills/orchestrator/bin/throne-orchestrator` to see the field you are responsible for. The
skill ships in this repo; run it from the workspace root with the relative path shown below.

## Before anything else

Your body is your memory, so give it the shape the memory needs. If your own `Intent.text` is not
marked up as an orchestrator body, **mark it up yourself** — `[ORCH] <zone>` plus the four sections
below — and record that as the first entry of the decision journal, with the reason. Do not stop for
it and do not ask: the marker is bookkeeping, the operator neither sets it nor should have to know
about it. Keep what was there: the gist goes into `## Зона`, the statement of work goes into a child
intent.

Stop and call the operator in exactly two cases: the intent carries several tags (the CLI refuses
anyway — the tag is ambiguous), or the body describes someone else's live work rather than something
to orchestrate.

Your field is the tag of your own intent, resolved by the CLI itself from `THRONE_INTENT_ID` — the
body text has no say in it. It is a discipline, not a permission boundary: the server will happily
act on any intent, so the guard is yours to keep.

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
skills/orchestrator/bin/throne-orchestrator run --intent <id> --effort low
skills/orchestrator/bin/throne-orchestrator stop --intent <id>
```

`run` starts an ordinary `work` session on a child intent of your tag: it previews the composition
first and passes the assembled `system_prompt`/`user_prompt` into the spawn — the server does not
assemble them on `run`, so skipping the preview would boot an executor with no rules and no task
while still looking like success.

### Model and effort: the launch policy

`--vendor`/`--model`/`--effort` are one launch axis, and the CLI holds it inside a policy — the table
lives in `skills/orchestrator/bin/_orchestrator.py` (`ALLOWED_EFFORTS`, `VENDOR_MODELS`), not here:

- effort is `low` or `medium` for every role; `high`/`xhigh` are refused;
- a review (`verify`) never runs on the vendor's strongest model (`opus` for claude);
- flags omitted → the policy default (claude: `sonnet` + `medium`), never the server's own default,
  which is the strongest model at `high`; `--vendor` omitted → the server's default vendor;
- forbidden flags are refused before any HTTP call; a default vendor outside the table — right after
  the settings read, before anything is created; `opencode` has no effort axis, so it gets no
  default model, only the common effort ceiling.

There is no task that needs `high`. Your output is an exhaustive list of instructions: each statement
of work spells out what to change, where, and how to prove it, so the executor carries it out rather
than works it out. If a task still seems to need `high`, it is under-decomposed — split it, or take
the fork out of it: an ambiguity you settle in the journal, an architectural fork you bring to the
operator, and the executor gets the decision, not the dilemma. Raising the effort instead of
decomposing is not an option — the CLI will not let you.

A child intent is worth launching only if it is a statement of work. Two sections are mandatory:
`## Ветка` — the branch the executor works in and pushes when done — and `## Definition of Done` —
what you will measure the result against. Without a DoD there is nothing to accept; without a
branch there is nothing to fetch.

```markdown
<one line: what and why>

## Ветка
fix/<repo-short>-<what>

## Definition of Done
- [ ] <observable outcome>
- [ ] tests: <command that must be green>
- [ ] branch pushed, `## Отчёт` written into this intent
```

The executor's `work` instruction already tells it to push that branch and to write its report into
its own body as `## Отчёт` — that report, not the chat, is what you read at acceptance.

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

Throne itself has no scheduler that could wake a stopped session — that limit is real. What can wake
you is the vendor's own background monitor: every line the monitored command prints arrives in your
session as a notification, a notification re-invokes you, and your first tool call trips the status
hook that moves you from `awaiting_operator` back to `work`.

A review intent (`[REVIEW] …`) that has parked in `awaiting_operator` with a `## Вердикт` already
written is not listed: its work is done, and without that rule every review round would leave one
more dead row per task. A parked review intent *without* a verdict stays in the picture — the
reviewer stopped before finishing, treat it like any executor that came back early. `--wait` still
reports the move to `awaiting_operator` for both, so the verdict never arrives unnoticed.

An executor that hit the vendor's usage limit is neither stopped nor gone: its session waits for
the reset, so `watch` shows it as `пауза до HH:MM (лимит вендора)` instead of `работает`, and
`list` marks the same row with `⏸`. The same label covers a session that died inside the pause
(the probe still carries the pause until Throne's guard respawns it shortly after the reset) — it
is not `сессии нет`, so do not `run` it yourself. Throne resumes it on its own — the vendor's
auto-continue first, Throne's own push after the reset — so do nothing: not `stop`, not `run`, not a
report to the operator. `--wait` treats entering and leaving the pause as no change; a repeated
limit right after a resume becomes another pause, and only past the attempt ceiling does the intent
land in `awaiting_operator` with the reason — that one you handle like any parked executor
(ADR-0055).

`watch` only reads. It never changes a status or touches a session.

### Standing watch in the background

Poll `watch` in a persistent background monitor and print **only on change**. Never pipe the raw
output: every printed line is a separate notification, and a chatty monitor buries your own turn.

```bash
prev="$(skills/orchestrator/bin/throne-orchestrator watch | grep <id> || echo UNKNOWN)"
while true; do
  sleep 30
  cur="$(skills/orchestrator/bin/throne-orchestrator watch | grep <id> || echo GONE)"
  [ "$cur" != "$prev" ] && { echo "executor moved: $cur"; prev="$cur"; }
done
```

Cover disappearance, not just success: an executor that dies leaves no status behind, and the
`|| echo GONE` branch is the only thing that reports it.

What the first live run (2026-09-02) actually showed:

- Delivery works across parking. The monitor survived the orchestrator being parked in
  `awaiting_operator` and survived the executor's session ending, then delivered the disappearance
  event into the parked session and woke it.
- One transition did **not** arrive: the executor moving `work → awaiting_operator`, even though the
  watch line changes on it. Cause unknown. Until it is understood, do not rely on the monitor to
  tell you an executor is ready — confirm with a plain `watch` of your own.
- Silence proves nothing. While no event had fired, the monitor counted as a live task yet its
  output file did not exist at all — neither the missing file nor the quiet says watching is healthy.
- The monitor lives inside your session. Kill the orchestrator session and watching dies with it;
  nothing restarts it, and the next session has to stand it up again.

So you may promise the operator that you will keep an eye on the executors — and then verify with
your own `watch` before you act on what the monitor told you.

## Independent review

```bash
skills/orchestrator/bin/throne-orchestrator review --intent <child>
```

The executor must not review its own branch, and neither do you — its report tells you what it
*thinks* it did. `review` starts a second agent that knows only the statement of work
(ADR-0054 §8): it cuts exactly three sections out of the child body — `## Ветка`,
`## Definition of Done`, `## Для человека` — creates a new review intent of your tag from them,
links it to the child («ревью вытекает из задачи»), and launches a `verify` session on it. Nothing
else crosses over: not `## Для агента`, not `## Отчёт`, not the executor's chat, not your journal.
The command prints the review intent id as soon as the intent exists — before the link and the
session start — so if either fails, the id is on your screen. Do not run `review` again on reflex:
that creates a second review intent for the same branch. Note the id in the journal, find out why
the link or the launch failed, and only then decide whether a fresh `review` is warranted. A body
without `## Ветка` or without a non-empty DoD is refused before anything is created; so is a body
with an unclosed code fence, which would hide those sections — the error names the fence line.

`--vendor`/`--model`/`--effort` work as for `run`, under the same policy — and the strongest
model is refused here on top of the effort ceiling: checking against a DoD is not the place for it. One reviewer at a time, for the same
reasons as one executor: the spawn is synchronous and the vendor trust file is shared.

The reviewer fetches the branch, reads the diff against the main branch, runs the project's tests
and gates, checks every DoD line against a fact, breaks at least one proof-test by mutation to see
it go red, and writes its verdict into its own body as `## Вердикт`: `принято` / `не принято` on
the first line, then each DoD line with the evidence, then defects with file and line. It does not
change code and does not push. Wait for it with `watch` like for any executor, then read the
verdict: `THRONE_INTENT_ID=<review id> skills/intent/bin/throne-intent get`.

Each `review` call creates a fresh review intent — a verdict belongs to one state of the branch, and
a re-run after rework gets its own. After you have read the verdict, leave the review intent where it
is: it stays in `awaiting_operator` as the record of that round, `watch` no longer lists it, and its
id goes into your journal next to the decision. You do not change its status — statuses are the
operator's, who closes review intents when tidying the tag.

## Accepting their work

An executor that came back (`awaiting_operator`, or a session that vanished) has handed you a
branch. Accepting it is your job — the operator is not a reviewer on call — and it rests on the
reviewer's verdict, not on the executor's report. The sequence:

1. `review --intent <child>`, wait for the reviewer, read `## Вердикт` from the review intent.
2. `не принято` → go to step 4. `принято` → you may still glance at `## Отчёт` of the child for what
   was left out, but do not re-read the diff as a substitute for the review: the verdict is the
   judgment, yours is to act on it.
3. Let the CLI do the deterministic part:

```bash
skills/orchestrator/bin/throne-orchestrator accept --repo <abs path to clone> --branch <name> \
  --check "<test command>"
```

`accept` refuses a dirty tree or unpushed local commits on the main branch (65), then fetches,
resets the local main branch to `origin/<main>` (pass `--into` when origin has no default branch;
`main` and `origin/main` mean the same), merges the executor's branch `--no-ff`, runs `--check`
inside the merged tree, and pushes. Every failure has its own exit code and leaves the main branch
exactly where origin has it: 66 — the branch was never pushed; 67 — merge conflict, the conflicting
files are listed on stderr for the executor; 68 — the check is red (the merge is rolled back, the
check's leftovers cleaned); 69 — the push was rejected (retry). A merge failure that is not a
conflict (unrelated histories, a hook) exits 1 with git's own output — a rebase will not fix it, so
read it. Re-running on an already merged branch is a no-op that prints «уже влито». The executor's
branch is never touched.

4. Accepted: journal it (`YYYY-MM-DD — принято <branch>. Ревью: <review id>. Интенты: <child>`),
   move the child out of `## В работе`, take the next statement of work.
   Not accepted (verdict `не принято`, or `accept` returned 66–68): append the reviewer's findings
   — DoD lines without evidence, defects with file and line — to the child intent and `run` it
   again; after it comes back, `review` again. Do not finish the work yourself, and do not carry it
   to the operator — a returned task is the ordinary loop, not an escalation.

Pushing and merging into the tag's own repositories is what this mode is for. The general
«ask before push» rule from the shared prompt parts does not apply here; the operator is not asked.

## Talking to the operator

The operator is the architect; you are the tech lead. Come with exactly three kinds of things:

- an architectural fork — two viable paths, each with its cost, and your recommendation;
- a contradiction in the statement of work that the decision journal cannot resolve;
- an irreversible action outside the tag: deleting data, touching someone else's repository,
  a force-push.

Everything else is your decision, written into the journal. «Проверять? Вливать? Продолжать?
Запускать следующую?» are not questions — they are the job. If you catch yourself typing one, do the
thing and report it done.

## Editing your own body

Your memory lives in your own `Intent.text` (ADR-0054): `## Зона`, `## Решения`, `## В работе`,
`## Открытые вопросы`. Append decisions to `## Решения` — newest at the bottom, never rewrite past
entries. Write through `skills/intent/bin/throne-intent replace-text` with a **fragment**, never the
whole body: the pre-flight modal edits the same text, and a full rewrite loses the race on version
conflict.

Keep the journal bounded. The body is injected as the task zone of every next orchestrator session,
so recent entries stay verbatim and older ones get folded into a summary line.

## Tests

`skills/orchestrator/tests/test_run_effort.py` property-tests the launch policy: no payload
carries a forbidden model×effort pair, omitted flags send the policy defaults, forbidden pairs are
refused before any HTTP call; `test_review_body.py` — how `review` cuts the review intent body
out of the child and that its payload runs `verify` (both need `pytest` + `hypothesis`):

```bash
python3 -m pytest skills/orchestrator/tests -q
```

## Environment

The script reads two variables from the environment. A Throne-spawned session has them set.

- `THRONE_API_BASE` — optional. Defaults to the local backend `http://localhost:5008`.
- `THRONE_INTENT_ID` — required. It is the orchestrator intent itself; without it there is no tag
  to resolve and the command correctly refuses to guess.

## Rules

- If your own tag's repository is Throne itself, run `scripts/check-deploy.sh` before spawning any
  executor; on drift, reinstall with `scripts/install-local.sh` first. A stale local instance hands
  every executor old prompts and old skills without anyone noticing.
- Do not write intent status from the agent. Throne derives status from session hooks.
- An orchestrator intent carries exactly one tag. Several tags is a broken setup — the CLI refuses
  instead of picking one.
- Every launch stays within the launch policy (effort `low`/`medium`, review off the strongest
  model). A task that seems to need more is a decomposition defect — split it, never raise the tier.
- You do not write the task's code yourself. Your output is a statement of work, a launched
  executor, an accepted and merged branch, a decision recorded in your body.
- Acceptance is yours, not the operator's. The operator sees merged results and architectural forks
  — never a «shall I check / merge / continue?».
- Review is neither yours nor the executor's: a separate `verify` session that knows only the DoD,
  the problem and the branch. You accept on its verdict, not on the executor's report or your own
  reading of the diff.
