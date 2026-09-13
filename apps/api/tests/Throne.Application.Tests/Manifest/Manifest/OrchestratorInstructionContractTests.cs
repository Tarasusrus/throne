using System.Text.RegularExpressions;
using FluentAssertions;
using Throne.Application.Manifest;

namespace Throne.Application.Tests.Manifest.Manifest;

/// <summary>
/// Contract of the orchestrator system instruction — the text every orchestrator session boots with.
/// The mode has no code of its own: the prompt IS the behaviour, so the rules that make a normal
/// start possible are pinned here rather than left to a reviewer's eye.
/// </summary>
public class OrchestratorInstructionContractTests
{
    // Word-boundary matching on purpose: substring «останов» also lives inside «постановка»,
    // which the rule legitimately uses.
    private static readonly Regex StopDirective = new(
        @"\b(остановись|останавливайся|остановить|стоп|не начинай|ничего не делай|доложи оператору)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string OrchestratorText() =>
        SkillManifestFixtures.RepoManifest()
            .SystemInstructions.Single(i => i.Kind == "orchestrator").Text;

    private static string RuleLineWithMarker(string text, string marker) =>
        text.Split('\n').Single(line => line.Contains(marker, StringComparison.Ordinal));

    [Fact(DisplayName = "Правило про [ORCH] предписывает разметить тело самому, а не остановиться")]
    public void Orch_marker_rule_prescribes_self_markup()
    {
        var rule = RuleLineWithMarker(OrchestratorText(), "[ORCH]");

        rule.Should().Contain("размет", "неразмеченное тело оркестратор размечает сам");
        StopDirective.IsMatch(rule).Should()
            .BeFalse("штатный старт режима не должен упираться в служебную разметку");
    }

    [Fact(DisplayName = "Разметка тела фиксируется первой записью журнала решений")]
    public void Self_markup_is_journaled()
    {
        var rule = RuleLineWithMarker(OrchestratorText(), "[ORCH]");

        rule.Should().Contain("журнал", "разметка — это решение, а решения живут в журнале");
    }

    [Fact(DisplayName = "Остановка остаётся ровно для двух случаев: несколько тегов и чужая активная работа")]
    public void Stop_survives_only_for_real_ambiguity()
    {
        var stopRule = RuleLineWithMarker(OrchestratorText(), "несколько тегов");

        StopDirective.IsMatch(stopRule).Should().BeTrue();
        stopRule.Should().ContainEquivalentOf("чужую активную работу");
    }

    [Theory(DisplayName = "Перечень операций называет все команды скилла")]
    [InlineData("обзор")]
    [InlineData("запуск")]
    [InlineData("остановк")]
    [InlineData("watch")]
    [InlineData("review")]
    [InlineData("приёмк")]
    public void Lists_every_skill_command(string command)
    {
        OrchestratorText().Should().ContainEquivalentOf(command);
    }

    [Fact(DisplayName = "Ожидание описано честно: внутри хода или ре-промпт оператором")]
    public void Waiting_model_is_stated()
    {
        var text = OrchestratorText();

        text.Should().Contain("--wait");
        text.Should().Contain("900");
    }

    [Fact(DisplayName = "Фоновое слежение названо и отправлено за деталями в SKILL.md")]
    public void Background_watching_is_pointed_at_the_skill()
    {
        var watchRule = OrchestratorText()
            .Split('\n')
            .Single(line => line.Contains("Фоновое слежение", StringComparison.Ordinal));

        watchRule.Should().Contain("SKILL.md");
    }

    [Fact(DisplayName = "Дочерний интент заводится с секцией ## Definition of Done")]
    public void Child_intents_carry_a_dod()
    {
        OrchestratorText().Should().Contain("## Definition of Done");
    }

    // Оператору запрещена внутренняя кухня оркестрации: id/хэши, имена веток/файлов/
    // функций/переменных, номера прогонов, служебные фразы. Каждый образец — то, что
    // реально уходило оператору 13.09 до появления стандарта (см. ## Примеры интента).
    [Theory(DisplayName = "Правило языка с оператором запрещает внутреннюю кухню")]
    [InlineData("хэш")]
    [InlineData("id интент")]
    [InlineData("имена веток")]
    [InlineData("номера прогонов")]
    [InlineData("кухн")]
    [InlineData("жду монитор")]
    public void Operator_language_rule_forbids_internal_kitchen(string forbiddenMention)
    {
        var rule = RuleLineWithMarker(OrchestratorText(), "Оператору нельзя нести");

        rule.Should().ContainEquivalentOf(forbiddenMention);
    }

    [Fact(DisplayName = "Правило языка с оператором разрешает статус одним из пяти слов")]
    public void Operator_language_rule_allows_the_five_status_words()
    {
        var rule = RuleLineWithMarker(OrchestratorText(), "Оператору нельзя нести");

        foreach (var status in new[] { "идёт", "встал", "принято", "влито", "в очереди" })
        {
            rule.Should().ContainEquivalentOf(status);
        }
    }

    [Fact(DisplayName = "Правило именования задачи: короткое имя из заголовка, не хэш, неизменное")]
    public void Operator_language_rule_names_tasks_by_title_not_hash()
    {
        var rule = RuleLineWithMarker(OrchestratorText(), "Оператору нельзя нести");

        rule.Should().ContainEquivalentOf("2–4 слов");
        rule.Should().ContainEquivalentOf("не хэшем");
        rule.Should().ContainEquivalentOf("неизменным");
    }

    [Fact(DisplayName = "Оператору пишут только по результату, развилке или противоречию — остальное молча в журнал")]
    public void Operator_is_addressed_only_on_result_fork_or_contradiction()
    {
        var rule = RuleLineWithMarker(OrchestratorText(), "Пишешь оператору только по результату");

        rule.Should().ContainEquivalentOf("результату");
        rule.Should().ContainEquivalentOf("развилке");
        rule.Should().ContainEquivalentOf("противоречию");
        rule.Should().ContainEquivalentOf("молча в журнал");
    }

    [Fact(DisplayName = "Потолок длины: реплика — 3 строки, сводка и отчёт — 7 строк")]
    public void Operator_language_rule_caps_message_length()
    {
        var rule = RuleLineWithMarker(OrchestratorText(), "Пишешь оператору только по результату");

        rule.Should().Contain("3 строк");
        rule.Should().Contain("7 строк");
    }
}
