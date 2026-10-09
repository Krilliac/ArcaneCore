using System.Runtime.CompilerServices;

namespace ArcaneCore.World.Tests;

/// <summary>Find the checkout that compiled these tests when the DLL lives under --artifacts-path.</summary>
internal static class RepositorySource
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

    private static string SourceDirectory([CallerFilePath] string sourceFile = "") => Path.GetDirectoryName(sourceFile)!;
}
