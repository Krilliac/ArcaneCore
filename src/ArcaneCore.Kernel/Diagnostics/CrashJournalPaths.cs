using System.Reflection;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>Where each daemon keeps its terminating-crash journal: one file per process name, so the Realm and the World never read or delete each other's records.</summary>
public static class CrashJournalPaths
{
    /// <summary>The file the shared journal of older builds used (every daemon in one bin directory wrote to it).</summary>
    public const string LegacyFileName = "crash-faults.jsonl";

    /// <summary>This process's name: the entry assembly's simple name (<c>ArcaneCore.World</c>, <c>ArcaneCore.Realm</c>), or the process name without an entry assembly.</summary>
    public static string ProcessName()
        => Assembly.GetEntryAssembly()?.GetName().Name is { Length: > 0 } name ? name : System.Diagnostics.Process.GetCurrentProcess().ProcessName;

    /// <summary><c>crash-faults.&lt;name&gt;.jsonl</c> in <paramref name="directory"/>, with characters a file name cannot hold replaced by '_'.</summary>
    public static string PathFor(string directory, string processName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = string.Create(processName.Length, processName, (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = Array.IndexOf(invalid, source[i]) >= 0 ? '_' : source[i];
            }
        });
        return Path.Combine(directory, $"crash-faults.{(safe.Length == 0 ? "process" : safe)}.jsonl");
    }

    /// <summary>The legacy shared journal in <paramref name="directory"/>.</summary>
    public static string LegacyPath(string directory) => Path.Combine(directory, LegacyFileName);
}
