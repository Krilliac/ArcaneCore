using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Updates;
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
/// fields changed, (4) a flush of every client's queued update blocks, (5) the grid and
/// terrain lifecycles, (6) work deferred until after the update (far teleports). Objects are
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
public sealed class Map
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
    private readonly List<Action> _afterUpdate = [];
    private readonly List<WorldObject> _valuesQueue = [];
    private readonly List<IMapUpdater> _updaters = [];
    private readonly List<Unit> _heartbeatUnits = [];

    // Consecutive failure count per updater; -1 means the fault breaker is skipping it. Empty
    // unless an updater throws and World:MaxConsecutiveUpdaterFaults is set.
    private readonly Dictionary<IMapUpdater, int> _updaterFaults = new(ReferenceEqualityComparer.Instance);
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

        _terrain = WorldMaps.Of(world).Terrain.For(mapId);
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
    }

    public uint MapId { get; }

    /// <summary>The instance this map object simulates: 0 for the shared copy, else a dungeon/raid instance id.</summary>
    public uint InstanceId { get; }

    /// <summary>Whether the map has been unloaded (<see cref="WorldRuntime.UnloadMap"/>); it can no longer take players.</summary>
    public bool IsUnloaded { get; private set; }

    public int PlayerCount => _players.Count;

    public IReadOnlyCollection<Player> Players => _players.Values;

    /// <summary>Every object in the map, players included.</summary>
    public int ObjectCount => _objects.Count;

    /// <summary>The map's grids: the spatial index and the grid load/unload lifecycle.</summary>
    public GridContainer Grids => _grid;

    /// <summary>The map's terrain (heights, areas, liquids).</summary>
    public TerrainInfo Terrain => _terrain;

    /// <summary>The map's <c>map_template</c> row, or null when the map is not registered.</summary>
    public MapTemplate? Template => WorldMaps.Of(_world).Registry.Find(MapId);

    /// <summary>Players removed from this map by a far teleport whose client has not confirmed the new world yet.</summary>
    public int TransitCount => _transit.Count;

    public Player? FindPlayer(ObjectGuid guid) => _players.GetValueOrDefault(guid);

    /// <summary>The systems attached with <see cref="AddUpdater"/>, in attach order.</summary>
    public IReadOnlyList<IMapUpdater> Updaters => _updaters;

    /// <summary>Attach a per-map system, updated every tick after the timers (world thread).</summary>
    public void AddUpdater(IMapUpdater updater)
    {
        ArgumentNullException.ThrowIfNull(updater);
        EnsureWorldThread();
        EnsureNotInUpdatePhase();
        _updaters.Add(updater);
    }

    /// <summary>
    /// Give every updater skipped by the fault breaker another chance, and forget the failure
    /// counts (world thread). A code hot reload calls this after each applied edit, because the
    /// edit may be the fix.
    /// </summary>
    public void ClearUpdaterFaults()
    {
        EnsureWorldThread();
        _updaterFaults.Clear();
    }

    /// <summary>The updaters the fault breaker is skipping (world thread).</summary>
    public IReadOnlyList<IMapUpdater> IsolatedUpdaters
        => [.. _updaters.Where(u => _updaterFaults.TryGetValue(u, out int faults) && faults < 0)];

    private void CountUpdaterFault(IMapUpdater updater)
    {
        int limit = _world.Options.MaxConsecutiveUpdaterFaults;
        if (limit <= 0)
        {
            return;
        }

        int faults = _updaterFaults.GetValueOrDefault(updater) + 1;
        if (faults < limit)
        {
            _updaterFaults[updater] = faults;
            return;
        }

        _updaterFaults[updater] = -1;
        _logger.LogError(
            "map {MapId} updater {Updater} failed {Limit} ticks in a row and is skipped until the next code edit is applied or the server restarts",
            MapId, updater.GetType().Name, limit);
    }

    /// <summary>
    /// Attach a per-viewer visibility veto, consulted by every visibility evaluation after the range check
    /// (vmangos <c>WorldObject::IsVisibleForInState</c>, the stealth/invisibility part). Docs: docs/integration/rogue-stealth-core.md.
    /// </summary>
    public void AddVisibilityRule(IVisibilityRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        EnsureWorldThread();
        EnsureNotInUpdatePhase();
        _visibilityRules.Add(rule);
    }

    /// <summary>
    /// Re-evaluate visibility of <paramref name="obj"/> against everything near it at once (vmangos <c>WorldObject::UpdateObjectVisibility</c> /
    /// <c>Unit::UpdateVisibilityAndView</c>, called when a unit's stealth state changes). A player is evaluated in both directions.
    /// </summary>
    public void RefreshVisibility(WorldObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        EnsureWorldThread();
        if (!ReferenceEquals(obj.Map, this))
        {
            return;
        }

        if (obj is Player player)
        {
            UpdateVisibility(player);
        }
        else
        {
            UpdateObjectVisibility(obj);
        }
    }

    /// <summary>
    /// One viewer/target visibility evaluation in detect mode (vmangos <c>IsVisibleForOrDetect(.., detect = true)</c> as run by
    /// <c>Player::HandleStealthedUnitsDetection</c>): the rules may now reveal a unit the regular movement-driven updates keep hidden.
    /// </summary>
    public void UpdateVisibilityWithDetection(Player viewer, WorldObject target)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(target);
        EnsureWorldThread();
        _detecting = true;
        try
        {
            UpdateVisibilityOf(viewer, target);
        }
        finally
        {
            _detecting = false;
        }
    }

    /// <summary>The first attached system of type <typeparamref name="T"/>, if any.</summary>
    public T? FindUpdater<T>()
        where T : class, IMapUpdater
        => _updaters.OfType<T>().FirstOrDefault();
    /// <summary>Any object in the map (players included) by GUID.</summary>
    public WorldObject? FindObject(ObjectGuid guid) => _objects.GetValueOrDefault(guid);

    /// <summary>Ground height under a position (<see cref="TerrainInfo.GetHeight"/>).</summary>
    public float GetHeight(float x, float y, float z) => _terrain.GetHeight(x, y, z);

    /// <summary>Zone and area at a position (<see cref="TerrainInfo.GetZoneAndAreaId"/>); (0, 0) when unknown.</summary>
    public (uint ZoneId, uint AreaId) GetZoneAndAreaId(float x, float y, float z) => _terrain.GetZoneAndAreaId(x, y, z);

    /// <summary>Position relative to liquid (<see cref="TerrainInfo.GetLiquidStatus"/>).</summary>
    public LiquidStatus GetLiquidStatus(float x, float y, float z, LiquidTypeFlags requiredType, out LiquidData data)
        => _terrain.GetLiquidStatus(x, y, z, requiredType, out data);

    /// <summary>
    /// Put a player into the map: its grid and the grids around it are loaded (vmangos
    /// Map::Add → EnsureGridLoadedAtEnter + LoadMapCellsAround), its own create block goes out
    /// at once as its own packet (vmangos Map::SendInitSelf), then it is exchanged with every
    /// object in range.
    /// </summary>
    public void AddPlayer(Player player)
    {
        EnsureWorldThread();
        EnsureNotInUpdatePhase();
        if (player.Map is not null)
        {
            throw new InvalidOperationException($"{player.Guid} is already in map {player.Map.MapId}");
        }

        if (IsUnloaded)
        {
            throw new InvalidOperationException($"map {MapId} instance {InstanceId} has been unloaded");
        }

        if (_objects.ContainsKey(player.Guid))
        {
            throw new InvalidOperationException($"an object with GUID {player.Guid} is already in map {MapId}");
        }

        player.MapId = MapId;
        player.Map = this;
        player.MapSequence = _nextSequence++;
        _players[player.Guid] = player;
        _objects[player.Guid] = player;
        _grid.Add(player, active: true);

        // The create block carries every current value, so pending changes are moot.
        player.ClearChangedFields();
        player.IsQueuedForUpdate = false;

        // vmangos Player::BuildCreateUpdateBlockForPlayer: the player's own items precede it.
        player.Inventory.WriteCreateBlocks(player.PendingUpdates, _world.NowMs);

        PacketWriter block = player.PendingUpdates.BeginBlock();
        UpdateBlockWriter.WriteCreateBlock(block, player, player, isNewObject: false, _world.NowMs);
        player.PendingUpdates.EndBlock();
        FlushPlayer(player);

        UpdateVisibility(player);
        player.NeedsVisibilityUpdate = false;
        ObjectRelocated?.Invoke(player);
    }

    /// <summary>
    /// Take a player out of the map. Every client that sees it gets SMSG_DESTROY_OBJECT
    /// (vmangos WorldObject::DestroyForNearbyPlayers / Object::DestroyForPlayer).
    /// </summary>
    public void RemovePlayer(Player player)
    {
        EnsureWorldThread();
        EnsureNotInUpdatePhase();
        if (!_players.TryGetValue(player.Guid, out Player? stored) || !ReferenceEquals(stored, player))
        {
            return;
        }

        _players.Remove(player.Guid);
        foreach (IMapUpdater updater in _updaters)
        {
            updater.OnPlayerRemoved(this, player);
        }
        RemoveFromWorld(player);
        player.NeedsVisibilityUpdate = false;
    }

    /// <summary>
    /// Put a non-player object (creature, game object, …) into the map — the seam for content
    /// that spawns into grids. Its grid is created (vmangos Map::Add&lt;T&gt; → EnsureGridCreated;
    /// an <paramref name="active"/> object also loads the grids around it, like a player), and
    /// every player in range gets its create block at the end of the tick.
    /// </summary>
    public void AddObject(WorldObject obj, bool active = false, bool isNewObject = false)
    {
        EnsureWorldThread();
        EnsureNotInUpdatePhase();
        if (obj is Player)
        {
            throw new ArgumentException("players enter through AddPlayer", nameof(obj));
        }

        if (obj.Map is not null)
        {
            throw new InvalidOperationException($"{obj.Guid} is already in map {obj.Map.MapId}");
        }

        if (_objects.ContainsKey(obj.Guid))
        {
            throw new InvalidOperationException($"an object with GUID {obj.Guid} is already in map {MapId}");
        }

        obj.MapId = MapId;
        obj.Map = this;
        obj.MapSequence = _nextSequence++;
        _objects[obj.Guid] = obj;
        _grid.Add(obj, active);
        obj.ClearChangedFields();
        obj.IsQueuedForUpdate = false;
        _movedObjects.Add(obj);
        if (isNewObject)
        {
            _newObjects.Add(obj.Guid);
        }

        ObjectRelocated?.Invoke(obj);
    }

    /// <summary>Take a non-player object out of the map; clients that see it get SMSG_DESTROY_OBJECT.</summary>
    public void RemoveObject(WorldObject obj)
    {
        EnsureWorldThread();
        EnsureNotInUpdatePhase();
        if (obj is Player player)
        {
            RemovePlayer(player);
            return;
        }

        if (!_objects.TryGetValue(obj.Guid, out WorldObject? stored) || !ReferenceEquals(stored, obj))
        {
            return;
        }

        RemoveFromWorld(obj);
    }

    /// <summary>
    /// Mark a non-player object active (vmangos Map::AddToActive): it keeps the grids around it
    /// loaded, as players do.
    /// </summary>
    public void SetActive(WorldObject obj, bool active)
    {
        EnsureWorldThread();
        if (obj is Player || !ReferenceEquals(obj.Map, this))
        {
            throw new InvalidOperationException($"{obj.Guid} is not a non-player object of map {MapId}");
        }

        _grid.SetActive(obj, active);
    }

    /// <summary>
    /// Relay a movement packet (packed mover GUID + movement block) to every player whose client
    /// has the mover — vmangos sends through the mover's broadcaster, whose listeners are the
    /// players that have it in their visible set.
    /// </summary>
    public void BroadcastToObservers(WorldObject source, WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (!_observers.TryGetValue(source.Guid, out HashSet<Player>? observers))
        {
            return;
        }

        foreach (Player other in observers)
        {
            if (!ReferenceEquals(other, source))
            {
                other.Session.Send(opcode, payload);
            }
        }
    }

    /// <summary>
    /// Send a packet to every player within <paramref name="range"/> of <paramref name="source"/>,
    /// as vmangos Map::MessageDistBroadcast → MessageDistDeliverer does: a 3D distance check
    /// plus both bounding radii (WorldObject::IsWithinDist defaults), an optional same-team
    /// filter (no exemptions), and a range of 0 meaning the whole map. Only the cells within
    /// range (plus the largest bounding radius in the map) are visited.
    /// </summary>
    public void BroadcastInRange(
        WorldObject source, float range, WorldOpcode opcode, ReadOnlySpan<byte> payload,
        bool includeSelf, Team? onlyTeam = null)
    {
        if (range <= 0)
        {
            foreach (Player player in _players.Values)
            {
                DeliverInRange(player, source, range, opcode, payload, includeSelf, onlyTeam);
            }

            return;
        }

        float searchRadius = range + source.BoundingRadius + _grid.MaxBoundingRadius;
        foreach (WorldObject obj in Query(source.X, source.Y, searchRadius, extra: null))
        {
            if (obj is Player player && ReferenceEquals(player.Map, this))
            {
                DeliverInRange(player, source, range, opcode, payload, includeSelf, onlyTeam);
            }
        }
    }

    /// <summary>One simulation step (world thread).</summary>
    public void Update(uint diffMs)
        => Update(diffMs, diagnostics: null);

    internal void Update(uint diffMs, MapUpdateDiagnostics? diagnostics)
    {
        diagnostics?.Begin();
        // (1) in-world packets
        foreach (Player player in _players.Values.ToArray())
        {
            if (player.Map != this)
            {
                continue; // removed while processing an earlier player
            }

            ProcessPackets(player);
        }

        // (1a) players in transit from this map: only their world-port ack is handled (the
        // session drops everything else while the player is in no map — vmangos STATUS_TRANSFER).
        foreach (Player player in _transit.ToArray())
        {
            if (player.Map is not null || !_world.IsOnline(player.Guid))
            {
                _transit.Remove(player);
                continue;
            }

            ProcessPackets(player);
        }

        // (1b) timers: logouts whose countdown is over (vmangos WorldSession::Update → LogoutPlayer)
        uint now = _world.NowMs;
        foreach (Player player in _players.Values.ToArray())
        {
            if (player.Map == this && player.IsLogoutDue(now, _world.Options.LogoutDelayMs))
            {
                _world.LogoutPlayer(player);
            }
        }

        // Reuse a snapshot so heartbeat listeners may remove objects without
        // invalidating enumeration, and keep each unit's timer across casts.
        foreach (WorldObject obj in _objects.Values)
            if (obj is Unit unit) _heartbeatUnits.Add(unit);
        try
        {
            foreach (Unit unit in _heartbeatUnits)
                if (ReferenceEquals(unit.Map, this) && unit.IsInWorld
                    && unit is not Player { IsQuestSettlementPending: true })
                    unit.UpdateHeartbeat(diffMs);
        }
        finally
        {
            _heartbeatUnits.Clear();
        }

        // (1c) per-map systems (creatures, …) — see IMapUpdater
        foreach (IMapUpdater updater in _updaters)
        {
            if (_updaterFaults.Count > 0 && _updaterFaults.TryGetValue(updater, out int faults) && faults < 0)
            {
                continue; // isolated by the fault breaker (see ClearUpdaterFaults)
            }

            try
            {
                updater.Update(this, diffMs);
                if (_updaterFaults.Count > 0)
                {
                    _updaterFaults.Remove(updater); // only consecutive failures count
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "map {MapId} updater {Updater} failed", MapId, updater.GetType().Name);
                CountUpdaterFault(updater);
            }
        }

        if (diagnostics is not null)
        {
            diagnostics.Players = _players.Count;
            diagnostics.MovedObjects = _movedObjects.Count;
            diagnostics.NewObjects = _newObjects.Count;
            diagnostics.EndSimulation();
        }

        _inUpdatePhase = true;
        try
        {
            // Objects whose fields changed this tick may have a new bounding radius; the
            // distance queries below must cover it.
            foreach (WorldObject obj in _valuesQueue)
            {
                _grid.TrackRadius(obj);
            }

            // (2) visibility: players that moved, then other objects that moved or appeared
            foreach (Player player in _players.Values)
            {
                if (player.NeedsVisibilityUpdate)
                {
                    UpdateVisibility(player);
                    player.NeedsVisibilityUpdate = false;
                }
            }

            if (_movedObjects.Count > 0)
            {
                foreach (WorldObject obj in _movedObjects.OrderBy(o => o.MapSequence).ToArray())
                {
                    if (ReferenceEquals(obj.Map, this))
                    {
                        UpdateObjectVisibility(obj);
                    }
                }

                _movedObjects.Clear();
            }

            _newObjects.Clear();

            diagnostics?.EndVisibility();

            // (3) values updates
            if (diagnostics is not null)
            {
                diagnostics.ChangedObjects = _valuesQueue.Count;
            }
            foreach (WorldObject obj in _valuesQueue)
            {
                SendValuesUpdate(obj);
                obj.ClearChangedFields();
                obj.IsQueuedForUpdate = false;
            }

            _valuesQueue.Clear();
            diagnostics?.EndValues();

            // (4) flush
            foreach (Player player in _players.Values)
            {
                FlushPlayer(player);
            }
            diagnostics?.EndFlush();
        }
        finally
        {
            _inUpdatePhase = false;
        }

        // (5) grid and terrain lifecycles (vmangos Map::Update → grid states; TerrainInfo::CleanUpGrids)
        _grid.Update(diffMs);
        _terrain.CleanUp(diffMs);

        // (6) work scheduled for after the update (vmangos MapManager::ScheduleFarTeleport)
        if (_afterUpdate.Count > 0)
        {
            Action[] pending = [.. _afterUpdate];
            _afterUpdate.Clear();
            foreach (Action action in pending)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "deferred work on map {MapId} failed", MapId);
                }
            }
        }

        diagnostics?.Complete();
    }

    /// <summary>
    /// vmangos <c>WorldObject::IsWithinVisibilityDistanceOf</c> for two players on the same
    /// map (no transport, not taxi-flying): a 2D distance check (<c>IsWithinDistInMap(...,
    /// is3D = false)</c>) against the map visibility distance, plus the grey distance when the
    /// target is already visible, plus both bounding radii (<c>SizeFactor::BoundingRadius</c>),
    /// with a strict <c>&lt;</c> comparison (<c>WorldObject::IsWithinDist</c>).
    /// </summary>
    public static bool IsWithinVisibilityDistance(WorldObject viewer, WorldObject target, bool alreadyVisible)
    {
        float dx = viewer.X - target.X;
        float dy = viewer.Y - target.Y;
        float maxDist = VisibilityRange
            + (alreadyVisible ? VisibilityGreyDistance : 0.0f)
            + viewer.BoundingRadius
            + target.BoundingRadius;
        return (dx * dx) + (dy * dy) < maxDist * maxDist;
    }

    internal void QueueValuesUpdate(WorldObject obj)
    {
        obj.IsQueuedForUpdate = true;
        _valuesQueue.Add(obj);
    }

    /// <summary>
    /// Raised when a unit's position changed or it joined this map (vmangos <c>Unit::OnRelocated</c>, called from Map.cpp:1407,
    /// 1473 and 1531). The creature AI schedules its proximity-aggro scan from it (docs/areas/creature-ai.md).
    /// </summary>
    internal event Action<WorldObject>? ObjectRelocated;

    /// <summary>Re-file an object whose X/Y changed; non-player objects get a visibility pass this tick.</summary>
    internal void OnObjectMoved(WorldObject obj)
    {
        if (!_grid.Relocate(obj) && !_grid.Contains(obj))
        {
            return;
        }

        ObjectRelocated?.Invoke(obj);

        if (obj is Player player)
        {
            player.NeedsVisibilityUpdate = true;
        }
        else
        {
            _movedObjects.Add(obj);
        }
    }

    /// <summary>
    /// Unload every grid (objects are evicted, grid/terrain events run) and refuse further
    /// players — vmangos <c>Map::UnloadAll</c> for a deleted instance. Called by
    /// <see cref="WorldRuntime"/> after <see cref="WorldRuntime.MapUnloading"/>.
    /// </summary>
    internal void UnloadAll()
    {
        EnsureWorldThread();
        EnsureNotInUpdatePhase();
        IsUnloaded = true;
        _grid.UnloadAll();
        _afterUpdate.Clear();
    }

    /// <summary>Run <paramref name="action"/> after this map's next update phase (vmangos ScheduleFarTeleport).</summary>
    internal void RunAfterUpdate(Action action) => _afterUpdate.Add(action);

    /// <summary>Keep processing a far-teleported player's packets here until it reaches its new map.</summary>
    internal void BeginTransit(Player player)
    {
        if (!_transit.Contains(player))
        {
            _transit.Add(player);
        }
    }

    internal bool EndTransit(Player player) => _transit.Remove(player);

    /// <summary>The players whose clients currently have <paramref name="obj"/> (the inverse of their visible sets).</summary>
    internal IReadOnlyCollection<Player> ObserversOf(WorldObject obj) =>
        _observers.TryGetValue(obj.Guid, out HashSet<Player>? observers) ? observers : [];

    private void ProcessPackets(Player player)
    {
        try
        {
            player.Session.ProcessWorldPackets(player);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "packet handling failed for {Player}; disconnecting", player.Name);
            player.Session.Kick();
        }
    }

    private static void DeliverInRange(
        Player player, WorldObject source, float range, WorldOpcode opcode, ReadOnlySpan<byte> payload,
        bool includeSelf, Team? onlyTeam)
    {
        if (ReferenceEquals(player, source))
        {
            if (includeSelf)
            {
                player.Session.Send(opcode, payload);
            }

            return;
        }

        if (onlyTeam is { } team && player.Team != team)
        {
            return;
        }

        if (range > 0)
        {
            float dx = player.X - source.X;
            float dy = player.Y - source.Y;
            float dz = player.Z - source.Z;
            float max = range + player.BoundingRadius + source.BoundingRadius;
            if ((dx * dx) + (dy * dy) + (dz * dz) >= max * max)
            {
                return;
            }
        }

        player.Session.Send(opcode, payload);
    }

    /// <summary>
    /// The objects a query around (x, y) must consider: everything in the touched cells plus
    /// <paramref name="extra"/>, without duplicates, in map-join order (so passes are
    /// deterministic, like the insertion-ordered scan they replace).
    /// </summary>
    private List<WorldObject> Query(float x, float y, float radius, IEnumerable<WorldObject>? extra)
    {
        var found = new List<WorldObject>();
        _grid.CollectObjects(x, y, radius, found);
        if (extra is not null)
        {
            found.AddRange(extra);
        }

        var seen = new HashSet<WorldObject>(found.Count, ReferenceEqualityComparer.Instance);
        found.RemoveAll(o => !seen.Add(o));
        found.Sort(static (a, b) => a.MapSequence.CompareTo(b.MapSequence));
        return found;
    }

    /// <summary>
    /// Everything whose visibility relation with <paramref name="center"/> could change: objects
    /// in the cells within visibility range (+ grey distance + both radii), whatever it sees,
    /// and whoever sees it. Anything else is out of range in both directions and invisible in
    /// both, so evaluating it would change nothing.
    /// </summary>
    private List<WorldObject> VisibilityCandidates(WorldObject center)
    {
        var extra = new List<WorldObject>();
        if (center is Player player)
        {
            foreach (ObjectGuid guid in player.VisibleObjects)
            {
                if (_objects.TryGetValue(guid, out WorldObject? seen))
                {
                    extra.Add(seen);
                }
            }
        }

        if (_observers.TryGetValue(center.Guid, out HashSet<Player>? observers))
        {
            extra.AddRange(observers);
        }

        float radius = VisibilityRange + VisibilityGreyDistance + center.BoundingRadius + _grid.MaxBoundingRadius;
        return Query(center.X, center.Y, radius, extra);
    }

    /// <summary>Re-evaluate visibility between a player and everything near it, in both directions.</summary>
    private void UpdateVisibility(Player player)
    {
        foreach (WorldObject other in VisibilityCandidates(player))
        {
            if (ReferenceEquals(other, player) || !ReferenceEquals(other.Map, this))
            {
                continue;
            }

            UpdateVisibilityOf(viewer: player, target: other);
            if (other is Player otherPlayer)
            {
                UpdateVisibilityOf(viewer: otherPlayer, target: player);
            }
        }
    }

    /// <summary>Re-evaluate which players see a non-player object (vmangos WorldObject::UpdateObjectVisibility).</summary>
    private void UpdateObjectVisibility(WorldObject obj)
    {
        foreach (WorldObject other in VisibilityCandidates(obj))
        {
            if (other is Player viewer && ReferenceEquals(viewer.Map, this))
            {
                UpdateVisibilityOf(viewer, obj);
            }
        }
    }

    /// <summary>vmangos Player::UpdateVisibilityOf: create on entering range, out-of-range on leaving.</summary>
    private void UpdateVisibilityOf(Player viewer, WorldObject target)
    {
        bool inVisibleList = viewer.VisibleObjects.Contains(target.Guid);
        bool inRange = IsWithinVisibilityDistance(viewer, target, inVisibleList);
        bool allowed = PassesVisibilityRules(viewer, target, inVisibleList);

        if (inVisibleList && (!inRange || !allowed))
        {
            RemoveVisible(viewer, target.Guid);
            viewer.PendingUpdates.AddOutOfRange(target.Guid);
        }
        else if (!inVisibleList && inRange && allowed)
        {
            AddVisible(viewer, target.Guid);
            PacketWriter block = viewer.PendingUpdates.BeginBlock();
            UpdateBlockWriter.WriteCreateBlock(block, target, viewer, _newObjects.Contains(target.Guid), _world.NowMs);
            viewer.PendingUpdates.EndBlock();
        }
    }

    private bool PassesVisibilityRules(Player viewer, WorldObject target, bool inVisibleList)
    {
        foreach (IVisibilityRule rule in _visibilityRules)
        {
            if (!rule.CanSee(viewer, target, inVisibleList, _detecting))
            {
                return false;
            }
        }

        return true;
    }

    private void AddVisible(Player viewer, ObjectGuid target)
    {
        viewer.VisibleObjects.Add(target);
        if (!_observers.TryGetValue(target, out HashSet<Player>? observers))
        {
            observers = new HashSet<Player>(ReferenceEqualityComparer.Instance);
            _observers[target] = observers;
        }

        observers.Add(viewer);
    }

    private bool RemoveVisible(Player viewer, ObjectGuid target)
    {
        if (!viewer.VisibleObjects.Remove(target))
        {
            return false;
        }

        if (_observers.TryGetValue(target, out HashSet<Player>? observers))
        {
            observers.Remove(viewer);
            if (observers.Count == 0)
            {
                _observers.Remove(target);
            }
        }

        return true;
    }

    /// <summary>
    /// Shared removal: out of the index, SMSG_DESTROY_OBJECT (vanilla: the full 8-byte GUID) to
    /// every client that has it, and — for a player — out of every observer list it was on.
    /// </summary>
    private void RemoveFromWorld(WorldObject obj)
    {
        _objects.Remove(obj.Guid);
        _grid.Remove(obj);
        _movedObjects.Remove(obj);
        _newObjects.Remove(obj.Guid);

        if (_observers.Remove(obj.Guid, out HashSet<Player>? observers))
        {
            Span<byte> destroy = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(destroy, obj.Guid.Value);
            foreach (Player other in observers.OrderBy(p => p.MapSequence))
            {
                if (other.VisibleObjects.Remove(obj.Guid))
                {
                    // A joining observer may still have this object's create queued.
                    // Flush it before destroying the object so the client cannot recreate it
                    // later from the end-of-tick flush (vmangos update-before-remove order).
                    FlushPlayer(other);
                    other.Session.Send(WorldOpcode.SmsgDestroyObject, destroy);
                }
            }
        }

        if (obj is Player player)
        {
            foreach (ObjectGuid seen in player.VisibleObjects)
            {
                if (_observers.TryGetValue(seen, out HashSet<Player>? seenBy))
                {
                    seenBy.Remove(player);
                    if (seenBy.Count == 0)
                    {
                        _observers.Remove(seen);
                    }
                }
            }

            player.VisibleObjects.Clear();
            player.PendingUpdates.Clear();
        }

        if (obj.IsQueuedForUpdate)
        {
            _valuesQueue.Remove(obj);
            obj.IsQueuedForUpdate = false;
        }

        obj.ClearChangedFields();
        obj.Map = null;
    }

    /// <summary>An object left in a grid that is being unloaded (vmangos ObjectGridUnloader).</summary>
    private void EvictFromGrid(WorldObject obj)
    {
        if (obj is Player)
        {
            return; // players keep their grids loaded; never evicted
        }

        if (_objects.TryGetValue(obj.Guid, out WorldObject? stored) && ReferenceEquals(stored, obj))
        {
            RemoveFromWorld(obj);
        }
    }

    private void SendValuesUpdate(WorldObject obj)
    {
        // An item's fields go to its owner only (vmangos Item::BuildUpdateData).
        if (obj is Items.Item item)
        {
            if (item.Inventory?.Player is { } owner && ReferenceEquals(owner.Map, this))
            {
                AppendValues(obj, owner);
            }

            return;
        }

        if (obj is Player self && ReferenceEquals(self.Map, this))
        {
            AppendValues(obj, self);
        }

        if (_observers.TryGetValue(obj.Guid, out HashSet<Player>? observers))
        {
            foreach (Player viewer in observers)
            {
                if (!ReferenceEquals(viewer, obj))
                {
                    AppendValues(obj, viewer);
                }
            }
        }
    }

    private static void AppendValues(WorldObject obj, Player viewer)
    {
        PacketWriter block = viewer.PendingUpdates.BeginBlock();
        int before = block.Length;
        if (UpdateBlockWriter.TryWriteValuesBlock(block, obj, viewer))
        {
            viewer.PendingUpdates.EndBlock();
        }
        else if (block.Length == before)
        {
            viewer.PendingUpdates.CancelBlock();
        }
    }

    private void FlushPlayer(Player player)
        => player.PendingUpdates.Flush(
            (opcode, payload) => player.Session.Send(opcode, payload),
            _world.Options.UpdateCompressionThreshold);

    internal void EnsureWorldThread()
    {
        if (!_world.IsWorldThread)
        {
            throw new InvalidOperationException("map state may only be changed on the world thread");
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
