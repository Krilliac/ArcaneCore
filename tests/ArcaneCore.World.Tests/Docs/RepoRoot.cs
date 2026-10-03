namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// Finds the repository root for the documentation checks: the directory above the test binary that holds
/// <c>ArcaneCore.slnx</c>. Not finding it throws; a docs check that cannot read its input must fail, never skip
/// (a skipped check is indistinguishable from a passing one).
/// </summary>
internal static class RepoRoot
{
    public static string Find()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ArcaneCore.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("ArcaneCore.slnx was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The text of a repo-relative file; a missing file throws.</summary>
    public static string ReadText(string relativePath)
    {
        string path = Path.Combine(Find(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllText(path);
    }
}
