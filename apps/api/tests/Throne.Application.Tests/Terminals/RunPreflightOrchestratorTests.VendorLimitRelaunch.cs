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

/// <summary>
/// Перезапуск после паузы восстанавливает не только разговор, но и инструменты: набор навыков
/// режима из записи запуска и привязку ревью — иначе возобновлённый исполнитель остаётся без
/// <c>throne-intent</c>, а запись запуска затирается пустым набором.
/// </summary>
public partial class RunPreflightOrchestratorTests
{
    private static readonly string[] RelaunchableSkills =
    [
        SessionSkillPackageIds.Intent,
        SessionSkillPackageIds.Dream,
        SessionSkillPackageIds.Orchestrator,
    ];

    private static readonly string[] RelaunchModes = [TerminalRunModes.Work, TerminalRunModes.Interview];

    [Fact(DisplayName = "Relaunch: навыки режима из записи запуска материализуются и сохраняются как есть")]
    public async Task Relaunch_restores_persisted_skill_selection()
    {
        var fixture = new Fixture().Setup(
            capabilityEnabled: true,
            intentExists: true,
            hasSession: false,
            bindings: [NewBinding(CloneStatusNames.Ready)],
            spawn: new TmuxSpawnResult(TmuxSessionName.For(IntentIdValue), IsAlive: true, Detail: null));
        string[] remembered = [SessionSkillPackageIds.Intent, SessionSkillPackageIds.Orchestrator];
        fixture.LaunchStore.GetAsync(IntentIdValue, Arg.Any<CancellationToken>())
            .Returns(new TerminalLaunchRecord(TerminalRunModes.Work, TerminalAgentCatalog.VendorClaude, "sonnet", "low",
                new Dictionary<string, IReadOnlyList<string>> { [TerminalRunModes.Work] = remembered }));

        await fixture.Resumer().RelaunchAsync(
            new VendorLimitPause(IntentIdValue, TerminalAgentCatalog.VendorClaude, Now, Now, 1, "limit", null, null, 0),
            CancellationToken.None);

        fixture.SpawnedSkillPackages.Select(p => p.Id).Should().BeEquivalentTo(remembered,
            "workspace после сброса материализует те же пакеты, что были у прошлого спавна");
        await fixture.LaunchStore.Received(1).SaveSelectedSkillIdsAsync(
            IntentIdValue,
            TerminalRunModes.Work,
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(remembered)),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Свойство: для любого режима и набора навыков relaunch материализует и сохраняет ровно этот набор")]
    public async Task Relaunch_preserves_any_persisted_selection()
    {
        for (var seed = 0; seed < 40; seed++)
        {
            var rng = new Random(seed);
            var mode = RelaunchModes[rng.Next(RelaunchModes.Length)];
            var remembered = RelaunchableSkills.Where(_ => rng.Next(2) == 0).ToArray();
            var otherMode = RelaunchModes.First(m => m != mode);
            var foreign = RelaunchableSkills.Where(_ => rng.Next(2) == 0).ToArray();

            var fixture = new Fixture().Setup(
                capabilityEnabled: true,
                intentExists: true,
                hasSession: false,
                bindings: [NewBinding(CloneStatusNames.Ready)],
                spawn: new TmuxSpawnResult(TmuxSessionName.For(IntentIdValue), IsAlive: true, Detail: null));
            fixture.LaunchStore.GetAsync(IntentIdValue, Arg.Any<CancellationToken>())
                .Returns(new TerminalLaunchRecord(mode, TerminalAgentCatalog.VendorClaude, "sonnet", "low",
                    new Dictionary<string, IReadOnlyList<string>> { [mode] = remembered, [otherMode] = foreign }));

            await fixture.Resumer().RelaunchAsync(
                new VendorLimitPause(IntentIdValue, TerminalAgentCatalog.VendorClaude, Now, Now, 1, "limit", null, null, 0),
                CancellationToken.None);

            fixture.SpawnedSkillPackages.Select(p => p.Id).Should().BeEquivalentTo(remembered, $"seed {seed}");
            await fixture.LaunchStore.Received(1).SaveSelectedSkillIdsAsync(
                IntentIdValue,
                mode,
                Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(remembered)),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact(DisplayName = "Relaunch ревью: привязка из записи запуска, а не первая попавшаяся")]
    public async Task Relaunch_restores_review_binding()
    {
        var first = NewBinding(CloneStatusNames.Ready, owner: "octo", repo: "first");
        var second = NewBinding(CloneStatusNames.Ready, owner: "octo", repo: "second");
        var fixture = new Fixture().Setup(
            capabilityEnabled: true,
            intentExists: true,
            hasSession: false,
            bindings: [first, second],
            spawn: new TmuxSpawnResult(TmuxSessionName.For(IntentIdValue), IsAlive: true, Detail: null));
        fixture.LaunchStore.GetAsync(IntentIdValue, Arg.Any<CancellationToken>())
            .Returns(new TerminalLaunchRecord(TerminalRunModes.Review, TerminalAgentCatalog.VendorClaude, "sonnet", "low",
                new Dictionary<string, IReadOnlyList<string>> { [TerminalRunModes.Review] = [SessionSkillPackageIds.Review] },
                ReviewBindingId: second.Id.Value));

        await fixture.Resumer().RelaunchAsync(
            new VendorLimitPause(IntentIdValue, TerminalAgentCatalog.VendorClaude, Now, Now, 1, "limit", null, null, 0),
            CancellationToken.None);

        fixture.SpawnedSkillPackages.OfType<ReviewSessionSkillPackage>().Single().Target.BindingId
            .Should().Be(second.Id.Value);
        await fixture.Tmux.Received(1).SpawnAsync(
            Arg.Is<TmuxSpawnRequest>(r => r.EnvironmentVariables!["THRONE_REPOSITORY_BINDING_ID"] == second.Id.Value),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Обычный run с ревью запоминает выбранную привязку в записи запуска")]
    public async Task Plain_run_persists_review_binding()
    {
        var first = NewBinding(CloneStatusNames.Ready, owner: "octo", repo: "first");
        var second = NewBinding(CloneStatusNames.Ready, owner: "octo", repo: "second");
        var fixture = new Fixture().Setup(
            capabilityEnabled: true,
            intentExists: true,
            hasSession: false,
            bindings: [first, second],
            spawn: new TmuxSpawnResult(TmuxSessionName.For(IntentIdValue), IsAlive: true, Detail: null));

        await fixture.Orchestrator.RunAsync(
            IntentIdValue, TerminalRunModes.Review, DefaultLaunch, NoPrompt,
            [SessionSkillPackageIds.Review], CancellationToken.None, reviewBindingId: second.Id.Value);

        await fixture.LaunchStore.Received(1).SaveAsync(
            IntentIdValue,
            Arg.Is<TerminalLaunchRecord>(r => r.ReviewBindingId == second.Id.Value),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Обычный run без ревью сбрасывает привязку ревью в записи запуска")]
    public async Task Plain_run_without_review_clears_review_binding()
    {
        var fixture = new Fixture().Setup(
            capabilityEnabled: true,
            intentExists: true,
            hasSession: false,
            bindings: [NewBinding(CloneStatusNames.Ready)],
            spawn: new TmuxSpawnResult(TmuxSessionName.For(IntentIdValue), IsAlive: true, Detail: null));

        await fixture.Orchestrator.RunAsync(
            IntentIdValue, TerminalRunModes.Work, DefaultLaunch, NoPrompt,
            [SessionSkillPackageIds.Intent], CancellationToken.None);

        await fixture.LaunchStore.Received(1).SaveAsync(
            IntentIdValue,
            Arg.Is<TerminalLaunchRecord>(r => r.ReviewBindingId == null),
            Arg.Any<CancellationToken>());
    }
}
