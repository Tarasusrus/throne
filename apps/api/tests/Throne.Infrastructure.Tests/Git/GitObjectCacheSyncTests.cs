using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Throne.Application.Ports;
using Throne.Infrastructure.Git;

namespace Throne.Infrastructure.Tests.Git;

/// <summary>
/// <see cref="GitObjectCacheSync"/>: best-effort maintenance of the per-repo bare mirror
/// that a workspace clone later references for objects. Cold cache primes via the
/// caller-supplied bare-clone delegate; warm cache refreshes via a plain <c>git fetch</c>;
/// any failure is swallowed and reported as "no cache available" rather than failing the
/// caller's real clone — the cache only ever accelerates, it is never the sole source.
/// </summary>
public class GitObjectCacheSyncTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"throne-cache-sync-test-{Guid.NewGuid():N}");

    private readonly IProcessLauncher _launcher = Substitute.For<IProcessLauncher>();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private GitObjectCacheSync BuildSync() => new(
        _launcher,
        Options.Create(new GitObjectCacheOptions { Root = _root }),
        NullLogger<GitObjectCacheSync>.Instance);

    [Fact(DisplayName = "Холодный кэш: вызывает переданный bare-clone делегат по вычисленному пути")]
    public async Task Cold_cache_invokes_clone_delegate_at_computed_path()
    {
        var sync = BuildSync();
        var expectedPath = GitObjectCachePathLayout.Compute(sync.ResolvedRoot, "github.com", "alice", "throne");
        string? seenPath = null;

        var result = await sync.EnsureUpToDateAsync(
            "github.com", "alice", "throne",
            (path, ct) =>
            {
                seenPath = path;
                return Task.FromResult(Ok());
            },
            default);

        seenPath.Should().Be(expectedPath);
        result.Should().Be(expectedPath);
        _launcher.ReceivedCalls().Should().BeEmpty(
            "холодный путь не бьёт git напрямую — bare-клон выполняет делегат вызывающего");
    }

    [Fact(DisplayName = "Холодный кэш: делегат падает → null и без мусора на диске")]
    public async Task Cold_cache_clone_failure_returns_null_and_cleans_up()
    {
        var sync = BuildSync();
        var expectedPath = GitObjectCachePathLayout.Compute(sync.ResolvedRoot, "github.com", "alice", "throne");

        var result = await sync.EnsureUpToDateAsync(
            "github.com", "alice", "throne",
            (path, ct) =>
            {
                Directory.CreateDirectory(path); // имитация частично распакованного bare-клона
                return Task.FromResult(Fail());
            },
            default);

        result.Should().BeNull();
        Directory.Exists(expectedPath).Should().BeFalse("неудачный приминг не должен оставлять частичное зеркало");
    }

    [Fact(DisplayName = "Тёплый кэш: существующее зеркало обновляется через git fetch, делегат не вызывается")]
    public async Task Warm_cache_refreshes_via_fetch_without_calling_delegate()
    {
        var sync = BuildSync();
        var cachePath = GitObjectCachePathLayout.Compute(sync.ResolvedRoot, "github.com", "alice", "throne");
        Directory.CreateDirectory(cachePath);
        _launcher.RunAsync(Arg.Any<ProcessRunRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Ok()));
        var delegateCalled = false;

        var result = await sync.EnsureUpToDateAsync(
            "github.com", "alice", "throne",
            (path, ct) =>
            {
                delegateCalled = true;
                return Task.FromResult(Ok());
            },
            default);

        delegateCalled.Should().BeFalse();
        result.Should().Be(cachePath);
        var request = (ProcessRunRequest)_launcher.ReceivedCalls().Single().GetArguments()[0]!;
        request.FileName.Should().Be("git");
        request.Arguments.Should().Contain(["--git-dir", cachePath, "fetch"]);
    }

    [Fact(DisplayName = "Тёплый кэш: fetch падает → путь всё равно возвращается (устаревший кэш лучше никакого)")]
    public async Task Warm_cache_fetch_failure_still_returns_existing_path()
    {
        var sync = BuildSync();
        var cachePath = GitObjectCachePathLayout.Compute(sync.ResolvedRoot, "github.com", "alice", "throne");
        Directory.CreateDirectory(cachePath);
        _launcher.RunAsync(Arg.Any<ProcessRunRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Fail()));

        var result = await sync.EnsureUpToDateAsync(
            "github.com", "alice", "throne", (path, ct) => Task.FromResult(Ok()), default);

        result.Should().Be(cachePath);
    }

    [Fact(DisplayName = "Исключение из делегата глотается — best-effort, не роняет вызывающий клон")]
    public async Task Delegate_exception_is_swallowed()
    {
        var sync = BuildSync();

        var result = await sync.EnsureUpToDateAsync(
            "github.com", "alice", "throne",
            (path, ct) => throw new InvalidOperationException("boom"),
            default);

        result.Should().BeNull();
    }

    [Fact(DisplayName = "Два параллельных приминга одного кэша: клонирует один раз, второй ждёт готовый кэш, ни один не удаляет чужой каталог")]
    public async Task Concurrent_cold_priming_of_same_cache_clones_once()
    {
        var sync = BuildSync();
        var cachePath = GitObjectCachePathLayout.Compute(sync.ResolvedRoot, "github.com", "alice", "throne");
        _launcher.RunAsync(Arg.Any<ProcessRunRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Ok()));

        var cloneStarted = new SemaphoreSlim(0, 1);
        var releaseClone = new SemaphoreSlim(0, 1);
        var cloneCalls = 0;

        // Первый вызов встаёт внутри делегата клона (имитирует долгий `git clone --bare`),
        // второй стартует, пока первый ещё не создал каталог на диске — раньше оба видели
        // Directory.Exists == false и оба лезли в git clone на один и тот же путь.
        var first = sync.EnsureUpToDateAsync(
            "github.com", "alice", "throne",
            async (path, ct) =>
            {
                Interlocked.Increment(ref cloneCalls);
                cloneStarted.Release();
                await releaseClone.WaitAsync(ct);
                Directory.CreateDirectory(path);
                return Ok();
            },
            default);

        await cloneStarted.WaitAsync();

        var second = sync.EnsureUpToDateAsync(
            "github.com", "alice", "throne",
            (path, ct) =>
            {
                Interlocked.Increment(ref cloneCalls);
                return Task.FromResult(Ok());
            },
            default);

        // Второй вызов должен блокироваться на локе, а не гонять собственный git clone —
        // дать ему шанс (неправильно) продвинуться, прежде чем отпускать первый.
        await Task.Delay(50);
        releaseClone.Release();

        var results = await Task.WhenAll(first, second);

        cloneCalls.Should().Be(1, "второй вызов обязан дождаться готового кэша, а не запускать свой bare-clone");
        results.Should().AllSatisfy(r => r.Should().Be(cachePath));
        Directory.Exists(cachePath).Should().BeTrue("готовый кэш первого вызова не должен быть удалён вторым");
    }

    private static ProcessRunResult Ok(string stdout = "") =>
        new(ExitCode: 0, StandardOutput: stdout, StandardError: string.Empty, Elapsed: TimeSpan.Zero);

    private static ProcessRunResult Fail(string stderr = "error") =>
        new(ExitCode: 1, StandardOutput: string.Empty, StandardError: stderr, Elapsed: TimeSpan.Zero);
}
