namespace Throne.Application.Terminals;

/// <summary>
/// Persisted launch axis of one intent: the resolved mode + vendor/model/effort the last
/// spawn actually used, plus the per-mode skill selection remembered for the next preflight.
/// Stored per intent and echoed back by the run response and the status probe so the launch
/// controls restore the operator's per-intent choice and, while a session is live, show its
/// real parameters (ADR-0041). Values are wire strings (snake_case constants) — no enum
/// serialization on the persistence boundary.
/// <para>
/// <see cref="SelectedSkillIdsByMode"/> is the single «what is loaded» source for the launch
/// modal and the live-session badges: written on every successful spawn (the run pipeline
/// persists the curated set for the spawned mode) and unioned on hot-attach for the live
/// session's mode, so both the spawn-time selection and hot-attached skills resolve to one
/// per-mode set. «Loaded into the live session of mode M» = <c>SelectedSkillIdsByMode[M]</c>
/// while the session is alive (liveness stays tmux-derived, never stored). Empty map when the
/// intent was never launched.
/// </para>
/// <para>
/// <see cref="ReviewBindingId"/> is the repository binding the last spawn's review artifact was
/// aimed at (null when the spawn carried no review skill). A relaunch after a vendor-limit pause
/// (ADR-0055) re-runs the spawn pipeline with this id so a multi-binding intent keeps reviewing
/// the same PR instead of silently falling back to the first binding.
/// </para>
/// </summary>
public sealed record TerminalLaunchRecord(
    string Mode,
    string Vendor,
    string Model,
    string? Effort,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SelectedSkillIdsByMode,
    string? ReviewBindingId = null);
