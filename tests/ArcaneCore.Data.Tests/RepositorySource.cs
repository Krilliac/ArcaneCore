using System.Runtime.CompilerServices;

namespace ArcaneCore.Data.Tests;

/// <summary>Find this test assembly's ArcaneCore checkout even when its DLL is built under --artifacts-path.</summary>
internal static class RepositorySource
{
    public static string? FindRoot(bool requireGit = false)
    {
        // Prefer the checkout that compiled this assembly; an artifacts-path run may start from another repository.
        foreach (string start in new[] { SourceDirectory(), AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (string? dir = start; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                if (!File.Exists(Path.Combine(dir, "ArcaneCore.slnx"))) continue;
                if (requireGit && !File.Exists(Path.Combine(dir, ".git")) && !Directory.Exists(Path.Combine(dir, ".git")))
                    continue;
                return dir;
            }
        }

        return null;
    }

    public static string? FindFile(params string[] path)
    {
        string? root = FindRoot();
        if (root is null) return null;
        string file = Path.Combine([root, .. path]);
        return File.Exists(file) ? file : null;
    }

    private static string SourceDirectory([CallerFilePath] string sourceFile = "") => Path.GetDirectoryName(sourceFile)!;
}
