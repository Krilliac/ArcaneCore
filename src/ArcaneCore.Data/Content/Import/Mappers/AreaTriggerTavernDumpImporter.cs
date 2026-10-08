using System.Globalization;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.Rest;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// Maps <c>areatrigger_tavern</c> of a cmangos classic-db or vmangos dump (<c>id, name</c>; only the id is stored, as vmangos
/// ObjectMgr::LoadTavernAreaTriggers reads only it). Whether the id names a real area trigger is checked when the rest feature
/// loads the table. The dumps are GPL data and are never committed.
/// </summary>
public sealed class AreaTriggerTavernDumpImporter
{
    private readonly SortedSet<uint> _ids = [];

    /// <summary>Whether any dump read so far carried the table (an absent table leaves the database untouched).</summary>
    public bool SawTable { get; private set; }

    public IReadOnlyCollection<uint> Ids => _ids;

    /// <summary>Read one dump (call again for further files).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is DumpRow row && string.Equals(row.Table, AreaTriggerTavernDataModule.Table, StringComparison.OrdinalIgnoreCase))
            {
                SawTable = true;
                if (!row.TryGet(out string? raw, "id") || raw is null
                    || !uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
                {
                    throw new ImportSchemaException(AreaTriggerTavernDataModule.Table, "id", $"table `{AreaTriggerTavernDataModule.Table}`: id '{raw}' is not an unsigned number");
                }

                _ids.Add(id);
            }
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

        await db.Set<AreaTriggerTavernRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, _ids.Select(id => new AreaTriggerTavernRow { Id = id }), cancellationToken).ConfigureAwait(false);
        return _ids.Count;
    }
}
