using ArcaneCore.Data.Content.Import;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Maps;

/// <summary>What a map/area import wrote: every Map.dbc map, every AreaTable.dbc area, how many maps took dump instance data, and warnings.</summary>
public sealed record MapAreaDbcImportReport(int MappedMaps, int MappedAreas, int InstanceRows, IReadOnlyList<string> Warnings);

/// <summary>The rows a map/area import writes, staged before the database is opened.</summary>
public sealed record MapAreaDbcSnapshot(IReadOnlyList<MapTemplateRow> Maps, IReadOnlyList<AreaTemplateRow> Areas, int InstanceRows,
    IReadOnlyList<string> Warnings);

/// <summary>
/// <c>map_template</c> and <c>area_template</c> from the build-5875 Map.dbc and AreaTable.dbc, as vmangos fills them: every map (the
/// continents, the dungeons, raids and battlegrounds, the test maps) and every area (instance areas included, and the two areas of maps
/// Map.dbc does not list; vmangos keeps the whole AreaTable). The dungeon columns Map.dbc does not carry come from the dump
/// (<see cref="Import.InstanceTemplateDumpImporter"/>). Both tables are replaced as a whole; the readers own the DBC layouts, this class the
/// admission (every parent area exists, no parent cycle) and the replacement.
/// </summary>
public static class MapAreaDbcImporter
{
    public static Task<MapAreaDbcImportReport> ImportAsync(WorldDbContext db, string mapPath, string areaPath, bool replace,
        CancellationToken cancellationToken = default)
        => ImportAsync(db, mapPath, areaPath, null, replace, cancellationToken);

    public static async Task<MapAreaDbcImportReport> ImportAsync(WorldDbContext db, string mapPath, string areaPath,
        IReadOnlyDictionary<uint, MapInstanceData>? instances, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        MapAreaDbcSnapshot snapshot = ReadSnapshot(mapPath, areaPath, instances);
        return await ImportSnapshotAsync(db, snapshot, replace, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Read both DBCs and merge the dump's instance rows (null: the dump carried none); nothing is written.</summary>
    public static MapAreaDbcSnapshot ReadSnapshot(string mapPath, string areaPath, IReadOnlyDictionary<uint, MapInstanceData>? instances)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(areaPath);
        return Build(MapDbcReader.Load(mapPath), AreaTableDbcReader.Load(areaPath), instances);
    }

    /// <summary>Merge and validate DBC rows (see <see cref="ReadSnapshot"/>).</summary>
    public static MapAreaDbcSnapshot Build(IReadOnlyList<MapTemplateRow> maps, IReadOnlyList<AreaTemplateRow> areas,
        IReadOnlyDictionary<uint, MapInstanceData>? instances)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(areas);
        var warnings = new List<string>();
        MapTemplateRow[] mapRows = maps.OrderBy(r => r.Entry).Select(Copy).ToArray();
        if (mapRows.GroupBy(r => r.Entry).Any(g => g.Count() != 1))
            throw new InvalidDataException("Map.dbc contains duplicate map ids.");

        int merged = 0;
        if (instances is null)
        {
            uint[] instanced = mapRows.Where(r => r.MapType is (byte)MapType.Instance or (byte)MapType.Raid).Select(r => r.Entry).ToArray();
            if (instanced.Length > 0)
            {
                warnings.Add($"no instance_template or map_template rows: the {instanced.Length} dungeon and raid map(s) have no player limit, " +
                    $"reset delay, ghost entrance or script ({string.Join(", ", instanced)})");
            }
        }
        else
        {
            var byEntry = mapRows.ToDictionary(r => r.Entry);
            foreach ((uint entry, MapInstanceData data) in instances.OrderBy(p => p.Key))
            {
                if (!byEntry.TryGetValue(entry, out MapTemplateRow? row))
                {
                    warnings.Add($"instance data for map {entry} has no Map.dbc row, skipped");
                    continue;
                }

                row.Parent = data.Parent;
                row.PlayerLimit = data.PlayerLimit;
                row.ResetDelay = data.ResetDelay;
                row.GhostEntranceMap = data.GhostEntranceMap;
                row.GhostEntranceX = data.GhostEntranceX;
                row.GhostEntranceY = data.GhostEntranceY;
                row.ScriptName = data.ScriptName;
                merged++;
            }

            foreach (MapTemplateRow row in mapRows.Where(r => r.MapType is (byte)MapType.Instance or (byte)MapType.Raid && !instances.ContainsKey(r.Entry)))
            {
                warnings.Add($"map {row.Entry} ({row.MapName}) is a dungeon or raid without an instance row: no player limit, reset delay or ghost entrance");
            }
        }

        return new MapAreaDbcSnapshot(mapRows, ValidateAreas(areas), merged, warnings);
    }

    /// <summary>Write a snapshot in its own transaction: refused when either table has rows, unless <paramref name="replace"/>.</summary>
    public static async Task<MapAreaDbcImportReport> ImportSnapshotAsync(WorldDbContext db, MapAreaDbcSnapshot snapshot, bool replace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(snapshot);
        await ImportTransaction.RunAsync(db, async token =>
        {
            if (!replace
                && (await db.Set<MapTemplateRow>().AnyAsync(token).ConfigureAwait(false)
                    || await db.Set<AreaTemplateRow>().AnyAsync(token).ConfigureAwait(false)))
            {
                throw new InvalidOperationException("Map/area import refuses a world that already has map_template or area_template rows without replace=true.");
            }

            await ReplaceAsync(db, snapshot, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return new(snapshot.Maps.Count, snapshot.Areas.Count, snapshot.InstanceRows, snapshot.Warnings);
    }

    /// <summary>Empty both tables and write the snapshot, inside the caller's <see cref="ImportTransaction"/>.</summary>
    public static async Task ReplaceAsync(WorldDbContext db, MapAreaDbcSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(snapshot);
        bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            await db.Set<MapTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Set<AreaTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await ImportBatch.InsertAsync(db, snapshot.Maps, cancellationToken).ConfigureAwait(false);
            await ImportBatch.InsertAsync(db, snapshot.Areas, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = detect;
        }
    }

    private static MapTemplateRow Copy(MapTemplateRow r) => new()
    {
        Entry = r.Entry, Parent = r.Parent, MapType = r.MapType, LinkedZone = r.LinkedZone, PlayerLimit = r.PlayerLimit, ResetDelay = r.ResetDelay,
        GhostEntranceMap = r.GhostEntranceMap, GhostEntranceX = r.GhostEntranceX, GhostEntranceY = r.GhostEntranceY, MapName = r.MapName,
        ScriptName = r.ScriptName,
    };

    private static AreaTemplateRow[] ValidateAreas(IReadOnlyList<AreaTemplateRow> areas)
    {
        if (areas.GroupBy(r => r.Entry).Any(g => g.Count() != 1))
            throw new InvalidDataException("AreaTable.dbc contains duplicate area ids.");
        var byId = areas.ToDictionary(r => r.Entry);
        foreach (AreaTemplateRow area in areas)
        {
            var seen = new HashSet<uint>();
            for (uint current = area.Entry; current != 0;)
            {
                if (!seen.Add(current)) throw new InvalidDataException($"Area {area.Entry} has a parent cycle.");
                if (!byId.TryGetValue(current, out AreaTemplateRow? row))
                    throw new InvalidDataException($"Area {area.Entry} has a missing parent {current}.");
                current = row.ZoneId;
            }
        }

        return areas.OrderBy(r => r.Entry).ToArray();
    }
}
