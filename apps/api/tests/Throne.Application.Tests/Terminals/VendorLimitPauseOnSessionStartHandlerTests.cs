using FluentAssertions;
using Throne.Application.Events;
using Throne.Application.Terminals;

namespace Throne.Application.Tests.Terminals;

/// <summary>
/// Новый спавн (оператор перезапустил интент) снимает старую паузу: иначе сторож толкнёт
/// свежую сессию по чужому расписанию (ADR-0055).
/// </summary>
public class VendorLimitPauseOnSessionStartHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 5, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "TerminalSessionStarted убирает запись паузы своего интента и не трогает чужие")]
    public async Task Session_started_clears_own_pause()
    {
        var pauses = new InMemoryVendorLimitPauseStore();
        pauses.Put(new VendorLimitPause("i1", "claude", Now, Now.AddHours(1), 1, "limit", null, null, 0));
        pauses.Put(new VendorLimitPause("i2", "claude", Now, Now.AddHours(1), 1, "limit", null, null, 0));
        var handler = new VendorLimitPauseOnSessionStartHandler(pauses);

        await handler.HandleAsync(new TerminalSessionStarted("i1"), CancellationToken.None);

        pauses.Find("i1").Should().BeNull();
        pauses.Find("i2").Should().NotBeNull();
    }

    [Fact(DisplayName = "Остановка сессии паузу не снимает — умершую в паузе сессию сторож перезапускает")]
    public async Task Session_stopped_keeps_pause()
    {
        var pauses = new InMemoryVendorLimitPauseStore();
        pauses.Put(new VendorLimitPause("i1", "claude", Now, Now.AddHours(1), 1, "limit", null, null, 0));
        var handler = new VendorLimitPauseOnSessionStartHandler(pauses);

        await handler.HandleAsync(new TerminalSessionStopped("i1"), CancellationToken.None);

        pauses.Find("i1").Should().NotBeNull();
    }
}
