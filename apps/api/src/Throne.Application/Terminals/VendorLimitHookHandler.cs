using Throne.Application.Events;
using Throne.Application.Intents;
using Throne.Application.Ports;
using Throne.Domain.Intents;
using Throne.Domain.Intents.Training;

namespace Throne.Application.Terminals;

/// <summary>
/// Turns vendor hook callbacks into the limit pause and back (ADR-0055). Runs beside
/// <see cref="TerminalHookStatusHandler"/> on the same bus and never moves the intent status
/// except for the escalation past the attempt ceiling: a limit is schedule, not a stop.
/// <list type="bullet">
///   <item><c>StopFailure</c> with <c>error=rate_limit</c> — parse the vendor line for the reset
///   time, ask <see cref="VendorLimitPolicy"/>, store the pause or park the intent in
///   <c>awaiting_operator</c> with the reason.</item>
///   <item><c>Notification quota_auto_resume_fired</c>, <c>PostToolUse</c>, <c>UserPromptSubmit</c>
///   — the session provably works: the pause is marked resumed (the attempt counter stays for the
///   repeat window).</item>
/// </list>
/// </summary>
public sealed class VendorLimitHookHandler(
    IVendorLimitPauseStore pauses,
    IIntentTerminalLaunchStore launches,
    IIntentRepository repository,
    SetIntentStatusHandler setStatus,
    IDomainEventDispatcher events,
    VendorLimitPolicyOptions options,
    TimeProvider clock) : ITerminalHookSubscriber
{
    public const string EscalationSource = "hook:terminal:vendor_limit";

    public async Task HandleAsync(TerminalHookEvent hook, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var payload = hook.Payload ?? TerminalHookPayload.Empty;

        if (hook.Event == TerminalHookEvents.StopFailure)
        {
            if (payload.Error == VendorLimitHookSignals.RateLimitError)
            {
                await OnLimitHitAsync(hook.IntentId, payload.LastAssistantMessage ?? payload.Message, ct);
            }
            return;
        }

        if (IsResumeSignal(hook.Event, payload.NotificationType))
        {
            await MarkResumedAsync(hook.IntentId, ct);
        }
    }

    /// <summary>Marks the intent's pause resumed; no-op without an active pause. Shared with the sweep.</summary>
    public async Task MarkResumedAsync(string intentId, CancellationToken ct)
    {
        var pause = pauses.Find(intentId);
        if (pause is null || !pause.IsActive)
        {
            return;
        }

        pauses.Put(pause with { ResumedAt = clock.GetUtcNow() });
        await events.DispatchAsync(new TerminalLimitResumed(intentId), ct);
    }

    private static bool IsResumeSignal(string hookEvent, string? notificationType) => hookEvent switch
    {
        TerminalHookEvents.PostToolUse or TerminalHookEvents.UserPromptSubmit => true,
        TerminalHookEvents.Notification => notificationType == VendorLimitHookSignals.QuotaAutoResumeFired,
        _ => false,
    };

    private async Task OnLimitHitAsync(string intentId, string? message, CancellationToken ct)
    {
        var intent = await repository.GetByIdAsync(new IntentId(intentId), ct);
        if (intent is null || IntentStatusNames.IsTerminal(intent.State.Status))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var vendor = (await launches.GetAsync(intentId, ct))?.Vendor ?? TerminalAgentCatalog.VendorClaude;
        // The hook already said rate_limit; an unparsed line only loses the reset time.
        var signal = VendorLimitMessageParser.TryParse(vendor, message, now)
            ?? new VendorLimitSignal(VendorLimitSignalKind.Hit, null, null, null, message ?? VendorLimitHookSignals.RateLimitError);

        switch (VendorLimitPolicy.OnHit(intentId, vendor, pauses.Find(intentId), signal, now, options))
        {
            case VendorLimitDecision.Pause pause:
                pauses.Put(pause.Value);
                await events.DispatchAsync(
                    new TerminalLimitPaused(intentId, pause.Value.ResumeAt, pause.Value.Attempts), ct);
                break;
            case VendorLimitDecision.Escalate escalate:
                pauses.Remove(intentId);
                await setStatus.HandleAsync(
                    new SetIntentStatusCommand(
                        intentId,
                        IntentStatusNames.AwaitingOperator,
                        escalate.Reason,
                        IntentTrainingAuthor.System,
                        EscalationSource),
                    ct);
                break;
        }
    }
}
