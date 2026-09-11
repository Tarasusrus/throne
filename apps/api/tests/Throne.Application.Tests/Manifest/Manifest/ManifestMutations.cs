namespace Throne.Application.Tests.Manifest.Manifest;

internal enum ManifestDefect
{
    DropReferencedSystemInstruction,
    DuplicateSystemKind,
    BlankSystemText,
    EmptySystemKind,
    UnknownIncludeScope,
    EmptyBundleMode,
    NoIncludes,
    UnsupportedVersion,
}

/// <summary>
/// One defect at a time, injected into an otherwise well-formed manifest. Every defect listed here
/// is one the validator claims to catch — the property is that none of them ever slips through.
/// </summary>
internal static class ManifestMutations
{
    public static readonly ManifestDefect[] All = Enum.GetValues<ManifestDefect>();

    public static ManifestModel Apply(ManifestModel model, ManifestDefect defect, Random rng) => defect switch
    {
        ManifestDefect.DropReferencedSystemInstruction => DropReferenced(model),
        ManifestDefect.DuplicateSystemKind => model with
        {
            SystemInstructions = [.. model.SystemInstructions, model.SystemInstructions[0]],
        },
        ManifestDefect.BlankSystemText => ReplaceInstruction(model, rng, (kind, _) => (kind, "")),
        ManifestDefect.EmptySystemKind => ReplaceInstruction(model, rng, (_, text) => ("", text)),
        ManifestDefect.UnknownIncludeScope => MapFirstInclude(model, inc => ("bogus", inc.Kind)),
        ManifestDefect.EmptyBundleMode => model with
        {
            Bundles = [("", model.Bundles[0].Includes), .. model.Bundles.Skip(1)],
        },
        ManifestDefect.NoIncludes => model with
        {
            Bundles = [(model.Bundles[0].Mode, Array.Empty<(string, string)>()), .. model.Bundles.Skip(1)],
        },
        ManifestDefect.UnsupportedVersion => model with { Version = 2 },
        _ => throw new ArgumentOutOfRangeException(nameof(defect)),
    };

    private static ManifestModel DropReferenced(ManifestModel model)
    {
        var referenced = model.Bundles[0].Includes.First(i => i.Scope == "system").Kind;
        return model with
        {
            SystemInstructions = [.. model.SystemInstructions.Where(i => i.Kind != referenced)],
        };
    }

    private static ManifestModel ReplaceInstruction(
        ManifestModel model,
        Random rng,
        Func<string, string, (string, string)> map)
    {
        var index = rng.Next(model.SystemInstructions.Count);
        var updated = model.SystemInstructions.ToArray();
        updated[index] = map(updated[index].Kind, updated[index].Text);
        return model with { SystemInstructions = updated };
    }

    private static ManifestModel MapFirstInclude(
        ManifestModel model,
        Func<(string Scope, string Kind), (string, string)> map)
    {
        var bundles = model.Bundles.ToArray();
        var includes = bundles[0].Includes.ToArray();
        includes[0] = map(includes[0]);
        bundles[0] = (bundles[0].Mode, includes);
        return model with { Bundles = bundles };
    }
}
