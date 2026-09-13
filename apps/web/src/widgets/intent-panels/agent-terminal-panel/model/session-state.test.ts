import { describe, expect, it } from "vitest";

import {
  applyLimitPaused,
  applyLimitResumed,
  isLiveSessionState
} from "./session-state";
import type { TerminalSessionState } from "./types";

const STATES: readonly (TerminalSessionState | "idle")[] = [
  "idle",
  "spawning",
  "running",
  "paused_by_limit",
  "blocked",
  "exited"
];

describe("session-state", () => {
  it("живые состояния — spawning, running, paused_by_limit", () => {
    expect(STATES.filter(isLiveSessionState)).toEqual([
      "spawning",
      "running",
      "paused_by_limit"
    ]);
  });

  it("свойство: paused переводит только живую сессию; resumed всегда снимает паузу и возвращает running только из paused", () => {
    const payload = {
      intent_id: "i",
      resume_at: "2026-09-13T06:40:00Z",
      attempts: 2
    };
    for (const state of STATES) {
      for (const prevPause of [
        null,
        { resume_at: "x", attempts: 1, message: "old" }
      ]) {
        const prev = { state, limitPause: prevPause, other: 42 };
        const paused = applyLimitPaused(prev, payload);
        if (isLiveSessionState(state)) {
          expect(paused.state).toBe("paused_by_limit");
          expect(paused.limitPause).toEqual({
            resume_at: payload.resume_at,
            attempts: 2,
            message: prevPause?.message ?? ""
          });
        } else {
          expect(paused).toBe(prev);
        }
        expect(paused.other).toBe(42);

        const resumed = applyLimitResumed(paused);
        expect(resumed.limitPause).toBeNull();
        expect(resumed.state).toBe(
          paused.state === "paused_by_limit" ? "running" : paused.state
        );
        // Идемпотентность: повторное resumed ничего не меняет.
        expect(applyLimitResumed(resumed)).toEqual(resumed);
      }
    }
  });
});
