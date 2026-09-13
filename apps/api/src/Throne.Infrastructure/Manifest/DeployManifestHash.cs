using System.Security.Cryptography;
using System.Text;

namespace Throne.Infrastructure.Manifest;

/// <summary>
/// Fingerprints the prompt/skill surface a running instance actually serves:
/// <c>specs/manifest/*.yaml</c> and <c>skills/*/SKILL.md</c>, the same files
/// install-local.sh copies from the repo into the deployed bundle. Exposed on
/// <c>/version</c> and <c>throne status</c> so a stale instance — old commit but
/// unnoticed because it still starts and answers health checks — is visible
/// without diffing directory trees by hand.
/// </summary>
public static class DeployManifestHash
{
    public static string Compute(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var files = CollectFiles(root)
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();

        using var buffer = new MemoryStream();
        foreach (var (relativePath, fullPath) in files)
        {
            buffer.Write(Encoding.UTF8.GetBytes(relativePath));
            buffer.WriteByte((byte)'\n');
            buffer.Write(File.ReadAllBytes(fullPath));
            buffer.WriteByte((byte)'\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    private static IEnumerable<(string RelativePath, string FullPath)> CollectFiles(string root)
    {
        var manifestDir = Path.Combine(root, "specs", "manifest");
        if (Directory.Exists(manifestDir))
        {
            foreach (var file in Directory.EnumerateFiles(manifestDir, "*.yaml"))
            {
                yield return ($"specs/manifest/{Path.GetFileName(file)}", file);
            }
        }

        var skillsDir = Path.Combine(root, "skills");
        if (Directory.Exists(skillsDir))
        {
            foreach (var skillDir in Directory.EnumerateDirectories(skillsDir))
            {
                var skillMd = Path.Combine(skillDir, "SKILL.md");
                if (File.Exists(skillMd))
                {
                    yield return ($"skills/{Path.GetFileName(skillDir)}/SKILL.md", skillMd);
                }
            }
        }
    }
}
