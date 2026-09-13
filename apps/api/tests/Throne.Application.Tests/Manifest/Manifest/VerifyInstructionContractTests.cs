using FluentAssertions;
using Throne.Application.Manifest;
using Throne.Application.Terminals;
using Throne.Domain.PromptParts;

namespace Throne.Application.Tests.Manifest.Manifest;

/// <summary>
/// Контракт независимого ревью (ADR-0054 §8): ревьюер — отдельная сессия в режиме <c>verify</c>,
/// которая знает только DoD, проблему и ветку. У режима нет кода — промпт и есть поведение,
/// поэтому правила изоляции закреплены здесь, а не оставлены на глаз ревьюера.
/// </summary>
public class VerifyInstructionContractTests
{
    private static SkillManifest Manifest() => SkillManifestFixtures.RepoManifest();

    private static string KindText(string kind) =>
        Manifest().SystemInstructions.Single(i => i.Kind == kind).Text;

    private static string VerifyText() => KindText("verify");

    private static string RuleLineWithMarker(string text, string marker) =>
        text.Split('\n').Single(line => line.Contains(marker, StringComparison.Ordinal));

    [Fact(DisplayName = "verify — известный режим на оси запуска и на оси prompt-частей")]
    public void Verify_is_a_known_mode_on_both_axes()
    {
        TerminalRunModes.IsKnown("verify").Should().BeTrue();
        TerminalRunModes.All.Should().Contain("verify");
        PromptPartModeNames.IsKnown("verify").Should().BeTrue();
    }

    [Fact(DisplayName = "Манифест несёт системную инструкцию verify и бандл system:verify + user:common")]
    public void Manifest_has_verify_instruction_and_bundle()
    {
        var bundle = Manifest().Bundles.Single(b => b.Mode == "verify");

        bundle.Includes.Should().ContainSingle(i => i.Scope == PromptPartScopeNames.System && i.Kind == "verify");
        bundle.Includes.Should().ContainSingle(i => i.Scope == PromptPartScopeNames.User && i.Kind == "common");
        // Новых user-ключей нет намеренно: seed-only-on-empty (ADR-0051) не создаст их на живой базе.
        bundle.Includes.Where(i => i.Scope == PromptPartScopeNames.User).Should().HaveCount(1);
    }

    [Fact(DisplayName = "Ревьюер знает ровно три вещи: DoD, проблему и ветку")]
    public void Reviewer_knows_exactly_three_things()
    {
        var rule = RuleLineWithMarker(VerifyText(), "ровно три");

        rule.Should().Contain("## Definition of Done");
        rule.Should().Contain("## Для человека");
        rule.Should().Contain("## Ветка");
    }

    // Мутация: убрать это правило из манифеста — Single() бросает, тест краснеет.
    [Fact(DisplayName = "Отчёт исполнителя, его тело, чат и журнал оркестратора ревьюеру запрещены")]
    public void Executor_report_is_off_limits()
    {
        var rule = RuleLineWithMarker(VerifyText(), "## Отчёт");

        rule.Should().Contain("не читай", "беспристрастность держится на незнании самооценки исполнителя");
        rule.Should().ContainEquivalentOf("чат");
        rule.Should().ContainEquivalentOf("журнал оркестратора");
    }

    [Fact(DisplayName = "Ревью — diff ветки против основной, тесты и гейты, каждый пункт DoD фактом")]
    public void Review_reads_diff_and_runs_gates()
    {
        var text = VerifyText();

        text.Should().Contain("diff");
        RuleLineWithMarker(text, "Проверь каждый пункт DoD").Should().ContainEquivalentOf("факт");
        text.Should().ContainEquivalentOf("гейт");
    }

    [Fact(DisplayName = "Хотя бы один тест-доказательство ломается мутацией")]
    public void At_least_one_test_is_mutated()
    {
        var rule = RuleLineWithMarker(VerifyText(), "мутаци");

        rule.Should().ContainEquivalentOf("хотя бы один");
        rule.Should().ContainEquivalentOf("красне");
    }

    [Fact(DisplayName = "Вердикт живёт в теле ревью-интента: принято / не принято + пункты DoD + дефекты с файлом и строкой")]
    public void Verdict_lives_in_the_review_intent_body()
    {
        var rule = RuleLineWithMarker(VerifyText(), "## Вердикт");

        rule.Should().Contain("`принято`").And.Contain("`не принято`");
        rule.Should().Contain("replace-text", "вердикт пишется через skill intent, чат приёмке недоступен");
        rule.Should().ContainEquivalentOf("файл").And.ContainEquivalentOf("строк");
    }

    [Fact(DisplayName = "Ревьюер не правит код и не пушит — найденное идёт в вердикт")]
    public void Reviewer_does_not_fix_or_push()
    {
        var rule = RuleLineWithMarker(VerifyText(), "не правь");

        rule.Should().ContainEquivalentOf("push");
    }

    [Fact(DisplayName = "Фоновые агенты с парковкой в ожидании ревьюеру запрещены явно")]
    public void Background_agents_are_forbidden_for_reviewer()
    {
        var rule = RuleLineWithMarker(VerifyText(), "оновых агентов");

        rule.Should().ContainEquivalentOf("не заводи");
    }

    // Сторона исполнителя: ревью делает отдельная сессия, свой /code-review не запускается.
    [Fact(DisplayName = "Work-инструкция: исполнитель не запускает /code-review и не заводит фоновых агентов")]
    public void Executor_does_not_review_itself()
    {
        var work = KindText("work");

        var reviewRule = RuleLineWithMarker(work, "/code-review");
        reviewRule.Should().ContainEquivalentOf("не запускай");
        reviewRule.Should().ContainEquivalentOf("отдельная сессия");

        RuleLineWithMarker(work, "оновых агентов").Should().ContainEquivalentOf("запрещ");
    }

    // Сторона оркестратора: независимое ревью — этап цикла, вердикт — основание приёмки.
    [Fact(DisplayName = "Цикл оркестратора: постановка → запуск → слежение → независимое ревью → приёмка → слияние")]
    public void Orchestrator_cycle_includes_independent_review()
    {
        var cycle = RuleLineWithMarker(KindText("orchestrator"), "постановка → запуск");

        cycle.Should().Contain("независимое ревью → приёмка");
    }

    [Fact(DisplayName = "Оркестратор принимает по вердикту: review --intent, ## Вердикт, diff сам не читает как замену ревью")]
    public void Orchestrator_accepts_by_verdict()
    {
        var text = KindText("orchestrator");

        var acceptance = RuleLineWithMarker(text, "Приёмка");
        acceptance.Should().Contain("review --intent");
        acceptance.Should().Contain("## Вердикт");
        acceptance.Should().Contain("`принято`");

        RuleLineWithMarker(text, "как замену ревью").Should().ContainEquivalentOf("diff");

        var rejected = RuleLineWithMarker(text, "Не принято");
        rejected.Should().ContainEquivalentOf("вердикт");
        rejected.Should().Contain("run");
    }
}
