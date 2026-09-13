using Throne.Application.Events;

namespace Throne.Application.Terminals;

/// <summary>
/// A fresh spawn (an operator run after the paused session died, or the sweep's own relaunch)
/// starts from a clean slate: the old pause record is dropped so the sweep never nudges a new
/// session on the previous session's schedule (ADR-0055). The sweep re-records its relaunch as
/// resumed right after, keeping the attempt counter for the repeat window. A session <em>stop</em>
/// deliberately keeps the record — a session that died while paused is what the relaunch is for.
/// </summary>
public sealed class VendorLimitPauseOnSessionStartHandler(IVendorLimitPauseStore pauses) : IDomainEventHandler
{
    public Task HandleAsync(IDomainEvent evt, CancellationToken ct)
    {
        if (evt is TerminalSessionStarted started)
        {
            pauses.Remove(started.IntentId);
        }
        return Task.CompletedTask;
    }
}
