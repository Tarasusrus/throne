using FluentAssertions;
using Throne.Infrastructure.Manifest;

namespace Throne.Infrastructure.Tests.Manifest;

/// <summary>
/// Properties of <see cref="DeployManifestHash"/>: it must be stable for an
/// unchanged bundle and must move whenever any input file it covers changes —
/// that instability is the whole point, it is what makes a deployed instance's
/// drift from the repository visible on <c>/version</c> and <c>throne status</c>.
/// Swept over randomly generated bundle layouts rather than one fixed fixture,
/// in the same style as <c>SkillManifestPropertyTests</c>.
/// </summary>
public class DeployManifestHashTests
{
    private const int Cases = 60;

    [Fact(DisplayName = "Свойство: повторный вызов на неизменном бандле даёт тот же хэш")]
    public void Repeated_call_is_stable()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            using var bundle = Bundle.Generate(rng);

            var first = DeployManifestHash.Compute(bundle.Root);
            var second = DeployManifestHash.Compute(bundle.Root);

            second.Should().Be(first, Counterexample(seed, bundle));
        }
    }

    [Fact(DisplayName = "Свойство: правка содержимого любого одного входного файла меняет хэш")]
    public void Editing_any_single_input_file_changes_the_hash()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            using var bundle = Bundle.Generate(rng);
            if (bundle.Files.Count == 0)
            {
                continue;
            }

            var before = DeployManifestHash.Compute(bundle.Root);
            var target = bundle.Files[rng.Next(bundle.Files.Count)];
            File.AppendAllText(target, "\n# drift\n");

            var after = DeployManifestHash.Compute(bundle.Root);

            after.Should().NotBe(before, Counterexample(seed, bundle) + $", edited={target}");
        }
    }

    [Fact(DisplayName = "Свойство: добавление или удаление покрытого файла (SKILL.md) меняет хэш")]
    public void Adding_or_removing_a_covered_file_changes_the_hash()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            using var bundle = Bundle.Generate(rng);

            var before = DeployManifestHash.Compute(bundle.Root);
            var addedSkillDir = Path.Combine(bundle.Root, "skills", $"extra-{seed}");
            Directory.CreateDirectory(addedSkillDir);
            File.WriteAllText(Path.Combine(addedSkillDir, "SKILL.md"), "extra skill\n");

            var afterAdd = DeployManifestHash.Compute(bundle.Root);
            afterAdd.Should().NotBe(before, Counterexample(seed, bundle) + " (add)");

            Directory.Delete(addedSkillDir, recursive: true);
            var afterRemove = DeployManifestHash.Compute(bundle.Root);
            afterRemove.Should().Be(before, Counterexample(seed, bundle) + " (remove)");
        }
    }

    [Fact(DisplayName = "Свойство: файлы вне specs/manifest/*.yaml и skills/ не влияют на хэш")]
    public void Files_outside_the_covered_set_do_not_affect_the_hash()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            using var bundle = Bundle.Generate(rng);

            var before = DeployManifestHash.Compute(bundle.Root);
            File.WriteAllText(Path.Combine(bundle.Root, "specs", "manifest", "notes.txt"), "irrelevant");
            File.WriteAllText(Path.Combine(bundle.Root, "root-level.txt"), "irrelevant");

            var after = DeployManifestHash.Compute(bundle.Root);

            after.Should().Be(before, Counterexample(seed, bundle));
        }
    }

    [Fact(DisplayName = "Свойство: правка файла в skills/*/bin меняет хэш")]
    public void Editing_a_file_under_skills_bin_changes_the_hash()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            using var bundle = Bundle.Generate(rng);
            var binDir = Path.Combine(bundle.Root, "skills", $"tooling-{seed}", "bin");
            Directory.CreateDirectory(binDir);
            var binFile = Path.Combine(binDir, "throne-orchestrator");
            File.WriteAllText(binFile, $"#!/bin/sh\necho {rng.Next()}\n");

            var before = DeployManifestHash.Compute(bundle.Root);
            File.AppendAllText(binFile, "\n# drift\n");
            var after = DeployManifestHash.Compute(bundle.Root);

            after.Should().NotBe(before, Counterexample(seed, bundle) + $", edited={binFile}");
        }
    }

    [Fact(DisplayName = "Свойство: файлы внутри __pycache__/.pytest_cache под skills/ не влияют на хэш")]
    public void Files_inside_pycache_or_pytest_cache_do_not_affect_the_hash()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var rng = new Random(seed);
            using var bundle = Bundle.Generate(rng);

            var before = DeployManifestHash.Compute(bundle.Root);

            var pycacheDir = Path.Combine(bundle.Root, "skills", $"orchestrator-{seed}", "bin", "__pycache__");
            Directory.CreateDirectory(pycacheDir);
            File.WriteAllText(Path.Combine(pycacheDir, "_orchestrator.cpython-312.pyc"), $"{rng.Next()}");

            var pytestCacheDir = Path.Combine(bundle.Root, "skills", $"orchestrator-{seed}", "tests", ".pytest_cache");
            Directory.CreateDirectory(pytestCacheDir);
            File.WriteAllText(Path.Combine(pytestCacheDir, "CACHEDIR.TAG"), "irrelevant");

            var after = DeployManifestHash.Compute(bundle.Root);

            after.Should().Be(before, Counterexample(seed, bundle));
        }
    }

    private static string Counterexample(int seed, Bundle bundle) =>
        $"seed={seed}, root={bundle.Root}";

    private sealed class Bundle : IDisposable
    {
        public required string Root { get; init; }
        public required List<string> Files { get; init; }

        public static Bundle Generate(Random rng)
        {
            var root = Directory.CreateTempSubdirectory("deploy-manifest-hash-").FullName;
            var manifestDir = Path.Combine(root, "specs", "manifest");
            var skillsDir = Path.Combine(root, "skills");
            Directory.CreateDirectory(manifestDir);
            Directory.CreateDirectory(skillsDir);

            var files = new List<string>();

            var yamlCount = rng.Next(0, 4);
            for (var i = 0; i < yamlCount; i++)
            {
                var path = Path.Combine(manifestDir, $"part-{i}.yaml");
                File.WriteAllText(path, $"seed-content: {rng.Next()}\n");
                files.Add(path);
            }

            var skillCount = rng.Next(0, 4);
            for (var i = 0; i < skillCount; i++)
            {
                var dir = Path.Combine(skillsDir, $"skill-{i}");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "SKILL.md");
                File.WriteAllText(path, $"# skill {i}\ncontent {rng.Next()}\n");
                files.Add(path);
            }

            return new Bundle { Root = root, Files = files };
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
