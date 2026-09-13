using System.Globalization;
using System.Text.RegularExpressions;

namespace Throne.Application.Terminals;

/// <summary>
/// Turns a vendor's limit message into a <see cref="VendorLimitSignal"/> by the pattern table in
/// <see cref="VendorLimitMessagePatterns"/>. Pure: the caller passes «now» so the wall-clock reset
/// time («1:40pm (Asia/Bangkok)») resolves to the next occurrence deterministically.
/// </summary>
public static class VendorLimitMessageParser
{
    /// <summary>
    /// A reset time that already passed by less than this is taken as-is (the hook or the pane
    /// read may lag the vendor by a moment); anything older rolls to the next day.
    /// </summary>
    public static readonly TimeSpan LateTolerance = TimeSpan.FromMinutes(5);

    public static VendorLimitSignal? TryParse(string vendor, string? message, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        foreach (var pattern in VendorLimitMessagePatterns.For(vendor))
        {
            var match = pattern.Regex.Match(message);
            if (!match.Success)
            {
                continue;
            }

            var tz = match.Groups["tz"].Success ? match.Groups["tz"].Value.Trim() : null;
            return new VendorLimitSignal(
                pattern.Kind,
                ResolveReset(match, tz, now),
                tz,
                ParsePercent(match),
                message);
        }

        return null;
    }

    private static int? ParsePercent(Match match) =>
        match.Groups["percent"].Success
        && int.TryParse(match.Groups["percent"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var p)
            ? p
            : null;

    private static DateTimeOffset? ResolveReset(Match match, string? tz, DateTimeOffset now)
    {
        if (!match.Groups["h"].Success)
        {
            return null;
        }

        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var ampm = match.Groups["ampm"].Success ? match.Groups["ampm"].Value.ToLowerInvariant() : null;
        if (ampm is not null)
        {
            hour %= 12;
            if (ampm == "pm")
            {
                hour += 12;
            }
        }
        if (hour > 23 || minute > 59)
        {
            return null;
        }

        return NextOccurrence(hour, minute, ResolveZone(tz), now);
    }

    private static TimeZoneInfo ResolveZone(string? tz)
    {
        if (string.IsNullOrEmpty(tz))
        {
            return TimeZoneInfo.Local;
        }
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tz);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static DateTimeOffset NextOccurrence(int hour, int minute, TimeZoneInfo zone, DateTimeOffset now)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = AtWallClock(localNow.Date, hour, minute, zone);
        if (candidate < now - LateTolerance)
        {
            candidate = AtWallClock(localNow.Date.AddDays(1), hour, minute, zone);
        }
        return candidate;
    }

    private static DateTimeOffset AtWallClock(DateTime date, int hour, int minute, TimeZoneInfo zone)
    {
        var wall = new DateTime(date.Year, date.Month, date.Day, hour, minute, 0, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
        {
            wall = wall.AddHours(1);
        }
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }
}
