using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Render;

/// <summary>
/// Where the render and export commands may write. Output is refused inside a repository working tree (a directory with a
/// <c>.git</c> entry above it, or the ArcaneCore solution file): captures and exports are derived from client data, which
/// must never end up where it could be committed. Files are written to a sibling temporary file and moved into place, so a
/// failed export never leaves a half-written file under the requested name.
/// </summary>
internal static class OutputPaths
{
    /// <summary>The solution file that marks the ArcaneCore source tree even when it is not a git checkout (a source archive).</summary>
    private const string SolutionMarker = "ArcaneCore.slnx";

    /// <summary>Most directory links resolved for one path (the POSIX <c>SYMLOOP_MAX</c> order of magnitude), so a link cycle ends.</summary>
    private const int MaxLinks = 40;

    /// <summary>
    /// Validates an <c>out</c> argument and returns its full path. The path must end with <paramref name="extension"/>,
    /// must not name an existing directory and must lie outside every repository working tree. A relative path is only
    /// accepted when <paramref name="allowRelative"/> is true (the command runs inside the caller's own process, so the
    /// current directory is unambiguous).
    /// </summary>
    /// <exception cref="CommandException"><see cref="CommandException.BadArguments"/> with the reason.</exception>
    public static string Resolve(string command, string requested, string extension, bool allowRelative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        if (string.IsNullOrWhiteSpace(requested))
        {
            throw Bad(command, "argument 'out' must be a file path.");
        }

        if (!allowRelative && !Path.IsPathFullyQualified(requested))
        {
            throw Bad(command, $"argument 'out' must be an absolute path (got '{requested}'); a running instance cannot know your current directory.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(requested);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Bad(command, $"argument 'out' is not a usable path: {e.Message}");
        }

        if (!full.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            throw Bad(command, $"argument 'out' must end with {extension} (got '{full}').");
        }

        if (Directory.Exists(full))
        {
            throw Bad(command, $"argument 'out' names an existing directory ('{full}').");
        }

        string? root;
        try
        {
            root = FindWorkingTree(full);
        }
        catch (IOException e)
        {
            throw Bad(command, $"argument 'out' ('{full}') cannot be checked: {e.Message}");
        }

        if (root is not null)
        {
            throw Bad(command, $"argument 'out' ('{full}') is inside the repository working tree '{root}'. Write outside every checkout: captures and exports are derived from client data.");
        }

        return full;
    }

    /// <summary>
    /// The nearest working tree root above <paramref name="fullPath"/>, or null. Both the path as written and the path the
    /// operating system would really write to are checked, so a directory link (symbolic link or junction) that points into
    /// a checkout does not hide it. Each link on the way is resolved once, at most <see cref="MaxLinks"/> per path.
    /// </summary>
    /// <exception cref="IOException">The directories of the path lead through more than <see cref="MaxLinks"/> links (a cycle).</exception>
    public static string? FindWorkingTree(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        return RootAtOrAbove(directory) ?? RootAtOrAbove(RealDirectory(directory));
    }

    /// <summary>
    /// Creates the target directory and writes through <paramref name="write"/> into a temporary file that replaces
    /// <paramref name="path"/> on success. When <paramref name="shouldCommit"/> is given and returns false after the write,
    /// the temporary file is discarded and the method returns false.
    /// </summary>
    public static bool WriteAtomically(string path, Action<Stream> write, Func<bool>? shouldCommit = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        string temp = PrepareTemp(path);
        bool done = false;
        try
        {
            using (FileStream stream = OpenTemp(temp))
            {
                write(stream);
            }

            if (shouldCommit is not null && !shouldCommit())
            {
                return false;
            }

            File.Move(temp, path, overwrite: true);
            done = true;
            return true;
        }
        finally
        {
            if (!done)
            {
                DeleteQuietly(temp);
            }
        }
    }

    /// <summary>Creates the directory of <paramref name="path"/> and returns an unused sibling name for staging it.</summary>
    public static string PrepareTemp(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string directory = Path.GetDirectoryName(path) ?? throw new ArgumentException($"'{path}' has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    }

    /// <summary>Creates the staging file exclusively.</summary>
    public static FileStream OpenTemp(string temp)
        => new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);

    /// <summary>Removes a staging file, ignoring a file that is already gone or locked.</summary>
    public static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover .tmp is harmless and the original error is the one worth reporting.
        }
    }

    private static string? RootAtOrAbove(string directory)
    {
        for (string? current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (IsRoot(current))
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="directory"/> with every directory link on the way replaced by its target, like <c>realpath</c>: the
    /// path is walked component by component and a link restarts the walk at its target, so links inside targets and
    /// links in the middle of the path are resolved too. Components that do not exist yet are kept as they are.
    /// </summary>
    private static string RealDirectory(string directory)
    {
        string root = Path.GetPathRoot(directory) ?? string.Empty;
        var pending = new Stack<string>(Components(directory[root.Length..]).Reverse());
        string current = root;
        int links = 0;
        while (pending.TryPop(out string? name))
        {
            string next = Path.Combine(current, name);
            if (LinkTargetOf(next) is { } target)
            {
                if (++links > MaxLinks)
                {
                    throw new IOException($"'{directory}' leads through more than {MaxLinks} directory links.");
                }

                current = Path.GetPathRoot(target) ?? string.Empty;
                foreach (string part in Components(target[current.Length..]).Reverse())
                {
                    pending.Push(part);
                }

                continue;
            }

            current = next;
        }

        return current;
    }

    private static IEnumerable<string> Components(string relative)
        => relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The absolute target of the link at <paramref name="path"/> (one hop), or null when it is not a link or cannot be read.</summary>
    private static string? LinkTargetOf(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: false)?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsRoot(string directory)
        => Directory.Exists(Path.Combine(directory, ".git"))
            || File.Exists(Path.Combine(directory, ".git"))
            || File.Exists(Path.Combine(directory, SolutionMarker));

    private static CommandException Bad(string command, string message) => new(CommandException.BadArguments, $"{command}: {message}");
}
