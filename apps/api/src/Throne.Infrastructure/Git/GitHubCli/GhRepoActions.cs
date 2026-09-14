using System.Globalization;
using Throne.Application.Git;
using Throne.Domain.Repositories;

namespace Throne.Infrastructure.Git.GitHubCli;

/// <summary>
/// File-system level git actions (<c>gh repo clone</c>, <c>gh repo sync</c>)
/// performed by <see cref="GitHubCliProvider"/>.
/// </summary>
internal sealed class GhRepoActions(GhCliInvoker gh, GitCheckoutRunner gitCheckout, GitObjectCacheSync objectCache)
{
    public async Task CloneAsync(
        string owner, string repo, string targetPath, CloneCheckout checkout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(checkout);

        // unbind не удаляет клон с диска (изоляция бранчей), но повторный bind
        // на ту же пару должен пройти. Если папка уже git-репо — переиспользуем
        // её (чиня прерванный checkout при необходимости), иначе чистим пустой
        // каталог и клонируем. В обоих случаях после этого применяем checkout,
        // чтобы reuse не вставал мимо выбранного PR/ветки.
        string? cachePath = null;
        if (!await TryReuseExistingCloneAsync(targetPath, ct))
        {
            // Тот же хост уже клонировал этот репозиторий для другого intent/binding —
            // объекты берём с диска (git-cache), а не по одному блобу с GitHub. См.
            // GitObjectCacheSync: best-effort, недоступность кэша не роняет клон.
            cachePath = await objectCache.EnsureUpToDateAsync(
                GitProviderHostDefaults.GitHub,
                owner,
                repo,
                (bareCachePath, cloneCt) => gh.RunCloneAsync(
                    ["repo", "clone", $"{owner}/{repo}", bareCachePath, "--", "--bare"], cloneCt),
                ct);

            // ADR-0026 §5: partial clone (`--filter=blob:none`). Метаданные + история тянутся
            // мгновенно, blob'ы — on-demand при `git checkout`/`bisect`/open. Без этого Run
            // pre-flight на больших monorepo блокирует tmux-spawn на минуты.
            // `gh repo clone owner/repo path -- --filter=blob:none [--reference-if-able <cache>]` —
            // флаги после `--` прокидываются в `git clone` без обёртки. `--reference-if-able`
            // роняет требование сети до дельты: недостающие blob'ы находятся через alternates
            // в кэше вместо fallback-запроса к GitHub по одному на файл.
            List<string> cloneArgs = ["repo", "clone", $"{owner}/{repo}", targetPath, "--", "--filter=blob:none"];
            if (cachePath is not null)
            {
                cloneArgs.Add("--reference-if-able");
                cloneArgs.Add(cachePath);
            }

            var result = await gh.RunCloneAsync(cloneArgs, ct);
            if (!result.IsSuccess)
            {
                throw GhExceptions.FromExit($"repo clone {owner}/{repo}", result);
            }
        }

        await ApplyCheckoutAsync(targetPath, checkout, ct);

        if (cachePath is not null)
        {
            // git-clone(1) про --reference: чтобы клон не зависел от кэша после его
            // удаления, объекты, занятые через alternates, нужно один раз затянуть
            // в собственные packfile'ы клона (`git repack -a`), иначе `rm -rf` кэша
            // оставляет в objects/info/alternates мёртвый путь и каждая git-команда
            // в клоне шумит в stderr "unable to normalize alternate object path".
            await gitCheckout.DetachFromObjectCacheAsync(targetPath, ct);
        }
    }

    public Task CheckoutAsync(
        string owner, string repo, string workspacePath, CloneCheckout checkout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentNullException.ThrowIfNull(checkout);
        return ApplyCheckoutAsync(workspacePath, checkout, ct);
    }

    private async Task ApplyCheckoutAsync(string workspacePath, CloneCheckout checkout, CancellationToken ct)
    {
        if (checkout.PullRequestNumber is int n)
        {
            // `gh pr checkout` корректно тянет PR из форка (настраивает remote/upstream).
            var result = await gh.RunInAsync(
                workspacePath, ["pr", "checkout", n.ToString(CultureInfo.InvariantCulture)], ct);
            if (!result.IsSuccess)
            {
                throw GhExceptions.FromExit($"pr checkout #{n}", result);
            }
            return;
        }

        await gitCheckout.CheckoutBranchAsync(workspacePath, checkout.Branch, ct);
    }

    public async Task SyncAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var result = await gh.RunInAsync(workspacePath, ["repo", "sync"], ct);
        if (!result.IsSuccess)
        {
            throw GhExceptions.FromExit($"repo sync in {workspacePath}", result);
        }
    }

    private async Task<bool> TryReuseExistingCloneAsync(string targetPath, CancellationToken ct)
    {
        if (!Directory.Exists(targetPath))
        {
            return false;
        }

        if (Directory.Exists(Path.Combine(targetPath, ".git")))
        {
            await gitCheckout.RepairIncompleteCheckoutAsync(targetPath, ct);
            return true;
        }

        // Пустую папку убираем, чтобы gh смог склонировать в неё без exit 128.
        if (!Directory.EnumerateFileSystemEntries(targetPath).Any())
        {
            Directory.Delete(targetPath);
            return false;
        }

        throw new GitProviderException(
            GitProviderErrorKind.CliFailure,
            $"workspace path '{targetPath}' already exists and is not a git clone; remove it manually before binding the repository again");
    }
}
