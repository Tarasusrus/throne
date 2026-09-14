using FluentAssertions;
using Throne.Infrastructure.Git;

namespace Throne.Infrastructure.Tests.Git;

/// <summary>
/// Свойства <see cref="GitStatusPorcelain.HasUnstagedDeletions"/> — детектора прерванного
/// checkout (индекс уже на целевом коммите, но файл не долетел до рабочего дерева).
/// Сценарии — случайные наборы porcelain-строк, сравниваемые с независимо посчитанным
/// ожиданием, чтобы не задублировать саму реализацию под видом теста.
/// </summary>
public class GitStatusPorcelainTests
{
    private const int Cases = 60;

    private static readonly char[] IndexCodes = [' ', 'M', 'A', 'D', 'R'];
    private static readonly char[] WorktreeCodes = [' ', 'M', 'D'];

    private sealed record Line(char Index, char Worktree, string Path)
    {
        public override string ToString() => $"{Index}{Worktree} {Path}";
    }

    private static IReadOnlyList<Line> Generate(int seed, out bool expected)
    {
        var rng = new Random(seed);
        var count = rng.Next(0, 6);
        var lines = new List<Line>();
        var anyUnstagedDelete = false;
        for (var i = 0; i < count; i++)
        {
            char index;
            char worktree;
            if (rng.Next(3) == 0)
            {
                // Untracked file — porcelain reports it as "??", never as an XY status pair.
                index = '?';
                worktree = '?';
            }
            else
            {
                index = IndexCodes[rng.Next(IndexCodes.Length)];
                worktree = WorktreeCodes[rng.Next(WorktreeCodes.Length)];
            }

            lines.Add(new Line(index, worktree, $"file{i}.txt"));
            if (index == ' ' && worktree == 'D')
            {
                anyUnstagedDelete = true;
            }
        }

        expected = anyUnstagedDelete;
        return lines;
    }

    [Fact(DisplayName = "Свойство: детектор true тогда и только тогда, когда есть строка ' D' (unstaged delete)")]
    public void Detects_unstaged_deletion_lines_only()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var lines = Generate(seed, out var expected);
            var porcelain = string.Join('\n', lines.Select(l => l.ToString()));

            var actual = GitStatusPorcelain.HasUnstagedDeletions(porcelain);

            actual.Should().Be(expected, $"seed {seed}: [{porcelain.Replace("\n", " | ")}]");
        }
    }

    [Fact(DisplayName = "Свойство: staged-удаление ('D ') само по себе не триггерит починку")]
    public void Staged_deletion_alone_does_not_trigger_repair()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            var count = rng.Next(1, 5);
            var porcelain = string.Join('\n', Enumerable.Range(0, count).Select(i => $"D  file{i}.txt"));

            GitStatusPorcelain.HasUnstagedDeletions(porcelain).Should().BeFalse($"seed {seed}: [{porcelain}]");
        }
    }

    [Theory(DisplayName = "Границы: пустой/null вывод не считается прерванным checkout'ом")]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_output_is_not_incomplete(string? porcelain)
    {
        GitStatusPorcelain.HasUnstagedDeletions(porcelain).Should().BeFalse();
    }
}
