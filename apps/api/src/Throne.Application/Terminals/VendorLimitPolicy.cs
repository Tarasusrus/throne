using System.Globalization;

namespace Throne.Application.Terminals;

/// <summary>
/// Tunables of the vendor-limit pause (ADR-0055). Bound from <c>Throne:VendorLimit</c>.
/// </summary>
/// <param name="DefaultRetry">Pause length when the vendor names no reset time.</param>
/// <param name="RepeatWindow">A new hit this soon after a resume counts as a repeat (attempt +1).</param>
/// <param name="MaxAttempts">Attempts past this escalate to the operator.</param>
/// <param name="ResumeGrace">How long after the reset Throne waits for the vendor to continue on its own.</param>
/// <param name="NudgeInterval">Minimum spacing between Throne's own pushes.</param>
/// <param name="MaxNudges">Pushes past this escalate to the operator.</param>
public sealed record VendorLimitPolicyOptions(
    TimeSpan DefaultRetry,
    TimeSpan RepeatWindow,
    int MaxAttempts,
    TimeSpan ResumeGrace,
    TimeSpan NudgeInterval,
    int MaxNudges)
{
    public static readonly VendorLimitPolicyOptions Default = new(
        DefaultRetry: TimeSpan.FromMinutes(30),
        RepeatWindow: TimeSpan.FromMinutes(15),
        MaxAttempts: 3,
        ResumeGrace: TimeSpan.FromSeconds(90),
        NudgeInterval: TimeSpan.FromMinutes(2),
        MaxNudges: 3);
}

/// <summary>Outcome of a limit hit: park the session in a pause, or hand it to the operator.</summary>
public abstract record VendorLimitDecision
{
    public sealed record Pause(VendorLimitPause Value) : VendorLimitDecision;

    public sealed record Escalate(string Reason) : VendorLimitDecision;
}

/// <summary>What the sweep does with one pause on a tick.</summary>
public enum VendorLimitTickAction
{
    None,
    Nudge,
    Relaunch,
    Escalate,
}

/// <summary>
/// Pure decision rules of the vendor-limit pause. Everything with a clock or a side effect lives
/// in <see cref="VendorLimitHookHandler"/> / <see cref="VendorLimitResumeSweep"/>; this type is
/// what the property tests pin.
/// </summary>
public static class VendorLimitPolicy
{
    public static VendorLimitDecision OnHit(
        string intentId,
        string vendor,
        VendorLimitPause? previous,
        VendorLimitSignal signal,
        DateTimeOffset now,
        VendorLimitPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(options);

        var attempts = IsRepeat(previous, now, options) ? previous!.Attempts + 1 : 1;
        if (attempts > options.MaxAttempts)
        {
            return new VendorLimitDecision.Escalate(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Лимит вендора {0} раз подряд (потолок {1}); последнее сообщение: {2}",
                    attempts, options.MaxAttempts, signal.Raw));
        }

        return new VendorLimitDecision.Pause(new VendorLimitPause(
            IntentId: intentId,
            Vendor: vendor,
            DetectedAt: now,
            ResumeAt: signal.ResetAt ?? now + options.DefaultRetry,
            Attempts: attempts,
            Message: signal.Raw,
            ResumedAt: null,
            LastNudgeAt: null,
            Nudges: 0));
    }

    public static VendorLimitTickAction OnTick(
        VendorLimitPause pause,
        DateTimeOffset now,
        bool tmuxAlive,
        VendorLimitPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(options);

        if (!pause.IsActive || now < pause.ResumeAt + options.ResumeGrace)
        {
            return VendorLimitTickAction.None;
        }
        if (!tmuxAlive)
        {
            return VendorLimitTickAction.Relaunch;
        }
        if (pause.LastNudgeAt is { } last && now - last < options.NudgeInterval)
        {
            return VendorLimitTickAction.None;
        }
        return pause.Nudges >= options.MaxNudges ? VendorLimitTickAction.Escalate : VendorLimitTickAction.Nudge;
    }

    private static bool IsRepeat(VendorLimitPause? previous, DateTimeOffset now, VendorLimitPolicyOptions options) =>
        previous is not null
        && (previous.ResumedAt is null || now - previous.ResumedAt.Value <= options.RepeatWindow);
}
