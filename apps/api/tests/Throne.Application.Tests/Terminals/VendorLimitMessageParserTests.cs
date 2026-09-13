using FluentAssertions;
using Throne.Application.Terminals;

namespace Throne.Application.Tests.Terminals;

/// <summary>
/// Парсер сообщений вендора о лимите: реальные строки Claude Code 2.1.263–2.1.270 из
/// транскриптов (13.09.2026) плюс свойства по сгенерированной грамматике — вид сигнала,
/// время сброса в часовом поясе из скобок, отсутствие времени → null, шум → null.
/// </summary>
public class VendorLimitMessageParserTests
{
    // 2026-09-13 12:00 Asia/Bangkok (UTC+7) = 05:00Z.
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 5, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Bangkok = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");

    private const string RealHit =
        "You've hit your monthly spend limit · raise it at claude.ai/settings/usage?from=cc_cli_limit_message · your session limit resets 1:40pm (Asia/Bangkok)";

    private const string RealHitPlain = "You've hit your monthly spend limit.";

    private const string RealWarning =
        "You've used 95% of your session limit · resets 2:10pm (Asia/Bangkok) · /upgrade to keep using Claude C…";

    [Fact(DisplayName = "Реальная строка лимита Claude: вид hit, сброс 13:40 Asia/Bangkok того же дня")]
    public void Real_hit_line_yields_hit_with_reset_time()
    {
        var signal = VendorLimitMessageParser.TryParse(TerminalAgentCatalog.VendorClaude, RealHit, Now);

        signal.Should().NotBeNull();
        signal!.Kind.Should().Be(VendorLimitSignalKind.Hit);
        signal.ResetAt.Should().Be(new DateTimeOffset(2026, 9, 13, 13, 40, 0, TimeSpan.FromHours(7)));
        signal.TimeZoneId.Should().Be("Asia/Bangkok");
    }

    [Fact(DisplayName = "Реальная строка лимита без времени: hit, ResetAt = null (дефолтный интервал повтора решает политика)")]
    public void Real_hit_without_time_has_null_reset()
    {
        var signal = VendorLimitMessageParser.TryParse(TerminalAgentCatalog.VendorClaude, RealHitPlain, Now);

        signal.Should().NotBeNull();
        signal!.Kind.Should().Be(VendorLimitSignalKind.Hit);
        signal.ResetAt.Should().BeNull();
    }

    [Fact(DisplayName = "Реальная строка предупреждения 95%: вид warning, процент и время сброса извлечены")]
    public void Real_warning_line_yields_warning_with_percent()
    {
        var signal = VendorLimitMessageParser.TryParse(TerminalAgentCatalog.VendorClaude, RealWarning, Now);

        signal.Should().NotBeNull();
        signal!.Kind.Should().Be(VendorLimitSignalKind.Warning);
        signal.Percent.Should().Be(95);
        signal.ResetAt.Should().Be(new DateTimeOffset(2026, 9, 13, 14, 10, 0, TimeSpan.FromHours(7)));
    }

    [Theory(DisplayName = "Прочие формулировки Claude о лимите распознаются как hit")]
    [InlineData("You've hit your channel's monthly spend limit.")]
    [InlineData("You've hit your team's shared budget. Switch to another model to continue.")]
    [InlineData("You've reached your Fable limit.")]
    [InlineData("You're out of usage credits.")]
    public void Other_claude_hit_phrasings_are_hits(string message)
    {
        var signal = VendorLimitMessageParser.TryParse(TerminalAgentCatalog.VendorClaude, message, Now);

        signal.Should().NotBeNull();
        signal!.Kind.Should().Be(VendorLimitSignalKind.Hit);
    }

    [Theory(DisplayName = "Обычный вывод агента, пустые строки и чужой вендор — не сигнал")]
    [InlineData(TerminalAgentCatalog.VendorClaude, null)]
    [InlineData(TerminalAgentCatalog.VendorClaude, "")]
    [InlineData(TerminalAgentCatalog.VendorClaude, "Готово: тесты зелёные, ветка запушена.")]
    [InlineData(TerminalAgentCatalog.VendorClaude, "The limit of 10 retries was reached while cloning.")]
    [InlineData(TerminalAgentCatalog.VendorClaude, "Session resets are handled by the scheduler at 1:40pm.")]
    [InlineData("some-vendor", RealHit)]
    public void Noise_and_unknown_vendor_are_not_a_signal(string vendor, string? message)
    {
        VendorLimitMessageParser.TryParse(vendor, message, Now).Should().BeNull();
    }

    [Theory(DisplayName = "Стенные часы: прошедшее сегодня время — завтра; прошедшее минуту назад — сегодня (хук мог опоздать)")]
    [InlineData("9:15am", 14, 9, 15)]
    [InlineData("11:59am", 13, 11, 59)]
    public void Wall_clock_rolls_only_past_the_late_tolerance(string clock, int day, int hour, int minute)
    {
        // Сейчас 12:00 Asia/Bangkok.
        var signal = VendorLimitMessageParser.TryParse(
            TerminalAgentCatalog.VendorClaude,
            $"You've hit your monthly spend limit · your session limit resets {clock} (Asia/Bangkok)",
            Now);

        signal!.ResetAt.Should().Be(new DateTimeOffset(2026, 9, day, hour, minute, 0, TimeSpan.FromHours(7)));
    }

    [Fact(DisplayName = "Неизвестный часовой пояс в скобках: время трактуется в поясе хоста, сигнал не теряется")]
    public void Unknown_time_zone_falls_back_to_local()
    {
        var signal = VendorLimitMessageParser.TryParse(
            TerminalAgentCatalog.VendorClaude,
            "You've hit your monthly spend limit · your session limit resets 1:40pm (Mars/Olympus)",
            Now);

        signal.Should().NotBeNull();
        signal!.ResetAt.Should().NotBeNull();
        signal.TimeZoneId.Should().Be("Mars/Olympus");
    }

    // ---- Свойства по сгенерированной грамматике ------------------------------------------

    private const int Cases = 400;

    private static readonly string[] HitPhrases =
    [
        "You've hit your monthly spend limit",
        "You've hit your channel's monthly spend limit",
        "You've hit your team's shared budget",
        "You've reached your Fable limit",
        "You're out of usage credits",
    ];

    private static readonly string[] LimitNames = ["session limit", "weekly limit", "5-hour limit", "limit"];

    private static readonly (string Id, int OffsetHours)[] Zones =
    [
        ("Asia/Bangkok", 7),
        ("Europe/Moscow", 3),
        ("UTC", 0),
        ("America/Toronto", -4),
    ];

    private static readonly string[] Tails =
    [
        "",
        " · raise it at claude.ai/settings/usage?from=cc_cli_limit_message",
        " · /upgrade to keep using Claude C…",
        " · Press Enter to continue",
    ];

    private sealed record Sample(
        string Message,
        VendorLimitSignalKind Kind,
        int? Percent,
        int? Hour,
        int? Minute,
        string? Zone,
        int ZoneOffsetHours);

    private static Sample Generate(int seed)
    {
        var rng = new Random(seed);
        var isWarning = rng.Next(3) == 0;
        var percent = isWarning ? 50 + rng.Next(50) : (int?)null;
        var hasTime = rng.Next(4) != 0;
        int? hour = hasTime ? rng.Next(24) : null;
        int? minute = hasTime ? rng.Next(60) : null;
        var (zoneId, zoneOffset) = Zones[rng.Next(Zones.Length)];
        var limitName = LimitNames[rng.Next(LimitNames.Length)];

        var head = isWarning
            ? $"You've used {percent}% of your {limitName}"
            : HitPhrases[rng.Next(HitPhrases.Length)];
        var timePart = "";
        if (hasTime)
        {
            var h12 = hour!.Value % 12 == 0 ? 12 : hour.Value % 12;
            var ampm = hour.Value < 12 ? "am" : "pm";
            var clock = minute!.Value == 0 && rng.Next(2) == 0 ? $"{h12}{ampm}" : $"{h12}:{minute:00}{ampm}";
            timePart = isWarning
                ? $" · resets {clock} ({zoneId})"
                : $" · your {limitName} resets {clock} ({zoneId})";
        }
        var tail = Tails[rng.Next(Tails.Length)];
        var message = head + timePart + tail;
        if (!hasTime && rng.Next(2) == 0)
        {
            message += ".";
        }
        return new Sample(message, isWarning ? VendorLimitSignalKind.Warning : VendorLimitSignalKind.Hit,
            percent, hour, minute, hasTime ? zoneId : null, zoneOffset);
    }

    [Fact(DisplayName = "Свойство: вид сигнала и процент читаются из любой сгенерированной строки")]
    public void Kind_and_percent_survive_generation()
    {
        var warnings = 0;
        for (var seed = 0; seed < Cases; seed++)
        {
            var s = Generate(seed);
            var signal = VendorLimitMessageParser.TryParse(TerminalAgentCatalog.VendorClaude, s.Message, Now);
            var why = $"seed {seed}: {s.Message}";
            signal.Should().NotBeNull(why);
            signal!.Kind.Should().Be(s.Kind, why);
            signal.Percent.Should().Be(s.Percent, why);
            if (s.Kind == VendorLimitSignalKind.Warning)
            {
                warnings++;
            }
        }
        warnings.Should().BeGreaterThan(Cases / 5, "генератор обязан покрывать обе ветки");
    }

    [Fact(DisplayName = "Свойство: время сброса — ближайшее вхождение стенных часов в поясе из скобок, окно [now-5m, now+24h)")]
    public void Reset_time_is_next_wall_clock_occurrence_in_named_zone()
    {
        var withTime = 0;
        for (var seed = 0; seed < Cases; seed++)
        {
            var s = Generate(seed);
            var signal = VendorLimitMessageParser.TryParse(TerminalAgentCatalog.VendorClaude, s.Message, Now)!;
            var why = $"seed {seed}: {s.Message}";
            if (s.Hour is null)
            {
                signal.ResetAt.Should().BeNull(why);
                signal.TimeZoneId.Should().BeNull(why);
                continue;
            }
            withTime++;
            signal.TimeZoneId.Should().Be(s.Zone, why);
            var reset = signal.ResetAt!.Value;
            reset.Should().BeOnOrAfter(Now - TimeSpan.FromMinutes(5), why);
            reset.Should().BeOnOrBefore(Now + TimeSpan.FromHours(24), why);
            var local = reset.ToOffset(TimeSpan.FromHours(s.ZoneOffsetHours));
            local.Hour.Should().Be(s.Hour, why);
            local.Minute.Should().Be(s.Minute, why);
        }
        withTime.Should().BeGreaterThan(Cases / 2, "генератор обязан покрывать строки со временем");
    }

    [Fact(DisplayName = "Свойство: парсер идемпотентен по now — тот же момент даёт тот же результат, сдвиг на сутки сдвигает сброс ровно на сутки")]
    public void Shifting_now_by_a_day_shifts_reset_by_a_day()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var s = Generate(seed);
            if (s.Hour is null)
            {
                continue;
            }
            var a = VendorLimitMessageParser.TryParse(TerminalAgentCatalog.VendorClaude, s.Message, Now)!;
            var b = VendorLimitMessageParser.TryParse(TerminalAgentCatalog.VendorClaude, s.Message, Now.AddDays(1))!;
            (b.ResetAt!.Value - a.ResetAt!.Value).Should().Be(TimeSpan.FromDays(1), $"seed {seed}: {s.Message}");
        }
    }

    [Fact(DisplayName = "Свойство: таблица паттернов — единственный источник; для каждого вендора из таблицы есть hit-паттерн")]
    public void Every_vendor_in_table_has_a_hit_pattern()
    {
        VendorLimitMessagePatterns.All.Should().NotBeEmpty();
        foreach (var vendor in VendorLimitMessagePatterns.All.Select(p => p.Vendor).Distinct())
        {
            VendorLimitMessagePatterns.All.Should().Contain(
                p => p.Vendor == vendor && p.Kind == VendorLimitSignalKind.Hit, vendor);
        }
    }

    [Fact(DisplayName = "Часовой пояс из скобок реально применяется: одинаковые стенные часы в разных поясах — разные моменты")]
    public void Zone_changes_the_instant()
    {
        var bangkok = VendorLimitMessageParser.TryParse(TerminalAgentCatalog.VendorClaude,
            "You've hit your monthly spend limit · your session limit resets 6:00pm (Asia/Bangkok)", Now)!;
        var utc = VendorLimitMessageParser.TryParse(TerminalAgentCatalog.VendorClaude,
            "You've hit your monthly spend limit · your session limit resets 6:00pm (UTC)", Now)!;

        (utc.ResetAt!.Value - bangkok.ResetAt!.Value).Should().Be(TimeSpan.FromHours(7));
        _ = Bangkok;
    }
}
