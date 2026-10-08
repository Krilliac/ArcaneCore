using System.Globalization;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.Transports;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// Maps <c>transports</c> of a cmangos classic-db dump (<c>entry, name, period</c>; one row per ship) or a vmangos dump
/// (<c>entry, build, name, period</c>; one row per ship and build) into the world table that
/// <c>TransportMgr::LoadTransportTemplates</c> reads (TransportWorldDataModule). A cmangos row has no build and is stored with
/// build 0 (every build); the world picks the newest build at or below 5875 at load. The dumps are GPL data and are never committed.
/// </summary>
public sealed class TransportDumpImporter
{
    private const string Table = TransportWorldDataModule.Table;

    private readonly SortedDictionary<(uint Entry, ushort Build), TransportRow> _rows = [];

    /// <summary>Whether any dump read so far carried the table (an absent table leaves the database untouched).</summary>
    public bool SawTable { get; private set; }

    public IReadOnlyCollection<TransportRow> Rows => _rows.Values;

    /// <summary>Read one dump (call again for further files; a later row replaces an earlier one with the same entry and build).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is not DumpRow row || !string.Equals(row.Table, Table, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SawTable = true;
            uint entry = Required(row, "entry");
            uint build = row.TryGet(out string? rawBuild, "build") && rawBuild is not null ? Parse("build", rawBuild) : 0;
            if (build > ushort.MaxValue)
            {
                throw new ImportSchemaException(Table, "build", $"table `{Table}`: build {build} of entry {entry} does not fit the column");
            }

            _rows[(entry, (ushort)build)] = new TransportRow
            {
                Entry = entry,
                Build = (ushort)build,
                Name = row.TryGet(out string? name, "name") ? name ?? string.Empty : string.Empty,
                Period = row.TryGet(out string? rawPeriod, "period") && rawPeriod is not null ? Parse("period", rawPeriod) : 0,
            };
        }
    }

    /// <summary>Replace the table with the rows read (inside the caller's <see cref="ImportTransaction"/>); nothing read: nothing changes.</summary>
    public async Task<int> ReplaceAsync(WorldDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!SawTable)
        {
            return 0;
        }

        await db.Set<TransportRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, _rows.Values, cancellationToken).ConfigureAwait(false);
        return _rows.Count;
    }

    private static uint Required(DumpRow row, string column)
        => row.TryGet(out string? raw, column) && raw is not null
            ? Parse(column, raw)
            : throw new ImportSchemaException(Table, column, $"table `{Table}`: the column `{column}` is missing or NULL");

    private static uint Parse(string column, string raw)
        => uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out uint value)
            ? value
            : throw new ImportSchemaException(Table, column, $"table `{Table}`, column `{column}`: value '{raw}' is not an unsigned number");
}
