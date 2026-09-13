namespace Throne.Application.Terminals;

/// <summary>
/// What a vendor's limit message means for Throne: an approaching-limit warning (save your work)
/// or the limit itself (the turn ended, the session waits for the reset).
/// </summary>
public enum VendorLimitSignalKind
{
    Warning,
    Hit,
}

/// <summary>
/// Parsed vendor limit message (ADR-0055). <see cref="ResetAt"/> is the absolute instant the
/// vendor named, resolved from the wall-clock time and the time zone in the message; null when
/// the message names no time — the pause policy then falls back to its default retry interval.
/// <see cref="TimeZoneId"/> is the zone token exactly as the vendor printed it (for display),
/// <see cref="Percent"/> the used share on a warning, <see cref="Raw"/> the message as received.
/// </summary>
public sealed record VendorLimitSignal(
    VendorLimitSignalKind Kind,
    DateTimeOffset? ResetAt,
    string? TimeZoneId,
    int? Percent,
    string Raw);
