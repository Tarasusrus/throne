using FluentAssertions;
using Throne.Application.Manifest;

namespace Throne.Application.Tests.Manifest.Manifest;

/// <summary>
/// Стандарт коммита, утверждённый оператором 14.09 (вариант A): заголовок Conventional
/// Commits без изменений, тело прозой, последние три непустые строки — трейлеры
/// Problem/Decision/Task в этом порядке. Правило живёт в промпте (эта часть), в
/// commit-msg хуке (<c>scripts/git-hooks/commit-msg</c>) и в гейте
/// (<c>commit-message-trailers</c>) — все три места закреплены здесь как контракт.
///
/// Мутация: убрать блок трейлеров из <c>specs/manifest/throne-user-prompt-seed-parts.yaml</c>
/// (ключ <c>commit</c>) — этот тест краснеет.
/// </summary>
public class CommitTrailerPromptContractTests
{
    private static string CommitText() =>
        SkillManifestFixtures.RepoUserPromptSeed().Parts.Single(p => p.Key == "commit").Text;

    [Fact(DisplayName = "commit — часть seed-манифеста, default_on в режиме work")]
    public void Commit_part_exists_and_is_default_on_for_work()
    {
        var part = SkillManifestFixtures.RepoUserPromptSeed().Parts.Single(p => p.Key == "commit");

        part.ModeRoles.Should().ContainSingle(r => r.Mode == "work" && r.Role == "default_on");
    }

    [Fact(DisplayName = "Заголовок — Conventional Commits, без изменений")]
    public void Header_stays_conventional_commits()
    {
        CommitText().Should().ContainEquivalentOf("Conventional Commits");
    }

    [Fact(DisplayName = "Тело — что сделано и почему, пересказ диффа запрещён")]
    public void Body_asks_what_and_why_and_forbids_diff_retelling()
    {
        var text = CommitText();

        text.Should().ContainEquivalentOf("почему");
        text.Should().Contain("Пересказ диффа запрещён");
    }

    [Fact(DisplayName = "Три трейлера в этом порядке: Problem, Decision, Task")]
    public void Trailer_block_lists_problem_decision_task_in_order()
    {
        var text = CommitText();

        var problemIdx = text.IndexOf("Problem:", StringComparison.Ordinal);
        var decisionIdx = text.IndexOf("Decision:", StringComparison.Ordinal);
        var taskIdx = text.IndexOf("Task:", StringComparison.Ordinal);

        problemIdx.Should().BeGreaterThan(-1);
        decisionIdx.Should().BeGreaterThan(problemIdx, "Decision должен идти после Problem");
        taskIdx.Should().BeGreaterThan(decisionIdx, "Task должен идти после Decision");
    }

    [Fact(DisplayName = "Decision — выбор и его причина, а не пересказ диффа (ADR в одну строку)")]
    public void Decision_trailer_is_a_one_line_adr()
    {
        var line = CommitText().Split('\n').Single(l => l.TrimStart().StartsWith("Decision:", StringComparison.Ordinal) && l.Contains("почему так"));

        line.Should().ContainEquivalentOf("почему так");
        line.Should().ContainEquivalentOf("альтернатива");
    }

    [Fact(DisplayName = "Task — короткое имя задачи словами, не хэш")]
    public void Task_trailer_is_a_name_not_a_hash()
    {
        var line = CommitText().Split('\n').Single(l => l.TrimStart().StartsWith("Task:", StringComparison.Ordinal) && l.Contains("короткое"));

        line.Should().ContainEquivalentOf("не хэш");
    }

    [Fact(DisplayName = "Лимит длины строки трейлера — 160 символов, все три обязательны")]
    public void Line_limit_is_160_and_all_three_are_required()
    {
        var text = CommitText();

        text.Should().Contain("160");
        text.Should().ContainEquivalentOf("все три обязательны");
    }

    [Fact(DisplayName = "Merge-коммиты и Revert вне проверки")]
    public void Merge_and_revert_are_exempt()
    {
        var text = CommitText();

        text.Should().ContainEquivalentOf("Merge");
        text.Should().Contain("Revert \"");
    }

    [Fact(DisplayName = "Промпт указывает, как поставить хук в клоне, где его ещё нет")]
    public void Prompt_points_to_the_hook_install_step()
    {
        CommitText().Should().Contain("scripts/git-hooks/install.sh");
    }

    [Fact(DisplayName = "AI-атрибуция запрещена явно")]
    public void Ai_attribution_is_forbidden()
    {
        CommitText().Should().ContainEquivalentOf("AI-атрибуция");
        CommitText().Should().ContainEquivalentOf("запрещена");
    }

    [Fact(DisplayName = "Пример сообщения показывает полный формат: заголовок + тело + три трейлера")]
    public void Example_shows_full_format()
    {
        var text = CommitText();
        var exampleIdx = text.IndexOf("Пример:", StringComparison.Ordinal);
        exampleIdx.Should().BeGreaterThan(-1);

        var example = text[exampleIdx..];
        example.Should().MatchRegex(@"^\s*Пример:[\s\S]*Problem: [\s\S]*Decision: [\s\S]*Task: ");
    }
}
