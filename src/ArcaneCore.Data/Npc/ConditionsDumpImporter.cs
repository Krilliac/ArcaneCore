using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Npc;

/// <summary>What a conditions import read and wrote.</summary>
/// <param name="Conditions">Distinct <c>condition_entry</c> rows (a later row replaces an earlier one with the same key).</param>
/// <param name="Replaced">Rows that replaced an earlier row of the same key within the dump.</param>
/// <param name="DbVersion">The dump's <c>db_version</c> row text when it had one, so an un-updated dump is visible.</param>
public sealed record ConditionsImportReport(int Conditions, int Replaced, string? DbVersion);

/// <summary>
/// Maps the <c>conditions</c> table of a cmangos classic-db dump (columns
/// <c>condition_entry, type, value1, value2, value3, value4, flags[, comments]</c>) by column name. The numbering of the
/// type column is cmangos's (mangos-classic Conditions.h:30-82); vmangos's table has a different layout (no value3/value4)
/// and a different numbering, so a vmangos dump is refused instead of being read with the wrong meaning. The dump is GPL
/// data and is never committed; tests use hand-written rows.
/// </summary>
public sealed class ConditionsDumpImporter
{
    private readonly Dictionary<uint, ConditionRow> _rows = [];
    private int _replaced;
    private string? _dbVersion;

    /// <summary>Read one dump (call again for further files; later rows replace earlier ones with the same key).</summary>
    public void Read(TextReader dump)
    {
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            if (string.Equals(row.Table, "db_version", StringComparison.OrdinalIgnoreCase))
            {
                _dbVersion = row.TryGet(out string? version, "version") && !string.IsNullOrWhiteSpace(version)
                    ? version
                    : row.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                continue;
            }

            if (!string.Equals(row.Table, ConditionsWorldModule.Table, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!row.Has("value3") || !row.Has("value4"))
            {
                throw new InvalidDataException(
                    "the conditions rows have no value3/value4 columns: this is the vmangos layout, whose condition type numbering differs from "
                    + "cmangos (classic-db). Only cmangos-numbered dumps can be imported.");
            }

            var parsed = new ConditionRow
            {
                ConditionEntry = Unsigned(row, "condition_entry"),
                Type = Signed(row, "type"),
                Value1 = Unsigned(row, "value1"),
                Value2 = Unsigned(row, "value2"),
                Value3 = Unsigned(row, "value3"),
                Value4 = Unsigned(row, "value4"),
                Flags = checked((byte)Unsigned(row, "flags")),
            };
            if (_rows.ContainsKey(parsed.ConditionEntry))
            {
                _replaced++;
            }

            _rows[parsed.ConditionEntry] = parsed;
        }
    }

    public IReadOnlyCollection<ConditionRow> Rows => _rows.Values;

    public ConditionsImportReport BuildReport() => new(_rows.Count, _replaced, _dbVersion);

    /// <summary>
    /// Write everything read so far atomically: an empty change tracker is required, a caller's EF transaction is
    /// protected by a savepoint and stays the caller's, otherwise this method owns the transaction; a failure restores
    /// the previous rows. With <paramref name="replace"/> the table is emptied first.
    /// </summary>
    public async Task<ConditionsImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.ChangeTracker.Entries().Any())
        {
            throw new InvalidOperationException("Conditions imports require an empty change tracker; save caller changes and clear tracking, or use a dedicated context.");
        }

        IDbContextTransaction? callerTransaction = db.Database.CurrentTransaction;
        if (callerTransaction is null && System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Conditions imports require an explicit EF transaction when a caller owns the transaction; ambient transactions are not supported.");
        }

        if (callerTransaction is { SupportsSavepoints: false })
        {
            throw new InvalidOperationException("The caller's transaction does not support savepoints; the import cannot protect its existing work.");
        }

        await ImportTransaction.RunAsync(db, "Conditions", "ArcaneConditionsImport_", async ct =>
        {
            bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                if (replace)
                {
                    await db.Set<ConditionRow>().ExecuteDeleteAsync(ct).ConfigureAwait(false);
                }

                const int BatchSize = 2000;
                int pending = 0;
                foreach (ConditionRow row in _rows.Values.OrderBy(r => r.ConditionEntry))
                {
                    db.Add(row);
                    if (++pending == BatchSize)
                    {
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                        db.ChangeTracker.Clear();
                        pending = 0;
                    }
                }

                if (pending > 0)
                {
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    db.ChangeTracker.Clear();
                }
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);

        return BuildReport();
    }

    private static uint Unsigned(DumpRow row, string column)
    {
        string raw = Raw(row, column);
        return DumpRowReader.ParseUnsigned(raw, column, NumberStyles.None, static (c, r) => new InvalidDataException($"conditions.{c} \"{r}\" is not an unsigned integer"));
    }

    private static int Signed(DumpRow row, string column)
    {
        string raw = Raw(row, column);
        return DumpRowReader.ParseSigned(raw, column, NumberStyles.AllowLeadingSign, static (c, r) => new InvalidDataException($"conditions.{c} \"{r}\" is not an integer"));
    }

    private static string Raw(DumpRow row, string column)
        => row.TryGet(out string? raw, column) && !string.IsNullOrWhiteSpace(raw)
            ? raw
            : throw new InvalidDataException($"conditions row has no {column} value");
}
