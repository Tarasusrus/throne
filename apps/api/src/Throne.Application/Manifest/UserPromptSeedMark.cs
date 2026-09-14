namespace Throne.Application.Manifest;

/// <summary>
/// What the instance was last seeded with for one user-part key (ADR-0051 amendment).
/// The merge compares the live text against <see cref="SeededText"/> to tell «untouched
/// since seeding» from «edited by the operator»; the seed text itself may have moved on.
/// </summary>
public sealed record UserPromptSeedMark(string Key, string SeededText, DateTimeOffset SeededAt);
