namespace Throne.Infrastructure.Git;

/// <summary>
/// Computes the absolute path of the per-repo bare object-cache mirror maintained
/// by <see cref="GitObjectCacheSync"/>: <c>{root}/{host}/{owner}/{repo}.git</c>.
/// One machine clones the same upstream repo for every intent/binding that touches
/// it; this cache is the single local source those clones borrow objects from via
/// <c>git clone --reference-if-able</c>, so the layout only needs to be stable and
/// collision-free per <c>(host, owner, repo)</c> — it is never parsed back.
/// </summary>
internal static class GitObjectCachePathLayout
{
    public const string DefaultRoot = "~/.throne/git-cache";

    public static string ExpandRoot(string configuredRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoot);
        return Path.GetFullPath(WorkspacePathExpansion.ExpandHome(configuredRoot));
    }

    public static string Compute(string cacheRoot, string host, string owner, string repo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        return Path.Combine(
            cacheRoot,
            host.ToLowerInvariant(),
            FlattenSlashes(owner),
            $"{repo}.git");
    }

    // GitLab namespaces may contain '/'; the cache path is a flat directory name,
    // never parsed back into a coordinate, mirroring WorkspacePathLayout's rule.
    private static string FlattenSlashes(string owner) => owner.Replace('/', '-');
}
