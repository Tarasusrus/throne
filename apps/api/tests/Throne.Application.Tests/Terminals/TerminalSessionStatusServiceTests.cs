using FluentAssertions;
using NSubstitute;
using Throne.Application.Events;
using Throne.Application.Ports;
using Throne.Application.Terminals;
using Throne.Application.Terminals.Capabilities;
using Throne.Domain.Intents;
using Throne.Domain.Repositories;

namespace Throne.Application.Tests.Terminals;

/// <summary>
/// Пробник сессии показывает паузу по лимиту (ADR-0055): живой tmux + активная пауза =
/// <c>paused_by_limit</c> с «до когда»; возобновлённая или отсутствующая пауза = обычный
/// <c>running</c>; мёртвый tmux = <c>exited</c>, запись паузы не трогается (её ждёт сторож).
/// </summary>
public class TerminalSessionStatusServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 5, 0, 0, TimeSpan.Zero);
    private const string IntentIdValue = "intent-status-1";

    [Fact(DisplayName = "Живой tmux + активная пауза → paused_by_limit с resume_at, попытками и сообщением вендора")]
    public async Task Live_session_with_active_pause_is_paused_by_limit()
    {
        var fixture = new Fixture(alive: true);
        fixture.Pauses.Put(Pause(resumedAt: null));

        var result = await fixture.Service.GetAsync(IntentIdValue, CancellationToken.None);

        result.SessionState.Should().Be(TerminalSessionStates.PausedByLimit);
        result.LimitPause.Should().NotBeNull();
        result.LimitPause!.ResumeAt.Should().Be(Now.AddHours(1));
        result.LimitPause.Attempts.Should().Be(2);
        result.LimitPause.Message.Should().Be("You've hit your monthly spend limit");
    }

    [Fact(DisplayName = "Живой tmux + возобновлённая пауза → running без limit_pause")]
    public async Task Live_session_with_resumed_pause_is_running()
    {
        var fixture = new Fixture(alive: true);
        fixture.Pauses.Put(Pause(resumedAt: Now));

        var result = await fixture.Service.GetAsync(IntentIdValue, CancellationToken.None);

        result.SessionState.Should().Be(TerminalSessionStates.Running);
        result.LimitPause.Should().BeNull();
    }

    [Fact(DisplayName = "Живой tmux без паузы → running")]
    public async Task Live_session_without_pause_is_running()
    {
        var fixture = new Fixture(alive: true);

        var result = await fixture.Service.GetAsync(IntentIdValue, CancellationToken.None);

        result.SessionState.Should().Be(TerminalSessionStates.Running);
        result.LimitPause.Should().BeNull();
    }

    [Fact(DisplayName = "Мёртвый tmux при активной паузе → exited; запись остаётся сторожу на перезапуск")]
    public async Task Dead_session_with_pause_is_exited_and_keeps_record()
    {
        var fixture = new Fixture(alive: false);
        fixture.Pauses.Put(Pause(resumedAt: null));

        var result = await fixture.Service.GetAsync(IntentIdValue, CancellationToken.None);

        result.SessionState.Should().Be(TerminalSessionStates.Exited);
        result.LimitPause.Should().NotBeNull("оператору видно, что сессия ждала сброс, когда пропала");
        fixture.Pauses.Find(IntentIdValue).Should().NotBeNull();
    }

    private static VendorLimitPause Pause(DateTimeOffset? resumedAt) =>
        new(IntentIdValue, "claude", Now, Now.AddHours(1), 2, "You've hit your monthly spend limit", resumedAt, null, 0);

    private sealed class Fixture
    {
        public Fixture(bool alive)
        {
            var intents = Substitute.For<IIntentRepository>();
            intents.GetByIdAsync(Arg.Any<IntentId>(), Arg.Any<CancellationToken>())
                .Returns(ci => Intent.Restore(ci.ArgAt<IntentId>(0), "x", IntentStatusNames.Work, 1, [], Now, Now));
            var detection = Substitute.For<ICapabilityDetectionCache>();
            detection.GetAsync("tmux", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<CapabilityProbeResult?>(new CapabilityProbeResult(true, "tmux 3.4")));
            var tmux = Substitute.For<ITmuxSessionManager>();
            tmux.HasSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(alive);
            var bindings = Substitute.For<IIntentRepositoryBindingRepository>();
            bindings.FindByIntentAsync(Arg.Any<IntentId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<IntentRepositoryBinding>>([]));
            var spawn = new RunPreflightSpawn(
                tmux, Substitute.For<Throne.Application.Git.IWorkspaceRootProvider>(),
                TerminalSpawnTestDoubles.EmptyWorkspacePreparer(), [], Substitute.For<IRunPreflightPromptDelivery>(),
                new RunPreflightOptions(), TerminalSpawnTestDoubles.VendorCatalog(),
                new Throne.Application.Intents.SetIntentStatusHandler(intents, new PassthroughUnitOfWork(), TimeProvider.System),
                Substitute.For<IDomainEventDispatcher>());
            var guards = new RunPreflightGuards(intents, detection, spawn);
            Pauses = new InMemoryVendorLimitPauseStore();
            Service = new TerminalSessionStatusService(
                guards, bindings, Substitute.For<IIntentTerminalLaunchStore>(), tmux, Pauses);
        }

        public InMemoryVendorLimitPauseStore Pauses { get; }
        public TerminalSessionStatusService Service { get; }
    }

    private sealed class PassthroughUnitOfWork : IUnitOfWork
    {
        public Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken ct) => work(ct);
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct) => work(ct);
        public Task<T> ExecuteOutsideTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct) => work(ct);
    }
}
