using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// One map instance: its objects, their visibility and the per-tick update pipeline.
/// <para>
/// Thread affinity: world thread only. <see cref="Update"/> runs, in order:
/// (1) each player's queued in-world packets (and those of players in transit from this map),
/// (2) visibility for players and objects that moved, (3) values updates for objects whose
/// fields changed, (4) a flush of every client's queued update blocks, (5) the grid
/// lifecycle, (6) work deferred until after the update (far teleports). Objects are
/// only added/removed outside (2)–(4), so a client never receives blocks about an object it
/// was just told to destroy.
/// </para>
/// <para>
/// Space is indexed by vmangos' grid/cell system (<see cref="GridContainer"/>): visibility,
/// observer broadcasts and range broadcasts only look at the cells a query touches, and then
/// apply exactly the distance rules the M4–M6 distance scan applied, so results are unchanged
/// (docs/areas/grid-terrain.md).
/// </para>
/// </summary>
public sealed partial class Map
{
    /// <summary>
    /// Continent visibility distance. vmangos <c>DEFAULT_VISIBILITY_DISTANCE</c> = 100 yards
    /// (ObjectDefines.h), the default of <c>Visibility.Distance.Continents</c>.
    /// </summary>
    public const float VisibilityRange = 100.0f;

    /// <summary>
    /// Extra distance an already-visible unit may move away before it is removed, so a player
    /// standing on the boundary does not flicker. vmangos <c>Visibility.Distance.Grey.Unit</c>
    /// default = 1 yard (World.cpp), applied in <c>WorldObject::IsWithinVisibilityDistanceOf</c>.
    /// </summary>
    public const float VisibilityGreyDistance = 1.0f;

    private readonly WorldRuntime _world;
    private readonly ILogger _logger;
    private readonly Dictionary<ObjectGuid, Player> _players = [];
    private readonly Dictionary<ObjectGuid, WorldObject> _objects = [];

    // Who has each object in its visible set: the inverse of Player.VisibleObjects, kept in
    // step by AddVisible / RemoveVisible.
    private readonly Dictionary<ObjectGuid, HashSet<Player>> _observers = [];
    private readonly HashSet<WorldObject> _movedObjects = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ObjectGuid> _newObjects = [];
    private readonly List<Player> _transit = [];
    private readonly List<Player> _updatePlayers = []; // Update's player snapshots (Update does not nest)
    private List<Action> _afterUpdate = [];
    private List<Action> _afterUpdateRunning = []; // the batch being run; swapped with _afterUpdate so neither is copied
    private readonly List<WorldObject> _valuesQueue = [];
    private readonly List<IMapUpdater> _updaters = [];
    private readonly List<Unit> _heartbeatUnits = [];
    private bool _heartbeatSnapshotDirty = true;
    private readonly List<Player> _viewerScratch = [];
    private readonly List<WorldObject> _movedScratch = [];
    private bool _viewerScratchInUse;
    private readonly List<WorldObject> _visibilityScratch = [];
    private readonly List<WorldObject> _candidateExtras = [];
    private readonly List<WorldObject> _candidateMergeScratch = [];
    private readonly List<Player> _viewerExtras = [];
    private readonly List<Player> _viewerMergeScratch = [];

    // Objects every player of the map has, whatever the distance (vmangos Map::m_transports: SendInitTransports at map entry, never
    // out of range while on the map): the elevators whose animation carries them out of the visibility range of their spawn.
    private readonly HashSet<WorldObject> _mapWide = new(ReferenceEqualityComparer.Instance);
    private readonly List<WorldObject> _mapWideOrder = [];

    private readonly List<IVisibilityRule> _visibilityRules = []; // rogue lane: stealth and other per-viewer visibility vetoes
    private bool _detecting;
    private readonly GridContainer _grid;
    private readonly TerrainInfo _terrain;
    private long _nextSequence;
    private bool _inUpdatePhase;

    internal Map(uint mapId, uint instanceId, WorldRuntime world, ILogger logger)
    {
        MapId = mapId;
        InstanceId = instanceId;
        _world = world;
        _logger = logger;

        // vmangos Map::Map holds its map id's terrain (Map.cpp:127, :153); UnloadAll lets go of it.
        _terrain = WorldMaps.Of(world).Terrain.Acquire(mapId);
        _grid = new GridContainer(world.Options.Maps, VisibilityRange) { EvictObject = EvictFromGrid };

        // vmangos Map::EnsureGridCreated loads the grid's terrain tile; Map::UnloadGrid unrefs it.
        _grid.GridCreated += grid =>
        {
            (int tx, int ty) = TerrainTile.TileOf(grid.Coord);
            _terrain.Load(tx, ty);
        };
        _grid.GridUnloaded += coord =>
        {
            (int tx, int ty) = TerrainTile.TileOf(coord);
            _terrain.Unload(tx, ty);
        };

        // Read the tile of a grid about to be created ahead of time (MapOptions.GridPrefetchDistance).
        TerrainManager terrain = WorldMaps.Of(world).Terrain;
        _grid.GridApproaching += coord =>
        {
            (int tx, int ty) = TerrainTile.TileOf(coord);
            terrain.Prefetch(mapId, tx, ty);
        };
    }

    public uint MapId { get; }

    /// <summary>The owning runtime, for map-local systems that publish world facts.</summary>
    internal WorldRuntime World => _world;

    /// <summary>The owning runtime's identity token (<see cref="WorldRuntime.Identity"/>); holds nothing alive.</summary>
    public object WorldIdentity => _world.Identity;

    /// <summary>The instance this map object simulates: 0 for the shared copy, else a dungeon/raid instance id.</summary>
    public uint InstanceId { get; }

    /// <summary>Whether the map has been unloaded (<see cref="WorldRuntime.UnloadMap"/>); it can no longer take players.</summary>
    public bool IsUnloaded { get; private set; }

    public int PlayerCount => _players.Count;

    public IReadOnlyCollection<Player> Players => _players.Values;

    /// <summary>The players as the dictionary's own value collection: a struct enumerator, for per-tick loops that do not mutate.</summary>
    internal Dictionary<ObjectGuid, Player>.ValueCollection PlayerValues => _players.Values;

    /// <summary>
    /// A snapshot of the players in a pooled array (for per-tick loops that may add or remove players). Return it with
    /// <see cref="ReturnPlayerSnapshot"/>; only the first <paramref name="count"/> entries are players.
    /// </summary>
    internal Player[] RentPlayerSnapshot(out int count)
    {
        count = _players.Count;
        Player[] snapshot = System.Buffers.ArrayPool<Player>.Shared.Rent(Math.Max(count, 1));
        _players.Values.CopyTo(snapshot, 0);
        return snapshot;
    }

    internal static void ReturnPlayerSnapshot(Player[] snapshot) => System.Buffers.ArrayPool<Player>.Shared.Return(snapshot, clearArray: true);

    /// <summary>Every object in the map, players included.</summary>
    public int ObjectCount => _objects.Count;

    /// <summary>The number of objects of type <typeparamref name="T"/> in the map (world thread; walks every object, so callers sample it, they do not call it per tick).</summary>
    public int CountObjectsOf<T>()
        where T : WorldObject
    {
        int count = 0;
        foreach (WorldObject obj in _objects.Values)
        {
            if (obj is T)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The map's grids: the spatial index and the grid load/unload lifecycle.</summary>
    public GridContainer Grids => _grid;

    /// <summary>The map's terrain (heights, areas, liquids): its map id's, shared by all its instances and held until the map is unloaded.</summary>
    public TerrainInfo Terrain => _terrain;

    /// <summary>The map's <c>map_template</c> row, or null when the map is not registered.</summary>
    public MapTemplate? Template => WorldMaps.Of(_world).Registry.Find(MapId);

    /// <summary>
    /// Cumulative number of candidates the visibility passes looked at for players that moved or joined
    /// (<see cref="UpdateVisibility"/>). A work counter for performance tests and diagnostics; world thread.
    /// </summary>
    internal long PlayerVisibilityCandidates { get; private set; }

    /// <summary>
    /// Cumulative number of candidates the visibility passes looked at for non-player objects that moved or
    /// appeared (<see cref="UpdateObjectVisibility"/>). A work counter for performance tests; world thread.
    /// </summary>
    internal long ObjectVisibilityCandidates { get; private set; }

    /// <summary>Objects scanned to build heartbeat snapshots (world-thread work counter).</summary>
    internal long HeartbeatSnapshotObjectsVisited { get; private set; }

    /// <summary>Players removed from this map by a far teleport whose client has not confirmed the new world yet.</summary>
    public int TransitCount => _transit.Count;

    public Player? FindPlayer(ObjectGuid guid) => _players.GetValueOrDefault(guid);

    private int _parallelOwnerThreadId;

    /// <summary>
    /// During the parallel maps phase: the managed id of the thread updating this map, -1 while it waits for one, 0 when it is not part
    /// of this tick's map list (a map created during the phase). 0 outside the phase. Written by the world's map scheduler.
    /// </summary>
    internal int ParallelOwnerThreadId
    {
        get => Volatile.Read(ref _parallelOwnerThreadId);
        set => Volatile.Write(ref _parallelOwnerThreadId, value);
    }

    /// <summary>How long this map's last update took (microseconds): the longest-first order of the parallel maps phase.</summary>
    internal long LastUpdateMicros { get; set; }

    /// <summary>How long this map's last parallel update waited for the world's serial gate (micros; 0 in the serial maps phase).</summary>
    internal long LastGateWaitMicros { get; set; }

    /// <summary>The world this map belongs to (its serial gate and parallel options, for map systems).</summary>
    internal WorldRuntime Runtime => _world;

    /// <summary>Any object in the map (players included) by GUID.</summary>
    public WorldObject? FindObject(ObjectGuid guid) => _objects.GetValueOrDefault(guid);

    /// <summary>
    /// The terrain height under a position (<see cref="TerrainInfo.GetHeight"/>): vmangos <c>TerrainInfo::GetHeightStatic</c> with
    /// <c>useVmaps = false</c> (GridMap.cpp:815, 823-824, 871), no model floor. The floor of vmangos' default <c>Map::GetHeight</c>
    /// (<c>vmap = true</c>, Map.cpp:3407-3411), terrain and static models, is <c>map.Collision.GetHeight</c>
    /// (<see cref="Collision.MapCollision.GetHeight"/>), which creature points use.
    /// </summary>
    public float GetHeight(float x, float y, float z) => _terrain.GetHeight(x, y, z);

    /// <summary><see cref="GetHeight"/> from a terrain tile already in memory (any thread; false when the tile would have to be loaded).</summary>
    internal bool TryGetLoadedHeight(float x, float y, out float height) => _terrain.TryGetLoadedHeight(x, y, out height);

    /// <summary>Zone and area at a position (<see cref="TerrainInfo.GetZoneAndAreaId"/>); (0, 0) when unknown.</summary>
    public (uint ZoneId, uint AreaId) GetZoneAndAreaId(float x, float y, float z) => _terrain.GetZoneAndAreaId(x, y, z);

    /// <summary>Zone and area at a position without reading a terrain file on this thread (<see cref="TerrainInfo.TryGetZoneAndAreaIdWithoutLoading"/>).</summary>
    public bool TryGetZoneAndAreaIdWithoutLoading(float x, float y, float z, out (uint ZoneId, uint AreaId) zoneAndArea)
        => _terrain.TryGetZoneAndAreaIdWithoutLoading(x, y, z, out zoneAndArea);

    /// <summary>Position relative to liquid (<see cref="TerrainInfo.GetLiquidStatus"/>).</summary>
    public LiquidStatus GetLiquidStatus(float x, float y, float z, LiquidTypeFlags requiredType, out LiquidData data)
        => _terrain.GetLiquidStatus(x, y, z, requiredType, out data);

    internal ILogger Logger => _logger;

    internal void EnsureWorldThread()
    {
        if (!_world.IsWorldThread)
        {
            throw new InvalidOperationException("map state may only be changed on the world thread");
        }

        if (_world.IsParallelPhase)
        {
            _world.CheckOwner(this, "map membership change");
        }
    }

    private void EnsureNotInUpdatePhase()
    {
        if (_inUpdatePhase)
        {
            throw new InvalidOperationException("objects cannot be added or removed during the visibility/values/flush phases");
        }
    }
}
