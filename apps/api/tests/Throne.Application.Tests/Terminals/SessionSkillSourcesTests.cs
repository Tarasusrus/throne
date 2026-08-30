using System.Diagnostics;
using FluentAssertions;
using Throne.Application.Terminals;

namespace Throne.Application.Tests.Terminals;

/// <summary>
/// Скилл живёт в двух местах сразу: дескриптор в DI и файлы в <c>skills/&lt;id&gt;/</c>
/// (ADR-0043/0045). Расхождение молчаливое — материализатор при отсутствии исходников
/// просто не копирует ничего, а агент получает инструкцию звать CLI, которого нет.
/// Отдельный класс поимки: файлы обязаны существовать И быть под гитом (иначе они есть
/// только на машине автора — ровно так `bin/` из .gitignore проглотил orchestrator).
/// </summary>
public class SessionSkillSourcesTests
{
    private static readonly string[] SkillIds =
    [
        SessionSkillPackageIds.Intent,
        SessionSkillPackageIds.Review,
        SessionSkillPackageIds.Dream,
        SessionSkillPackageIds.Orchestrator,
    ];

    [Fact(DisplayName = "У каждого зарегистрированного скилла есть SKILL.md и CLI в репозитории")]
    public void Every_registered_skill_has_sources()
    {
        var root = RepoRoot();

        foreach (var id in SkillIds)
        {
            var dir = Path.Combine(root, "skills", id);
            File.Exists(Path.Combine(dir, "SKILL.md")).Should().BeTrue($"skills/{id}/SKILL.md must exist");
            Directory.EnumerateFiles(Path.Combine(dir, "bin"), "throne-*")
                .Should().NotBeEmpty($"skills/{id}/bin must ship a throne-* CLI");
        }
    }

    [Fact(DisplayName = "Исходники скиллов отслеживаются гитом, а не лежат только локально")]
    public void Every_skill_source_is_tracked_by_git()
    {
        var root = RepoRoot();
        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            return; // Опубликованная копия без гита — проверять нечего.
        }

        var tracked = Git(root, "ls-files skills")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var id in SkillIds)
        {
            tracked.Should().Contain($"skills/{id}/SKILL.md");
            tracked.Where(path => path.StartsWith($"skills/{id}/bin/", StringComparison.Ordinal))
                .Should().NotBeEmpty($"skills/{id}/bin must be committed, not ignored");
        }
    }

    private static string Git(string root, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static string RepoRoot()
    {
        // Тот же якорь, что и у манифест-тестов: specs/AGENTS.local.md есть только в корне.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "specs", "AGENTS.local.md")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Repo root not found from " + AppContext.BaseDirectory);
    }
}
