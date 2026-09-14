using FluentAssertions;
using Throne.Infrastructure.Git;

namespace Throne.Infrastructure.Tests.Git;

/// <summary>
/// Свойства <see cref="GitObjectCachePathLayout"/>: путь до bare-зеркала должен быть
/// детерминированным, лежать строго под корнем кэша и не давать коллизий между разными
/// (host, owner, repo) — иначе клоны разных репозиториев подставят друг другу чужие
/// blob'ы через alternates. Сценарии генерируются по seed, как в
/// <c>OrchestratorAcceptPropertyTests</c> — каждый seed воспроизводимый контрпример.
/// </summary>
public class GitObjectCachePathLayoutTests
{
    private const int Cases = 40;
    private const string Root = "/cache-root";

    private static readonly string[] Hosts = ["github.com", "gitlab.example.com", "GITLAB.EXAMPLE.COM"];
    private static readonly string[] OwnerPool = ["alice", "acme-corp", "team/sub", "Bob_2"];
    private static readonly string[] RepoPool = ["throne", "my-repo", "svc_api", "Repo.Name"];

    private sealed record Case(string Host, string Owner, string Repo);

    private static Case Generate(int seed)
    {
        var rng = new Random(seed);
        return new Case(
            Hosts[rng.Next(Hosts.Length)],
            OwnerPool[rng.Next(OwnerPool.Length)],
            RepoPool[rng.Next(RepoPool.Length)]);
    }

    [Fact(DisplayName = "Свойство: путь детерминирован, лежит под корнем и оканчивается на {repo}.git")]
    public void Path_is_deterministic_and_rooted()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var c = Generate(seed);
            var path1 = GitObjectCachePathLayout.Compute(Root, c.Host, c.Owner, c.Repo);
            var path2 = GitObjectCachePathLayout.Compute(Root, c.Host, c.Owner, c.Repo);
            var why = $"seed {seed}: {c}";

            path2.Should().Be(path1, why);
            path1.Should().StartWith(Root + Path.DirectorySeparatorChar, why);
            path1.Should().EndWith($"{c.Repo}.git", why);
            path1.Should().NotContain("//", why);
        }
    }

    [Fact(DisplayName = "Свойство: host сравнивается регистронезависимо — путь одинаков для любого регистра")]
    public void Host_is_case_insensitive()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var c = Generate(seed);
            var lower = GitObjectCachePathLayout.Compute(Root, c.Host.ToLowerInvariant(), c.Owner, c.Repo);
            var upper = GitObjectCachePathLayout.Compute(Root, c.Host.ToUpperInvariant(), c.Owner, c.Repo);

            upper.Should().Be(lower, $"seed {seed}: {c}");
        }
    }

    [Fact(DisplayName = "Свойство: owner с '/' (GitLab subgroup) не оставляет '/' внутри своего сегмента")]
    public void Owner_slashes_are_flattened()
    {
        var any = false;
        for (var seed = 0; seed < Cases; seed++)
        {
            var c = Generate(seed);
            if (!c.Owner.Contains('/'))
            {
                continue;
            }

            any = true;
            var path = GitObjectCachePathLayout.Compute(Root, c.Host, c.Owner, c.Repo);
            var segments = path.Split(Path.DirectorySeparatorChar);
            var ownerSegment = segments[^2];

            ownerSegment.Should().NotContain("/", $"seed {seed}: {c}");
        }

        any.Should().BeTrue("тестовый пул owner обязан содержать хотя бы один вариант с '/'");
    }

    [Fact(DisplayName = "Свойство: разные repo при фиксированных host/owner дают разные пути (без коллизий)")]
    public void Distinct_repo_yields_distinct_path()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            var repoA = $"repo-{rng.Next(1000)}-a";
            var repoB = $"repo-{rng.Next(1000)}-b";

            var pathA = GitObjectCachePathLayout.Compute(Root, "github.com", "alice", repoA);
            var pathB = GitObjectCachePathLayout.Compute(Root, "github.com", "alice", repoB);

            pathA.Should().NotBe(pathB, $"seed {seed}");
        }
    }
}
