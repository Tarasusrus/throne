using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Throne.Application.Ports;

namespace Throne.Infrastructure.Git;

/// <summary>
/// Keeps a per-repo bare mirror under <see cref="GitObjectCacheOptions.Root"/> fresh
/// and hands its path back so a workspace clone can borrow objects from it via
/// <c>git clone --reference-if-able</c> instead of fetching them over the network —
/// the fix for the parent incident where a partial clone (<c>--filter=blob:none</c>)
/// on a slow link had to lazily fetch thousands of blobs one HTTP request at a time
/// during checkout.
///
/// Best-effort by construction: the cache only ever accelerates a clone, it is never
/// the sole source of an object (the workspace clone keeps its own promisor remote),
/// so any failure here is logged and swallowed — the caller falls back to a plain
/// network clone exactly as before this existed. This is also what makes deleting
/// the cache directory safe at any time: worst case, the next clone is as slow as it
/// was before the cache existed, not broken.
/// </summary>
internal sealed class GitObjectCacheSync(
    IProcessLauncher launcher,
    IOptions<GitObjectCacheOptions> options,
    ILogger<GitObjectCacheSync> log)
{
    public string ResolvedRoot { get; } = GitObjectCachePathLayout.ExpandRoot(options.Value.Root);

    // Two intents landing on the same machine at the same moment can both ask to prime the
    // same repo's mirror before either sees a directory on disk. Without serializing on the
    // cache path, both race into `git clone --bare` on the same target — the loser fails with
    // "destination path already exists" and its failure handler used to delete the directory
    // out from under the winner, wiping the mirror the winner had just finished building.
    // Keyed per cache path (not a single lock) so unrelated repos still prime concurrently.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Ensures the mirror for <paramref name="host"/>/<paramref name="owner"/>/<paramref name="repo"/>
    /// exists and is reasonably fresh, creating it via <paramref name="cloneBareAsync"/> the first
    /// time (network cost paid once) and fetching the delta on every call after that. Returns the
    /// mirror path on success, or <see langword="null"/> when the cache could not be established —
    /// callers must treat that as "clone without a reference", not as an error.
    /// </summary>
    public async Task<string?> EnsureUpToDateAsync(
        string host,
        string owner,
        string repo,
        Func<string, CancellationToken, Task<ProcessRunResult>> cloneBareAsync,
        CancellationToken ct)
    {
        var cachePath = GitObjectCachePathLayout.Compute(ResolvedRoot, host, owner, repo);
        var fullName = $"{owner}/{repo}";
        var gate = _locks.GetOrAdd(cachePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Directory.Exists(cachePath))
            {
                var fetch = await RunGitAsync(
                    cachePath,
                    ["--git-dir", cachePath, "fetch", "--prune", "origin",
                        "+refs/heads/*:refs/heads/*", "+refs/tags/*:refs/tags/*"],
                    ct);
                if (!fetch.IsSuccess)
                {
                    GitObjectCacheSyncLog.FetchFailed(log, fullName, cachePath, fetch.StandardError.Trim());
                }
                return cachePath;
            }

            var parent = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var clone = await cloneBareAsync(cachePath, ct);
            if (!clone.IsSuccess)
            {
                GitObjectCacheSyncLog.CloneFailed(log, fullName, cachePath, clone.StandardError.Trim());
                TryDeletePartialClone(cachePath);
                return null;
            }
            return cachePath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            GitObjectCacheSyncLog.Unexpected(log, fullName, ex);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ProcessRunResult> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct) =>
        await launcher.RunAsync(
            new ProcessRunRequest(
                FileName: "git",
                Arguments: arguments,
                WorkingDirectory: workingDirectory,
                Timeout: options.Value.FetchTimeout),
            ct);

    private static void TryDeletePartialClone(string cachePath)
    {
        try
        {
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only — a leftover partial mirror just means the next
            // attempt re-clones into it and gets a "not empty" style git failure, which
            // surfaces as the same CloneFailed log line, not silent corruption.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
