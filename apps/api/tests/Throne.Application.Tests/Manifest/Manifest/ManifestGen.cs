using System.Globalization;
using System.Text;

namespace Throne.Application.Tests.Manifest.Manifest;

/// <summary>
/// Seeded generator of manifest shapes for the property tests. Deliberately dependency-free:
/// every failure reports the seed that produced it, so a counterexample is replayable.
/// </summary>
internal sealed record ManifestModel(
    int Version,
    IReadOnlyList<(string Kind, string Text)> SystemInstructions,
    IReadOnlyList<(string Mode, IReadOnlyList<(string Scope, string Kind)> Includes)> Bundles);

internal static class ManifestGen
{
    private static readonly string[] KindPool =
        ["interview", "work", "review", "dream", "orchestrator", "custom_a", "custom_b"];

    private static readonly string[] UserKindPool = ["common", "interview", "work", "review"];

    private static readonly string[] LineTokens =
    [
        "Цель", "orchestrator", "`[ORCH]`", "watch", "—", "##", "Definition", "of", "Done",
        "«кавычки»", "a:b", "#hash", "1.", "-", "*", "\"quoted\"", "'single'", "{brace}", "[bracket]",
        "%percent", "&anchor", "@at", "\\backslash", "tab\there", "…", "ё", "🙂",
    ];

    /// <summary>A well-formed manifest: every documented invariant holds by construction.</summary>
    public static ManifestModel WellFormed(Random rng)
    {
        var kindCount = rng.Next(1, 6);
        var kinds = KindPool.OrderBy(_ => rng.Next()).Take(kindCount).ToArray();
        var systemInstructions = kinds.Select(k => (k, Text(rng))).ToArray();

        var bundleCount = rng.Next(1, kinds.Length + 1);
        var bundles = kinds.Take(bundleCount)
            .Select(mode => (mode, Includes(rng, kinds)))
            .ToArray();

        return new ManifestModel(1, systemInstructions, bundles);
    }

    private static IReadOnlyList<(string Scope, string Kind)> Includes(Random rng, IReadOnlyList<string> kinds)
    {
        var includes = new List<(string, string)>
        {
            ("system", kinds[rng.Next(kinds.Count)]),
            ("user", UserKindPool[rng.Next(UserKindPool.Length)]),
        };
        if (rng.Next(2) == 0)
        {
            includes.Add(("system", kinds[rng.Next(kinds.Count)]));
        }
        return includes;
    }

    /// <summary>Multi-line prompt text with the punctuation a real instruction carries.</summary>
    public static string Text(Random rng)
    {
        var lines = new List<string>();
        var lineCount = rng.Next(1, 6);
        for (var i = 0; i < lineCount; i++)
        {
            // An empty line is legal inside a block scalar; a whitespace-only or leading-space line
            // would re-negotiate the scalar's indentation, so the generator never emits one.
            if (i > 0 && rng.Next(5) == 0)
            {
                lines.Add("");
                continue;
            }
            var tokens = Enumerable.Range(0, rng.Next(1, 7)).Select(_ => LineTokens[rng.Next(LineTokens.Length)]);
            lines.Add(string.Join(' ', tokens));
        }
        // A trailing empty line cannot survive the strip chomping used on emit, so it is excluded.
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return lines.Count == 0 ? "Цель" : string.Join('\n', lines);
    }

    public static string Emit(ManifestModel model)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"version: {model.Version}\n");
        sb.Append("system_instructions:\n");
        foreach (var (kind, text) in model.SystemInstructions)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  - kind: {Quote(kind)}\n");
            AppendText(sb, text);
        }
        sb.Append("bundles:\n");
        foreach (var (mode, includes) in model.Bundles)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  - mode: {Quote(mode)}\n");
            sb.Append("    includes:\n");
            foreach (var (scope, kind) in includes)
            {
                sb.Append(CultureInfo.InvariantCulture, $"      - {{ scope: {Quote(scope)}, kind: {Quote(kind)} }}\n");
            }
        }
        return sb.ToString();
    }

    private static void AppendText(StringBuilder sb, string text)
    {
        if (text.Length == 0)
        {
            sb.Append("    text: \"\"\n");
            return;
        }
        sb.Append("    text: |-\n");
        foreach (var line in text.Split('\n'))
        {
            sb.Append(line.Length == 0 ? "\n" : $"      {line}\n");
        }
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
