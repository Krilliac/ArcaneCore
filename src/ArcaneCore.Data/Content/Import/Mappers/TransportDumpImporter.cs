using System.Globalization;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Data.World.Transports;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// Maps <c>transports</c> of a cmangos classic-db dump (<c>entry, name, period</c>; one row per ship) or a vmangos dump
/// (<c>entry, build, name, period</c>; one row per ship and build) into the world table that
/// <c>TransportMgr::LoadTransportTemplates</c> reads (TransportWorldDataModule). A cmangos row has no build and is stored with
/// build 0 (every build); the world picks the newest build at or below 5875 at load. The dumps are GPL data and are never committed.
/// <para>
/// It also keeps the dump's <c>gameobject_template</c> rows of type 15 (MO_TRANSPORT: data0 TaxiPath id, data1 speed, data2
/// acceleration), mapped as the full game object import maps them (<see cref="GameObjectLootDumpImporter.MapTemplate"/>), so the
/// content refresh can bring an older world's ship templates up to the dump without touching any other object.
/// </para>
/// </summary>
public sealed class TransportDumpImporter
{
    private const string Table = TransportWorldDataModule.Table;

    /// <summary>GAMEOBJECT_TYPE_MO_TRANSPORT.</summary>
    public const uint ShipType = 15;

    private readonly SortedDictionary<(uint Entry, ushort Build), TransportRow> _rows = [];

    // Every gameobject_template entry's newest row up to 1.12: whether an entry is a ship is decided by that row.
    private readonly SortedDictionary<uint, (int Patch, GameObjectTemplateRow Row)> _objects = [];

    /// <summary>Whether any dump read so far carried the table (an absent table leaves the database untouched).</summary>
    public bool SawTable { get; private set; }

    public IReadOnlyCollection<TransportRow> Rows => _rows.Values;

    /// <summary>The dump's ship templates (<c>gameobject_template</c> type 15), by entry; for a vmangos dump the newest patch up to 1.12.</summary>
    public IReadOnlyList<GameObjectTemplateRow> Ships => [.. _objects.Values.Select(o => o.Row).Where(r => r.Type == ShipType)];

    /// <summary>Read one dump (call again for further files; a later row replaces an earlier one with the same entry and build).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            if (string.Equals(row.Table, "gameobject_template", StringComparison.OrdinalIgnoreCase))
            {
                ReadShip(row);
                continue;
            }

            if (!string.Equals(row.Table, Table, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Replace the world's templates of the dump's ships (inside the caller's <see cref="ImportTransaction"/>): each entry the dump has as a
    /// type 15 row is written as the dump has it; every other <c>gameobject_template</c> row stays as it is. No ship read: nothing changes.
    /// </summary>
    public async Task<int> ReplaceShipTemplatesAsync(WorldDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        IReadOnlyList<GameObjectTemplateRow> ships = Ships;
        if (ships.Count == 0)
        {
            return 0;
        }

        List<uint> entries = [.. ships.Select(s => s.Entry)]; // a List: an array's Contains binds to the span overload, which EF cannot translate
        await db.Set<GameObjectTemplateRow>().Where(t => entries.Contains(t.Entry)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, ships, cancellationToken).ConfigureAwait(false);
        return ships.Count;
    }

    // A later row of the same entry replaces an earlier one unless it belongs to an older patch (vmangos patch-versioned templates),
    // as in the full game object import.
    private void ReadShip(DumpRow row)
    {
        if (GameObjectLootDumpImporter.MapTemplate(row) is not { } mapped)
        {
            return;
        }

        if (_objects.TryGetValue(mapped.Row.Entry, out var existing) && existing.Patch > mapped.Patch)
        {
            return;
        }

        _objects[mapped.Row.Entry] = mapped;
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
