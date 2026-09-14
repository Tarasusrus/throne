using FluentAssertions;
using Throne.Application.Git;

namespace Throne.Infrastructure.Tests.Git.GitHubCli;

/// <summary>
/// <c>CloneRepositoryAsync</c> object-cache priming (<see cref="Throne.Infrastructure.Git.GitObjectCacheSync"/>)
/// and interrupted-checkout repair on reuse. Split out of <see cref="GitHubCliProviderTests"/>
/// to stay inside the maintainability budget — see that class's own doc comment.
/// </summary>
public class GitHubCliProviderCloneCacheTests
{
    private static readonly string[] CloneArgs = ["repo", "clone", "alice/throne", "/tmp/x", "--", "--filter=blob:none"];

    private readonly GitHubCliProviderFixture _fx = new();

    [Fact(DisplayName = "кэш недоступен (bare-клон в кэш падает) → --reference-if-able не добавляется")]
    public async Task Clone_invokes_repo_clone_without_reference_when_cache_priming_fails()
    {
        _fx.OnRun(req => GitHubCliProviderFixture.IsCacheBareCloneCall(req)
            ? GitHubCliProviderFixture.Fail(128, "network unreachable")
            : GitHubCliProviderFixture.Ok(string.Empty));

        await _fx.Provider.CloneRepositoryAsync("alice", "throne", "/tmp/x", CloneCheckout.None, default);

        var mainClone = _fx.Calls.Single(c => c.FileName == "gh" && !GitHubCliProviderFixture.IsCacheBareCloneCall(c));
        mainClone.Arguments.Should().BeEquivalentTo(CloneArgs);
    }

    [Fact(DisplayName = "холодный кэш — приминг bare-клоном, дальше --reference-if-able на кэш")]
    public async Task Clone_primes_cache_then_references_it_when_cache_is_cold()
    {
        _fx.OnRun(_ => GitHubCliProviderFixture.Ok(string.Empty));
        var cachePath = _fx.CacheMirrorPath("alice", "throne");

        await _fx.Provider.CloneRepositoryAsync("alice", "throne", "/tmp/x", CloneCheckout.None, default);

        var primeCall = _fx.Calls.Single(GitHubCliProviderFixture.IsCacheBareCloneCall);
        primeCall.Arguments.Should().BeEquivalentTo(
            ["repo", "clone", "alice/throne", cachePath, "--", "--bare"]);

        var mainClone = _fx.Calls.Single(c => c.FileName == "gh" && !GitHubCliProviderFixture.IsCacheBareCloneCall(c));
        mainClone.Arguments.Should().BeEquivalentTo(
            [.. CloneArgs, "--reference-if-able", cachePath]);
    }

    [Fact(DisplayName = "тёплый кэш — fetch дельты вместо повторного bare-клона")]
    public async Task Clone_refreshes_existing_cache_via_fetch_instead_of_recloning()
    {
        _fx.OnRun(_ => GitHubCliProviderFixture.Ok(string.Empty));
        var cachePath = _fx.CacheMirrorPath("alice", "throne");
        Directory.CreateDirectory(cachePath);

        await _fx.Provider.CloneRepositoryAsync("alice", "throne", "/tmp/x", CloneCheckout.None, default);

        _fx.Calls.Should().NotContain(
            c => GitHubCliProviderFixture.IsCacheBareCloneCall(c),
            "кэш уже существует — не должно быть повторного bare-клона");
        var fetch = _fx.Calls.Single(c => c.FileName == "git" && c.Arguments.Contains("fetch"));
        fetch.Arguments.Should().Contain(["--git-dir", cachePath]);

        var mainClone = _fx.Calls.Single(c => c.FileName == "gh" && c.Arguments.Contains("clone"));
        mainClone.Arguments.Should().Contain("--reference-if-able").And.Contain(cachePath);
    }

    [Fact(DisplayName = "клон сослался на кэш → после checkout выполняется repack -a -d (детач от кэша)")]
    public async Task Clone_detaches_from_cache_via_repack_when_reference_was_used()
    {
        _fx.OnRun(_ => GitHubCliProviderFixture.Ok(string.Empty));
        var cachePath = _fx.CacheMirrorPath("alice", "throne");

        await _fx.Provider.CloneRepositoryAsync("alice", "throne", "/tmp/x", CloneCheckout.None, default);

        string[] expectedRepackArgs = ["-C", "/tmp/x", "repack", "-a", "-d"];
        _fx.Calls.Should().Contain(
            c => c.FileName == "git" && c.Arguments.SequenceEqual(expectedRepackArgs),
            "клон занял объекты через --reference-if-able, значит должен уметь их себе присвоить");
    }

    [Fact(DisplayName = "кэш не использовался (приминг упал) → repack не запускается")]
    public async Task Clone_skips_repack_when_cache_was_not_used()
    {
        _fx.OnRun(req => GitHubCliProviderFixture.IsCacheBareCloneCall(req)
            ? GitHubCliProviderFixture.Fail(128, "network unreachable")
            : GitHubCliProviderFixture.Ok(string.Empty));

        await _fx.Provider.CloneRepositoryAsync("alice", "throne", "/tmp/x", CloneCheckout.None, default);

        _fx.Calls.Should().NotContain(
            c => c.FileName == "git" && c.Arguments.Contains("repack"),
            "клон не занимал объекты у кэша — детачить нечего");
    }

    [Fact(DisplayName = "прерванный checkout (deleted в рабочем дереве) чинится git checkout -- .")]
    public async Task Clone_repairs_incomplete_checkout_on_reuse()
    {
        _fx.OnRun(req => req.Arguments.Contains("status")
            ? GitHubCliProviderFixture.Ok(" D src/Program.cs\n")
            : GitHubCliProviderFixture.Ok(string.Empty));
        var path = Path.Combine(Path.GetTempPath(), $"throne-clone-broken-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        try
        {
            await _fx.Provider.CloneRepositoryAsync("alice", "throne", path, CloneCheckout.None, default);

            var expectedRepairArgs = new[] { "-C", path, "checkout", "--", "." };
            _fx.Calls.Should().Contain(c => c.Arguments.SequenceEqual(expectedRepairArgs));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
