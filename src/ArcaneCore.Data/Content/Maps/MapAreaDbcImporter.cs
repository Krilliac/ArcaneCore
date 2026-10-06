using ArcaneCore.Data.Content.Import;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Maps;

/// <summary>Counts and writes the selected continent rows from the build-5875 map and area DBCs.</summary>
public sealed record MapAreaDbcImportReport(int MappedMaps, int MappedAreas, int SkippedAreas);

internal sealed record MapAreaDbcSnapshot(MapTemplateRow[] Maps, AreaTemplateRow[] Areas, int Skipped);

/// <summary>
/// Stages Map.dbc and AreaTable.dbc rows before touching EF. The readers own DBC layout parsing;
/// this importer owns the small world-data admission and replacement policy for maps 0 and 1.
/// </summary>
public static class MapAreaDbcImporter
{
    public static async Task<MapAreaDbcImportReport> ImportAsync(WorldDbContext db, string mapPath,
        string areaPath, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        MapAreaDbcSnapshot snapshot = ReadSnapshot(mapPath, areaPath);
        return await ImportSnapshotAsync(db, snapshot, replace, cancellationToken).ConfigureAwait(false);
    }

    internal static MapAreaDbcSnapshot ReadSnapshot(string mapPath, string areaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(areaPath);

        IReadOnlyList<MapTemplateRow> maps = MapDbcReader.Load(mapPath);
        IReadOnlyList<AreaTemplateRow> allAreas = AreaTableDbcReader.Load(areaPath);
        MapTemplateRow[] selectedMaps = ValidateMaps(maps);
        AreaTemplateRow[] selectedAreas = ValidateAreas(allAreas, out int skipped);
        return new(selectedMaps, selectedAreas, skipped);
    }

    internal static async Task<MapAreaDbcImportReport> ImportSnapshotAsync(WorldDbContext db,
        MapAreaDbcSnapshot snapshot, bool replace, CancellationToken cancellationToken)
    {
        await ImportTransaction.RunAsync(db, async token =>
        {
            bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                uint[] areaIds = snapshot.Areas.Select(r => r.Entry).ToArray();
                List<AreaTemplateRow> existingAreas = await db.Set<AreaTemplateRow>().AsNoTracking()
                    .Where(r => Enumerable.Contains(areaIds, r.Entry)).ToListAsync(token).ConfigureAwait(false);
                HashSet<uint> reusableForeign = [];
                foreach (AreaTemplateRow existing in existingAreas)
                {
                    AreaTemplateRow source = snapshot.Areas.Single(r => r.Entry == existing.Entry);
                    bool sourceContinent = source.MapId is 0 or 1;
                    bool existingContinent = existing.MapId is 0 or 1;
                    if (sourceContinent != existingContinent)
                        throw new InvalidDataException($"Area {source.Entry} collides across continent and foreign map metadata.");
                    if (!sourceContinent)
                    {
                        if (!SameArea(source, existing))
                            throw new InvalidDataException($"Foreign ancestor area {source.Entry} conflicts with existing custom metadata.");
                        reusableForeign.Add(source.Entry);
                    }
                }

                if (!replace)
                {
                    if (await db.Set<MapTemplateRow>().AnyAsync(r => r.Entry == 0 || r.Entry == 1, token).ConfigureAwait(false)
                        || await db.Set<AreaTemplateRow>().AnyAsync(r => r.MapId == 0 || r.MapId == 1, token).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("Map/area import refuses existing continent rows without replace=true.");
                    }
                }
                else
                {
                    await db.Set<MapTemplateRow>().Where(r => r.Entry == 0 || r.Entry == 1).ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<AreaTemplateRow>().Where(r => r.MapId == 0 || r.MapId == 1).ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                await ImportBatch.InsertAsync(db, snapshot.Maps, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, snapshot.Areas.Where(r => !reusableForeign.Contains(r.Entry)), token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);

        return new(snapshot.Maps.Length, snapshot.Areas.Length, snapshot.Skipped);
    }

    private static MapTemplateRow[] ValidateMaps(IReadOnlyList<MapTemplateRow> maps)
    {
        ArgumentNullException.ThrowIfNull(maps);
        if (maps.Count(r => r.Entry == 0) != 1 || maps.Count(r => r.Entry == 1) != 1)
            throw new InvalidDataException("Map.dbc must contain exactly one map row for both continent maps 0 and 1.");
        if (maps.GroupBy(r => r.Entry).Any(g => g.Count() != 1))
            throw new InvalidDataException("Map.dbc contains duplicate map ids.");
        return maps.Where(r => r.Entry is 0 or 1).OrderBy(r => r.Entry).ToArray();
    }

    private static AreaTemplateRow[] ValidateAreas(IReadOnlyList<AreaTemplateRow> allAreas, out int skipped)
    {
        ArgumentNullException.ThrowIfNull(allAreas);
        if (allAreas.GroupBy(r => r.Entry).Any(g => g.Count() != 1))
            throw new InvalidDataException("AreaTable.dbc contains duplicate area ids.");
        var byId = allAreas.ToDictionary(r => r.Entry);
        var closure = new HashSet<uint>(allAreas.Where(r => r.MapId is 0 or 1).Select(r => r.Entry));
        foreach (uint seed in closure.ToArray())
        {
            var seen = new HashSet<uint>();
            for (uint current = seed; current != 0;)
            {
                if (!seen.Add(current)) throw new InvalidDataException($"Area {seed} has a parent cycle.");
                if (!byId.TryGetValue(current, out AreaTemplateRow? area))
                    throw new InvalidDataException($"Area {seed} has a missing parent {current}.");
                closure.Add(current);
                current = area.ZoneId;
            }
        }

        skipped = allAreas.Count - closure.Count;
        return allAreas.Where(r => closure.Contains(r.Entry)).OrderBy(r => r.MapId).ThenBy(r => r.Entry).ToArray();
    }

    private static bool SameArea(AreaTemplateRow left, AreaTemplateRow right)
        => left.Entry == right.Entry && left.MapId == right.MapId && left.ZoneId == right.ZoneId
            && left.ExploreFlag == right.ExploreFlag && left.Flags == right.Flags && left.AreaLevel == right.AreaLevel
            && left.Name == right.Name && left.Team == right.Team && left.LiquidTypeId == right.LiquidTypeId;
}
