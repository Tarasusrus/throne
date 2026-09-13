using FluentAssertions;
using Throne.Application.Terminals;

namespace Throne.Application.Tests.Terminals;

/// <summary>
/// Политика паузы по лимиту вендора (ADR-0055): переход в паузу, повтор сразу после
/// возобновления — новая пауза со счётчиком, потолок попыток — эскалация; тик сторожа —
/// ничего / толкнуть / перезапустить / эскалировать. Свойства по сгенерированным сценариям.
/// </summary>
public class VendorLimitPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 5, 0, 0, TimeSpan.Zero);

    private static readonly VendorLimitPolicyOptions Options = new(
        DefaultRetry: TimeSpan.FromMinutes(30),
        RepeatWindow: TimeSpan.FromMinutes(15),
        MaxAttempts: 3,
        ResumeGrace: TimeSpan.FromSeconds(90),
        NudgeInterval: TimeSpan.FromMinutes(2),
        MaxNudges: 3);

    private static VendorLimitSignal Hit(DateTimeOffset? resetAt) =>
        new(VendorLimitSignalKind.Hit, resetAt, resetAt is null ? null : "UTC", Percent: null, Raw: "You've hit your monthly spend limit");

    [Fact(DisplayName = "Первый лимит: пауза до времени сброса, попытка 1")]
    public void First_hit_pauses_until_reset()
    {
        var reset = Now.AddHours(2);

        var decision = VendorLimitPolicy.OnHit("i1", "claude", previous: null, Hit(reset), Now, Options);

        var pause = decision.Should().BeOfType<VendorLimitDecision.Pause>().Subject.Value;
        pause.IntentId.Should().Be("i1");
        pause.ResumeAt.Should().Be(reset);
        pause.Attempts.Should().Be(1);
        pause.ResumedAt.Should().BeNull();
    }

    [Fact(DisplayName = "Лимит без времени сброса: пауза на дефолтный интервал повтора")]
    public void Hit_without_reset_uses_default_retry()
    {
        var decision = VendorLimitPolicy.OnHit("i1", "claude", previous: null, Hit(null), Now, Options);

        decision.Should().BeOfType<VendorLimitDecision.Pause>()
            .Which.Value.ResumeAt.Should().Be(Now + Options.DefaultRetry);
    }

    [Fact(DisplayName = "Повторный лимит сразу после возобновления — новая пауза, попытка +1")]
    public void Repeat_right_after_resume_increments_attempts()
    {
        var first = Pause(attempts: 1, resumedAt: Now.AddMinutes(-1));

        var decision = VendorLimitPolicy.OnHit("i1", "claude", first, Hit(Now.AddHours(1)), Now, Options);

        decision.Should().BeOfType<VendorLimitDecision.Pause>().Which.Value.Attempts.Should().Be(2);
    }

    [Fact(DisplayName = "Лимит после долгой работы — не повтор: счётчик начинается заново")]
    public void Hit_long_after_resume_resets_attempts()
    {
        var first = Pause(attempts: 2, resumedAt: Now - Options.RepeatWindow - TimeSpan.FromSeconds(1));

        var decision = VendorLimitPolicy.OnHit("i1", "claude", first, Hit(Now.AddHours(1)), Now, Options);

        decision.Should().BeOfType<VendorLimitDecision.Pause>().Which.Value.Attempts.Should().Be(1);
    }

    [Fact(DisplayName = "Потолок попыток: следующий повтор — эскалация оператору с причиной")]
    public void Ceiling_escalates()
    {
        var previous = Pause(attempts: Options.MaxAttempts, resumedAt: Now.AddSeconds(-30));

        var decision = VendorLimitPolicy.OnHit("i1", "claude", previous, Hit(Now.AddHours(1)), Now, Options);

        var escalate = decision.Should().BeOfType<VendorLimitDecision.Escalate>().Subject;
        escalate.Reason.Should().Contain((Options.MaxAttempts + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact(DisplayName = "Тик до resume_at + люфт — ничего не делать")]
    public void Tick_before_grace_does_nothing()
    {
        var pause = Pause(attempts: 1, resumedAt: null, resumeAt: Now.AddMinutes(10));

        VendorLimitPolicy.OnTick(pause, Now.AddMinutes(10) + Options.ResumeGrace - TimeSpan.FromSeconds(1), tmuxAlive: true, Options)
            .Should().Be(VendorLimitTickAction.None);
    }

    [Fact(DisplayName = "Тик после люфта при живом tmux — толкнуть сессию")]
    public void Tick_after_grace_nudges_live_session()
    {
        var pause = Pause(attempts: 1, resumedAt: null, resumeAt: Now);

        VendorLimitPolicy.OnTick(pause, Now + Options.ResumeGrace, tmuxAlive: true, Options)
            .Should().Be(VendorLimitTickAction.Nudge);
    }

    [Fact(DisplayName = "Тик после люфта при мёртвом tmux — перезапуск с контекстом")]
    public void Tick_after_grace_relaunches_dead_session()
    {
        var pause = Pause(attempts: 1, resumedAt: null, resumeAt: Now);

        VendorLimitPolicy.OnTick(pause, Now + Options.ResumeGrace, tmuxAlive: false, Options)
            .Should().Be(VendorLimitTickAction.Relaunch);
    }

    [Fact(DisplayName = "Уже возобновлённая пауза тик не трогает")]
    public void Resumed_pause_is_inert()
    {
        var pause = Pause(attempts: 1, resumedAt: Now, resumeAt: Now.AddHours(-1));

        VendorLimitPolicy.OnTick(pause, Now.AddHours(1), tmuxAlive: true, Options)
            .Should().Be(VendorLimitTickAction.None);
    }

    [Fact(DisplayName = "Толчки не чаще NudgeInterval, после MaxNudges — эскалация")]
    public void Nudges_are_rate_limited_and_capped()
    {
        var pause = Pause(attempts: 1, resumedAt: null, resumeAt: Now) with
        {
            LastNudgeAt = Now + Options.ResumeGrace,
            Nudges = 1,
        };
        var soon = Now + Options.ResumeGrace + TimeSpan.FromSeconds(10);
        VendorLimitPolicy.OnTick(pause, soon, tmuxAlive: true, Options).Should().Be(VendorLimitTickAction.None);

        var later = Now + Options.ResumeGrace + Options.NudgeInterval;
        VendorLimitPolicy.OnTick(pause, later, tmuxAlive: true, Options).Should().Be(VendorLimitTickAction.Nudge);

        var exhausted = pause with { Nudges = Options.MaxNudges };
        VendorLimitPolicy.OnTick(exhausted, later, tmuxAlive: true, Options).Should().Be(VendorLimitTickAction.Escalate);
    }

    // ---- Свойства ------------------------------------------------------------------------

    private const int Cases = 300;

    private sealed record Scenario(int Seed, VendorLimitPause? Previous, VendorLimitSignal Signal, DateTimeOffset At);

    private static Scenario Generate(int seed)
    {
        var rng = new Random(seed);
        var at = Now.AddMinutes(rng.Next(0, 24 * 60));
        VendorLimitPause? previous = null;
        if (rng.Next(3) != 0)
        {
            var attempts = rng.Next(1, Options.MaxAttempts + 2);
            DateTimeOffset? resumedAt = rng.Next(4) == 0
                ? null
                : at - TimeSpan.FromSeconds(rng.Next(0, (int)Options.RepeatWindow.TotalSeconds * 3));
            previous = Pause(attempts, resumedAt, resumeAt: at.AddMinutes(-rng.Next(0, 120)));
        }
        DateTimeOffset? reset = rng.Next(4) == 0 ? null : at.AddMinutes(rng.Next(-4, 24 * 60));
        return new Scenario(seed, previous, Hit(reset), at);
    }

    private static bool IsRepeat(Scenario s) =>
        s.Previous is not null
        && (s.Previous.ResumedAt is null || s.At - s.Previous.ResumedAt.Value <= Options.RepeatWindow);

    [Fact(DisplayName = "Свойство: счётчик попыток растёт ровно на повторе в окне, иначе 1; эскалация ⇔ счётчик > потолка")]
    public void Attempts_grow_only_on_repeat_and_escalate_past_ceiling()
    {
        var escalations = 0;
        for (var seed = 0; seed < Cases; seed++)
        {
            var s = Generate(seed);
            var expectedAttempts = IsRepeat(s) ? s.Previous!.Attempts + 1 : 1;
            var decision = VendorLimitPolicy.OnHit("i1", "claude", s.Previous, s.Signal, s.At, Options);
            var why = $"seed {seed}: {s}";

            if (expectedAttempts > Options.MaxAttempts)
            {
                decision.Should().BeOfType<VendorLimitDecision.Escalate>(why);
                escalations++;
                continue;
            }

            var pause = decision.Should().BeOfType<VendorLimitDecision.Pause>(why).Subject.Value;
            pause.Attempts.Should().Be(expectedAttempts, why);
            pause.ResumedAt.Should().BeNull(why);
            pause.Nudges.Should().Be(0, why);
            pause.LastNudgeAt.Should().BeNull(why);
            pause.DetectedAt.Should().Be(s.At, why);
            pause.ResumeAt.Should().Be(s.Signal.ResetAt ?? s.At + Options.DefaultRetry, why);
            pause.Message.Should().Be(s.Signal.Raw, why);
        }
        escalations.Should().BeGreaterThan(0, "генератор обязан доходить до потолка");
    }

    [Fact(DisplayName = "Свойство: тик — None до люфта; после люфта Relaunch при мёртвом tmux, Nudge/None/Escalate по толчкам при живом")]
    public void Tick_action_follows_grace_liveness_and_nudge_budget()
    {
        var seen = new HashSet<VendorLimitTickAction>();
        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            var resumeAt = Now.AddMinutes(rng.Next(-60, 60));
            var pause = Pause(1, resumedAt: rng.Next(5) == 0 ? Now : null, resumeAt) with
            {
                Nudges = rng.Next(0, Options.MaxNudges + 1),
                LastNudgeAt = rng.Next(2) == 0 ? null : Now.AddSeconds(-rng.Next(0, 300)),
            };
            var alive = rng.Next(4) != 0;
            var action = VendorLimitPolicy.OnTick(pause, Now, alive, Options);
            seen.Add(action);
            var why = $"seed {seed}: {pause} alive={alive}";

            if (pause.ResumedAt is not null || Now < pause.ResumeAt + Options.ResumeGrace)
            {
                action.Should().Be(VendorLimitTickAction.None, why);
                continue;
            }
            if (!alive)
            {
                action.Should().Be(VendorLimitTickAction.Relaunch, why);
                continue;
            }
            if (pause.LastNudgeAt is not null && Now - pause.LastNudgeAt.Value < Options.NudgeInterval)
            {
                action.Should().Be(VendorLimitTickAction.None, why);
                continue;
            }
            action.Should().Be(
                pause.Nudges >= Options.MaxNudges ? VendorLimitTickAction.Escalate : VendorLimitTickAction.Nudge, why);
        }
        seen.Should().BeEquivalentTo(Enum.GetValues<VendorLimitTickAction>(), "генератор обязан покрыть все действия");
    }

    private static VendorLimitPause Pause(int attempts, DateTimeOffset? resumedAt, DateTimeOffset? resumeAt = null) =>
        new(
            IntentId: "i1",
            Vendor: "claude",
            DetectedAt: Now.AddHours(-1),
            ResumeAt: resumeAt ?? Now,
            Attempts: attempts,
            Message: "You've hit your monthly spend limit",
            ResumedAt: resumedAt,
            LastNudgeAt: null,
            Nudges: 0);
}
