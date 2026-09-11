using System.Text.RegularExpressions;
using FluentAssertions;
using Throne.Application.Tests.Manifest;

namespace Throne.Application.Tests.Terminals;

/// <summary>
/// Поверхность скилла orchestrator описана в трёх местах — диспетчер CLI, usage, SKILL.md — и
/// упоминается в системной инструкции. Расхождение молчаливое: агент зовёт команду из документа,
/// а CLI отвечает «unknown command». Здесь три множества обязаны совпасть.
/// </summary>
public class OrchestratorSkillSurfaceTests
{
    private static readonly Regex Dispatched = new(@"^\s+([a-z]+)\)\s+cmd_[a-z]+ ""\$@"" ;;", RegexOptions.Multiline);
    private static readonly Regex Usage = new(@"^\s+throne-orchestrator ([a-z]+)", RegexOptions.Multiline);
    private static readonly Regex Documented = new(@"skills/orchestrator/bin/throne-orchestrator ([a-z]+)");

    // <root>/specs/manifest/<file> → три уровня вверх.
    private static string Root => Path.GetFullPath(Path.Combine(SkillManifestFixtures.RepoManifestPath(), "..", "..", ".."));
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static HashSet<string> Names(Regex regex, string text) =>
        regex.Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    [Fact(DisplayName = "Команды диспетчера CLI, usage и SKILL.md — одно и то же множество")]
    public void Cli_usage_and_skill_agree_on_commands()
    {
        var cli = Read("skills", "orchestrator", "bin", "throne-orchestrator");
        var skill = Read("skills", "orchestrator", "SKILL.md");

        var dispatched = Names(Dispatched, cli);
        var usage = Names(Usage, cli);
        var documented = Names(Documented, skill);

        dispatched.Should().NotBeEmpty();
        usage.Should().BeEquivalentTo(dispatched, "usage перечисляет ровно то, что диспетчер принимает");
        documented.Should().BeEquivalentTo(dispatched, "SKILL.md показывает ровно то, что CLI умеет");
    }

    [Fact(DisplayName = "Приёмка есть в поверхности скилла")]
    public void Accept_is_part_of_the_surface()
    {
        var cli = Read("skills", "orchestrator", "bin", "throne-orchestrator");

        Names(Dispatched, cli).Should().Contain("accept");
    }
}
