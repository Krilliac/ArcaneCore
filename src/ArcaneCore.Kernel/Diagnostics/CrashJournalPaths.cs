using System.Reflection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>Where each daemon keeps its terminating-crash journal: one file per daemon kind (the process name), so the Realm and the World never read or delete each other's records. Two processes of the same name in one bin directory still share a journal.</summary>
public static class CrashJournalPaths
{
    /// <summary>The file the shared journal of older builds used (every daemon in one bin directory wrote to it).</summary>
    public const string LegacyFileName = "crash-faults.jsonl";

    /// <summary>This process's name: the entry assembly's simple name (<c>ArcaneCore.World</c>, <c>ArcaneCore.Realm</c>), or the process name without an entry assembly.</summary>
    public static string ProcessName()
    {
        if (Assembly.GetEntryAssembly()?.GetName().Name is { Length: > 0 } name)
        {
            return name;
        }

        using var current = System.Diagnostics.Process.GetCurrentProcess();
        return current.ProcessName;
    }

    /// <summary><c>crash-faults.&lt;name&gt;.jsonl</c> in <paramref name="directory"/>, with characters a file name cannot hold replaced by '_'.</summary>
    public static string PathFor(string directory, string processName)
        => Path.Combine(directory, $"crash-faults.{SafeName(processName)}.jsonl");

    /// <summary>
    /// <c>crash-report.&lt;name&gt;.&lt;yyyyMMdd-HHmmss&gt;Z.txt</c> in <paramref name="directory"/>: the file the crash dialog
    /// (<c>Diagnostics:CrashDialog</c>) writes its text and the full crash report to. Written only when that dialog is shown.
    /// </summary>
    public static string ReportPathFor(string directory, string processName, DateTime utc)
        => Path.Combine(directory, $"crash-report.{SafeName(processName)}.{utc.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}Z.txt");

    private static string SafeName(string processName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = string.Create(processName.Length, processName, (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = Array.IndexOf(invalid, source[i]) >= 0 ? '_' : source[i];
            }
        });
        return safe.Length == 0 ? "process" : safe;
    }

    /// <summary>The legacy shared journal in <paramref name="directory"/>.</summary>
    public static string LegacyPath(string directory) => Path.Combine(directory, LegacyFileName);

    /// <summary>
    /// Point <paramref name="ledger"/> at this process's journal in <paramref name="directory"/>, migrate the legacy shared journal
    /// (own records replayed, others kept, unattributed ones discarded and warned about) and replay the per-process journal.
    /// Returns the number of crash records replayed from both files.
    /// </summary>
    public static int ConfigureAndReplay(FaultLedger ledger, string directory, string processName, ILogger logger)
    {
        string journal = PathFor(directory, processName);
        ledger.ConfigureCrashJournal(journal, processName);
        (int legacyReplayed, int legacyDropped) = ledger.MigrateLegacyCrashJournal(LegacyPath(directory), processName);
        if (legacyDropped > 0)
        {
            logger.LogWarning(
                "Diagnostics: discarded {Count} unattributed record(s) from the legacy shared crash journal; it named no writing process, so these pre-upgrade crashes will not reach QA",
                legacyDropped);
        }

        int replayed = ledger.ReplayCrashJournal(journal) + legacyReplayed;
        if (replayed > 0)
        {
            logger.LogWarning("Diagnostics: replayed {Count} prior terminating crash fault(s) from {Path} into FaultLedger", replayed, journal);
        }

        return replayed;
    }
}
