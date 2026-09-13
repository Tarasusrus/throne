using System.Reflection;
using FluentAssertions;
using Throne.Api.Cli;

namespace Throne.Api.Tests.Cli;

/// <summary>
/// Contract for <see cref="ThroneVersion"/>: the source of truth keeps the full
/// informational version verbatim (commit suffix included — that is what makes a
/// stale deploy visible), and only <see cref="ThroneVersion.WithoutBuildMetadata"/>,
/// the function <see cref="UpdateCommand"/> uses to compare against a GitHub
/// release tag, strips it. Swept over generated shapes rather than one hand-picked
/// example, in the same style as <c>SkillManifestPropertyTests</c>.
/// </summary>
public class ThroneVersionTests
{
    private const int Cases = 300;

    [Fact(DisplayName = "Свойство: строка без '+' переживает WithoutBuildMetadata байт в байт")]
    public void No_plus_round_trips_unchanged()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            var semver = RandomSemVerCore(rng);

            ThroneVersion.WithoutBuildMetadata(semver).Should().Be(semver, Counterexample(seed, semver));
        }
    }

    [Fact(DisplayName = "Свойство: для 'core+metadata' WithoutBuildMetadata всегда возвращает ровно core")]
    public void Plus_suffix_is_always_dropped()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            var core = RandomSemVerCore(rng);
            var metadata = RandomBuildMetadata(rng);
            var informational = $"{core}+{metadata}";

            ThroneVersion.WithoutBuildMetadata(informational)
                .Should().Be(core, Counterexample(seed, informational));
        }
    }

    [Fact(DisplayName = "Свойство: только первый '+' режет границу — метаданные с внутренним '+' не проглатываются частично")]
    public void Only_first_plus_is_the_boundary()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            var core = RandomSemVerCore(rng);
            var metadata = $"{RandomBuildMetadata(rng)}+{RandomBuildMetadata(rng)}";
            var informational = $"{core}+{metadata}";

            var result = ThroneVersion.WithoutBuildMetadata(informational);

            result.Should().Be(core, Counterexample(seed, informational));
            result.Should().NotContain("+", Counterexample(seed, informational));
        }
    }

    [Fact(DisplayName = "Контракт DoD: Current несёт информационную версию сборки байт в байт, включая суффикс сборки")]
    public void Current_matches_assembly_informational_version_byte_for_byte()
    {
        // Reads the real attribute off the running assembly instead of a literal —
        // a Resolve() that strips "+<sha>" at the source (the bug this DoD forbids)
        // makes Current diverge from this and the test goes red. Verified against a
        // build stamped the way install-local.sh does it: `dotnet build
        // apps/api/src/Throne.Api -p:Version=0.0.0-test+abc`.
        var informational = typeof(ThroneVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        informational.Should().NotBeNullOrWhiteSpace();
        ThroneVersion.Current.Should().Be(informational);
    }

    private static string RandomSemVerCore(Random rng) =>
        $"{rng.Next(0, 50)}.{rng.Next(0, 50)}.{rng.Next(0, 50)}";

    private static string RandomBuildMetadata(Random rng)
    {
        const string alphabet = "0123456789abcdef";
        var length = rng.Next(4, 13);
        return new string(Enumerable.Range(0, length).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
    }

    private static string Counterexample(int seed, string input) =>
        $"seed={seed}, input=\"{input}\"";
}
