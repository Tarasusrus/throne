using FluentAssertions;
using NSubstitute;
using Throne.Application.Git;
using Throne.Application.Ports;
using Throne.Application.Terminals;
using Throne.Domain.Repositories;
using Throne.Domain.Tags;

namespace Throne.Application.Tests.Terminals;

/// <summary>
/// Auto-bind on Run is driven solely by <see cref="Tag.DefaultRepositories"/>
/// (<see cref="RunPreflightAutoBind"/>). Nothing else populates a fresh intent's binding —
/// не наследование от родителя по link, не соседний интент с тем же тегом.
///
/// Продовый инцидент: у всех тегов в базе <c>default_repositories = []</c>, поэтому Run
/// на дочернем интенте не биндил репозиторий вовсе, воркспейс оставался пустым и агент
/// уходил работать в общий клон. Второй тест здесь — ровно эта конфигурация.
/// </summary>
public partial class RunPreflightOrchestratorTests
{
    private static readonly TagId TagOnIntent = TagId.New();

    private static readonly TagDefaultRepository JobHuntDefault =
        new(new RepoCoordinate(GitProviderNames.GitHub, "octo", "hello"), "main");

    [Fact(DisplayName = "Run биндит репозиторий из default_repositories тега")]
    public async Task Run_binds_repository_from_tag_defaults()
    {
        var fixture = new Fixture()
            .WithAuthenticatedProvider()
            .WithBindingInsertSucceeding()
            .Setup(
                capabilityEnabled: true,
                intentExists: true,
                hasSession: false,
                bindings: [],
                spawn: new TmuxSpawnResult(TmuxSessionName.For(IntentIdValue), IsAlive: true, Detail: null),
                tagDefaults: [JobHuntDefault]);

        await fixture.Orchestrator.RunAsync(
            IntentIdValue, TerminalRunModes.Work, DefaultLaunch, NoPrompt, CancellationToken.None);

        await fixture.Bindings.Received(1).CreateAsync(
            Arg.Is<IntentRepositoryBinding>(b =>
                b.IntentId.Value == IntentIdValue
                && b.Coordinate.Owner == "octo"
                && b.Coordinate.Repo == "hello"),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Тег с пустым default_repositories не биндит ничего — продовая конфигурация")]
    public async Task Run_binds_nothing_when_tag_defaults_empty()
    {
        var fixture = new Fixture()
            .WithAuthenticatedProvider()
            .WithBindingInsertSucceeding()
            .Setup(
                capabilityEnabled: true,
                intentExists: true,
                hasSession: false,
                bindings: [],
                spawn: new TmuxSpawnResult(TmuxSessionName.For(IntentIdValue), IsAlive: true, Detail: null),
                tagDefaults: []);

        await fixture.Orchestrator.RunAsync(
            IntentIdValue, TerminalRunModes.Work, DefaultLaunch, NoPrompt, CancellationToken.None);

        await fixture.Bindings.DidNotReceive().CreateAsync(
            Arg.Any<IntentRepositoryBinding>(), Arg.Any<CancellationToken>());
    }

}
