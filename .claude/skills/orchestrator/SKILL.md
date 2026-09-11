---
name: orchestrator
description: Thin entry point to the canonical Throne orchestrator skill at skills/orchestrator/SKILL.md. See the intents of your own tag and drive executors on them via skills/orchestrator/bin/throne-orchestrator. For manual dev sessions opened on the Throne mono-repo (not spawned by the Throne runtime).
---

# Throne Orchestrator (wrapper)

Thin wrapper, not a copy. The skill body is canonical in `skills/orchestrator/SKILL.md`; the CLI is
`skills/orchestrator/bin/throne-orchestrator`. Read the canon for the commands, the scope rules, and
the constraints — they are not duplicated here so the two never drift.

If your own intent body is not marked up as an orchestrator body, mark it up yourself and journal
that as a decision — do not stop for it; stop only when the intent carries several tags or the body
describes someone else's live work. Then `list` shows the intents of your tag with their statuses
and attached tracker cards, and `run` / `stop` drive executors on them — one at a time, because the
spawn is synchronous and the vendor trust file is shared; a child intent needs a
`## Definition of Done`. `watch` reports who moved; with `--wait` it blocks inside the turn, while a
background monitor built on it can wake a parked session — see the canon. The script degrades gracefully: `THRONE_API_BASE` defaults to
the local backend `http://localhost:5008`, while an unset `THRONE_INTENT_ID` means there is no
orchestrator intent to resolve a tag from — the command refuses instead of guessing. See
`skills/orchestrator/SKILL.md` for the full picture.
