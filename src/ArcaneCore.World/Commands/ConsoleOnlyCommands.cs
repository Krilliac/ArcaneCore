using System.Collections.Concurrent;

namespace ArcaneCore.World.Commands;

/// <summary>
/// The commands only the true server console may run (design 1.7, docs/ops/remote-admin.md): paths as the command table resolves them
/// (<c>CommandLookup.Path</c>, for example <c>server remote token rotate</c>), a path also covering the commands below it. There is no Console
/// account level, so a command a plan marks Console-only is listed here, and the action channel refuses it with <c>console-only</c> for every
/// tool actor (the local actor and web actors run through <c>CommandContext.ForRemote</c>, never as the console) before it executes, even when the
/// handler forgot its own check. Thread-safe; registrations are process-wide and are made at startup.
/// </summary>
public static class ConsoleOnlyCommands
{
    private static readonly ConcurrentDictionary<string, string[]> Registered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mark command paths (words separated by single spaces, case-insensitive, no leading dot) as Console-only. Repeating a path is harmless.</summary>
    public static void Register(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (string path in paths)
        {
            string[] words = Words(path);
            if (words.Length == 0)
            {
                throw new ArgumentException("a Console-only command path must not be empty", nameof(paths));
            }

            Registered[string.Join(' ', words)] = words;
        }
    }

    /// <summary>Remove paths added by <see cref="Register"/> (a path that is not registered is ignored). For tests that register a scratch path in the process-wide registry.</summary>
    public static void Unregister(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (string path in paths)
        {
            Registered.TryRemove(string.Join(' ', Words(path)), out _);
        }
    }

    /// <summary>Whether the command at <paramref name="resolvedPath"/> (a <c>CommandLookup.Path</c>) is Console-only, itself or by a group above it.</summary>
    public static bool IsConsoleOnly(string resolvedPath)
    {
        string[] path = Words(resolvedPath);
        foreach (string[] entry in Registered.Values)
        {
            if (entry.Length <= path.Length && entry.Zip(path).All(pair => pair.First.Equals(pair.Second, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] Words(string path) => (path ?? string.Empty).Trim().TrimStart('.', '!').Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
