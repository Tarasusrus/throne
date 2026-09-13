using System.Reflection;

namespace Throne.Api;

/// <summary>
/// Single source of truth for the running build's version. Sourced from the
/// assembly's informational version (set by MSBuild <c>Version</c> / the release
/// tag), consumed by the <c>/version</c> endpoint, <c>throne status</c> and
/// <c>throne update</c>. Kept verbatim, including the "+&lt;commit-sha&gt;" build
/// metadata install-local.sh stamps on local builds — that suffix is what makes a
/// silently stale deploy visible, so it must not be dropped at the source. Strip
/// it only at the point that needs a bare SemVer, via <see cref="WithoutBuildMetadata"/>.
/// </summary>
public static class ThroneVersion
{
    public static string Current { get; } = Resolve();

    /// <summary>
    /// Drops "+&lt;build-metadata&gt;" (SemVer §10) — needed when comparing the
    /// running build against a GitHub release tag, which never carries a commit
    /// suffix, in <see cref="Cli.UpdateCommand"/>.
    /// </summary>
    public static string WithoutBuildMetadata(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }

    private static string Resolve()
    {
        var informational = typeof(ThroneVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return !string.IsNullOrWhiteSpace(informational)
            ? informational
            : typeof(ThroneVersion).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
