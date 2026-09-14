using FluentAssertions;
using NSubstitute;
using Throne.Application.Ports;
using Throne.Infrastructure.Git;

namespace Throne.Infrastructure.Tests.Git;

/// <summary>
/// <see cref="GitCheckoutRunner.DetachFromObjectCacheAsync"/> — the fix for the acceptance
/// finding that a deleted git-cache mirror left a dangling <c>objects/info/alternates</c>
/// entry behind in every clone that had borrowed objects from it (git then prints
/// "unable to normalize alternate object path" on every subsequent command in that clone).
/// A successful <c>git repack -a -d</c> is what git-clone(1) documents as the way to make a
/// <c>--reference</c>'d clone self-contained, so once it succeeds the alternates file is no
/// longer needed and is removed; on any failure the clone is left untouched (best-effort,
/// matching <see cref="GitObjectCacheSync"/>'s contract that the cache only ever accelerates).
/// </summary>
public sealed class GitCheckoutRunnerDetachFromObjectCacheTests : IDisposable
{
    private readonly string _workspacePath =
        Path.Combine(Path.GetTempPath(), $"throne-detach-cache-test-{Guid.NewGuid():N}");
    private readonly IProcessLauncher _launcher = Substitute.For<IProcessLauncher>();

    public GitCheckoutRunnerDetachFromObjectCacheTests()
    {
        Directory.CreateDirectory(AlternatesDirectory);
    }

    private string AlternatesDirectory => Path.Combine(_workspacePath, ".git", "objects", "info");

    private string AlternatesPath => Path.Combine(AlternatesDirectory, "alternates");

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
    }

    [Fact(DisplayName = "успешный repack -a -d → alternates удаляется, клон больше не зависит от кэша")]
    public async Task Successful_repack_removes_alternates_file()
    {
        File.WriteAllText(AlternatesPath, "/some/cache/path/objects\n");
        _launcher.RunAsync(Arg.Any<ProcessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Ok()));
        var runner = new GitCheckoutRunner(_launcher);

        await runner.DetachFromObjectCacheAsync(_workspacePath, default);

        File.Exists(AlternatesPath).Should().BeFalse();
        string[] expectedArgs = ["-C", _workspacePath, "repack", "-a", "-d"];
        await _launcher.Received(1).RunAsync(
            Arg.Is<ProcessRunRequest>(r => r.FileName == "git" && r.Arguments.SequenceEqual(expectedArgs)),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "repack завершился с ошибкой → alternates остаётся, исключение не летит")]
    public async Task Failed_repack_leaves_alternates_file_in_place()
    {
        File.WriteAllText(AlternatesPath, "/some/cache/path/objects\n");
        _launcher.RunAsync(Arg.Any<ProcessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Fail()));
        var runner = new GitCheckoutRunner(_launcher);

        await runner.DetachFromObjectCacheAsync(_workspacePath, default);

        File.Exists(AlternatesPath).Should().BeTrue();
    }

    [Fact(DisplayName = "repack падает по таймауту → best-effort, исключение не летит наружу")]
    public async Task Timed_out_repack_does_not_throw()
    {
        File.WriteAllText(AlternatesPath, "/some/cache/path/objects\n");
        _launcher.RunAsync(Arg.Any<ProcessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ProcessRunResult>>(_ => throw new TimeoutException("git repack превысил тайм-аут"));
        var runner = new GitCheckoutRunner(_launcher);

        var act = async () => await runner.DetachFromObjectCacheAsync(_workspacePath, default);

        await act.Should().NotThrowAsync();
        File.Exists(AlternatesPath).Should().BeTrue();
    }

    [Fact(DisplayName = "нет alternates файла (кэш не использовался) → repack всё равно best-effort, без исключения")]
    public async Task Missing_alternates_file_is_not_an_error()
    {
        _launcher.RunAsync(Arg.Any<ProcessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Ok()));
        var runner = new GitCheckoutRunner(_launcher);

        var act = async () => await runner.DetachFromObjectCacheAsync(_workspacePath, default);

        await act.Should().NotThrowAsync();
        File.Exists(AlternatesPath).Should().BeFalse();
    }

    private static ProcessRunResult Ok() =>
        new(ExitCode: 0, StandardOutput: string.Empty, StandardError: string.Empty, Elapsed: TimeSpan.Zero);

    private static ProcessRunResult Fail() =>
        new(ExitCode: 1, StandardOutput: string.Empty, StandardError: "fatal: repack failed", Elapsed: TimeSpan.Zero);
}
