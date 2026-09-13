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
/// Хук-подписчик лимита вендора (ADR-0055): <c>StopFailure error=rate_limit</c> → пауза с
/// временем сброса из сообщения; <c>Notification quota_auto_resume_fired</c>, <c>PostToolUse</c>,
/// <c>UserPromptSubmit</c> → возобновление; повтор в окне копит попытки, потолок → awaiting_operator
/// с причиной. Статус интента пауза не трогает.
/// </summary>
public class VendorLimitHookHandlerTests
{
    // 2026-09-13 12:00 Asia/Bangkok.
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 5, 0, 0, TimeSpan.Zero);

    private const string RealHit =
        "You've hit your monthly spend limit · raise it at claude.ai/settings/usage?from=cc_cli_limit_message · your session limit resets 1:40pm (Asia/Bangkok)";

    private static readonly VendorLimitPolicyOptions Options = VendorLimitPolicyOptions.Default with { MaxAttempts = 2 };

    [Fact(DisplayName = "StopFailure rate_limit с реальной строкой: активная пауза до 13:40 Bangkok, попытка 1, статус не тронут, событие паузы ушло")]
    public async Task Rate_limit_stop_failure_pauses()
    {
        var rig = Rig.Create();

        await rig.Handler.HandleAsync(Rig.StopFailure(RealHit), CancellationToken.None);

        var pause = rig.Pauses.Find("intent-1");
        pause.Should().NotBeNull();
        pause!.IsActive.Should().BeTrue();
        pause.ResumeAt.Should().Be(new DateTimeOffset(2026, 9, 13, 13, 40, 0, TimeSpan.FromHours(7)));
        pause.Attempts.Should().Be(1);
        pause.Vendor.Should().Be(TerminalAgentCatalog.VendorClaude);
        await rig.NoStatusSet();
        rig.Events.Should().ContainSingle(e => e is TerminalLimitPaused)
            .Which.As<TerminalLimitPaused>().ResumeAt.Should().Be(pause.ResumeAt);
    }

    [Fact(DisplayName = "StopFailure rate_limit без текста: пауза на дефолтный интервал")]
    public async Task Rate_limit_without_message_uses_default_retry()
    {
        var rig = Rig.Create();

        await rig.Handler.HandleAsync(Rig.StopFailure(message: null), CancellationToken.None);

        rig.Pauses.Find("intent-1")!.ResumeAt.Should().Be(Now + Options.DefaultRetry);
    }

    [Fact(DisplayName = "StopFailure с другой ошибкой (server_error) паузу не заводит")]
    public async Task Other_stop_failure_is_ignored()
    {
        var rig = Rig.Create();

        await rig.Handler.HandleAsync(Rig.StopFailure("Internal server error", error: "server_error"), CancellationToken.None);

        rig.Pauses.Find("intent-1").Should().BeNull();
        rig.Events.Should().BeEmpty();
    }

    [Theory(DisplayName = "Сигнал возобновления снимает паузу: quota_auto_resume_fired, PostToolUse, UserPromptSubmit")]
    [InlineData(TerminalHookEvents.Notification, "quota_auto_resume_fired")]
    [InlineData(TerminalHookEvents.PostToolUse, null)]
    [InlineData(TerminalHookEvents.UserPromptSubmit, null)]
    public async Task Resume_signals_end_the_pause(string hookEvent, string? notificationType)
    {
        var rig = Rig.Create();
        await rig.Handler.HandleAsync(Rig.StopFailure(RealHit), CancellationToken.None);

        await rig.Handler.HandleAsync(Rig.Hook(hookEvent, notificationType: notificationType), CancellationToken.None);

        var pause = rig.Pauses.Find("intent-1");
        pause.Should().NotBeNull("счётчик попыток живёт до конца окна повтора");
        pause!.IsActive.Should().BeFalse();
        pause.ResumedAt.Should().Be(Now);
        rig.Events.Should().ContainSingle(e => e is TerminalLimitResumed);
    }

    [Fact(DisplayName = "PostToolUse без паузы — тишина: ни записи, ни события")]
    public async Task Tool_use_without_pause_is_noop()
    {
        var rig = Rig.Create();

        await rig.Handler.HandleAsync(Rig.Hook(TerminalHookEvents.PostToolUse), CancellationToken.None);

        rig.Pauses.Find("intent-1").Should().BeNull();
        rig.Events.Should().BeEmpty();
    }

    [Fact(DisplayName = "Notification permission_prompt паузу не трогает — это парковка, не лимит")]
    public async Task Permission_notification_does_not_touch_pause()
    {
        var rig = Rig.Create();
        await rig.Handler.HandleAsync(Rig.StopFailure(RealHit), CancellationToken.None);

        await rig.Handler.HandleAsync(
            Rig.Hook(TerminalHookEvents.Notification, notificationType: "permission_prompt"), CancellationToken.None);

        rig.Pauses.Find("intent-1")!.IsActive.Should().BeTrue();
    }

    [Fact(DisplayName = "Повтор сразу после возобновления — новая пауза, попытка 2; потолок — awaiting_operator с причиной, запись снята")]
    public async Task Repeat_counts_and_ceiling_escalates()
    {
        var rig = Rig.Create();

        await rig.Handler.HandleAsync(Rig.StopFailure(RealHit), CancellationToken.None);
        await rig.Handler.HandleAsync(Rig.Hook(TerminalHookEvents.PostToolUse), CancellationToken.None);
        await rig.Handler.HandleAsync(Rig.StopFailure(RealHit), CancellationToken.None);
        rig.Pauses.Find("intent-1")!.Attempts.Should().Be(2);
        await rig.NoStatusSet();

        await rig.Handler.HandleAsync(Rig.Hook(TerminalHookEvents.PostToolUse), CancellationToken.None);
        await rig.Handler.HandleAsync(Rig.StopFailure(RealHit), CancellationToken.None);

        rig.Pauses.Find("intent-1").Should().BeNull();
        await rig.Repo.Received(1).SetStatusAsync(
            Arg.Any<IntentId>(),
            Arg.Is(IntentStatusNames.AwaitingOperator),
            Arg.Is<string?>(t => t == null),
            Arg.Is<string?>(r => r != null && r.Contains('3') && r.Contains("monthly spend limit", StringComparison.Ordinal)),
            Arg.Is(IntentTrainingAuthor.System),
            Arg.Is(VendorLimitHookHandler.EscalationSource),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Закрытый интент (done) лимит не воскрешает: ни паузы, ни статуса")]
    public async Task Terminal_intent_is_left_alone()
    {
        var rig = Rig.Create(status: IntentStatusNames.Done);

        await rig.Handler.HandleAsync(Rig.StopFailure(RealHit), CancellationToken.None);

        rig.Pauses.Find("intent-1").Should().BeNull();
        await rig.NoStatusSet();
    }

    [Fact(DisplayName = "Вендор для таблицы паттернов берётся из записи запуска: codex-сессия Claude-строку не распознаёт, но rate_limit всё равно паузит на дефолт")]
    public async Task Vendor_comes_from_launch_record()
    {
        var rig = Rig.Create(vendor: TerminalAgentCatalog.VendorCodex);

        await rig.Handler.HandleAsync(Rig.StopFailure(RealHit), CancellationToken.None);

        var pause = rig.Pauses.Find("intent-1")!;
        pause.Vendor.Should().Be(TerminalAgentCatalog.VendorCodex);
        pause.ResumeAt.Should().Be(Now + Options.DefaultRetry, "строка чужого вендора не парсится — время сброса неизвестно");
    }

    // ---- Свойство: случайная последовательность событий ----------------------------------

    private const int Cases = 200;

    [Fact(DisplayName = "Свойство: после любой последовательности hit/resume/tool пауза активна ⇔ последний hit не закрыт возобновлением; попытки ≤ потолка; эскалация ровно при переполнении")]
    public async Task Random_event_sequences_keep_invariants()
    {
        var escalated = 0;
        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            var rig = Rig.Create();
            var expectedActive = false;
            var expectedAttempts = 0;
            var expectEscalated = false;
            var steps = rng.Next(1, 8);
            var log = new List<string>();

            for (var i = 0; i < steps && !expectEscalated; i++)
            {
                switch (rng.Next(3))
                {
                    case 0:
                        log.Add("hit");
                        await rig.Handler.HandleAsync(Rig.StopFailure(RealHit), CancellationToken.None);
                        // Все шаги в один момент времени — любой повтор попадает в окно.
                        expectedAttempts++;
                        if (expectedAttempts > Options.MaxAttempts)
                        {
                            expectEscalated = true;
                        }
                        else
                        {
                            expectedActive = true;
                        }
                        break;
                    case 1:
                        log.Add("resume");
                        await rig.Handler.HandleAsync(
                            Rig.Hook(TerminalHookEvents.Notification, notificationType: "quota_auto_resume_fired"),
                            CancellationToken.None);
                        expectedActive = false;
                        break;
                    default:
                        log.Add("tool");
                        await rig.Handler.HandleAsync(Rig.Hook(TerminalHookEvents.PostToolUse), CancellationToken.None);
                        expectedActive = false;
                        break;
                }
            }

            var why = $"seed {seed}: {string.Join(" → ", log)}";
            var pause = rig.Pauses.Find("intent-1");
            if (expectEscalated)
            {
                escalated++;
                pause.Should().BeNull(why);
                await rig.Repo.Received(1).SetStatusAsync(
                    Arg.Any<IntentId>(), IntentStatusNames.AwaitingOperator, Arg.Any<string?>(), Arg.Any<string?>(),
                    Arg.Any<IntentTrainingAuthor>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
                continue;
            }
            await rig.NoStatusSet();
            (pause?.IsActive ?? false).Should().Be(expectedActive, why);
            if (pause is not null)
            {
                pause.Attempts.Should().Be(expectedAttempts, why);
                pause.Attempts.Should().BeLessThanOrEqualTo(Options.MaxAttempts, why);
            }
            else
            {
                expectedAttempts.Should().Be(0, why);
            }
        }
        escalated.Should().BeGreaterThan(0, "генератор обязан доходить до потолка");
    }

    private sealed class Rig
    {
        public required VendorLimitHookHandler Handler { get; init; }
        public required IVendorLimitPauseStore Pauses { get; init; }
        public required IIntentRepository Repo { get; init; }
        public required List<IDomainEvent> Events { get; init; }

        public static TerminalHookEvent StopFailure(string? message, string error = "rate_limit") =>
            new("intent-1", TerminalHookEvents.StopFailure, TerminalRunModes.Work, Now,
                new TerminalHookPayload(Error: error, LastAssistantMessage: message, NotificationType: null, Message: null));

        public static TerminalHookEvent Hook(string hookEvent, string? notificationType = null) =>
            new("intent-1", hookEvent, TerminalRunModes.Work, Now,
                new TerminalHookPayload(Error: null, LastAssistantMessage: null, NotificationType: notificationType, Message: null));

        public Task NoStatusSet() =>
            Repo.DidNotReceive().SetStatusAsync(
                Arg.Any<IntentId>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<IntentTrainingAuthor>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        public static Rig Create(string status = IntentStatusNames.Work, string vendor = TerminalAgentCatalog.VendorClaude)
        {
            var repo = Substitute.For<IIntentRepository>();
            repo.GetByIdAsync(Arg.Any<IntentId>(), Arg.Any<CancellationToken>())
                .Returns(ci => Intent.Restore(ci.ArgAt<IntentId>(0), "x", status, 1, [], Now, Now));
            repo.SetStatusAsync(
                    Arg.Any<IntentId>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                    Arg.Any<IntentTrainingAuthor>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(ci => new SetIntentStatusOutcome.Updated(
                    Intent.Restore(ci.ArgAt<IntentId>(0), "x", ci.ArgAt<string>(1), 1, [], Now, Now)));

            var launches = Substitute.For<IIntentTerminalLaunchStore>();
            launches.GetAsync("intent-1", Arg.Any<CancellationToken>())
                .Returns(new TerminalLaunchRecord(TerminalRunModes.Work, vendor, "m", null,
                    new Dictionary<string, IReadOnlyList<string>>()));

            var events = new List<IDomainEvent>();
            var dispatcher = Substitute.For<IDomainEventDispatcher>();
            dispatcher.DispatchAsync(Arg.Do<IDomainEvent>(events.Add), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            var pauses = new InMemoryVendorLimitPauseStore();
            var clock = new FixedClock(Now);
            var setStatus = new SetIntentStatusHandler(repo, new PassthroughUnitOfWork(), clock);
            var handler = new VendorLimitHookHandler(pauses, launches, repo, setStatus, dispatcher, Options, clock);
            return new Rig { Handler = handler, Pauses = pauses, Repo = repo, Events = events };
        }
    }

    private sealed class PassthroughUnitOfWork : IUnitOfWork
    {
        public Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken ct) => work(ct);
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct) => work(ct);
        public Task<T> ExecuteOutsideTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct) => work(ct);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
