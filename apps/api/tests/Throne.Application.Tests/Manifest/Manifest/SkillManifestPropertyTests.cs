using System.Globalization;
using FluentAssertions;
using Throne.Application.Manifest;

namespace Throne.Application.Tests.Manifest.Manifest;

/// <summary>
/// Properties of the manifest contract, swept over generated shapes rather than one hand-picked
/// example. Each case is driven by a seed, so a failure names the counterexample that produced it.
/// </summary>
public class SkillManifestPropertyTests
{
    private const int Cases = 300;

    [Fact(DisplayName = "Свойство: well-formed манифест переживает YAML round-trip без потери байтов промпта")]
    public void Well_formed_manifest_round_trips()
    {
        var emptyLineSeen = false;
        var nonAsciiSeen = false;

        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            var model = ManifestGen.WellFormed(rng);
            var yaml = ManifestGen.Emit(model);

            SkillManifest parsed;
            try
            {
                parsed = SkillManifestParser.Parse(yaml);
            }
            catch (SkillManifestException ex)
            {
                throw new InvalidOperationException(Counterexample(seed, yaml), ex);
            }

            parsed.Version.Should().Be(1, Counterexample(seed, yaml));
            parsed.SystemInstructions.Select(i => (i.Kind, i.Text))
                .Should().Equal(model.SystemInstructions, Counterexample(seed, yaml));
            parsed.Bundles.Select(b => b.Mode)
                .Should().Equal(model.Bundles.Select(b => b.Mode), Counterexample(seed, yaml));
            parsed.Bundles.SelectMany(b => b.Includes).Select(i => (i.Scope, i.Kind))
                .Should().Equal(model.Bundles.SelectMany(b => b.Includes), Counterexample(seed, yaml));

            emptyLineSeen |= model.SystemInstructions.Any(i => i.Text.Contains("\n\n", StringComparison.Ordinal));
            nonAsciiSeen |= model.SystemInstructions.Any(i => i.Text.Any(c => c > 127));
        }

        emptyLineSeen.Should().BeTrue("генератор обязан покрывать пустые строки внутри блочного скаляра");
        nonAsciiSeen.Should().BeTrue("промпты пишутся по-русски — non-ASCII должен попадать в выборку");
    }

    [Fact(DisplayName = "Свойство: ни один одиночный дефект манифеста не проходит валидацию")]
    public void No_single_defect_passes_validation()
    {
        var covered = new HashSet<ManifestDefect>();

        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            var model = ManifestGen.WellFormed(rng);
            var defect = ManifestMutations.All[rng.Next(ManifestMutations.All.Length)];
            var broken = ManifestMutations.Apply(model, defect, rng);
            var yaml = ManifestGen.Emit(broken);

            var act = () => SkillManifestParser.Parse(yaml);
            act.Should().Throw<SkillManifestException>($"{defect} — {Counterexample(seed, yaml)}");
            covered.Add(defect);
        }

        covered.Should().BeEquivalentTo(ManifestMutations.All, "выборка обязана задеть каждый класс дефекта");
    }

    [Fact(DisplayName = "Свойство: в реальном манифесте каждый bundle разрешается в непустой system-текст")]
    public void Every_bundle_resolves_to_non_empty_system_text()
    {
        var manifest = SkillManifestFixtures.RepoManifest();
        var texts = manifest.SystemInstructions.ToDictionary(i => i.Kind, i => i.Text, StringComparer.Ordinal);

        manifest.Bundles.Should().NotBeEmpty();
        foreach (var bundle in manifest.Bundles)
        {
            var systemKinds = bundle.Includes.Where(i => i.Scope == "system").Select(i => i.Kind).ToArray();
            systemKinds.Should().NotBeEmpty($"режим '{bundle.Mode}' без system-части поднимет сессию без правил");
            foreach (var kind in systemKinds)
            {
                texts.Should().ContainKey(kind);
                texts[kind].Trim().Should().NotBeEmpty();
            }
            bundle.Includes.Should().Contain(i => i.Scope == "user" && i.Kind == "common");
        }
    }

    [Fact(DisplayName = "Свойство: каждая system-инструкция держит форму «Цель … Правила: - …»")]
    public void Every_system_instruction_keeps_its_shape()
    {
        foreach (var instruction in SkillManifestFixtures.RepoManifest().SystemInstructions)
        {
            var lines = instruction.Text.Split('\n');
            var because = $"kind '{instruction.Kind}'";

            lines[0].Should().StartWith($"Цель {instruction.Kind} —", because);
            lines.Should().Contain("Правила:", because);

            var rules = lines.SkipWhile(l => l != "Правила:").Skip(1).Where(l => l.Length > 0).ToArray();
            rules.Should().NotBeEmpty(because);
            rules.Should().OnlyContain(l => l.StartsWith("- ", StringComparison.Ordinal), because);
            lines.Should().OnlyContain(l => l.TrimEnd() == l, $"{because}: хвостовые пробелы едут в промпт");
        }
    }

    private static string Counterexample(int seed, string yaml) =>
        string.Create(CultureInfo.InvariantCulture, $"seed {seed}, manifest:\n{yaml}");
}
