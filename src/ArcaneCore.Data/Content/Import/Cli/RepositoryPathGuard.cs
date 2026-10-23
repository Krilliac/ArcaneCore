using System.Diagnostics;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// Keeps imported content out of version control. The source data is GPL data with Blizzard
/// copyright material in it (classic-db COPYRIGHT.md); a database or report built from it must
/// not land in a repository by accident. A path inside a git work tree is allowed only when
/// <c>git check-ignore</c> says it is ignored; when git cannot be run or answers anything
/// other than "ignored" or "not ignored", the path is refused (fail closed).
/// </summary>
public static class RepositoryPathGuard
{
    private static readonly TimeSpan s_gitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Link hops followed before the path is refused as a probable loop.</summary>
    private const int MaxLinks = 40;

    /// <summary>Null when <paramref name="path"/> may be written; otherwise the reason it may not.</summary>
    public static string? Check(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string requested = Path.GetFullPath(path);
        string? full = ResolveLinks(requested);
        if (full is null)
        {
            return $"'{requested}' goes through a link that cannot be resolved (loop, too many links, or unreadable link); refusing to write it";
        }

        string? root = FindWorkTreeRoot(Path.GetDirectoryName(full));
        if (root is null)
        {
            return null;
        }

        try
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in new[] { "-C", root, "check-ignore", "-q", "--", full })
            {
                info.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(info)
                ?? throw new InvalidOperationException("git did not start");
            // Drain both pipes so a chatty git cannot block on a full buffer.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(s_gitTimeout))
            {
                process.Kill(entireProcessTree: true);
                return $"'{full}' is inside the git work tree '{root}' and git check-ignore did not answer in time; refusing to write it";
            }

            Task.WaitAll(stdout, stderr);
            return process.ExitCode switch
            {
                0 => null,
                1 => $"'{full}' is inside the git work tree '{root}' and is not git-ignored; imported content is GPL/copyrighted data and must stay out of the repository. " +
                     "Choose a path outside the work tree, or add it to .gitignore",
                _ => $"'{full}' is inside the git work tree '{root}' but git check-ignore failed (exit {process.ExitCode}): {stderr.Result.Trim()}",
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"'{full}' is inside the git work tree '{root}' and git could not be run to check whether it is ignored ({ex.Message}); refusing to write it";
        }
    }

    /// <summary>
    /// Resolves every directory or file link (junction or symlink) along <paramref name="full"/> so the guard
    /// judges where the bytes really land. Walks like realpath: each link target is spliced in and the walk
    /// continues through it, so links inside a target are followed too, up to <see cref="MaxLinks"/> hops.
    /// Components that do not exist yet are kept as written. Null when a link cannot be resolved (loop, budget
    /// exceeded, unreadable), so the caller can fail closed.
    /// </summary>
    private static string? ResolveLinks(string full)
    {
        string? pathRoot = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(pathRoot))
        {
            return full;
        }

        var pending = new Stack<string>(Enumerable.Reverse(full[pathRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)));
        string current = pathRoot;
        int links = 0;
        while (pending.Count > 0)
        {
            current = Path.Combine(current, pending.Pop());
            FileSystemInfo? info = Directory.Exists(current) ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current)
                : null;
            if (info is null)
            {
                continue;
            }

            try
            {
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null)
                {
                    continue;
                }

                if (++links > MaxLinks)
                {
                    return null;
                }

                string targetPath = target.FullName;
                string targetRoot = Path.GetPathRoot(targetPath) ?? string.Empty;
                foreach (string part in Enumerable.Reverse(targetPath[targetRoot.Length..].Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)))
                {
                    pending.Push(part);
                }

                current = targetRoot;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>The nearest ancestor holding a <c>.git</c> directory or file (a linked worktree has a file), or null.</summary>
    private static string? FindWorkTreeRoot(string? directory)
    {
        for (string? current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            string marker = Path.Combine(current, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return current;
            }
        }

        return null;
    }
}
