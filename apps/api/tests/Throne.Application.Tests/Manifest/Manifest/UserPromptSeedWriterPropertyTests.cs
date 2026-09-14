using System.Globalization;
using FluentAssertions;
using Throne.Application.Manifest;
using Throne.Domain.PromptParts;

namespace Throne.Application.Tests.Manifest.Manifest;

/// <summary>
/// The snapshot writer is the reverse path of the seed (instance → repo). Its only
/// contract: whatever it writes, <see cref="UserPromptSeedParser"/> reads back
/// byte-for-byte — otherwise a snapshot commit would itself become drift.
/// </summary>
public class UserPromptSeedWriterPropertyTests
{
    private const int Cases = 300;

    private static readonly string[] KeyPool =
        ["common", "interview", "work", "review", "dream", "commit", "tests", "orchestrator"];

    private static readonly string[] Fragments =
    [
        "Веди диалог на языке оператора", "- пункт списка", "  - вложенный", "# не комментарий",
        "ключ: значение", "`code` и \"кавычки\"", "'одинарные'", "| pipe", "> quote", "trailing space ",
        "⚬\tтаб", "{ mode: work }", "[x]", "~", "null", "yes", "1.5", "---", "...", "%percent", "@at", "&amp", "*star",
    ];

    [Fact(DisplayName = "Свойство: Parse(Write(seed)) == seed — тексты, описания, роли и порядок")]
    public void Write_then_parse_round_trips()
    {
        var blankLineSeen = false;
        var trailingSpaceSeen = false;
        var nullDescriptionSeen = false;

        for (var seedNo = 0; seedNo < Cases; seedNo++)
        {
            var rng = new Random(seedNo);
            var seed = Seed(rng);

            var yaml = UserPromptSeedWriter.Write(seed);
            UserPromptSeed parsed;
            try
            {
                parsed = UserPromptSeedParser.Parse(yaml);
            }
            catch (SkillManifestException ex)
            {
                throw new InvalidOperationException(Why(seedNo, yaml), ex);
            }

            parsed.Version.Should().Be(1, Why(seedNo, yaml));
            parsed.Parts.Select(p => p.Key).Should().Equal(seed.Parts.Select(p => p.Key), Why(seedNo, yaml));
            foreach (var (expected, actual) in seed.Parts.Zip(parsed.Parts))
            {
                actual.Text.Should().Be(expected.Text, Why(seedNo, yaml));
                actual.Description.Should().Be(expected.Description, Why(seedNo, yaml));
                actual.ModeRoles.Should().Equal(expected.ModeRoles, Why(seedNo, yaml));
            }

            blankLineSeen |= seed.Parts.Any(p => p.Text.Contains("\n\n", StringComparison.Ordinal));
            trailingSpaceSeen |= seed.Parts.Any(p => p.Text.Contains(" \n", StringComparison.Ordinal));
            nullDescriptionSeen |= seed.Parts.Any(p => p.Description is null);
        }

        blankLineSeen.Should().BeTrue("генератор обязан покрывать пустые строки внутри текста");
        trailingSpaceSeen.Should().BeTrue("генератор обязан покрывать хвостовые пробелы в строке");
        nullDescriptionSeen.Should().BeTrue("генератор обязан покрывать части без description");
    }

    [Fact(DisplayName = "Свойство: writer стабилен — Write(Parse(Write(x))) == Write(x)")]
    public void Writer_is_stable_under_round_trip()
    {
        for (var seedNo = 0; seedNo < Cases; seedNo++)
        {
            var rng = new Random(seedNo);
            var seed = Seed(rng);

            var once = UserPromptSeedWriter.Write(seed);
            var twice = UserPromptSeedWriter.Write(UserPromptSeedParser.Parse(once));

            twice.Should().Be(once, Why(seedNo, once));
        }
    }

    private static UserPromptSeed Seed(Random rng)
    {
        var keys = KeyPool.OrderBy(_ => rng.Next()).Take(rng.Next(1, 6)).ToArray();
        var parts = keys.Select(key => new UserPromptSeedPart(
            key,
            Text(rng),
            rng.Next(3) == 0 ? null : Fragments[rng.Next(Fragments.Length)],
            PromptPartModeNames.All.OrderBy(_ => rng.Next()).Take(rng.Next(1, 4))
                .Select(m => new PromptPartModeRole(m, PromptPartRoleNames.All[rng.Next(PromptPartRoleNames.All.Count)], rng.Next(0, 20)))
                .ToArray()))
            .ToArray();
        return new UserPromptSeed(1, parts);
    }

    private static string Text(Random rng)
    {
        var lines = Enumerable.Range(0, rng.Next(1, 6))
            .Select(_ => rng.Next(5) == 0
                ? ""
                : string.Join(" ", Enumerable.Range(0, rng.Next(1, 4)).Select(_ => Fragments[rng.Next(Fragments.Length)])))
            .ToList();
        // Parser rejects whitespace-only text; keep at least one non-blank line.
        lines[0] = lines[0].Length == 0 ? Fragments[0] : lines[0];
        var body = string.Join("\n", lines);
        return rng.Next(4) == 0 ? body : body + "\n";
    }

    private static string Why(int seedNo, string yaml) =>
        $"seed={seedNo.ToString(CultureInfo.InvariantCulture)}\n{yaml}";
}
