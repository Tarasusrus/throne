using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Throne.Application.Ports;
using Throne.Infrastructure.Git;
using Throne.Infrastructure.Git.GitHubCli;

namespace Throne.Infrastructure.Tests.Git.GitHubCli;

/// <summary>
/// Shared assembly of <see cref="GitHubCliProvider"/> + a mocked
/// <see cref="IProcessLauncher"/> for the per-method test classes. Records every
/// <see cref="ProcessRunRequest"/> on <see cref="Calls"/> so tests can assert
/// command lines without re-deriving them.
/// </summary>
internal sealed class GitHubCliProviderFixture
{
    public GitHubCliProviderFixture()
    {
        Launcher = Substitute.For<IProcessLauncher>();
        var options = Options.Create(new GitHubCliOptions { ExecutablePath = "gh", PageSize = 50 });
        var invoker = new GhCliInvoker(Launcher, options);
        var listExec = new GhRepoListExecutor(invoker);
        var searcher = new GhRepoSearcher(invoker, listExec);
        // Fresh GUID root per fixture instance: the cache is always "cold" (Directory.Exists
        // false) unless a test explicitly pre-creates the mirror path, and never touches the
        // real ~/.throne/git-cache on the machine running the tests.
        CacheRoot = Path.Combine(Path.GetTempPath(), $"throne-git-cache-test-{Guid.NewGuid():N}");
        var objectCache = new GitObjectCacheSync(
            Launcher,
            Options.Create(new GitObjectCacheOptions { Root = CacheRoot }),
            NullLogger<GitObjectCacheSync>.Instance);
        var actions = new GhRepoActions(invoker, new GitCheckoutRunner(Launcher), objectCache);
        var probe = new GhAuthProbe(invoker);
        var threadsReader = new GhReviewThreadsReader(invoker, NullLogger<GhReviewThreadsReader>.Instance);
        var prActions = new GhPullRequestActions(invoker, threadsReader);
        var refListers = new GhRefListers(new GhBranchLister(invoker), new GhPullRequestLister(invoker));
        var reviewWorkspace = new GhReviewWorkspaceActions(invoker);
        var merge = new GhMergeActions(invoker);
        Provider = new GitHubCliProvider(searcher, actions, probe, prActions, refListers, reviewWorkspace, merge);
    }

    public IProcessLauncher Launcher { get; }

    public GitHubCliProvider Provider { get; }

    public string CacheRoot { get; }

    public List<ProcessRunRequest> Calls { get; } = new();

    public void OnRun(Func<ProcessRunRequest, ProcessRunResult> factory)
    {
        Launcher.RunAsync(Arg.Any<ProcessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var req = ci.Arg<ProcessRunRequest>();
                Calls.Add(req);
                return Task.FromResult(factory(req));
            });
    }

    public static ProcessRunResult Ok(string stdout) =>
        new(ExitCode: 0, StandardOutput: stdout, StandardError: string.Empty, Elapsed: TimeSpan.Zero);

    public static ProcessRunResult Fail(int exit, string stderr) =>
        new(ExitCode: exit, StandardOutput: string.Empty, StandardError: stderr, Elapsed: TimeSpan.Zero);

    public static bool IsApiCall(ProcessRunRequest req) =>
        req.Arguments.Count > 0 && req.Arguments[0] == "api";

    /// <summary>The <c>gh repo clone ... -- --bare</c> call <see cref="GitObjectCacheSync"/> issues to prime the cache.</summary>
    public static bool IsCacheBareCloneCall(ProcessRunRequest req) =>
        req.Arguments.Contains("--bare");

    /// <summary>Where <see cref="GitObjectCacheSync"/> resolves the <c>owner/repo</c> mirror for this fixture.</summary>
    public string CacheMirrorPath(string owner, string repo) =>
        GitObjectCachePathLayout.Compute(CacheRoot, "github.com", owner, repo);
}
