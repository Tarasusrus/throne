namespace Throne.Application.Terminals;

/// <summary>
/// One intent's pause on a vendor usage limit (ADR-0055). Lives for the Throne process (the
/// vendor session itself survives in tmux and waits on its own); <see cref="ResumedAt"/> is set
/// when the session provably works again — the vendor's own auto-continue notification or the
/// next tool call — and the record then only carries the attempt counter for the repeat window.
/// </summary>
/// <param name="ResumeAt">When the vendor said the limit resets (or detection + default retry).</param>
/// <param name="Attempts">Consecutive limit hits within the repeat window; the ceiling escalates.</param>
/// <param name="Message">The vendor line as received — shown to the operator verbatim.</param>
/// <param name="ResumedAt">Null while paused; set once the session is working again.</param>
/// <param name="LastNudgeAt">Last time Throne itself pushed the session past the reset.</param>
/// <param name="Nudges">How many times Throne pushed; capped by policy.</param>
public sealed record VendorLimitPause(
    string IntentId,
    string Vendor,
    DateTimeOffset DetectedAt,
    DateTimeOffset ResumeAt,
    int Attempts,
    string Message,
    DateTimeOffset? ResumedAt,
    DateTimeOffset? LastNudgeAt,
    int Nudges)
{
    public bool IsActive => ResumedAt is null;
}
