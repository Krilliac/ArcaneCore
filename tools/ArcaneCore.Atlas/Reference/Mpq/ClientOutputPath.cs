using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Reference.Mpq;

/// <summary>
/// Decides where <c>client.read</c> may write. Client data must never end up in a git working tree (it is not ours to
/// commit) or inside the client installation itself (which stays read-only). Stateless and thread-safe.
/// </summary>
public static class ClientOutputPath
{
    /// <summary>
    /// Validates <paramref name="requested"/> and returns the full path to write to.
    /// </summary>
    /// <param name="requested">The caller's <c>out</c> argument; must be absolute.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    /// <param name="protectedRoots">Folders the output must not be inside of (for example the client root).</param>
    /// <exception cref="CommandException">The path is relative, a directory, inside a git working tree or a protected folder, or already exists without <paramref name="overwrite"/>.</exception>
    public static string Check(string requested, bool overwrite, IEnumerable<string> protectedRoots)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(protectedRoots);
        if (string.IsNullOrWhiteSpace(requested) || !Path.IsPathFullyQualified(requested))
        {
            throw Refuse($"'out' must be an absolute path; '{requested}' is not.");
        }

        string full = Path.GetFullPath(requested);
        if (full.EndsWith(Path.DirectorySeparatorChar) || full.EndsWith(Path.AltDirectorySeparatorChar) || Directory.Exists(full))
        {
            throw Refuse($"'out' names a directory ('{full}'); give a file path.");
        }

        string real = ResolveLinks(full);
        if (FindWorkingTree(real) is { } tree)
        {
            throw Refuse($"'out' is inside the git working tree '{tree}'; client data must not be written into a repository.");
        }

        foreach (string root in protectedRoots)
        {
            string protectedReal = ResolveLinks(Path.GetFullPath(root));
            if (IsInside(real, protectedReal))
            {
                throw Refuse($"'out' is inside '{protectedReal}', which is read-only for Atlas.");
            }
        }

        if (File.Exists(full) && !overwrite)
        {
            throw Refuse($"'{full}' already exists; pass overwrite=true to replace it.");
        }

        return full;
    }

    /// <summary>
    /// The nearest enclosing git working tree of <paramref name="fullPath"/> (a folder holding a <c>.git</c> directory or a
    /// <c>.git</c> file, as worktrees have), or null.
    /// </summary>
    public static string? FindWorkingTree(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        for (string? directory = Path.GetDirectoryName(fullPath); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            string marker = Path.Combine(directory, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return directory;
            }
        }

        return null;
    }

    /// <summary>
    /// Replaces every existing symbolic link or junction along <paramref name="fullPath"/> by its final target, so a link
    /// into a repository cannot hide the repository. Components that do not exist yet are kept as given.
    /// </summary>
    public static string ResolveLinks(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        string current = root;
        string[] parts = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            string next = Path.Combine(current, part);
            FileSystemInfo? info = Directory.Exists(next) ? new DirectoryInfo(next) : File.Exists(next) ? new FileInfo(next) : null;
            if (info?.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                next = target.FullName;
            }

            current = next;
        }

        return current;
    }

    private static bool IsInside(string path, string folder)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    private static CommandException Refuse(string message) => new(CommandException.BadArguments, $"client.read: {message}");
}
