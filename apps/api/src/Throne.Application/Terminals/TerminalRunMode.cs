namespace Throne.Application.Terminals;

/// <summary>
/// Embedded run modes. They select which mandatory parts the pre-flight preview projects and the
/// spawn phase the status hooks return to (ADR-0034/0035). The embedded contour injects the
/// curated system/user context upfront rather than asking the agent to read a bundle;
/// <see cref="Free"/> has no mandatory parts (the operator curates everything).
/// <see cref="Orchestrator"/> drives a tag-wide intent from its own intent body (ADR-0054).
/// <see cref="Verify"/> is the independent reviewer of an executor's branch: it boots from a
/// review intent that carries only the DoD, the problem and the branch (ADR-0054 §8).
/// </summary>
public static class TerminalRunModes
{
    public const string Work = "work";
    public const string Interview = "interview";
    public const string Review = "review";
    public const string Dream = "dream";
    public const string Free = "free";
    public const string Orchestrator = "orchestrator";
    public const string Verify = "verify";

    public static readonly IReadOnlyList<string> All = [Interview, Review, Work, Free, Dream, Orchestrator, Verify];

    public static bool IsKnown(string value) =>
        value is Work or Interview or Review or Dream or Free or Orchestrator or Verify;
}
