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
        string[] projects = [.. new[] { "src", "tests", "tools" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*.csproj", SearchOption.AllDirectories))
            .Where(p => !IsBuildOutput(root, p) && GovernedByRepoProps(root, p))];

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
    public void EveryScriptedRestore_RunsInLockedMode(string file)
    {
        string[] restores = [.. Regex.Matches(RepoRoot.ReadText(file), @"dotnet restore[^\r\n&]*").Select(m => m.Value.TrimEnd(' ', '\\'))];
        Assert.NotEmpty(restores);
        Assert.All(restores, r => Assert.Contains("--locked-mode", r, StringComparison.Ordinal));
    }

    [Fact]
    public void TheContributingGuide_SaysHowToUpdateTheLockFiles()
    {
        string guide = RepoRoot.ReadText("docs/guide/contributing.md");
        Assert.Contains("packages.lock.json", guide, StringComparison.Ordinal);
        Assert.Contains("restore ArcaneCore.slnx --force-evaluate", guide, StringComparison.Ordinal);
    }

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
