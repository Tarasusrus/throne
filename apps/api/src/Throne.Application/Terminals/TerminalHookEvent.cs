namespace Throne.Application.Terminals;

public sealed record TerminalHookEvent(
    string IntentId,
    string Event,
    string? Mode,
    DateTimeOffset ReceivedAt,
    TerminalHookPayload? Payload = null);

/// <summary>
/// The few fields Throne reads from the vendor's hook stdin JSON (forwarded as the callback body).
/// Everything is optional: Codex and OpenCode bodies carry none of them, and a Claude
/// <c>Stop</c> carries only <see cref="LastAssistantMessage"/>. <see cref="Error"/> is the
/// <c>StopFailure</c> error class (<c>rate_limit</c>, …), <see cref="NotificationType"/> the
/// Claude <c>Notification</c> type (<c>permission_prompt</c>, <c>quota_auto_resume_fired</c>, …),
/// <see cref="Message"/> the notification text.
/// </summary>
public sealed record TerminalHookPayload(
    string? Error,
    string? LastAssistantMessage,
    string? NotificationType,
    string? Message)
{
    public static readonly TerminalHookPayload Empty = new(null, null, null, null);
}

public interface ITerminalHookBus
{
    ValueTask PublishAsync(TerminalHookEvent hook, CancellationToken ct);
}

public interface ITerminalHookEventReader
{
    IAsyncEnumerable<TerminalHookEvent> ReadAllAsync(CancellationToken ct);
}

public interface ITerminalHookSubscriber
{
    Task HandleAsync(TerminalHookEvent hook, CancellationToken ct);
}
