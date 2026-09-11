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

    [Theory(DisplayName = "Перечень операций называет все четыре команды скилла")]
    [InlineData("обзор")]
    [InlineData("запуск")]
    [InlineData("остановк")]
    [InlineData("watch")]
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
}
