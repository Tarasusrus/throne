using FluentAssertions;
using NSubstitute;
using Throne.Application.Git;
using Throne.Application.Ports;
using Throne.Application.Terminals;
using Throne.Domain.Repositories;

namespace Throne.Application.Tests.Terminals;

/// <summary>
/// Перезапуск после паузы по лимиту (ADR-0055): та же ось запуска из записи, флаг продолжения
/// разговора от адаптера вендора, сохранённые правила из workspace и промпт-продолжение.
/// </summary>
public partial class RunPreflightOrchestratorTests
{
    [Fact(DisplayName = "Relaunch: спавн с --continue, правилами из workspace и промптом-продолжением; ось — из записи запуска")]
    public async Task Relaunch_continues_conversation_with_persisted_axis()
    {
        var fixture = new Fixture().Setup(
            capabilityEnabled: true,
            intentExists: true,
            hasSession: false,
            bindings: [NewBinding(CloneStatusNames.Ready)],
            spawn: new TmuxSpawnResult(TmuxSessionName.For(IntentIdValue), IsAlive: true, Detail: null));
        fixture.LaunchStore.GetAsync(IntentIdValue, Arg.Any<CancellationToken>())
            .Returns(new TerminalLaunchRecord(TerminalRunModes.Work, TerminalAgentCatalog.VendorClaude, "sonnet", "low",
                new Dictionary<string, IReadOnlyList<string>>()));
        var resumer = fixture.Resumer();

        await resumer.RelaunchAsync(
            new VendorLimitPause(IntentIdValue, TerminalAgentCatalog.VendorClaude, Now, Now, 1, "limit", null, null, 0),
            CancellationToken.None);

        await fixture.Tmux.Received(1).SpawnAsync(
            Arg.Is<TmuxSpawnRequest>(r =>
                r.Arguments.SequenceEqual(new[]
                {
                    "--model", "sonnet", "--effort", "low", "--remote-control", "--settings", SettingsPath, "--continue",
                })),
            Arg.Any<CancellationToken>());
        fixture.Delivery.Received(1).Kick(Arg.Is<TerminalPromptDeliveryRequest>(r =>
            r.UserPrompt == VendorLimitSessionResumer.ContinuationPrompt && r.Mode == TerminalRunModes.Work));
        fixture.SpawnedSystemPrompt.Should().Be(RunPreflightOrchestratorFixtureStubs.PersistedRules,
            "правила берутся из файла прошлого спавна — сервер их заново не собирает");
    }

    [Fact(DisplayName = "Relaunch без записи запуска — отказ: нечем восстановить ось")]
    public async Task Relaunch_without_launch_record_fails()
    {
        var fixture = new Fixture().Setup(capabilityEnabled: true, intentExists: true, hasSession: false);
        fixture.LaunchStore.GetAsync(IntentIdValue, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TerminalLaunchRecord?>(null));

        var act = () => fixture.Resumer().RelaunchAsync(
            new VendorLimitPause(IntentIdValue, TerminalAgentCatalog.VendorClaude, Now, Now, 1, "limit", null, null, 0),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await fixture.Tmux.DidNotReceiveWithAnyArgs().SpawnAsync(default!, default);
    }

    [Fact(DisplayName = "Обычный run не несёт флаг продолжения — --continue появляется только на relaunch")]
    public async Task Plain_run_has_no_continue_flag()
    {
        var fixture = new Fixture().Setup(
            capabilityEnabled: true,
            intentExists: true,
            hasSession: false,
            bindings: [NewBinding(CloneStatusNames.Ready)],
            spawn: new TmuxSpawnResult(TmuxSessionName.For(IntentIdValue), IsAlive: true, Detail: null));

        await fixture.Orchestrator.RunAsync(IntentIdValue, TerminalRunModes.Work, DefaultLaunch, NoPrompt, CancellationToken.None);

        await fixture.Tmux.Received(1).SpawnAsync(
            Arg.Is<TmuxSpawnRequest>(r => !r.Arguments.Contains("--continue")),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Nudge: промпт-продолжение печатается в живую панель и подтверждается Enter")]
    public async Task Nudge_types_continuation_and_submits()
    {
        var fixture = new Fixture();
        var tmux = Substitute.For<ITmuxSessionManager>();
        var resumer = new VendorLimitSessionResumer(
            tmux,
            Substitute.For<IIntentTerminalLaunchStore>(),
            Substitute.For<IWorkspaceRootProvider>(),
            [],
            fixture.Orchestrator);

        await resumer.NudgeAsync(
            new VendorLimitPause("i1", TerminalAgentCatalog.VendorClaude, Now, Now, 1, "limit", null, null, 0),
            CancellationToken.None);

        Received.InOrder(() =>
        {
            tmux.SendLiteralTextAsync("i1", VendorLimitSessionResumer.ContinuationPrompt, Arg.Any<CancellationToken>());
            tmux.SendEnterAsync("i1", Arg.Any<CancellationToken>());
        });
    }
}
