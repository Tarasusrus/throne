using Throne.Application.Events;
using Throne.Realtime.Contracts;
using Throne.Realtime.Contracts.Generated;

namespace Throne.Api.Realtime;

internal static class TerminalRealtimeMapper
{
    public static RealtimeEventEnvelope? TryMap(IDomainEvent evt) => evt switch
    {
        TerminalSessionStarted started => new RealtimeEventEnvelope(
            RealtimeEventNames.TerminalSessionStarted,
            new { intent_id = started.IntentId }),
        TerminalSessionStopped stopped => new RealtimeEventEnvelope(
            RealtimeEventNames.TerminalSessionStopped,
            new { intent_id = stopped.IntentId }),
        TerminalPromptSubmitUnconfirmed unconfirmed => new RealtimeEventEnvelope(
            RealtimeEventNames.TerminalPromptSubmitUnconfirmed,
            new { intent_id = unconfirmed.IntentId }),
        TerminalLimitPaused paused => new RealtimeEventEnvelope(
            RealtimeEventNames.TerminalLimitPaused,
            new { intent_id = paused.IntentId, resume_at = paused.ResumeAt, attempts = paused.Attempts }),
        TerminalLimitResumed resumed => new RealtimeEventEnvelope(
            RealtimeEventNames.TerminalLimitResumed,
            new { intent_id = resumed.IntentId }),
        _ => null,
    };
}
