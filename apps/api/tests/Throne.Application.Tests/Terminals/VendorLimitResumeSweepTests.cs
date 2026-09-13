using FluentAssertions;
using NSubstitute;
using Throne.Application.Events;
using Throne.Application.Intents;
using Throne.Application.Ports;
using Throne.Application.Terminals;
using Throne.Domain.Intents;
using Throne.Domain.Intents.Training;

namespace Throne.Application.Tests.Terminals;

/// <summary>
/// Сторож паузы (ADR-0055): на тике после resume_at + люфт живую сессию толкает, мёртвую
/// перезапускает с контекстом, после потолка толчков — awaiting_operator. Возобновлённые записи
/// живут окно повтора и убираются.
/// </summary>
public class VendorLimitResumeSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 5, 0, 0, TimeSpan.Zero);
    private static readonly VendorLimitPolicyOptions Options = VendorLimitPolicyOptions.Default;

    [Fact(DisplayName = "До resume_at + люфт сторож ничего не делает")]
    public async Task Before_grace_nothing_happens()
    {
        var rig = Rig.Create(Now);
        rig.Pauses.Put(Rig.Pause(resumeAt: Now.AddMinutes(5)));
        rig.Alive(true);

        await rig.Sweep.RunOnceAsync(CancellationToken.None);

        await rig.Resumer.DidNotReceiveWithAnyArgs().NudgeAsync(default!, default);
        await rig.Resumer.DidNotReceiveWithAnyArgs().RelaunchAsync(default!, default);
        rig.Pauses.Find("i1")!.IsActive.Should().BeTrue();
    }

    [Fact(DisplayName = "После люфта живая сессия получает толчок один раз за NudgeInterval")]
    public async Task After_grace_live_session_is_nudged_once_per_interval()
    {
        var rig = Rig.Create(Now);
        rig.Pauses.Put(Rig.Pause(resumeAt: Now - Options.ResumeGrace));
        rig.Alive(true);

        await rig.Sweep.RunOnceAsync(CancellationToken.None);
        await rig.Sweep.RunOnceAsync(CancellationToken.None);

        await rig.Resumer.Received(1).NudgeAsync(Arg.Is<VendorLimitPause>(p => p.IntentId == "i1"), Arg.Any<CancellationToken>());
        var pause = rig.Pauses.Find("i1")!;
        pause.Nudges.Should().Be(1);
        pause.LastNudgeAt.Should().Be(Now);
        pause.IsActive.Should().BeTrue("толчок — не доказательство возобновления; его даст PostToolUse");
    }

    [Fact(DisplayName = "После люфта мёртвая сессия перезапускается с контекстом и пауза считается возобновлённой")]
    public async Task After_grace_dead_session_is_relaunched()
    {
        var rig = Rig.Create(Now);
        rig.Pauses.Put(Rig.Pause(resumeAt: Now - Options.ResumeGrace));
        rig.Alive(false);

        await rig.Sweep.RunOnceAsync(CancellationToken.None);

        await rig.Resumer.Received(1).RelaunchAsync(Arg.Is<VendorLimitPause>(p => p.IntentId == "i1"), Arg.Any<CancellationToken>());
        rig.Pauses.Find("i1")!.ResumedAt.Should().Be(Now);
        rig.Events.Should().ContainSingle(e => e is TerminalLimitResumed);
    }

    [Fact(DisplayName = "Перезапуск упал — запись остаётся, попытка учитывается как толчок, следующий тик не раньше NudgeInterval")]
    public async Task Failed_relaunch_counts_as_a_nudge()
    {
        var rig = Rig.Create(Now);
        rig.Pauses.Put(Rig.Pause(resumeAt: Now - Options.ResumeGrace));
        rig.Alive(false);
        rig.Resumer.RelaunchAsync(Arg.Any<VendorLimitPause>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("boom"));

        await rig.Sweep.RunOnceAsync(CancellationToken.None);

        var pause = rig.Pauses.Find("i1")!;
        pause.IsActive.Should().BeTrue();
        pause.Nudges.Should().Be(1);
        pause.LastNudgeAt.Should().Be(Now);
    }

    [Fact(DisplayName = "Потолок толчков исчерпан — awaiting_operator с причиной, запись снята")]
    public async Task Exhausted_nudges_escalate()
    {
        var rig = Rig.Create(Now);
        rig.Pauses.Put(Rig.Pause(resumeAt: Now - Options.ResumeGrace - Options.NudgeInterval) with
        {
            Nudges = Options.MaxNudges,
            LastNudgeAt = Now - Options.NudgeInterval,
        });
        rig.Alive(true);

        await rig.Sweep.RunOnceAsync(CancellationToken.None);

        rig.Pauses.Find("i1").Should().BeNull();
        await rig.Repo.Received(1).SetStatusAsync(
            Arg.Any<IntentId>(),
            Arg.Is(IntentStatusNames.AwaitingOperator),
            Arg.Is<string?>(t => t == null),
            Arg.Is<string?>(r => r != null && r.Contains("возобнов", StringComparison.Ordinal)),
            Arg.Is(IntentTrainingAuthor.System),
            Arg.Is(VendorLimitResumeSweep.EscalationSource),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Возобновлённая запись живёт окно повтора и затем убирается")]
    public async Task Resumed_records_are_collected_after_repeat_window()
    {
        var rig = Rig.Create(Now);
        rig.Pauses.Put(Rig.Pause(resumeAt: Now.AddHours(-2)) with { ResumedAt = Now - Options.RepeatWindow });
        rig.Pauses.Put(Rig.Pause(resumeAt: Now.AddHours(-2), intentId: "i2") with { ResumedAt = Now - Options.RepeatWindow - TimeSpan.FromSeconds(1) });

        await rig.Sweep.RunOnceAsync(CancellationToken.None);

        rig.Pauses.Find("i1").Should().NotBeNull("ровно на границе окна ещё повтор");
        rig.Pauses.Find("i2").Should().BeNull();
        await rig.Resumer.DidNotReceiveWithAnyArgs().NudgeAsync(default!, default);
    }

    [Fact(DisplayName = "Закрытый интент: пауза снимается без эскалации и без толчков")]
    public async Task Terminal_intent_pause_is_dropped()
    {
        var rig = Rig.Create(Now, status: IntentStatusNames.Done);
        rig.Pauses.Put(Rig.Pause(resumeAt: Now - Options.ResumeGrace));
        rig.Alive(true);

        await rig.Sweep.RunOnceAsync(CancellationToken.None);

        rig.Pauses.Find("i1").Should().BeNull();
        await rig.Resumer.DidNotReceiveWithAnyArgs().NudgeAsync(default!, default);
        await rig.NoStatusSet();
    }

    [Fact(DisplayName = "Свойство: за любую серию тиков толчков ≤ потолка, перезапуск не более одного, эскалация ровно при исчерпании")]
    public async Task Random_tick_series_respect_budgets()
    {
        var escalations = 0;
        for (var seed = 0; seed < 150; seed++)
        {
            var rng = new Random(seed);
            var clock = new MutableClock(Now);
            var rig = Rig.Create(clock);
            rig.Pauses.Put(Rig.Pause(resumeAt: Now.AddMinutes(rng.Next(0, 5))));
            var alive = rng.Next(4) != 0;
            rig.Alive(alive);
            var ticks = rng.Next(1, 40);
            for (var i = 0; i < ticks; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(rng.Next(5, 90)));
                await rig.Sweep.RunOnceAsync(CancellationToken.None);
            }

            var why = $"seed {seed}: alive={alive} ticks={ticks}";
            var nudges = rig.Resumer.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IVendorLimitSessionResumer.NudgeAsync));
            var relaunches = rig.Resumer.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IVendorLimitSessionResumer.RelaunchAsync));
            var statusSets = rig.Repo.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IIntentRepository.SetStatusAsync));
            nudges.Should().BeLessThanOrEqualTo(Options.MaxNudges, why);
            relaunches.Should().BeLessThanOrEqualTo(1, why);
            if (!alive)
            {
                nudges.Should().Be(0, why);
                statusSets.Should().Be(0, why);
            }
            statusSets.Should().BeLessThanOrEqualTo(1, why);
            if (statusSets == 1)
            {
                escalations++;
                nudges.Should().Be(Options.MaxNudges, why);
                rig.Pauses.Find("i1").Should().BeNull(why);
            }
        }
        escalations.Should().BeGreaterThan(0, "генератор обязан доходить до эскалации");
    }

    private sealed class Rig
    {
        public required VendorLimitResumeSweep Sweep { get; init; }
        public required IVendorLimitPauseStore Pauses { get; init; }
        public required IVendorLimitSessionResumer Resumer { get; init; }
        public required ITmuxSessionManager Tmux { get; init; }
        public required IIntentRepository Repo { get; init; }
        public required List<IDomainEvent> Events { get; init; }

        public void Alive(bool alive) =>
            Tmux.HasSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(alive);

        public Task NoStatusSet() =>
            Repo.DidNotReceive().SetStatusAsync(
                Arg.Any<IntentId>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<IntentTrainingAuthor>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        public static VendorLimitPause Pause(DateTimeOffset resumeAt, string intentId = "i1") =>
            new(intentId, TerminalAgentCatalog.VendorClaude, resumeAt.AddHours(-1), resumeAt, 1,
                "You've hit your monthly spend limit", ResumedAt: null, LastNudgeAt: null, Nudges: 0);

        public static Rig Create(DateTimeOffset now, string status = IntentStatusNames.Work) =>
            Create(new MutableClock(now), status);

        public static Rig Create(MutableClock clock, string status = IntentStatusNames.Work)
        {
            var repo = Substitute.For<IIntentRepository>();
            repo.GetByIdAsync(Arg.Any<IntentId>(), Arg.Any<CancellationToken>())
                .Returns(ci => Intent.Restore(ci.ArgAt<IntentId>(0), "x", status, 1, [], clock.GetUtcNow(), clock.GetUtcNow()));
            repo.SetStatusAsync(
                    Arg.Any<IntentId>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                    Arg.Any<IntentTrainingAuthor>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(ci => new SetIntentStatusOutcome.Updated(
                    Intent.Restore(ci.ArgAt<IntentId>(0), "x", ci.ArgAt<string>(1), 1, [], clock.GetUtcNow(), clock.GetUtcNow())));

            var events = new List<IDomainEvent>();
            var dispatcher = Substitute.For<IDomainEventDispatcher>();
            dispatcher.DispatchAsync(Arg.Do<IDomainEvent>(events.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

            var tmux = Substitute.For<ITmuxSessionManager>();
            var resumer = Substitute.For<IVendorLimitSessionResumer>();
            var pauses = new InMemoryVendorLimitPauseStore();
            var setStatus = new SetIntentStatusHandler(repo, new PassthroughUnitOfWork(), clock);
            var sweep = new VendorLimitResumeSweep(pauses, tmux, resumer, repo, setStatus, dispatcher, Options, clock);
            return new Rig { Sweep = sweep, Pauses = pauses, Resumer = resumer, Tmux = tmux, Repo = repo, Events = events };
        }
    }

    private sealed class PassthroughUnitOfWork : IUnitOfWork
    {
        public Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken ct) => work(ct);
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct) => work(ct);
        public Task<T> ExecuteOutsideTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct) => work(ct);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
