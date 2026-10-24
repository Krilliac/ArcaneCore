using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>What the terrain says about one graveyard: its liquid status and the area level under it.</summary>
internal readonly record struct PopulationPlacementSite(LiquidStatus Liquid, int AreaLevel);

/// <summary>
/// Everything the placement safety check needs, built once on a background task and then only read: the spawn index of one creature
/// content object, and for every graveyard of one atlas the liquid status and area level read from the terrain files
/// (<see cref="TerrainManager.ReadTileDetached"/>, never the world thread's tile cache). With it, <see cref="PopulationProvisioner.IsSafe"/>
/// on the world thread is dictionary lookups only: no file read and no index build inside a tick.
/// <para>
/// Immutable after <see cref="Build"/> returns (the dictionary is never written again, the index is read-only), so it is handed to the
/// world thread by <c>WorldRuntime.Post</c> and read from there without a lock. It answers for the creature content and area table it
/// was built from (<see cref="IsCurrentFor"/>); a reload of either makes it stale and the provisioner builds a new one.
/// </para>
/// <para>
/// The area level reads the tile's cell value. The live lookup (<c>TerrainInfo.GetAreaFlag</c>) prefers the WMO group's area when the
/// model tile is loaded, which needs the world thread's model service; the two differ only for a graveyard inside a WMO whose model tile
/// is loaded, the same gap <see cref="TerrainManager.ReadZoneAndAreaIdDetached"/> accepts.
/// </para>
/// </summary>
internal sealed class PopulationPlacementFacts
{
    private PopulationPlacementFacts(CreatureContent content, AreaTable areas, PopulationSpawnIndex spawns,
        IReadOnlyDictionary<WorldSafeLoc, PopulationPlacementSite> sites)
    {
        Content = content;
        Areas = areas;
        Spawns = spawns;
        Sites = sites;
    }

    internal CreatureContent Content { get; }

    internal AreaTable Areas { get; }

    internal PopulationSpawnIndex Spawns { get; }

    /// <summary>Per graveyard (by value: the record's id, map, position and name), as the terrain answered when this was built.</summary>
    internal IReadOnlyDictionary<WorldSafeLoc, PopulationPlacementSite> Sites { get; }

    /// <summary>Whether this was built from exactly these objects (by reference: a content or area table reload replaces them).</summary>
    internal bool IsCurrentFor(CreatureContent content, AreaTable areas)
        => ReferenceEquals(Content, content) && ReferenceEquals(Areas, areas);

    /// <summary>
    /// Build the facts. Thread affinity: any thread, and meant for a background one: it indexes every creature spawn and reads each
    /// terrain tile the graveyards stand in once. <paramref name="content"/> is read through its immutable lists and
    /// <paramref name="areas"/> is immutable. Without <paramref name="terrain"/> every site is dry at area level 0, which is what the
    /// placement check saw without terrain data. Throws <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    internal static PopulationPlacementFacts Build(CreatureContent content, AreaTable areas, TerrainManager? terrain,
        IEnumerable<WorldSafeLoc> locations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(locations);
        var spawns = new PopulationSpawnIndex(content);
        var sites = new Dictionary<WorldSafeLoc, PopulationPlacementSite>();
        var byTile = new Dictionary<(uint Map, int X, int Y), List<WorldSafeLoc>>();
        foreach (WorldSafeLoc location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sites.ContainsKey(location)) continue;
            sites[location] = default; // dry, level 0: also the answer where there is no terrain or no tile
            if (terrain is null || !float.IsFinite(location.X) || !float.IsFinite(location.Y)
                || TerrainTile.TileOf(location.X, location.Y) is not { } tile) continue;
            var key = (location.MapId, tile.X, tile.Y);
            if (!byTile.TryGetValue(key, out List<WorldSafeLoc>? group)) byTile[key] = group = [];
            group.Add(location);
        }

        if (terrain is not null)
            foreach (((uint map, int tileX, int tileY), List<WorldSafeLoc> group) in byTile)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TerrainTile tile = terrain.ReadTileDetached(map, tileX, tileY);
                foreach (WorldSafeLoc location in group)
                {
                    LiquidStatus liquid = TerrainInfo.LiquidStatusOn(tile, location.X, location.Y, location.Z, LiquidTypeFlags.AllLiquids, out _);
                    ushort flag = tile.GetAreaFlag(location.X, location.Y);
                    int level = flag == 0 ? 0 : Math.Max(0, areas.GetByAreaFlagAndMap(flag, map)?.AreaLevel ?? 0);
                    sites[location] = new PopulationPlacementSite(liquid, level);
                }
            }

        return new PopulationPlacementFacts(content, areas, spawns, sites);
    }
}
