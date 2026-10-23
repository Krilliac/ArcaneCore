using System.Runtime.CompilerServices;

namespace ArcaneCore.TestSupport;

/// <summary>
/// Find the ArcaneCore checkout that compiled the tests even when their DLL lives under --artifacts-path: the checkout holding this
/// source file first, then the test binaries' location and the working directory. <see cref="FindRoot"/> answers null so a test can
/// skip; <see cref="RequireRoot"/> throws so a docs or hygiene check fails instead of skipping.
/// </summary>
public static class RepositorySource
{
    public static string RequireRoot()
    {
        foreach (string start in new[] { SourceDirectory(), AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (string? dir = start; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (File.Exists(Path.Combine(dir, "ArcaneCore.slnx"))) return dir;
        }

        throw new InvalidOperationException("ArcaneCore.slnx was not found in the compiling checkout or test location");
    }

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
