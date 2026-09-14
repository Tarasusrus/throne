namespace Throne.Infrastructure.Git;

/// <summary>
/// Bind target for <c>Throne:GitObjectCache</c>. One machine clones the same
/// upstream repo for every intent that touches it; instead of paying the network
/// cost (and, on a partial clone, the per-blob lazy-fetch cost) again each time,
/// <see cref="GitObjectCacheSync"/> keeps a bare mirror here that later clones
/// reference for objects.
/// </summary>
public sealed class GitObjectCacheOptions
{
    public const string SectionName = "Throne:GitObjectCache";

    /// <summary>Root directory holding <c>{host}/{owner}/{repo}.git</c> bare mirrors.</summary>
    public string Root { get; set; } = GitObjectCachePathLayout.DefaultRoot;

    /// <summary>Timeout for the incremental <c>git fetch</c> that refreshes an existing mirror.</summary>
    public TimeSpan FetchTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
