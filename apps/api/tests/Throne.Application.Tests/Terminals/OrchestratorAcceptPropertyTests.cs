using System.Diagnostics;
using FluentAssertions;

namespace Throne.Application.Tests.Terminals;

/// <summary>
/// Свойства `throne-orchestrator accept` — детерминированной части приёмки ветки исполнителя
/// (ADR-0054 §7). Сценарии генерируются по seed: запушена ли ветка, разошёлся ли origin,
/// конфликтует ли слияние, зелёная ли проверка, влито ли уже. Каждый seed — воспроизводимый
/// контрпример.
/// </summary>
[Trait("Category", "Integration")]
public class OrchestratorAcceptPropertyTests
{
    private const int Cases = 24;

    private const int ExitAccepted = 0;
    private const int ExitDirtyTree = 65;
    private const int ExitBranchNotPushed = 66;
    private const int ExitConflict = 67;
    private const int ExitCheckFailed = 68;

    private sealed record Scenario(
        int Seed,
        string Into,
        string Branch,
        int BranchCommits,
        bool Pushed,
        bool RemoteAhead,
        bool Conflicting,
        bool AlreadyMerged,
        bool? CheckPasses);

    private static readonly string[] BranchPool =
        ["fix/job-hunt-30.5-direction-title", "feat/accept", "chore/x_y", "task-42", "исправление/заголовок"];

    private static Scenario Generate(int seed)
    {
        var rng = new Random(seed);
        var pushed = rng.Next(4) != 0;
        var alreadyMerged = pushed && rng.Next(5) == 0;
        var conflicting = pushed && !alreadyMerged && rng.Next(3) == 0;
        bool? checkPasses = rng.Next(3) switch { 0 => null, 1 => true, _ => false };
        return new Scenario(
            Seed: seed,
            Into: rng.Next(2) == 0 ? "main" : "master",
            Branch: BranchPool[rng.Next(BranchPool.Length)],
            BranchCommits: rng.Next(1, 4),
            Pushed: pushed,
            RemoteAhead: rng.Next(2) == 0,
            Conflicting: conflicting,
            AlreadyMerged: alreadyMerged,
            CheckPasses: checkPasses);
    }

    private static int Expected(Scenario s) => s switch
    {
        { Pushed: false } => ExitBranchNotPushed,
        { AlreadyMerged: true } => ExitAccepted,
        { Conflicting: true } => ExitConflict,
        { CheckPasses: false } => ExitCheckFailed,
        _ => ExitAccepted,
    };

    [Fact(DisplayName = "Свойство: основная ветка на origin двигается только при чистом слиянии и зелёной проверке")]
    public void Origin_advances_only_on_clean_merge_and_green_check()
    {
        var covered = new HashSet<int>();

        for (var seed = 0; seed < Cases; seed++)
        {
            var s = Generate(seed);
            using var world = World.Build(s);
            var intoBefore = world.OriginSha(s.Into);
            var branchBefore = s.Pushed ? world.OriginSha(s.Branch) : null;

            var run = world.Accept(s);
            var why = $"seed {seed}: {s}\n{run.Output}";

            run.ExitCode.Should().Be(Expected(s), why);
            covered.Add(run.ExitCode);

            // Инвариант: после любого исхода дерево чистое, локальная основная ветка = origin.
            world.Git("status --porcelain").Should().BeEmpty(why);
            world.Git($"rev-parse {s.Into}").Should().Be(world.Git($"rev-parse origin/{s.Into}"), why);
            world.Git("rev-parse --abbrev-ref HEAD").Should().Be(s.Into, why);

            // Инвариант: ветка исполнителя не тронута ни в одном исходе.
            if (branchBefore is not null)
            {
                world.OriginSha(s.Branch).Should().Be(branchBefore, why);
            }

            var intoAfter = world.OriginSha(s.Into);
            var mergedNow = run.ExitCode == ExitAccepted && !s.AlreadyMerged;
            if (!mergedNow)
            {
                intoAfter.Should().Be(intoBefore, why);
                continue;
            }

            // Влито: merge-коммит поверх актуального origin, второй родитель — тип ветки.
            var parents = world.Git($"rev-list --parents -n 1 {intoAfter}").Split(' ');
            parents.Should().HaveCount(3, why);
            parents[1].Should().Be(intoBefore, why);
            parents[2].Should().Be(branchBefore, why);
            run.Output.Should().Contain("влито", why);
        }

        covered.Should().BeEquivalentTo(
            [ExitAccepted, ExitBranchNotPushed, ExitConflict, ExitCheckFailed],
            "выборка обязана задеть каждый исход приёмки");
    }

    [Fact(DisplayName = "Свойство: повторная приёмка уже влитой ветки — no-op с кодом 0")]
    public void Accept_is_idempotent()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var s = Generate(seed) with { Pushed = true, Conflicting = false, AlreadyMerged = false, CheckPasses = null };
            using var world = World.Build(s);

            world.Accept(s).ExitCode.Should().Be(ExitAccepted, $"seed {seed}: первая приёмка");
            var afterFirst = world.OriginSha(s.Into);

            var second = world.Accept(s);
            second.ExitCode.Should().Be(ExitAccepted, $"seed {seed}: {second.Output}");
            second.Output.Should().Contain("уже влито", $"seed {seed}");
            world.OriginSha(s.Into).Should().Be(afterFirst, $"seed {seed}: повтор не должен плодить коммиты");
        }
    }

    [Fact(DisplayName = "Грязное дерево оркестратора — отказ до fetch, ничего не меняется")]
    public void Dirty_tree_is_refused()
    {
        var s = Generate(1) with { Pushed = true, Conflicting = false, AlreadyMerged = false, CheckPasses = null };
        using var world = World.Build(s);
        File.WriteAllText(Path.Combine(world.Repo, "scratch.txt"), "dirty");
        var before = world.OriginSha(s.Into);

        var run = world.Accept(s);

        run.ExitCode.Should().Be(ExitDirtyTree, run.Output);
        world.OriginSha(s.Into).Should().Be(before);
        File.Exists(Path.Combine(world.Repo, "scratch.txt")).Should().BeTrue("чужие правки не выбрасываются");
    }

    [Fact(DisplayName = "Проверка запускается в репозитории уже после слияния")]
    public void Check_runs_inside_merged_tree()
    {
        var s = Generate(2) with { Pushed = true, Conflicting = false, AlreadyMerged = false, CheckPasses = true };
        using var world = World.Build(s);

        // green.txt приезжает только с веткой: проверка видит его ⇒ она шла по слитому дереву.
        var run = world.Accept(s, check: "test -e green.txt && test -e base.txt");

        run.ExitCode.Should().Be(ExitAccepted, run.Output);
    }

    private sealed record RunResult(int ExitCode, string Output);

    /// <summary>Bare origin + клон исполнителя + клон оркестратора в temp-каталоге.</summary>
    private sealed class World : IDisposable
    {
        private readonly string _root;
        public string Origin { get; }
        public string Repo { get; }
        private readonly string _home;

        private World(string root)
        {
            _root = root;
            Origin = Path.Combine(root, "origin.git");
            Repo = Path.Combine(root, "orchestrator");
            _home = Path.Combine(root, "home");
            Directory.CreateDirectory(_home);
        }

        public static World Build(Scenario s)
        {
            var root = Path.Combine(Path.GetTempPath(), "throne-accept-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var w = new World(root);

            w.Run(root, "git", $"init --bare -b {s.Into} origin.git");

            var executor = Path.Combine(root, "executor");
            w.Run(root, "git", $"clone -q {w.Origin} executor");
            w.Run(executor, "git", $"checkout -q -b {s.Into}");
            File.WriteAllText(Path.Combine(executor, "base.txt"), "base\n");
            File.WriteAllText(Path.Combine(executor, "shared.txt"), "line\n");
            w.Run(executor, "git", "add -A");
            w.Run(executor, "git", "commit -q -m base");
            w.Run(executor, "git", $"push -q origin {s.Into}");

            // Клон оркестратора снимается с базового состояния — до того как origin уедет вперёд.
            w.Run(root, "git", $"clone -q {w.Origin} orchestrator");

            w.Run(executor, "git", $"checkout -q -b \"{s.Branch}\"");
            for (var i = 0; i < s.BranchCommits; i++)
            {
                File.WriteAllText(Path.Combine(executor, $"work{i}.txt"), $"work {i}\n");
                if (s.Conflicting)
                {
                    File.WriteAllText(Path.Combine(executor, "shared.txt"), $"executor {i}\n");
                }
                if (s.CheckPasses == true)
                {
                    File.WriteAllText(Path.Combine(executor, "green.txt"), "ok\n");
                }
                w.Run(executor, "git", "add -A");
                w.Run(executor, "git", $"commit -q -m \"work {i}\"");
            }
            if (s.Pushed)
            {
                w.Run(executor, "git", $"push -q origin \"{s.Branch}\"");
            }

            if (s.RemoteAhead || s.Conflicting)
            {
                w.Run(executor, "git", $"checkout -q {s.Into}");
                File.WriteAllText(Path.Combine(executor, "ahead.txt"), "ahead\n");
                if (s.Conflicting)
                {
                    File.WriteAllText(Path.Combine(executor, "shared.txt"), "operator\n");
                }
                w.Run(executor, "git", "add -A");
                w.Run(executor, "git", "commit -q -m ahead");
                w.Run(executor, "git", $"push -q origin {s.Into}");
            }

            if (s.AlreadyMerged)
            {
                w.Run(executor, "git", $"checkout -q {s.Into}");
                w.Run(executor, "git", $"merge -q --no-ff --no-edit \"{s.Branch}\"");
                w.Run(executor, "git", $"push -q origin {s.Into}");
            }

            return w;
        }

        public RunResult Accept(Scenario s, string? check = null)
        {
            check ??= s.CheckPasses switch
            {
                null => null,
                true => "test -e green.txt",
                false => "test -e never-there.txt",
            };
            var args = $"accept --repo \"{Repo}\" --branch \"{s.Branch}\"";
            if (check is not null)
            {
                args += $" --check \"{check}\"";
            }
            var (code, output) = Exec(_root, Cli, args);
            return new RunResult(code, output);
        }

        public string OriginSha(string branch) =>
            Exec(Origin, "git", $"rev-parse \"refs/heads/{branch}\"").Output.Trim();

        public string Git(string args) => Exec(Repo, "git", args).Output.Trim();

        private void Run(string cwd, string file, string args)
        {
            var (code, output) = Exec(cwd, file, args);
            code.Should().Be(0, $"setup: {file} {args}\n{output}");
        }

        private (int Code, string Output) Exec(string cwd, string file, string args)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // Изоляция от глобального git-конфига и хуков машины: тест про скрипт, не про окружение.
            psi.Environment["HOME"] = _home;
            psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
            psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            psi.Environment["GIT_AUTHOR_NAME"] = "throne-test";
            psi.Environment["GIT_AUTHOR_EMAIL"] = "throne-test@localhost";
            psi.Environment["GIT_COMMITTER_NAME"] = "throne-test";
            psi.Environment["GIT_COMMITTER_EMAIL"] = "throne-test@localhost";
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout + stderr);
        }

        private static string Cli => Path.Combine(RepoRoot(), "skills", "orchestrator", "bin", "throne-orchestrator");

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "specs", "AGENTS.local.md")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("repo root (specs/AGENTS.local.md) not found above " + AppContext.BaseDirectory);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
