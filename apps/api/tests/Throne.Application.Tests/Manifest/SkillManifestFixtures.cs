using Throne.Application.Manifest;
using Throne.Application.PromptParts;
using Throne.Domain.PromptParts;


namespace Throne.Application.Tests.Manifest;

internal static class SkillManifestFixtures
{
    public static readonly IReadOnlyList<string> Keys = ["interview", "work", "review"];

    public static SkillManifest Sample()
    {
        var systemInstructions = Keys
            .Select(key => new SystemInstructionEntry(key, $"system text for {key}"))
            .ToArray();

        BundleDefinition Bundle(string mode, string key) => new(
            Mode: mode,
            Includes:
            [
                new BundleInclude(PromptPartScopeNames.System, key),
                new BundleInclude(PromptPartScopeNames.User, "common"),
                new BundleInclude(PromptPartScopeNames.User, key),
            ]);

        var bundles = new[]
        {
            Bundle(PromptPartModeNames.Interview, "interview"),
            Bundle(PromptPartModeNames.Work, "work"),
            Bundle(PromptPartModeNames.Review, "review"),
        };

        return new SkillManifest(1, systemInstructions, bundles, Array.Empty<DreamSourceManifestEntry>());
    }

    public static InMemorySkillManifestProvider Provider() => new(Sample());

    /// <summary>
    /// Parsed repo-root manifest — the artefact the prompt contract tests assert against.
    /// The manifest is also copied into bin output by Throne.Api.csproj, so we anchor on
    /// specs/AGENTS.local.md (present only at the repo root) while walking up.
    /// </summary>
    public static SkillManifest RepoManifest() => SkillManifestParser.Parse(File.ReadAllText(RepoManifestPath()));

    public static string RepoManifestPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var manifestPath = Path.Combine(dir.FullName, "specs", "manifest", "throne-system-prompt-parts.yaml");
            var anchor = Path.Combine(dir.FullName, "specs", "AGENTS.local.md");
            if (File.Exists(manifestPath) && File.Exists(anchor))
            {
                return manifestPath;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Cannot locate repo-root throne-system-prompt-parts.yaml (looked for specs/manifest/throne-system-prompt-parts.yaml + specs/AGENTS.local.md) walking up from " +
            AppContext.BaseDirectory);
    }
}
