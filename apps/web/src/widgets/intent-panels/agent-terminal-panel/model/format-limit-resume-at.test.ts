import { describe, expect, it } from "vitest";

import { formatLimitResumeAt } from "./format-limit-resume-at";

describe("formatLimitResumeAt", () => {
  const now = new Date(2026, 8, 13, 12, 0, 0);

  it("сегодня — только время", () => {
    const at = new Date(2026, 8, 13, 13, 40, 0);
    expect(formatLimitResumeAt(at.toISOString(), now)).toBe("в 13:40");
  });

  it("не сегодня — дата и время", () => {
    const at = new Date(2026, 8, 14, 9, 15, 0);
    const text = formatLimitResumeAt(at.toISOString(), now);
    expect(text).toMatch(/^14 сент\.? в 09:15$/);
  });

  it("мусор вместо даты не прячется", () => {
    expect(formatLimitResumeAt("not-a-date", now)).toBe("в not-a-date");
  });

  it("свойство: для любого момента текст содержит его локальные часы и минуты; дата появляется ровно при смене дня", () => {
    let seed = 7;
    const rand = () => {
      seed = (seed * 1103515245 + 12345) % 2147483648;
      return seed / 2147483648;
    };
    for (let i = 0; i < 300; i++) {
      const at = new Date(
        now.getTime() + Math.floor(rand() * 72 * 3600 * 1000)
      );
      const text = formatLimitResumeAt(at.toISOString(), now);
      const hh = String(at.getHours()).padStart(2, "0");
      const mm = String(at.getMinutes()).padStart(2, "0");
      expect(text, at.toISOString()).toContain(`в ${hh}:${mm}`);
      const sameDay =
        at.getDate() === now.getDate() && at.getMonth() === now.getMonth();
      expect(text.startsWith("в "), at.toISOString()).toBe(sameDay);
    }
  });
});
