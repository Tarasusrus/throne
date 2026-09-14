using Throne.Domain.PromptParts;

namespace Throne.Application.Manifest;

/// <summary>
/// One seed part whose live counterpart follows the seed: text and roles
/// are replaced because the live text still equals what was seeded last time.
/// </summary>
public sealed record UserPromptSeedUpdate(PromptPart Live, UserPromptSeedPart Seed);

/// <summary>
/// Result of <see cref="UserPromptSeedMerge.Plan"/>. Every seed key lands in at most one
/// bucket; a key already in sync and already marked lands in none. Keys the seed does
/// not know never appear — the merge neither deletes nor edits operator-only parts.
/// </summary>
public sealed record UserPromptSeedMergePlan(
    IReadOnlyList<UserPromptSeedPart> Create,
    IReadOnlyList<UserPromptSeedUpdate> Update,
    IReadOnlyList<string> Adopt,
    IReadOnlyList<string> Drift);

/// <summary>
/// Merge rule of the user-prompt seed (ADR-0051 amendment): the repository seed is the
/// canon, but an operator edit on the instance wins over it. The discriminator is the text
/// the instance was seeded with last time (<see cref="UserPromptSeedMark"/>):
/// <list type="bullet">
/// <item>no live part → <c>Create</c>;</item>
/// <item>live text equals the last seeded text (nobody edited it) → <c>Update</c> to the new
/// seed, skipped when text and roles already match;</item>
/// <item>live text equals the seed already (the operator converged by hand, or a row seeded
/// before marks existed) → follows the seed as above; when nothing differs only the mark is
/// written → <c>Adopt</c>;</item>
/// <item>otherwise the part was hand-edited and diverged → kept, reported as <c>Drift</c>.</item>
/// </list>
/// Text comparison ignores line-ending flavour and trailing whitespace: a YAML block
/// scalar and a UI edit legitimately differ only there.
/// </summary>
public static class UserPromptSeedMerge
{
    public static UserPromptSeedMergePlan Plan(
        UserPromptSeed seed,
        IReadOnlyList<PromptPart> live,
        IReadOnlyDictionary<string, string> lastSeeded)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(lastSeeded);

        var byKey = live.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var create = new List<UserPromptSeedPart>();
        var update = new List<UserPromptSeedUpdate>();
        var adopt = new List<string>();
        var drift = new List<string>();

        foreach (var part in seed.Parts)
        {
            if (!byKey.TryGetValue(part.Key, out var current))
            {
                create.Add(part);
                continue;
            }

            // Untouched since the last seeding, or converged to the seed by hand: either way
            // the part follows the seed. Anything else is an operator edit — kept, reported.
            var follows = (lastSeeded.TryGetValue(part.Key, out var last) && SameText(current.Text, last))
                || SameText(current.Text, part.Text);
            if (!follows)
            {
                drift.Add(part.Key);
                continue;
            }

            if (!MatchesSeed(current, part))
            {
                update.Add(new UserPromptSeedUpdate(current, part));
            }
            else if (last is null || !SameText(last, part.Text))
            {
                adopt.Add(part.Key);
            }
        }

        return new UserPromptSeedMergePlan(create, update, adopt, drift);
    }

    /// <summary>Content equality: line endings normalised, trailing whitespace ignored.</summary>
    public static bool SameText(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    private static string Normalize(string text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    // Description is a UI label, set on create only — the merge compares text and roles.
    private static bool MatchesSeed(PromptPart live, UserPromptSeedPart seed) =>
        SameText(live.Text, seed.Text) && SameRoles(live.ModeRoles, seed.ModeRoles);

    private static bool SameRoles(IReadOnlyList<PromptPartModeRole> a, IReadOnlyList<PromptPartModeRole> b) =>
        a.OrderBy(r => r.Mode, StringComparer.Ordinal)
            .SequenceEqual(b.OrderBy(r => r.Mode, StringComparer.Ordinal));
}
