/**
 * «до когда» для паузы по лимиту вендора (ADR-0055): время в локали оператора; если сброс не
 * сегодня — с датой. Невалидная строка отдаётся как есть, чтобы не спрятать ошибку сервера.
 */
export function formatLimitResumeAt(
  resumeAt: string,
  now: Date = new Date()
): string {
  const at = new Date(resumeAt);
  if (Number.isNaN(at.getTime())) return `в ${resumeAt}`;
  const time = at.toLocaleTimeString("ru-RU", {
    hour: "2-digit",
    minute: "2-digit"
  });
  const sameDay =
    at.getFullYear() === now.getFullYear() &&
    at.getMonth() === now.getMonth() &&
    at.getDate() === now.getDate();
  if (sameDay) return `в ${time}`;
  const date = at.toLocaleDateString("ru-RU", {
    day: "numeric",
    month: "short"
  });
  return `${date} в ${time}`;
}
