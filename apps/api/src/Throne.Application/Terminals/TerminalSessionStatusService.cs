using Throne.Application.Ports;
using Throne.Domain.Repositories;

namespace Throne.Application.Terminals;

/// <summary>
/// Status probe. Liveness stays tmux-derived; the vendor-limit pause (ADR-0055) is layered on
/// top: a live session with an active pause reports <see cref="TerminalSessionStates.PausedByLimit"/>
/// so the UI and the orchestrator show «paused until …» instead of «running».
/// </summary>
public sealed class TerminalSessionStatusService(
    RunPreflightGuards guards,
    IIntentRepositoryBindingRepository bindings,
    IIntentTerminalLaunchStore launchStore,
    ITmuxSessionManager tmux,
    IVendorLimitPauseStore pauses)
{
    public async Task<RunPreflightResult> GetAsync(string intentId, CancellationToken ct)
    {
        await guards.EnsureTmuxDetectedAsync(ct);
        var intent = await guards.LoadIntentAsync(intentId, ct);
        var sessionName = TmuxSessionName.For(intent.Id.Value);
        var snapshot = await bindings.FindByIntentAsync(intent.Id, ct);
        var launch = await launchStore.GetAsync(intent.Id.Value, ct);
        var pause = pauses.Find(intent.Id.Value) is { IsActive: true } active ? active : null;
        var alive = await tmux.HasSessionAsync(intent.Id.Value, ct);
        var state = (alive, pause) switch
        {
            (true, not null) => TerminalSessionStates.PausedByLimit,
            (true, null) => TerminalSessionStates.Running,
            _ => TerminalSessionStates.Exited,
        };

        return RunPreflightSession.BuildResult(
            intent.Id.Value,
            sessionName,
            state,
            snapshot,
            RunPreflightSession.CollectBlocking(snapshot),
            launch) with
        { LimitPause = pause };
    }
}
