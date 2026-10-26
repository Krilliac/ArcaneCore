using System.Text.RegularExpressions;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// Supply chain: every project restores against a committed packages.lock.json and every scripted restore (CI, node CI, the local
/// full gate, the Dockerfile and the deploy build) runs in locked mode, so a package graph that differs from the lock files fails
/// the restore (OpenSSF Scorecard Pinned-Dependencies, "nugetCommand not pinned by hash"). docs/guide/contributing.md says how to
/// update the lock files.
/// </summary>
public sealed class PackageLockFileTests
{
    [Fact]
    public void DirectoryBuildProps_TurnsOnLockFilesForEveryProject()
    {
        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", RepoRoot.ReadText("Directory.Build.props"), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProjectUnderTheRepoProps_HasALockFileBesideIt()
    {
        string root = RepoRoot.Find();
        string[] projects = RepoPropsProjects(root);

        // The solution alone has more than twenty projects; a handful means the walk is not looking where the projects are.
        Assert.True(projects.Length >= 20, "expected the repo's projects, found " + projects.Length);
        string[] missing = [.. projects.Where(p => !File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "packages.lock.json")))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))];
        Assert.True(missing.Length == 0, "projects without packages.lock.json (run dotnet restore and commit it):\n" + string.Join("\n", missing));
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/workflows/dotnet.yml")]
    [InlineData("apps/docker/Dockerfile")]
    [InlineData("tools/ops/ci/node-ci.ps1")]
    [InlineData("tools/ops/ci/full-suite-gate.ps1")]
    [InlineData("tools/ops/deploy/build-integrated.sh")]
    [InlineData("tools/ops/deploy/publish-binaries.sh")]
    public void EveryScriptedRestore_RunsInLockedMode(string file)
    {
        string[] restores = [.. Regex.Matches(RepoRoot.ReadText(file), @"dotnet restore[^\r\n&]*").Select(m => m.Value.TrimEnd(' ', '\\'))];
        Assert.NotEmpty(restores);
        Assert.All(restores, r => Assert.Contains("--locked-mode", r, StringComparison.Ordinal));
    }

    /// <summary>The deploy build's own build step must not restore again: an implicit restore there is unlocked and would re-resolve.</summary>
    [Fact]
    public void TheDeployBuild_BuildsFromItsLockedRestore()
    {
        // The command, not prose: the script's header comment says "the dotnet build does not emit".
        string[] builds = [.. Regex.Matches(RepoRoot.ReadText("tools/ops/deploy/build-integrated.sh"), @"dotnet build \S+\.slnx[^\r\n]*").Select(m => m.Value)];
        Assert.NotEmpty(builds);
        Assert.All(builds, b => Assert.Contains("--no-restore", b, StringComparison.Ordinal));
    }

    /// <summary>The release publish script restores in locked mode, so its publish commands must not restore again (an implicit restore is unlocked) and must not switch the audit off.</summary>
    [Fact]
    public void ThePublishScript_PublishesFromItsLockedRestoreWithAuditOn()
    {
        string script = RepoRoot.ReadText("tools/ops/deploy/publish-binaries.sh");
        string[] publishes = [.. Regex.Matches(script, @"dotnet publish [^\r\n]*").Select(m => m.Value)];
        Assert.NotEmpty(publishes);
        Assert.All(publishes, p => Assert.Contains("--no-restore", p, StringComparison.Ordinal));
        Assert.DoesNotContain("NuGetAudit=false", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The release rids live in the release-binaries matrix, in the RuntimeIdentifiers of every published executable (which is what puts the
    /// runtime targets into its lock file, so the locked restore of publish-binaries.sh works) and in the asset names the workflow header and
    /// the README promise. A rid added in one place and not the others fails the locked restore only in the release workflow, after the merge.
    /// </summary>
    [Fact]
    public void TheReleaseRids_AreTheSameInTheMatrixTheProjectsAndTheDocumentedAssets()
    {
        string workflow = RepoRoot.ReadText(".github/workflows/release-binaries.yml");
        string[] matrix = [.. Regex.Matches(workflow, @"\{ rid: (?<rid>[a-z0-9-]+),").Select(m => m.Groups["rid"].Value).Order(StringComparer.Ordinal)];
        Assert.Equal(["linux-arm64", "linux-x64", "win-arm64", "win-x64"], matrix);

        string[] exes =
        [
            "src/ArcaneCore.Realm/ArcaneCore.Realm.csproj",
            "src/ArcaneCore.World/ArcaneCore.World.csproj",
            "tools/ArcaneCore.AccountTool/ArcaneCore.AccountTool.csproj",
            "tools/ArcaneCore.ContentImporter/ArcaneCore.ContentImporter.csproj",
            "tools/ArcaneCore.DbUpgrade/ArcaneCore.DbUpgrade.csproj",
        ];
        foreach (string exe in exes)
        {
            Match declared = Regex.Match(RepoRoot.ReadText(exe), @"<RuntimeIdentifiers>(?<rids>[^<]*)</RuntimeIdentifiers>");
            Assert.True(declared.Success, exe + " declares no RuntimeIdentifiers");
            string[] rids = [.. declared.Groups["rids"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal)];
            Assert.True(matrix.SequenceEqual(rids), exe + " RuntimeIdentifiers (" + string.Join(';', rids) + ") differ from the release matrix (" + string.Join(';', matrix) + ")");
        }

        string readme = RepoRoot.ReadText("README.md");
        foreach (string rid in matrix)
        {
            Assert.Contains("arcanecore-" + rid + ".", workflow, StringComparison.Ordinal);
            Assert.Contains("arcanecore-" + rid + ".", readme, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheContributingGuide_SaysHowToUpdateTheLockFiles()
    {
        string guide = RepoRoot.ReadText("docs/guide/contributing.md");
        Assert.Contains("packages.lock.json", guide, StringComparison.Ordinal);
        Assert.Contains("restore ArcaneCore.slnx --force-evaluate", guide, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guide's refresh recipe restores exactly the locked projects that the solution restore does not reach (neither in
    /// ArcaneCore.slnx nor referenced from it), so no lock file is left out and no line names a tool that moved or went away.
    /// No CI job restores those projects, so the recipe is the only thing that refreshes their lock files.
    /// </summary>
    [Fact]
    public void TheContributingGuide_RestoresEveryLockedProjectTheSolutionDoesNotReach()
    {
        string root = RepoRoot.Find();
        HashSet<string> reached = SolutionClosure(root);

        // The walk must follow references (the fixtures are not in the solution) and see the whole solution.
        Assert.Contains("tests/hotmodule-fixtures/SampleGm/SampleGm.csproj", reached);
        Assert.True(reached.Count >= 30, "expected the solution and the projects it references, found " + reached.Count);

        string[] expected = [.. RepoPropsProjects(root).Select(p => Relative(root, p)).Where(p => !reached.Contains(p)).Order(StringComparer.Ordinal)];
        Assert.NotEmpty(expected);

        string guide = RepoRoot.ReadText("docs/guide/contributing.md").Replace("\r\n", "\n", StringComparison.Ordinal);
        Match recipe = Regex.Match(guide, @"```powershell\n(?<body>[^`]*?restore ArcaneCore\.slnx --force-evaluate[^`]*?)```");
        Assert.True(recipe.Success, "the guide's lock-file recipe (a powershell fence restoring ArcaneCore.slnx --force-evaluate) was not found");
        string[] lines = recipe.Groups["body"].Value.TrimEnd('\n').Split('\n');
        Assert.EndsWith("restore ArcaneCore.slnx --force-evaluate", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("restore ArcaneCore.slnx --locked-mode", lines[^1], StringComparison.Ordinal);
        string[] restored = [.. lines.Select(l => Regex.Match(l, @"restore (\S+\.csproj) --force-evaluate")).Where(m => m.Success).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal)];

        Assert.Equal(expected, restored);
    }

    private static string[] RepoPropsProjects(string root)
        => [.. new[] { "src", "tests", "tools" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*.csproj", SearchOption.AllDirectories))
            .Where(p => !IsBuildOutput(root, p) && GovernedByRepoProps(root, p))];

    /// <summary>The projects in ArcaneCore.slnx and every project they reference, transitively, as repo-relative '/' paths.</summary>
    private static HashSet<string> SolutionClosure(string root)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(Regex.Matches(RepoRoot.ReadText("ArcaneCore.slnx"), @"Path=""([^""]+\.csproj)""").Select(m => m.Groups[1].Value));
        while (pending.TryDequeue(out string? project))
        {
            if (!reached.Add(project))
            {
                continue;
            }

            string full = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            foreach (Match m in Regex.Matches(File.ReadAllText(full), @"<ProjectReference\s+Include=""([^""]+)"""))
            {
                string reference = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
                pending.Enqueue(Relative(root, reference));
            }
        }

        return reached;
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static bool IsBuildOutput(string root, string path)
    {
        string[] parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is "bin" or "obj");
    }

    /// <summary>True when the nearest Directory.Build.props above the project is the repo root's (tools/hotcode-spike opts out with its own).</summary>
    private static bool GovernedByRepoProps(string root, string project)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (string? dir = Path.GetDirectoryName(Path.GetFullPath(project)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "Directory.Build.props")))
            {
                return string.Equals(Path.TrimEndingDirectorySeparator(dir), fullRoot, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }
}
