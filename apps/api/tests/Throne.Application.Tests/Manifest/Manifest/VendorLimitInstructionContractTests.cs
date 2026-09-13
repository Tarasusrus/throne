using FluentAssertions;
using Throne.Application.Manifest;

namespace Throne.Application.Tests.Manifest.Manifest;

/// <summary>
/// Контракт подготовки к паузе по лимиту вендора (ADR-0055): у правила нет кода — промпт и есть
/// поведение. Исполнитель обязан сохранять сделанное до паузы (коммит, push,
/// <c>## Отчёт (промежуточный)</c>), оркестратор — не трогать исполнителя в паузе.
/// </summary>
public class VendorLimitInstructionContractTests
{
    private static string KindText(string kind) =>
        SkillManifestFixtures.RepoManifest().SystemInstructions.Single(i => i.Kind == kind).Text;

    private static string RuleLineWithMarker(string text, string marker) =>
        text.Split('\n').Single(line => line.Contains(marker, StringComparison.Ordinal));

    // Мутация: убрать правило из манифеста — Single() бросает, тест краснеет.
    [Fact(DisplayName = "work: перед паузой по лимиту — коммит, push и промежуточный отчёт в тело интента")]
    public void Work_saves_progress_before_the_limit_pause()
    {
        var rule = RuleLineWithMarker(KindText("work"), "## Отчёт (промежуточный)");

        rule.Should().ContainEquivalentOf("лимит");
        rule.Should().ContainEquivalentOf("коммит");
        rule.Should().Contain("push");
        rule.Should().ContainEquivalentOf("после возобновления", "после паузы работа продолжается с сохранённого");
    }

    [Fact(DisplayName = "orchestrator: исполнитель в паузе по лимиту — ждать, не перезапускать и не эскалировать")]
    public void Orchestrator_leaves_paused_executor_alone()
    {
        var rule = RuleLineWithMarker(KindText("orchestrator"), "пауза до");

        rule.Should().ContainEquivalentOf("лимит");
        rule.Should().ContainEquivalentOf("не встал");
        rule.Should().Contain("awaiting_operator", "только потолок попыток паркует исполнителя");
    }
}
