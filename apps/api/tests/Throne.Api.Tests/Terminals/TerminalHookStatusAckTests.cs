using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Throne.Api.Terminals;
using Throne.Application.Terminals;
using Throne.Terminal.Contracts.Generated;

namespace Throne.Api.Tests.Terminals;

public class TerminalHookStatusAckTests
{
    [Fact(DisplayName = "Hook ack публикует событие в terminal-hook шину")]
    public async Task Publishes_terminal_hook_event()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero));
        var bus = new RecordingTerminalHookBus();
        var ack = new TerminalHookStatusAck(bus, clock, NullLogger<TerminalHookStatusAck>.Instance);

        await ack.HandleAsync(
            "intent-1",
            Event.UserPromptSubmit,
            TerminalRunMode.Review,
            payload: null,
            CancellationToken.None);

        bus.Events.Should().ContainSingle().Which.Should().Be(
            new TerminalHookEvent(
                "intent-1",
                TerminalHookEvents.UserPromptSubmit,
                TerminalRunModes.Review,
                clock.GetUtcNow()));
    }

    [Fact(DisplayName = "StopFailure с телом хука едет в шину вместе с payload (лимит вендора, ADR-0055)")]
    public async Task Publishes_stop_failure_with_payload()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 13, 5, 0, 0, TimeSpan.Zero));
        var bus = new RecordingTerminalHookBus();
        var ack = new TerminalHookStatusAck(bus, clock, NullLogger<TerminalHookStatusAck>.Instance);
        var payload = new TerminalHookPayload("rate_limit", "You've hit your monthly spend limit", null, null);

        await ack.HandleAsync("intent-1", Event.StopFailure, TerminalRunMode.Work, payload, CancellationToken.None);

        bus.Events.Should().ContainSingle().Which.Should().Be(
            new TerminalHookEvent("intent-1", TerminalHookEvents.StopFailure, TerminalRunModes.Work, clock.GetUtcNow(), payload));
    }

    private sealed class RecordingTerminalHookBus : ITerminalHookBus
    {
        public List<TerminalHookEvent> Events { get; } = [];

        public ValueTask PublishAsync(TerminalHookEvent hook, CancellationToken ct)
        {
            Events.Add(hook);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
