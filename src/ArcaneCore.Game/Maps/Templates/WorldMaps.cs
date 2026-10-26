using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Maps.Templates;

/// <summary>
/// The map services of one world: the map registry, terrain, area table and
/// the area-trigger / teleport-location tables. Attached to the <see cref="WorldRuntime"/>
/// without changing it (a weak side table), created with defaults on first use — continents
/// only, terrain from <see cref="WorldRuntimeOptions.Maps"/> — and filled from the world
/// database by <see cref="Load"/> (the world daemon's teleport feature does this at startup).
/// <para>Thread affinity: world thread (and startup, before the world thread runs).</para>
/// </summary>
public sealed class WorldMaps
{
    private static readonly ConditionalWeakTable<WorldRuntime, WorldMaps> Attached = new();

    private Dictionary<uint, AreaTriggerTemplate> _areaTriggers = [];
    private Dictionary<uint, AreaTriggerTeleport> _areaTriggerTeleports = [];
    private IReadOnlyList<GameTele> _gameTeles = [];

    private readonly ILogger _logger;

    private readonly WorldRuntime _world;

    private WorldMaps(WorldRuntime world, ILogger? logger)
    {
        _logger = logger ?? NullLogger.Instance;
        _world = world;
        Terrain = new TerrainManager(world.Options.Maps.DataDirectory, logger) { Collision = WorldCollision.Of(world) };
    }

    /// <summary>
    /// Start reading, on the thread pool, the terrain, vmap and navmesh tiles of the grids a player arriving at (<paramref name="x"/>,
    /// <paramref name="y"/>) on <paramref name="mapId"/> will load (<see cref="Grid.GridContainer.LoadGridsAround"/> with
    /// <see cref="Grid.MapOptions.GridActivationDistance"/>), so that creating them installs parsed tiles instead of reading the files inside
    /// the tick. Asked when a teleport starts (<see cref="Teleport.TeleportService"/>):
    /// the transfer screen or the teleport ack gives the reads their time. Only the timing moves, as with the approach prefetch
    /// (<see cref="Grid.MapOptions.GridPrefetchDistance"/>, whose 0 turns this off too): a tile already in memory or on its way is not asked
    /// again, an unfinished read is not waited for, and the loader then reads the file as it always did. World thread.
    /// </summary>
    public void PrefetchArrival(uint mapId, float x, float y)
    {
        Grid.MapOptions options = _world.Options.Maps;
        if (!(options.GridPrefetchDistance > 0) || !float.IsFinite(x) || !float.IsFinite(y))
        {
            return;
        }

        WorldCollision collision = WorldCollision.Of(_world);
        ICollisionTilePrefetch? los = collision.LineOfSight as ICollisionTilePrefetch;
        ICollisionTilePrefetch? path = ReferenceEquals(collision.Pathfinder, collision.LineOfSight) ? null : collision.Pathfinder as ICollisionTilePrefetch;
        Grid.CellArea area = Grid.GridDefines.CalculateCellArea(x, y, options.GridActivationDistance);
        for (int gx = area.Low.Grid.X; gx <= area.High.Grid.X; gx++)
        {
            for (int gy = area.Low.Grid.Y; gy <= area.High.Grid.Y; gy++)
            {
                (int tx, int ty) = TerrainTile.TileOf(new Grid.GridCoord(gx, gy));
                Terrain.Prefetch(mapId, tx, ty);
                try
                {
                    los?.Prefetch(mapId, tx, ty);
                    path?.Prefetch(mapId, tx, ty);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // As MapCollision.OnGridApproaching: a failed prefetch is only a lost head start; the grid's own load reports the tile.
                    ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record("collision.tile-prefetch", ex);
                    _logger.LogWarning(ex, "Prefetching the collision tile ({TileX}, {TileY}) of map {MapId} for an arrival failed", tx, ty, mapId);
                }
            }
        }
    }

    public MapRegistry Registry { get; private set; } = MapRegistry.Default;

    public TerrainManager Terrain { get; }

    public AreaTable Areas => Terrain.Areas;

    public IReadOnlyCollection<AreaTriggerTemplate> AreaTriggers => _areaTriggers.Values;

    public IReadOnlyList<GameTele> GameTeles => _gameTeles;

    /// <summary>The map services of <paramref name="world"/> (created with defaults on first use).</summary>
    public static WorldMaps Of(WorldRuntime world) => Attached.GetValue(world, w => new WorldMaps(w, null));

    /// <summary>
    /// Create the map services of <paramref name="world"/> with a logger (before anything else
    /// touched them, i.e. at startup). Returns the existing instance if there already is one.
    /// </summary>
    public static WorldMaps Attach(WorldRuntime world, ILogger logger) => Attached.GetValue(world, w => new WorldMaps(w, logger));

    /// <summary>
    /// Install the world database's map content, applying vmangos' loader rules
    /// (ObjectMgr::LoadAreaTriggerTeleports): a teleport row needs an area trigger row, a known
    /// target map and a non-zero target position. Returns the rows skipped, for logging.
    /// </summary>
    public IReadOnlyList<string> Load(MapContent content)
    {
        Registry = new MapRegistry(content.Maps);
        Terrain.Areas = new AreaTable(content.Areas, Registry);
        Terrain.WmoAreas = new WmoAreaTable(content.WmoAreas);
        if (content.Areas.Count > 0 && content.WmoAreas.Count == 0)
        {
            _logger.LogWarning("Areas: the world has no wmo_area_template rows; a position inside a WMO (Undercity, Ironforge, ...) reads the area of the "
                + "terrain above it. Import WMOAreaTable.dbc (arcane-content-importer refresh --dbc-dir, or import-map-dbc with a third path)");
        }

        AreaTriggerTables tables = BuildAreaTriggerTables(content);
        _areaTriggers = tables.Triggers;
        _areaTriggerTeleports = tables.Teleports;
        _gameTeles = content.GameTeles.OrderBy(t => t.Id).ToArray();
        return tables.Skipped;
    }

    /// <summary>
    /// Build the area-trigger and teleport tables of <paramref name="content"/> without installing them
    /// (vmangos ObjectMgr::LoadAreaTriggerTeleports rules, checked against the current map registry), so
    /// a reload can prepare them off the world thread and swap them with <see cref="ReplaceAreaTriggerTables"/>.
    /// </summary>
    public AreaTriggerTables BuildAreaTriggerTables(MapContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        MapRegistry registry = Registry;
        Dictionary<uint, AreaTriggerTemplate> triggers = content.AreaTriggers.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());

        var skipped = new List<string>();
        var teleports = new Dictionary<uint, AreaTriggerTeleport>();
        foreach (AreaTriggerTeleport teleport in content.AreaTriggerTeleports)
        {
            if (!triggers.ContainsKey(teleport.Id))
            {
                skipped.Add($"areatrigger_teleport {teleport.Id}: no areatrigger_template row");
            }
            else if (!registry.Contains(teleport.TargetMap))
            {
                skipped.Add($"areatrigger_teleport {teleport.Id}: unknown target map {teleport.TargetMap}");
            }
            else if (teleport.TargetX == 0 && teleport.TargetY == 0 && teleport.TargetZ == 0)
            {
                skipped.Add($"areatrigger_teleport {teleport.Id}: target position is zero");
            }
            else
            {
                teleports[teleport.Id] = teleport;
            }
        }

        return new AreaTriggerTables(triggers, teleports, skipped);
    }

    /// <summary>Install tables from <see cref="BuildAreaTriggerTables"/> and return the ones replaced (world thread).</summary>
    public AreaTriggerTables ReplaceAreaTriggerTables(AreaTriggerTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var previous = new AreaTriggerTables(_areaTriggers, _areaTriggerTeleports, []);
        _areaTriggers = tables.Triggers;
        _areaTriggerTeleports = tables.Teleports;
        return previous;
    }

    /// <summary>The number of area trigger teleports in force.</summary>
    public int AreaTriggerTeleportCount => _areaTriggerTeleports.Count;

    /// <summary>Replace the teleport locations (<c>game_tele</c>, ordered by id) and return the ones replaced (world thread).</summary>
    public IReadOnlyList<GameTele> ReplaceGameTeles(IEnumerable<GameTele> teles)
    {
        ArgumentNullException.ThrowIfNull(teles);
        IReadOnlyList<GameTele> previous = _gameTeles;
        _gameTeles = teles.OrderBy(t => t.Id).ToArray();
        return previous;
    }

    public AreaTriggerTemplate? FindAreaTrigger(uint id) => _areaTriggers.GetValueOrDefault(id);

    public AreaTriggerTeleport? FindAreaTriggerTeleport(uint id) => _areaTriggerTeleports.GetValueOrDefault(id);

    /// <summary>
    /// A teleport location by name (vmangos/cmangos <c>ObjectMgr::GetGameTele</c>): an exact
    /// case-insensitive match, else the first location (by id) whose name contains the text.
    /// </summary>
    public GameTele? FindGameTele(string name)
    {
        GameTele? partial = null;
        foreach (GameTele tele in _gameTeles)
        {
            if (string.Equals(tele.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return tele;
            }

            if (partial is null && tele.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                partial = tele;
            }
        }

        return partial;
    }
}

/// <summary>
/// Area triggers and their teleports, built from a <see cref="MapContent"/> (immutable once built;
/// <see cref="WorldMaps.BuildAreaTriggerTables"/>).
/// </summary>
public sealed class AreaTriggerTables
{
    internal AreaTriggerTables(
        Dictionary<uint, AreaTriggerTemplate> triggers,
        Dictionary<uint, AreaTriggerTeleport> teleports,
        IReadOnlyList<string> skipped)
    {
        Triggers = triggers;
        Teleports = teleports;
        Skipped = skipped;
    }

    internal Dictionary<uint, AreaTriggerTemplate> Triggers { get; }

    internal Dictionary<uint, AreaTriggerTeleport> Teleports { get; }

    /// <summary>The teleport rows the loader rejected, for logging.</summary>
    public IReadOnlyList<string> Skipped { get; }

    public int TriggerCount => Triggers.Count;

    public int TeleportCount => Teleports.Count;
}
