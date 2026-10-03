using System.Globalization;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>What a kill-reputation import read and wrote.</summary>
public sealed record ReputationOnKillImportReport(int Entries, int SkippedRows, IReadOnlyList<string> Warnings);

/// <summary>
/// Maps <c>creature_onkill_reputation</c> of a cmangos classic-db or vmangos dump into
/// <see cref="CreatureOnKillReputationRow"/> by column name. vmangos rows are patch-versioned: the
/// row with the highest <c>patch</c> not above <see cref="MaxPatch"/> wins
/// (<c>ObjectMgr::LoadReputationOnKill</c>, src/game/ObjectMgr.cpp:8902). Rows for creatures that have
/// no template or factions that are not in Faction.dbc are skipped by vmangos at load
/// (:8935-8957); the importer keeps them (it has neither set), and <c>verify</c> counts the
/// creatures. The dumps are GPL data and are never committed.
/// </summary>
public sealed class OnKillReputationDumpImporter
{
    /// <summary>vmangos WowPatch for 1.12.1 (shared with the other dump importers).</summary>
    public const int MaxPatch = CreatureDumpImporter.MaxPatch;

    private static readonly RowMapper<CreatureOnKillReputationRow> s_mapper = new();

    private readonly SpecKeyReader _keys = new();
    private readonly Dictionary<uint, (int Patch, CreatureOnKillReputationRow Row)> _rows = [];
    private readonly MapDiagnostics _diagnostics = new();
    private int _skipped;

    /// <summary>Whether a column of the table is read (the spec's mapped-column rule).</summary>
    internal static bool ReadsColumn(string column) => s_mapper.Maps(column) || column.Equals("patch", StringComparison.OrdinalIgnoreCase);

    /// <summary>Read one dump (call again for further files; later rows replace earlier ones with the same key).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is DumpRow row && row.Table.Equals("creature_onkill_reputation", StringComparison.OrdinalIgnoreCase))
            {
                ReadRow(row);
            }
        }
    }

    /// <summary>The rows that would be written (for inspection and tests).</summary>
    public IReadOnlyCollection<CreatureOnKillReputationRow> Snapshot() => [.. _rows.Values.Select(r => r.Row)];

    public ReputationOnKillImportReport BuildReport() => new(_rows.Count, _skipped, _diagnostics.Samples);

    /// <summary>
    /// Write the rows atomically with the contract of <see cref="ImportTransaction"/>. With
    /// <paramref name="replace"/> the table is emptied first; without it an existing key fails the
    /// write and nothing changes.
    /// </summary>
    public async Task<ReputationOnKillImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        IReadOnlyCollection<CreatureOnKillReputationRow> rows = Snapshot();
        await ImportTransaction.RunAsync(db, async token =>
        {
            bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                if (replace)
                {
                    await db.Set<CreatureOnKillReputationRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                await ImportBatch.InsertAsync(db, rows, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }

    private void ReadRow(DumpRow row)
    {
        uint[] key = _keys.Read(row, "creature_onkill_reputation");
        int patch = row.TryGet(out string? raw, "patch") && !string.IsNullOrEmpty(raw)
            ? (int)double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)
            : 0;
        if (patch > MaxPatch || (_rows.TryGetValue(key[0], out var existing) && existing.Patch > patch))
        {
            _skipped++;
            return;
        }

        _rows[key[0]] = (patch, s_mapper.Map(row, _diagnostics));
    }
}
