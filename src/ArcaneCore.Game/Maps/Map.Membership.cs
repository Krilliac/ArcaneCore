using System.Buffers.Binary;
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

public sealed partial class Map
{
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
        _heartbeatSnapshotDirty = true;
        _grid.Add(player, active: true);

        // The create block carries every current value, so pending changes are moot.
        player.ClearChangedFields();
        player.IsQueuedForUpdate = false;

        // vmangos Map::Add: SendInitTransports goes out before SendInitSelf (IMapUpdater.OnPlayerAdding).
        foreach (IMapUpdater updater in _updaters)
        {
            updater.OnPlayerAdding(this, player);
        }

        // vmangos Map::SendInitSelf: the player's own transport goes first in the self packet (IMapUpdater.OnWritingSelf).
        foreach (IMapUpdater updater in _updaters)
        {
            updater.OnWritingSelf(this, player, player.PendingUpdates);
        }

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
        try
        {
            foreach (IMapUpdater updater in _updaters)
            {
                updater.OnPlayerRemoved(this, player);
            }
        }
        finally
        {
            // A throwing hook still propagates, but must not leave the player in _objects, the grid
            // and every observer's visible set.
            RemoveFromWorld(player);
            player.NeedsVisibilityUpdate = false;
        }
    }

    /// <summary>
    /// Put a non-player object (creature, game object, …) into the map — the seam for content
    /// that spawns into grids. Its grid is created (vmangos Map::Add&lt;T&gt; → EnsureGridCreated;
    /// an <paramref name="active"/> object pins only its own grid; only players load the grids around them), and
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
        if (obj is Unit) _heartbeatSnapshotDirty = true;
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
    /// Mark a non-player object active (vmangos Map::AddToActive): it is updated wherever it stands and pins its own grid;
    /// unlike a player it loads no grid around it (<see cref="Grid.GridContainer.SetActive"/>).
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
    /// Make a non-player object of this map visible to every player on the map, at any distance, and never out of range while both are on
    /// it (vmangos keeps its transports in <c>Map::m_transports</c> instead of the grid, sends them at map entry, Map.cpp:520-553 and
    /// 1720-1733, and a visibility pass never sends them out of range). Used for the trams of the Deeprun Tram
    /// (<see cref="GameObjects.GameObjectMapSystem"/>): a passenger rides 2,480 yards away from the car's spawn. Every player already on the
    /// map re-evaluates its visibility at the next update.
    /// </summary>
    public void AddMapWideObject(WorldObject obj)
    {
        EnsureWorldThread();
        if (obj is Player || !ReferenceEquals(obj.Map, this))
        {
            throw new InvalidOperationException($"{obj.Guid} is not a non-player object of map {MapId}");
        }

        if (!_mapWide.Add(obj))
        {
            return;
        }

        _mapWideOrder.Add(obj);
        foreach (WorldObject other in _objects.Values)
        {
            if (other is Player player)
            {
                player.NeedsVisibilityUpdate = true;
            }
        }
    }

    /// <summary>Whether <paramref name="obj"/> is seen map-wide (<see cref="AddMapWideObject"/>).</summary>
    public bool IsMapWide(WorldObject obj) => _mapWide.Contains(obj);

    /// <summary>
    /// Unload every grid (objects are evicted, grid/terrain events run) and refuse further
    /// players — vmangos <c>Map::UnloadAll</c> for a deleted instance — then let go of the
    /// terrain (<see cref="TerrainManager.Release"/>, vmangos <c>Map::~Map</c>). Called by
    /// <see cref="WorldRuntime"/> after <see cref="WorldRuntime.MapUnloading"/>.
    /// </summary>
    internal void UnloadAll()
    {
        EnsureWorldThread();
        EnsureNotInUpdatePhase();
        bool holdsTerrain = !IsUnloaded;
        IsUnloaded = true;
        try
        {
            _grid.UnloadAll();
            _afterUpdate.Clear();
        }
        finally
        {
            // vmangos Map::~Map (Map.cpp:76-78): after its grids, the map lets go of its map id's terrain. A grid that failed to unload
            // still references its tile, which keeps the terrain.
            if (holdsTerrain)
            {
                WorldMaps.Of(_world).Terrain.Release(_terrain, _grid.Options.GridUnload);
            }
        }
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

    /// <summary>True while <paramref name="player"/> has left this map for a far teleport and has not yet entered the destination map.</summary>
    internal bool IsInTransit(Player player) => _transit.Contains(player);

    /// <summary>
    /// Shared removal: out of the index, SMSG_DESTROY_OBJECT (vanilla: the full 8-byte GUID) to
    /// every client that has it, and — for a player — out of every observer list it was on.
    /// </summary>
    private void RemoveFromWorld(WorldObject obj)
    {
        _objects.Remove(obj.Guid);
        NoteRemoval();
        if (obj is Unit) _heartbeatSnapshotDirty = true;
        if (_mapWide.Remove(obj))
        {
            _mapWideOrder.Remove(obj);
        }

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
                    other.VisibilityVersion++;
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
            player.VisibilityVersion++;
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
}
