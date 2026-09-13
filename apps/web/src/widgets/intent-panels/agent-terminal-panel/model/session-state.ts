import type {
  RunIntentTerminalResponse,
  TerminalLimitPause,
  TerminalSessionState
} from "./types";

export interface LimitPausedPayload {
  intent_id: string;
  resume_at: string;
  attempts: number;
}

/** Живая tmux-сессия: к ней можно подключиться, её можно погасить. Пауза по лимиту — тоже живая. */
export function isLiveSessionState(
  state: TerminalSessionState | "idle"
): boolean {
  return (
    state === "running" || state === "spawning" || state === "paused_by_limit"
  );
}

interface LimitPauseSlice {
  state: TerminalSessionState | "idle";
  limitPause: TerminalLimitPause | null;
}

/**
 * Пауза по лимиту приходит событием, а не пробником (ADR-0055): сессия жива, меняется только её
 * состояние и «до когда». Сообщение вендора в событии не едет — остаётся прежнее или пустое.
 * На неживой сессии событие игнорируется: паузить нечего.
 */
export function applyLimitPaused<T extends LimitPauseSlice>(
  prev: T,
  payload: LimitPausedPayload
): T {
  if (!isLiveSessionState(prev.state)) return prev;
  return {
    ...prev,
    state: "paused_by_limit",
    limitPause: {
      resume_at: payload.resume_at,
      attempts: payload.attempts,
      message: prev.limitPause?.message ?? ""
    }
  };
}

/** Сессия снова работает: пауза снимается, paused_by_limit возвращается в running. */
export function applyLimitResumed<T extends LimitPauseSlice>(prev: T): T {
  return prev.state === "paused_by_limit"
    ? { ...prev, state: "running", limitPause: null }
    : { ...prev, limitPause: null };
}

export interface SessionStartedAtSlice {
  attempt: number;
  sessionName: string;
}

interface SessionResponseSlice extends LimitPauseSlice {
  lastResponse: RunIntentTerminalResponse | null;
  error: string | null;
  submitUnconfirmed: boolean;
  startedAt: SessionStartedAtSlice | null;
}

/**
 * Ответ run/kill/пробника целиком задаёт состояние сессии. Пробник, увидевший exited, оставляет
 * панель в idle (нечего «завершать»). Мягкая подсказка о неотправленном промпте сбрасывается —
 * realtime поднимет её снова, только если фоновая доставка реально не подтвердилась.
 */
export function applySessionResponse<T extends SessionResponseSlice>(
  prev: T,
  response: RunIntentTerminalResponse,
  fromProbe: boolean
): T {
  const hasLiveSession = isLiveSessionState(response.session_state);
  const nextAttempt = hasLiveSession
    ? (prev.startedAt?.attempt ?? 0) + 1
    : (prev.startedAt?.attempt ?? 0);
  return {
    ...prev,
    state:
      fromProbe && response.session_state === "exited"
        ? "idle"
        : response.session_state,
    lastResponse: response,
    error:
      response.session_state === "blocked"
        ? "Клон части репозиториев не готов — спавн агента невозможен."
        : null,
    submitUnconfirmed: false,
    limitPause: response.limit_pause ?? null,
    startedAt: hasLiveSession
      ? { attempt: nextAttempt, sessionName: response.session_name }
      : null
  };
}
