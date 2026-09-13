using System.Globalization;
using Throne.Application.Events;
using Throne.Application.Intents;
using Throne.Application.Ports;
using Throne.Domain.Intents;
using Throne.Domain.Intents.Training;

namespace Throne.Application.Terminals;

/// <summary>
/// What the sweep can do to a paused session. Nudging pushes a continuation prompt into the live
/// pane (the vendor's own auto-continue did not fire); relaunching respawns a dead session with
/// the conversation continued (<c>--continue</c>) and the same continuation prompt.
/// </summary>
public interface IVendorLimitSessionResumer
{
    Task NudgeAsync(VendorLimitPause pause, CancellationToken ct);

    Task RelaunchAsync(VendorLimitPause pause, CancellationToken ct);
}

/// <summary>
/// Per-tick pass over the vendor-limit pauses (ADR-0055). The vendor gets the first chance to
/// resume on its own (<see cref="VendorLimitPolicyOptions.ResumeGrace"/>); past it Throne nudges a
/// live session, relaunches a dead one, and past the nudge ceiling hands the intent to the operator.
/// Hosted by a <c>BackgroundService</c> in Infrastructure; this type owns the decisions so it is
/// unit-testable without a host.
/// </summary>
public sealed class VendorLimitResumeSweep(
    IVendorLimitPauseStore pauses,
    ITmuxSessionManager tmux,
    IVendorLimitSessionResumer resumer,
    IIntentRepository repository,
    SetIntentStatusHandler setStatus,
    IDomainEventDispatcher events,
    VendorLimitPolicyOptions options,
    TimeProvider clock)
{
    public const string EscalationSource = "sweep:terminal:vendor_limit";

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var pause in pauses.All())
        {
            await VisitAsync(pause, ct);
        }
    }

    private async Task VisitAsync(VendorLimitPause pause, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!pause.IsActive)
        {
            // Resumed records only carry the attempt counter for the repeat window.
            if (now - pause.ResumedAt!.Value > options.RepeatWindow)
            {
                pauses.Remove(pause.IntentId);
            }
            return;
        }

        var alive = await tmux.HasSessionAsync(pause.IntentId, ct);
        var action = VendorLimitPolicy.OnTick(pause, now, alive, options);
        if (action == VendorLimitTickAction.None)
        {
            return;
        }

        var intent = await repository.GetByIdAsync(new IntentId(pause.IntentId), ct);
        if (intent is null || IntentStatusNames.IsTerminal(intent.State.Status))
        {
            pauses.Remove(pause.IntentId);
            return;
        }

        switch (action)
        {
            case VendorLimitTickAction.Nudge:
                await resumer.NudgeAsync(pause, ct);
                pauses.Put(pause with { LastNudgeAt = now, Nudges = pause.Nudges + 1 });
                break;
            case VendorLimitTickAction.Relaunch:
                await RelaunchAsync(pause, now, ct);
                break;
            case VendorLimitTickAction.Escalate:
                pauses.Remove(pause.IntentId);
                await setStatus.HandleAsync(
                    new SetIntentStatusCommand(
                        pause.IntentId,
                        IntentStatusNames.AwaitingOperator,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Сессия не возобновилась после сброса лимита вендора: {0} толчков без ответа. Последнее сообщение вендора: {1}",
                            pause.Nudges, pause.Message),
                        IntentTrainingAuthor.System,
                        EscalationSource),
                    ct);
                break;
        }
    }

    private async Task RelaunchAsync(VendorLimitPause pause, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await resumer.RelaunchAsync(pause, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A failed respawn is retried on the nudge cadence and counts against the same ceiling,
            // so a persistently broken relaunch ends in the operator's inbox, not in a hot loop.
            pauses.Put(pause with { LastNudgeAt = now, Nudges = pause.Nudges + 1 });
            return;
        }

        pauses.Put(pause with { ResumedAt = now });
        await events.DispatchAsync(new TerminalLimitResumed(pause.IntentId), ct);
    }
}
