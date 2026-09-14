using System.Globalization;
using System.Text;

namespace Throne.Application.Manifest;

/// <summary>
/// Reverse path of the seed: renders user parts in the exact YAML dialect
/// <see cref="UserPromptSeedParser"/> reads, so a snapshot of the live instance can be
/// committed as <c>specs/manifest/throne-user-prompt-seed-parts.yaml</c>. Texts go out as
/// literal block scalars (chomping and indentation indicators chosen per text) so every
/// byte survives the round trip; CR line endings are normalised to LF — YAML folds them
/// on read anyway and the merge compares texts modulo line endings.
/// </summary>
public static class UserPromptSeedWriter
{
    private const string Header = """
        # Throne user-prompt seed — canonical set of user-scope prompt parts (ADR-0051).
        #
        # This file is the single source of truth for the editable user-scope parts. On every
        # start (`UserPromptSeedSeeder`) — and on `POST /api/v1/prompt-parts:seed-sync` — the
        # instance merges it into `prompt_parts(scope=user)`:
        #   * a key missing on the instance is created;
        #   * a part whose live text still equals what was seeded last time follows the new
        #     seed text and roles;
        #   * a part edited by the operator is kept and reported as drift
        #     (`GET /version` → `user_prompt_drift`, `scripts/check-deploy.sh`).
        # Parts the seed does not know are never touched; keys removed here are not deleted.
        #
        # Reverse path: `scripts/prompt-parts-snapshot.sh` renders the live user parts back
        # into this format — commit the result to carry operator edits into the repository.
        #
        # Per part: {key, text, description?, mode_roles[]}. `mode_roles` mirrors the
        # PromptPart domain model: {mode, role, order}. Core parts (common/work/interview/
        # review/dream) carry `mandatory` roles matching the system manifest's bundles.

        """;

    private const string Indent = "      ";

    public static string Write(UserPromptSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var sb = new StringBuilder();
        sb.Append(Header.Replace("\r\n", "\n", StringComparison.Ordinal)).Append('\n');
        sb.Append("version: ").Append(seed.Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append('\n');
        sb.Append("seed_parts:\n");

        var separated = false;
        foreach (var part in seed.Parts)
        {
            sb.Append("  - key: ").Append(Scalar(part.Key)).Append('\n');
            if (!string.IsNullOrWhiteSpace(part.Description))
            {
                sb.Append("    description: ").Append(Scalar(part.Description)).Append('\n');
            }
            sb.Append("    mode_roles:");
            sb.Append(part.ModeRoles.Count == 0 ? " []\n" : "\n");
            foreach (var role in part.ModeRoles)
            {
                sb.Append("      - { mode: ").Append(Scalar(role.Mode))
                    .Append(", role: ").Append(Scalar(role.Role))
                    .Append(", order: ").Append(role.Order.ToString(CultureInfo.InvariantCulture))
                    .Append(" }\n");
            }
            // One blank line separates parts — unless the literal keeps its own trailing
            // breaks («|+»), where an extra blank line would read back as part of the text.
            separated = !AppendLiteral(sb, part.Text);
            if (separated)
            {
                sb.Append('\n');
            }
        }

        if (separated)
        {
            sb.Length -= 1;
        }
        return sb.ToString();
    }

    /// <summary>Returns true when the scalar keeps its trailing breaks («|+»).</summary>
    private static bool AppendLiteral(StringBuilder sb, string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var body = normalized.TrimEnd('\n');
        var trailingBreaks = normalized.Length - body.Length;

        // Chomping: «-» strips the final break, «|» keeps exactly one, «+» keeps them all.
        var chomping = trailingBreaks switch { 0 => "-", 1 => "", _ => "+" };
        // A body starting with whitespace needs an explicit indentation indicator, otherwise
        // the parser cannot tell content indentation from the leading blank.
        var indentation = body.Length > 0 && (body[0] == ' ' || body[0] == '\n') ? "2" : "";

        sb.Append("    text: |").Append(indentation).Append(chomping).Append('\n');
        foreach (var line in body.Split('\n'))
        {
            if (line.Length > 0)
            {
                sb.Append(Indent).Append(line);
            }
            sb.Append('\n');
        }
        // «+» keeps every trailing break: emit the extra blank lines it stands for.
        for (var i = 1; i < trailingBreaks; i++)
        {
            sb.Append('\n');
        }
        return trailingBreaks > 1;
    }

    /// <summary>Plain scalar when YAML reads it back as the same string; double-quoted otherwise.</summary>
    private static string Scalar(string value)
    {
        if (IsPlainSafe(value))
        {
            return value;
        }

        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    private static bool IsPlainSafe(string value)
    {
        if (value.Length == 0 || value != value.Trim() || value.Length > 200)
        {
            return false;
        }
        if (value.Any(c => char.IsControl(c) || "#:{}[],&*!|>'\"%@`?".Contains(c, StringComparison.Ordinal)))
        {
            return false;
        }
        if (value[0] == '-')
        {
            return false;
        }
        // Words YAML 1.1 resolves to something other than a string.
        var lower = value.ToLowerInvariant();
        return lower is not ("true" or "false" or "yes" or "no" or "on" or "off" or "null" or "~" or "y" or "n")
            && !double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
    }
}
