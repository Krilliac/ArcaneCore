namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// Finds the compiling checkout for documentation checks. Not finding it throws; a docs check must fail, never skip
/// (a skipped check is indistinguishable from a passing one).
/// </summary>
internal static class RepoRoot
{
    public static string Find() => RepositorySource.RequireRoot();

    /// <summary>The text of a repo-relative file; a missing file throws.</summary>
    public static string ReadText(string relativePath)
    {
        string path = Path.Combine(Find(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllText(path);
    }
}
