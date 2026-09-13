using System.Text.RegularExpressions;

namespace Throne.Application.Terminals;

/// <summary>
/// One row of the vendor limit-message table: which vendor prints it, what it means, and the
/// regex that recognises it. The optional named groups <c>percent</c> (warning share),
/// <c>h</c>/<c>m</c>/<c>ampm</c> (wall-clock reset time) and <c>tz</c> (zone in parentheses)
/// are read by <see cref="VendorLimitMessageParser"/>; a pattern without them yields a signal
/// with no reset time.
/// </summary>
public sealed record VendorLimitPattern(string Vendor, VendorLimitSignalKind Kind, Regex Regex);

/// <summary>
/// The single place vendor limit phrasings live (ADR-0055). Claude Code rows are verified against
/// real strings from 2.1.263–2.1.270 transcripts and the CLI binary (13.09.2026):
/// <c>You've hit your monthly spend limit · raise it at … · your session limit resets 1:40pm (Asia/Bangkok)</c>
/// and the 95 % warning <c>You've used 95% of your session limit · resets 2:10pm (Asia/Bangkok) · …</c>.
/// Adding a vendor = adding rows here plus a test on its real lines; nothing else branches on
/// the wording.
/// </summary>
public static class VendorLimitMessagePatterns
{
    // «resets 1:40pm (Asia/Bangkok)», «resets 2pm (UTC)», «resets at 13:40». Zone optional.
    private const string ResetClause =
        @"resets?\s+(?:at\s+)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ampm>am|pm)?(?:\s*\((?<tz>[^)]+)\))?";

    private const RegexOptions Flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static readonly IReadOnlyList<VendorLimitPattern> All =
    [
        // Claude Code — limit reached (the turn ends with this text; StopFailure error=rate_limit).
        new(
            TerminalAgentCatalog.VendorClaude,
            VendorLimitSignalKind.Hit,
            new Regex(
                @"You(?:'|’)(?:ve|re)\s+(?:hit|reached)\s+your\s+.*?\b(?:limit|budget)\b(?:.*?\b" + ResetClause + ")?",
                Flags)),
        new(
            TerminalAgentCatalog.VendorClaude,
            VendorLimitSignalKind.Hit,
            new Regex(@"You(?:'|’)re\s+out\s+of\s+usage\s+credits(?:.*?\b" + ResetClause + ")?", Flags)),
        // Claude Code — approaching-limit warning (TUI status line at 95 %).
        new(
            TerminalAgentCatalog.VendorClaude,
            VendorLimitSignalKind.Warning,
            new Regex(
                @"You(?:'|’)ve\s+used\s+(?<percent>\d{1,3})%\s+of\s+your\s+.*?\blimit\b(?:.*?\b" + ResetClause + ")?",
                Flags)),
    ];

    public static IEnumerable<VendorLimitPattern> For(string vendor) =>
        All.Where(p => string.Equals(p.Vendor, vendor, StringComparison.Ordinal));
}
