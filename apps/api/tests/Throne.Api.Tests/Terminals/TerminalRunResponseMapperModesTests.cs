using FluentAssertions;
using Throne.Api.Terminals;
using Throne.Application.Terminals;

namespace Throne.Api.Tests.Terminals;

/// <summary>
/// Персистнутая ось запуска (ADR-0041) читается обратно через <c>ParseRunMode</c>, который бросает
/// на неизвестном режиме. Забытая ветка в нём означает 500 на <c>GET /terminal/session</c> после
/// первого же спавна — компилятор такой пропуск не ловит, поэтому режимы проверяются списком.
/// </summary>
public class TerminalRunResponseMapperModesTests
{
    [Fact(DisplayName = "Каждый известный режим переживает round-trip через launch-эхо")]
    public void Every_known_mode_round_trips_through_launch_echo()
    {
        foreach (var mode in TerminalRunModes.All)
        {
            var dto = TerminalRunResponseMapper.ToDto(ResultWith(mode));

            dto.Launch.Should().NotBeNull();
            TerminalRunResponseMapper.ToDomainMode(dto.Launch!.Mode).Should().Be(mode);
        }
    }

    private static RunPreflightResult ResultWith(string mode) => new(
        IntentId: "intent-1",
        SessionName: "throne-intent-1",
        SessionState: "running",
        Bindings: [],
        BlockingBindings: [],
        Launch: new TerminalLaunchRecord(
            Mode: mode,
            Vendor: "claude",
            Model: "opus",
            Effort: null,
            SelectedSkillIdsByMode: new Dictionary<string, IReadOnlyList<string>>()));
}
