using System.ComponentModel;
using Throne.Application.Git;
using Throne.Application.Ports;

namespace Throne.Infrastructure.Git;

/// <summary>
/// Провайдер-нейтральный checkout ветки через plain <c>git</c> (тот же seam
/// <see cref="IProcessLauncher"/>, что и <see cref="LocalGitWorkspaceSync"/>). Используется
/// post-clone шагом обоих провайдеров для ветки-override из биндинга. PR-путь сюда не заходит —
/// PR форка переключается провайдерным CLI (`gh pr checkout` / `glab mr checkout`).
/// Контракт мягкий: ветка-плейсхолдер "main" на репо с дефолтом master не должна ронять клон,
/// поэтому отсутствие ref на origin — тихий no-op, а не ошибка.
/// </summary>
internal sealed class GitCheckoutRunner(IProcessLauncher launcher)
{
    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RepackTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Detects and repairs the on-disk signature of an interrupted checkout (see
    /// <see cref="GitStatusPorcelain"/>): the index matches the target commit but files
    /// never made it to the working tree because a prior process was killed mid-checkout
    /// (classic symptom of a partial clone's per-blob lazy fetch stalling for hours on a
    /// slow link). Restoring from the index is now cheap once the object cache is in
    /// place — see <see cref="GitObjectCacheSync"/> — so this runs unconditionally on
    /// every reuse of an existing clone instead of leaving a broken tree for the executor.
    /// Returns whether a repair was actually performed.
    /// </summary>
    public async Task<bool> RepairIncompleteCheckoutAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var status = await RunAllowingFailureAsync(workspacePath, ["status", "--porcelain"], "status", ct);
        if (!status.IsSuccess || !GitStatusPorcelain.HasUnstagedDeletions(status.StandardOutput))
        {
            return false;
        }

        await RunAsync(workspacePath, ["checkout", "--", "."], "checkout --", ct);
        return true;
    }

    /// <summary>
    /// Makes a clone created with <c>--reference-if-able &lt;cache&gt;</c> self-contained by
    /// pulling every object it currently only borrows from the cache's alternates into its
    /// own packfiles (<c>git repack -a</c> — the exact remedy git-clone(1) documents for
    /// <c>--reference</c> when the reference repository may later disappear). Without this,
    /// deleting the object cache leaves a dangling <c>objects/info/alternates</c> entry that
    /// makes every subsequent git command in the clone print
    /// "unable to normalize alternate object path" to stderr.
    /// Best-effort: a repack failure is logged-and-swallowed by the caller's contract (this
    /// method just returns without throwing), because the clone remains correct either way —
    /// it just stays dependent on the cache a little longer.
    /// </summary>
    public async Task DetachFromObjectCacheAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        ProcessRunResult repack;
        try
        {
            repack = await RunAllowingFailureAsync(
                workspacePath, ["repack", "-a", "-d"], "repack", ct, RepackTimeout);
        }
        catch (GitProviderException)
        {
            // Таймаут/отсутствие git на PATH — та же best-effort семантика, что и у
            // GitObjectCacheSync: клон остаётся рабочим (просто ещё зависит от кэша).
            return;
        }

        if (!repack.IsSuccess)
        {
            return;
        }

        var alternates = Path.Combine(workspacePath, ".git", "objects", "info", "alternates");
        try
        {
            File.Delete(alternates);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async Task CheckoutBranchAsync(string workspacePath, string? branch, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        if (string.IsNullOrWhiteSpace(branch))
        {
            return;
        }

        // Уже на нужной ветке (свежий клон встал на неё) — не дёргаем рабочее дерево.
        var current = await RunAsync(workspacePath, ["rev-parse", "--abbrev-ref", "HEAD"], "rev-parse", ct);
        if (string.Equals(current.StandardOutput.Trim(), branch, StringComparison.Ordinal))
        {
            return;
        }

        // rev-parse --verify легитимно возвращает ненулевой код, когда ref отсутствует —
        // это плейсхолдер "main" на репо с дефолтом master. Обрабатываем по ExitCode, не кидаем.
        var verify = await RunAllowingFailureAsync(
            workspacePath, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}"], "rev-parse", ct);
        if (!verify.IsSuccess)
        {
            return;
        }

        var result = await RunAllowingFailureAsync(workspacePath, ["checkout", branch], "checkout", ct);
        if (!result.IsSuccess)
        {
            throw new GitProviderException(
                GitProviderErrorKind.CliFailure,
                $"git checkout ветки '{branch}' завершился с кодом {result.ExitCode}.",
                result.StandardError.Trim());
        }
    }

    /// <summary>
    /// Запуск git с маппингом сбоев процесса (таймаут / нет executable) в
    /// <see cref="GitProviderException"/> и проверкой нулевого кода возврата.
    /// </summary>
    private async Task<ProcessRunResult> RunAsync(
        string workspacePath, IReadOnlyList<string> operation, string label, CancellationToken ct)
    {
        var result = await RunAllowingFailureAsync(workspacePath, operation, label, ct);
        if (!result.IsSuccess)
        {
            throw new GitProviderException(
                GitProviderErrorKind.CliFailure,
                $"git {label} завершился с кодом {result.ExitCode}.",
                result.StandardError.Trim());
        }

        return result;
    }

    /// <summary>
    /// Запуск git без проверки кода возврата — для команд (rev-parse --verify), где
    /// ненулевой exit означает «ref отсутствует», а не сбой. Сбои самого процесса
    /// (таймаут / нет executable) всё равно мапятся в <see cref="GitProviderException"/>.
    /// </summary>
    private async Task<ProcessRunResult> RunAllowingFailureAsync(
        string workspacePath,
        IReadOnlyList<string> operation,
        string label,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        string[] arguments = ["-C", workspacePath, .. operation];
        try
        {
            return await launcher.RunAsync(
                new ProcessRunRequest(FileName: "git", Arguments: arguments, Timeout: timeout ?? QuickTimeout), ct);
        }
        catch (TimeoutException ex)
        {
            throw new GitProviderException(
                GitProviderErrorKind.NetworkError, $"git {label} превысил тайм-аут {QuickTimeout}.", null, ex);
        }
        catch (Win32Exception ex)
        {
            throw new GitProviderException(
                GitProviderErrorKind.CliFailure, "git executable not found on PATH.", ex.Message, ex);
        }
    }
}
