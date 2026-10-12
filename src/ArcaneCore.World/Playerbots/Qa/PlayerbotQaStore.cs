using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>The create/comment times the hourly caps are counted from (they must survive a restart, or a restart loop would flood GitHub).</summary>
internal sealed record QaLedgerState(DateTime[] CreatesUtc, DateTime[] CommentsUtc, DateTime[]? SpellCreatesUtc = null);

/// <summary><c>qa/findings.json</c>: the format version, the bot-hash salt, the findings, the bot hashes and the publish ledger.</summary>
internal sealed record QaStoreFile
{
    public int FormatVersion { get; init; } = PlayerbotQaStore.CurrentFormat;

    public string SaltBase64 { get; init; } = string.Empty;

    public IReadOnlyList<QaFindingView> Findings { get; init; } = [];

    public IReadOnlyDictionary<string, ulong[]> Bots { get; init; } = new Dictionary<string, ulong[]>();

    /// <summary>The ids of findings merged into another (and the issue of each that had one): never reused, and the absorbed issue is closed as a duplicate.</summary>
    public IReadOnlyList<QaAbsorbed> Absorbed { get; init; } = [];

    public QaLedgerState Ledger { get; init; } = new([], []);
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
            File.WriteAllText(temp, JsonSerializer.Serialize(file, QaStoreJsonContext.Default.QaStoreFile));
            File.Move(temp, FilePath, overwrite: true);
            _failedWriteLogged = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_failedWriteLogged)
            {
                _failedWriteLogged = true;
                logger.LogError("Bot QA could not save {Path} ({Type}); it retries on the next pass", FilePath, ex.GetType().Name);
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
