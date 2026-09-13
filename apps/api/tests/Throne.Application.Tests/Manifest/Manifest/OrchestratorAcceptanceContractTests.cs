using System.Text.RegularExpressions;
using FluentAssertions;

namespace Throne.Application.Tests.Manifest.Manifest;

/// <summary>
/// Контракт приёмки в системной инструкции оркестратора (ADR-0054 §7): цикл задачи замыкает
/// оркестратор — приёмка, слияние, push — а оператору достаются только архитектурные развилки.
/// Вопрос «Проверять и вливать?» из первого живого прогона не должен повториться.
/// </summary>
public class OrchestratorAcceptanceContractTests
{
    private static readonly Regex StopDirective = new(
        @"\b(остановись|останавливайся|остановить|стоп|не начинай|ничего не делай|доложи оператору)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string KindText(string kind) =>
        SkillManifestFixtures.RepoManifest()
            .SystemInstructions.Single(i => i.Kind == kind).Text;

    private static string OrchestratorText() => KindText("orchestrator");

    private static string RuleLineWithMarker(string text, string marker) =>
        text.Split('\n').Single(line => line.Contains(marker, StringComparison.Ordinal));

    [Fact(DisplayName = "Приёмка — обязанность оркестратора: ревью, проверка, слияние и push своими руками")]
    public void Acceptance_is_the_orchestrators_own_job()
    {
        var rule = RuleLineWithMarker(OrchestratorText(), "Приёмка");

        rule.Should().Contain("accept", "детерминированная часть приёмки — команда скилла, не ручной tool loop");
        rule.Should().Contain("сольёт").And.Contain("запушит", "принятая ветка вливается в основную самим оркестратором");
        StopDirective.IsMatch(rule).Should().BeFalse("приёмка не упирается в оператора");
    }

    [Fact(DisplayName = "Push и merge в репозитории своего тега — внутри режима, общее правило «спроси перед push» на них не действует")]
    public void Push_into_own_tag_repos_needs_no_permission()
    {
        var text = OrchestratorText();
        var rule = text.Split('\n').Single(line => line.Contains("push", StringComparison.OrdinalIgnoreCase)
            && line.Contains("внутри режима", StringComparison.Ordinal));

        rule.Should().Contain("не относится", "иначе общая часть снова родит «Проверять и вливать?»");
    }

    [Theory(DisplayName = "Вопросы «проверять? вливать? продолжать?» названы антипаттерном")]
    [InlineData("Проверять?")]
    [InlineData("Вливать?")]
    [InlineData("Продолжать?")]
    public void Permission_questions_are_named_as_an_antipattern(string question)
    {
        var rule = RuleLineWithMarker(OrchestratorText(), "Антипаттерн");

        rule.Should().Contain(question);
    }

    [Fact(DisplayName = "К оператору — только архитектурная развилка, противоречие постановки и необратимое вне тега")]
    public void Operator_is_reserved_for_architecture()
    {
        var rule = RuleLineWithMarker(OrchestratorText(), "К оператору");

        rule.Should().ContainEquivalentOf("архитектур");
        rule.Should().ContainEquivalentOf("необратим");
        rule.Should().Contain("рекоменд", "развилка приносится с рекомендацией, а не голым вопросом");
    }

    [Fact(DisplayName = "Красная приёмка возвращается исполнителю, а не чинится оркестратором")]
    public void Red_acceptance_goes_back_to_the_executor()
    {
        var rule = RuleLineWithMarker(OrchestratorText(), "Не принято");

        rule.Should().Contain("run", "правки — новый запуск исполнителя с замечаниями");
        rule.Should().NotContainEquivalentOf("почини сам");
    }

    [Fact(DisplayName = "Постановка фиксирует ветку исполнителя — иначе приёмке нечего fetch-ить")]
    public void Statement_of_work_names_the_branch()
    {
        OrchestratorText().Should().Contain("## Ветка");
    }

    // Сторона исполнителя того же контракта: приёмка читает ветку из постановки и отчёт из тела.
    [Fact(DisplayName = "Work-инструкция велит пушить ветку из постановки и дублировать отчёт в тело интента")]
    public void Work_side_delivers_branch_and_report()
    {
        var work = KindText("work");

        var branchRule = RuleLineWithMarker(work, "## Ветка");
        branchRule.Should().ContainEquivalentOf("запушь");
        StopDirective.IsMatch(branchRule).Should().BeFalse();

        var reportRule = RuleLineWithMarker(work, "`## Отчёт`");
        reportRule.Should().Contain("replace-text", "отчёт пишется через skill intent, а не в чат");
    }
}
