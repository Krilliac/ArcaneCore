using System.Text.Json;
using System.Text.Json.Serialization;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>The create/comment times the hourly caps are counted from (they must survive a restart, or a restart loop would flood GitHub).</summary>
internal sealed record QaLedgerState(DateTime[] CreatesUtc, DateTime[] CommentsUtc, DateTime[]? SpellCreatesUtc = null, DateTime[]? StatusEditsUtc = null, DateTime[]? ServerCreatesUtc = null);

/// <summary><c>qa/findings.json</c>: the format version, the bot-hash salt, the findings, the bot hashes and the publish ledger.</summary>
internal sealed record QaStoreFile
{
    public int FormatVersion { get; init; } = PlayerbotQaStore.CurrentFormat;

    public string SaltBase64 { get; init; } = string.Empty;

    public IReadOnlyList<QaFindingView> Findings { get; init; } = [];

    public IReadOnlyDictionary<string, ulong[]> Bots { get; init; } = new Dictionary<string, ulong[]>();

    /// <summary>The ids of findings merged into another (and the issue of each that had one): never reused, and the absorbed issue is closed as a duplicate.</summary>
    public IReadOnlyList<QaAbsorbed> Absorbed { get; init; } = [];

    /// <summary>The findings <c>prune closed</c> forgot (and their merge aliases), bounded: a recurrence takes its closed issue number back.</summary>
    public IReadOnlyList<QaTombstone> Tombstones { get; init; } = [];

    public QaLedgerState Ledger { get; init; } = new([], []);

    /// <summary>The builds this store has run and when each was first seen (at most <see cref="PlayerbotQaAggregator.MaxKnownBuilds"/>): what orders builds. Absent in files from before it.</summary>
    public IReadOnlyList<QaBuildSeen> Builds { get; init; } = [];

    /// <summary>Builds that left <see cref="Builds"/> (at most <see cref="PlayerbotQaAggregator.MaxEvictedBuilds"/>), with their first-seen time: a rollback to one is still known to be old.</summary>
    public IReadOnlyList<QaBuildSeen> EvictedBuilds { get; init; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(QaStoreFile))]
internal sealed partial class QaStoreJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes <c>qa/findings.json</c>. Writes are atomic (a temp file, then replace). Fails closed: a file that cannot be read, or that was
/// written by a newer format, is never overwritten (the evidence stays for a person); persistence is then switched off for the process with one
/// Error log and <see cref="DisabledReason"/> (shown by <c>.playerbot qa</c>) while aggregation goes on in memory. Not thread-safe: the QA loop
/// (and the final stop) are its only callers, one at a time.
/// </summary>
internal sealed class PlayerbotQaStore(string directory, ILogger logger)
{
    /// <summary>
    /// Still 1: the build stamps (per finding) and the build registry are additive and nullable, a file without them loads with every build unknown, and an older
    /// build ignores the members it does not know. A bump would make a rolled-back build disable persistence and lose its hourly-cap ledger and its KnownClosed memory.
    /// </summary>
    internal const int CurrentFormat = 1;

    private bool _failedWriteLogged;

    internal string FilePath { get; } = Path.Combine(directory, "findings.json");

    /// <summary>Why persistence is off, or null.</summary>
    internal string? DisabledReason { get; private set; }

    /// <summary>The saved state; null when there is none yet or persistence got disabled (<see cref="DisabledReason"/>).</summary>
    internal QaStoreFile? Load()
    {
        string json;
        try
        {
            if (!File.Exists(FilePath)) return null;
            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Disable($"{FilePath} cannot be read ({ex.GetType().Name})");
        }

        QaStoreFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, QaStoreJsonContext.Default.QaStoreFile);
        }
        catch (JsonException ex)
        {
            FaultLedger.Shared.Record("playerbot.qa", ex);
            AnomalyLedger.Shared.Report("qa", "store-corrupt", FilePath, ex.GetType().Name, AnomalySeverity.Error);
            return Disable($"{FilePath} is not valid findings JSON ({ex.GetType().Name}); it is kept untouched");
        }

        if (file is null) return Disable($"{FilePath} is empty or null; it is kept untouched");
        if (file.FormatVersion != CurrentFormat)
        {
            return Disable($"{FilePath} has format version {file.FormatVersion}, this build reads {CurrentFormat}; it is kept untouched");
        }

        return file;
    }

    /// <summary>Write <paramref name="file"/> atomically; false when persistence is disabled or the write failed (logged once, retried next pass).</summary>
    internal bool Save(QaStoreFile file)
    {
        if (DisabledReason is not null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            string temp = FilePath + ".tmp";
            // Straight to the file: the store is ~16 MB of JSON on the live server, and serializing it to a string first put a 16 MB
            // string (32 MB of UTF-16) plus its re-encoding on the large object heap at every pass (issues #744/#755, 2026-10-17).
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024))
            {
                JsonSerializer.Serialize(stream, file, QaStoreJsonContext.Default.QaStoreFile);
            }

            File.Move(temp, FilePath, overwrite: true);
            _failedWriteLogged = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_failedWriteLogged)
            {
                _failedWriteLogged = true;
                logger.LogError(ex, "Bot QA could not save {Path} ({Type}); it retries on the next pass", FilePath, ex.GetType().Name);
            }

            return false;
        }
    }

    private QaStoreFile? Disable(string reason)
    {
        DisabledReason = reason;
        logger.LogError("Bot QA persistence is off: {Reason}", reason);
        return null;
    }
}
