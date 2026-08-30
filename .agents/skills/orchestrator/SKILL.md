---
name: orchestrator
description: Thin entry point to the canonical Throne orchestrator skill at skills/orchestrator/SKILL.md. See the intents of your own tag and drive executors on them via skills/orchestrator/bin/throne-orchestrator. For manual dev sessions opened on the Throne mono-repo (not spawned by the Throne runtime).
---

# Throne Orchestrator (wrapper)

Thin wrapper, not a copy. The skill body is canonical in `skills/orchestrator/SKILL.md`; the CLI is
`skills/orchestrator/bin/throne-orchestrator`. Read the canon for the commands, the scope rules, and
the constraints — they are not duplicated here so the two never drift.

First check that your own intent body starts with `[ORCH]`; if it does not, stop and report to the
operator rather than acting on a foreign tag. Then `list` shows the intents of your tag with their
statuses and attached tracker cards. The script degrades gracefully: `THRONE_API_BASE` defaults to
the local backend `http://localhost:5008`, while an unset `THRONE_INTENT_ID` means there is no
orchestrator intent to resolve a tag from — the command refuses instead of guessing. See
`skills/orchestrator/SKILL.md` for the full picture.
