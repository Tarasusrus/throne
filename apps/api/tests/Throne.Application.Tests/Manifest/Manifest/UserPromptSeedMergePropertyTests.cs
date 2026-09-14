using System.Globalization;
using FluentAssertions;
using Throne.Application.Manifest;
using Throne.Domain.PromptParts;

namespace Throne.Application.Tests.Manifest.Manifest;

/// <summary>
/// Properties of the seed→instance merge rule (ADR-0051 amendment): the repo seed is the
/// canon, a part untouched since its last seeding follows the seed, a hand-edited part is
/// kept and reported as drift, and the planner never touches keys outside the seed. Each
/// case is generated from a seed so a failure names its counterexample.
/// </summary>
public class UserPromptSeedMergePropertyTests
{
    private const int Cases = 400;

    [Fact(DisplayName = "Свойство: каждый ключ seed попадает ровно в одну ветку плана, чужие ключи — ни в одну")]
    public void Every_seed_key_lands_in_exactly_one_bucket()
    {
        for (var seedNo = 0; seedNo < Cases; seedNo++)
        {
            var rng = new Random(seedNo);
            var world = MergeGen.World(rng);
            var plan = UserPromptSeedMerge.Plan(world.Seed, world.Live, world.LastSeeded);

            var buckets = plan.Create.Select(p => p.Key)
                .Concat(plan.Update.Select(u => u.Seed.Key))
                .Concat(plan.Adopt)
                .Concat(plan.Drift)
                .ToList();

            buckets.Should().OnlyHaveUniqueItems(Why(seedNo, world));
            buckets.Should().BeSubsetOf(world.Seed.Parts.Select(p => p.Key), Why(seedNo, world));

            var seedKeys = world.Seed.Parts.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var liveKeys = world.Live.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            plan.Create.Select(p => p.Key).Should()
                .BeEquivalentTo(seedKeys.Except(liveKeys), Why(seedNo, world));
        }
    }

    [Fact(DisplayName = "Свойство: не правили руками → идёт за seed; правили → остаётся и виден как дрейф")]
    public void Untouched_follows_seed_and_edited_is_kept()
    {
        var updateSeen = false;
        var adoptSeen = false;
        var driftSeen = false;
        var silentSeen = false;

        for (var seedNo = 0; seedNo < Cases; seedNo++)
        {
            var rng = new Random(seedNo);
            var world = MergeGen.World(rng);
            var plan = UserPromptSeedMerge.Plan(world.Seed, world.Live, world.LastSeeded);

            foreach (var seedPart in world.Seed.Parts)
            {
                var live = world.Live.SingleOrDefault(p => p.Key == seedPart.Key);
                if (live is null)
                {
                    continue;
                }

                var untouched = world.LastSeeded.TryGetValue(seedPart.Key, out var last)
                    && UserPromptSeedMerge.SameText(live.Text, last);
                // Description is not merged (no port to update it) — only text and roles count.
                var equalsSeed = UserPromptSeedMerge.SameText(live.Text, seedPart.Text)
                    && MergeGen.SameRoles(live.ModeRoles, seedPart.ModeRoles);
                var sameTextAsSeed = UserPromptSeedMerge.SameText(live.Text, seedPart.Text);

                var inUpdate = plan.Update.Any(u => u.Seed.Key == seedPart.Key);
                var inAdopt = plan.Adopt.Contains(seedPart.Key);
                var inDrift = plan.Drift.Contains(seedPart.Key);

                if (untouched)
                {
                    inDrift.Should().BeFalse($"{seedPart.Key} не правили — это не дрейф. {Why(seedNo, world)}");
                    inUpdate.Should().Be(!equalsSeed, $"{seedPart.Key} не правили → обновление ровно когда есть что менять. {Why(seedNo, world)}");
                    inAdopt.Should().BeFalse($"{seedPart.Key} и так помечен последним посевом. {Why(seedNo, world)}");
                    updateSeen |= inUpdate;
                    silentSeen |= !inUpdate;
                }
                else if (sameTextAsSeed)
                {
                    // Converged by hand (or a legacy row without a mark): follows the seed,
                    // roles included; with nothing to change only the mark is refreshed.
                    inDrift.Should().BeFalse($"{seedPart.Key} совпал с seed — это не дрейф. {Why(seedNo, world)}");
                    inUpdate.Should().Be(!equalsSeed, $"{seedPart.Key}: текст как в seed → роли догоняют seed. {Why(seedNo, world)}");
                    inAdopt.Should().Be(equalsSeed, $"{seedPart.Key}: сошёлся с seed → только пометка. {Why(seedNo, world)}");
                    adoptSeen |= inAdopt;
                }
                else
                {
                    inUpdate.Should().BeFalse($"{seedPart.Key} правили руками — затирать нельзя. {Why(seedNo, world)}");
                    inAdopt.Should().BeFalse($"{seedPart.Key} правили руками — пометка не сдвигается. {Why(seedNo, world)}");
                    inDrift.Should().BeTrue($"{seedPart.Key}: правлен и ≠ seed → дрейф. {Why(seedNo, world)}");
                    driftSeen = true;
                }
            }
        }

        updateSeen.Should().BeTrue("генератор обязан дать случай «не правили, seed изменился»");
        adoptSeen.Should().BeTrue("генератор обязан дать случай «сошлись вручную»");
        driftSeen.Should().BeTrue("генератор обязан дать случай дрейфа");
        silentSeen.Should().BeTrue("генератор обязан дать случай «всё уже совпадает»");
    }

    [Fact(DisplayName = "Свойство: применение плана идемпотентно — повторный план ничего не пишет, дрейф тот же")]
    public void Applying_the_plan_is_idempotent()
    {
        for (var seedNo = 0; seedNo < Cases; seedNo++)
        {
            var rng = new Random(seedNo);
            var world = MergeGen.World(rng);
            var plan = UserPromptSeedMerge.Plan(world.Seed, world.Live, world.LastSeeded);

            var (live, lastSeeded) = MergeGen.Apply(world, plan);
            var again = UserPromptSeedMerge.Plan(world.Seed, live, lastSeeded);

            again.Create.Should().BeEmpty(Why(seedNo, world));
            again.Update.Should().BeEmpty(Why(seedNo, world));
            again.Adopt.Should().BeEmpty(Why(seedNo, world));
            again.Drift.Should().BeEquivalentTo(plan.Drift, Why(seedNo, world));

            // Everything not drifted now equals the seed byte-for-byte modulo trailing whitespace.
            foreach (var seedPart in world.Seed.Parts.Where(p => !plan.Drift.Contains(p.Key)))
            {
                var part = live.Single(p => p.Key == seedPart.Key);
                UserPromptSeedMerge.SameText(part.Text, seedPart.Text).Should().BeTrue(Why(seedNo, world));
            }
        }
    }

    [Fact(DisplayName = "Свойство: сравнение текста терпимо к хвостовым пробелам и CRLF, но не к содержанию")]
    public void Text_equality_ignores_trailing_whitespace_and_line_endings_only()
    {
        for (var seedNo = 0; seedNo < Cases; seedNo++)
        {
            var rng = new Random(seedNo);
            var text = MergeGen.Text(rng);
            var trailing = text + new string(rng.Next(2) == 0 ? '\n' : ' ', rng.Next(0, 4));
            var crlf = text.Replace("\n", "\r\n", StringComparison.Ordinal);

            UserPromptSeedMerge.SameText(text, trailing).Should().BeTrue($"seed={seedNo} text={Quote(text)}");
            UserPromptSeedMerge.SameText(text, crlf).Should().BeTrue($"seed={seedNo} text={Quote(text)}");
            UserPromptSeedMerge.SameText(text, text + "x").Should().BeFalse($"seed={seedNo} text={Quote(text)}");
            UserPromptSeedMerge.SameText(text, "x" + text).Should().BeFalse($"seed={seedNo} text={Quote(text)}");
        }
    }

    private static string Why(int seedNo, MergeGen.MergeWorld world) =>
        $"seed={seedNo.ToString(CultureInfo.InvariantCulture)}; {world}";

    private static string Quote(string s) => "\"" + s.Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}

/// <summary>
/// Generates a seed, a live user-part set and a «last seeded» map that together cover every
/// branch of the merge rule: missing key, untouched-and-stale, untouched-and-current,
/// hand-edited-and-diverged, hand-edited-but-converged, legacy row without a mark, and live
/// keys the seed does not know.
/// </summary>
internal static class MergeGen
{
    private static readonly string[] KeyPool =
        ["common", "interview", "work", "review", "dream", "commit", "tests", "contracts", "analysis", "orchestrator"];

    private static readonly string[] Words =
        ["веди", "диалог", "на языке", "оператора", "commit", "trailers", "Problem:", "Decision:", "—", "`code`", "# not a comment", "- item", "  indented"];

    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    public sealed record MergeWorld(
        UserPromptSeed Seed,
        IReadOnlyList<PromptPart> Live,
        IReadOnlyDictionary<string, string> LastSeeded)
    {
        public override string ToString()
        {
            var seed = string.Join(", ", Seed.Parts.Select(p => p.Key));
            var live = string.Join(", ", Live.Select(p => $"{p.Key}:{Short(p.Text)}"));
            var last = string.Join(", ", LastSeeded.Select(kv => $"{kv.Key}:{Short(kv.Value)}"));
            return $"seed=[{seed}] live=[{live}] last=[{last}]";
        }

        private static string Short(string s) =>
            (s.Length > 18 ? s[..18] + "…" : s).Replace("\n", "⏎", StringComparison.Ordinal);
    }

    public static MergeWorld World(Random rng)
    {
        var keys = KeyPool.OrderBy(_ => rng.Next()).Take(rng.Next(1, 7)).ToArray();
        var seedParts = new List<UserPromptSeedPart>();
        var live = new List<PromptPart>();
        var last = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            var seedText = Text(rng);
            var seedRoles = Roles(rng);
            var seedDesc = rng.Next(3) == 0 ? null : "desc-" + key;
            seedParts.Add(new UserPromptSeedPart(key, seedText, seedDesc, seedRoles));

            switch (rng.Next(7))
            {
                case 0:
                    // Missing on the instance → must be created.
                    break;
                case 1:
                    // Untouched since an older seed → must follow the new seed.
                    {
                        var old = Distinct(rng, seedText);
                        last[key] = old;
                        live.Add(Part(key, Decorate(rng, old), rng.Next(2) == 0 ? seedDesc : "old", rng.Next(2) == 0 ? seedRoles : Roles(rng)));
                    }
                    break;
                case 2:
                    // Untouched and already current (maybe only roles/description differ).
                    last[key] = seedText;
                    live.Add(Part(key, Decorate(rng, seedText), rng.Next(2) == 0 ? seedDesc : "old", rng.Next(2) == 0 ? seedRoles : Roles(rng)));
                    break;
                case 3:
                    // Hand-edited and diverged → keep, report drift.
                    last[key] = rng.Next(2) == 0 ? seedText : Distinct(rng, seedText);
                    live.Add(Part(key, Distinct(rng, seedText, last[key]), seedDesc, seedRoles));
                    break;
                case 4:
                    // Hand-edited but converged to the seed by hand → adopt (mark only).
                    last[key] = Distinct(rng, seedText);
                    live.Add(Part(key, Decorate(rng, seedText), seedDesc, Roles(rng)));
                    break;
                case 5:
                    // Legacy row without any mark, diverged.
                    live.Add(Part(key, Distinct(rng, seedText), seedDesc, seedRoles));
                    break;
                default:
                    // Legacy row without any mark, equal to the seed.
                    live.Add(Part(key, Decorate(rng, seedText), seedDesc, seedRoles));
                    break;
            }
        }

        // Live keys the seed does not know — the planner must never touch them.
        foreach (var stray in KeyPool.Except(keys).Take(rng.Next(0, 3)))
        {
            live.Add(Part(stray, Text(rng), null, Roles(rng)));
            if (rng.Next(2) == 0)
            {
                last[stray] = Text(rng);
            }
        }

        return new MergeWorld(new UserPromptSeed(1, seedParts), live, last);
    }

    /// <summary>Simulates executing the plan on the in-memory world (create/update/adopt).</summary>
    public static (IReadOnlyList<PromptPart> Live, IReadOnlyDictionary<string, string> LastSeeded) Apply(
        MergeWorld world, UserPromptSeedMergePlan plan)
    {
        var live = world.Live.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var last = new Dictionary<string, string>(world.LastSeeded, StringComparer.Ordinal);

        foreach (var created in plan.Create)
        {
            live[created.Key] = Part(created.Key, created.Text, created.Description, created.ModeRoles);
            last[created.Key] = created.Text;
        }
        foreach (var update in plan.Update)
        {
            live[update.Seed.Key] = Part(update.Seed.Key, update.Seed.Text, update.Seed.Description, update.Seed.ModeRoles);
            last[update.Seed.Key] = update.Seed.Text;
        }
        foreach (var key in plan.Adopt)
        {
            last[key] = world.Seed.Parts.Single(p => p.Key == key).Text;
        }

        return (live.Values.ToList(), last);
    }

    public static string Text(Random rng)
    {
        var lines = Enumerable.Range(0, rng.Next(1, 5))
            .Select(_ => rng.Next(6) == 0 ? "" : string.Join(" ", Enumerable.Range(0, rng.Next(1, 4)).Select(_ => Words[rng.Next(Words.Length)])));
        return string.Join("\n", lines).Trim('\n') + "\n";
    }

    public static bool SameRoles(IReadOnlyList<PromptPartModeRole> a, IReadOnlyList<PromptPartModeRole> b) =>
        a.OrderBy(r => r.Mode, StringComparer.Ordinal).SequenceEqual(b.OrderBy(r => r.Mode, StringComparer.Ordinal));

    private static IReadOnlyList<PromptPartModeRole> Roles(Random rng) =>
        PromptPartModeNames.All.OrderBy(_ => rng.Next()).Take(rng.Next(1, 4))
            .Select(m => new PromptPartModeRole(m, PromptPartRoleNames.All[rng.Next(PromptPartRoleNames.All.Count)], rng.Next(0, 20)))
            .ToArray();

    private static PromptPart Part(string key, string text, string? description, IReadOnlyList<PromptPartModeRole> roles) =>
        PromptPart.Create(PromptPartId.New(), PromptPartScopeNames.User, key, text, description, roles, Now);

    /// <summary>A text that differs from every given text in content (not just whitespace).</summary>
    private static string Distinct(Random rng, params string[] others)
    {
        while (true)
        {
            var candidate = Text(rng);
            if (others.All(o => !UserPromptSeedMerge.SameText(candidate, o)))
            {
                return candidate;
            }
        }
    }

    /// <summary>Same content, possibly different trailing whitespace / line endings — must compare equal.</summary>
    private static string Decorate(Random rng, string text) =>
        rng.Next(3) switch
        {
            0 => text.TrimEnd(),
            1 => text.Replace("\n", "\r\n", StringComparison.Ordinal),
            _ => text + "\n",
        };
}
