using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Maps.Grid;

/// <summary>
/// Lifecycle of a loaded grid (vmangos <c>grid_state_t</c>, GridStates.cpp). Invalid exists
/// in vmangos only as an unused sentinel and is not modelled.
/// </summary>
public enum GridState
{
    /// <summary>Objects nearby; checked for activity every tenth of the expiry time.</summary>
    Active,

    /// <summary>Transition state: moves straight to <see cref="Removal"/> with a full expiry timer.</summary>
    Idle,

    /// <summary>Unloaded once the expiry timer runs out, unless locked or something active came near.</summary>
    Removal,
}

/// <summary>
/// One 533⅓-yard grid of a map (vmangos <c>NGrid</c> + <c>GridInfo</c>): its 16 × 16 cells,
/// its lifecycle state and expiry timer, and its unload locks.
/// <para>Thread affinity: world thread (owned by its map).</para>
/// </summary>
public sealed class Grid
{
    private readonly List<WorldObject>?[] _cells = new List<WorldObject>?[GridDefines.MaxNumberOfCells * GridDefines.MaxNumberOfCells];

    // The players of each cell, also listed in _cells: vmangos keeps them in a separate per-cell
    // container (GridDefines.h: AllWorldObjectTypes, the "world" half of a cell), so a query that is
    // only about players (Map::UpdateObjectVisibility) does not walk the creatures and game objects.
    private readonly List<Player>?[] _playerCells = new List<Player>?[GridDefines.MaxNumberOfCells * GridDefines.MaxNumberOfCells];

    // The objects of each cell again, in map-join order (WorldObject.MapSequence), for the visibility pass: it merges
    // ordered cells instead of sorting every mover's candidates. _cells keeps its insertion order, which spell, trap and
    // totem searches iterate. _playerCells is join-ordered too: its only readers sort into join order anyway.
    private readonly List<WorldObject>?[] _orderedCells = new List<WorldObject>?[GridDefines.MaxNumberOfCells * GridDefines.MaxNumberOfCells];
    private long _timerMs;
    private int _unloadActiveLocks;

    internal Grid(GridCoord coord, long expiryMs, bool unloadAllowed)
    {
        Coord = coord;
        _timerMs = expiryMs;

        // vmangos GridInfo(expiry, unload): i_unloadExplicitLock(!unload) — with GridUnload off
        // every grid is explicitly locked and never unloads.
        UnloadExplicitLock = !unloadAllowed;
    }

    public GridCoord Coord { get; }

    public GridState State { get; internal set; } = GridState.Idle;

    /// <summary>Whether the grid's objects were loaded (vmangos <c>isGridObjectDataLoaded</c>).</summary>
    public bool ObjectDataLoaded { get; internal set; }

    /// <summary>Number of objects in all cells.</summary>
    public int ObjectCount { get; private set; }

    /// <summary>Number of active objects (players and objects marked active) in all cells (vmangos <c>ActiveObjectsInGrid</c>).</summary>
    public int ActiveObjectCount { get; internal set; }

    /// <summary>Remaining time before the next state check (vmangos <c>GridInfo::i_timer</c>).</summary>
    public long TimerMs => _timerMs;

    /// <summary>An explicit no-unload lock (vmangos <c>setUnloadExplicitLock</c>, e.g. <c>Map::LoadGrid(.., no_unload)</c>).</summary>
    public bool UnloadExplicitLock { get; set; }

    /// <summary>Whether something prevents unloading (vmangos <c>GridInfo::getUnloadLock</c>).</summary>
    public bool IsUnloadLocked => _unloadActiveLocks > 0 || UnloadExplicitLock;

    /// <summary>Objects in a cell of this grid (local cell coordinates 0..15).</summary>
    public IReadOnlyList<WorldObject> ObjectsIn(int localX, int localY)
        => _cells[(localX * GridDefines.MaxNumberOfCells) + localY] ?? (IReadOnlyList<WorldObject>)[];

    /// <summary>Every object in the grid (allocates; for unloading and diagnostics).</summary>
    public List<WorldObject> AllObjects()
    {
        var all = new List<WorldObject>(ObjectCount);
        foreach (List<WorldObject>? cell in _cells)
        {
            if (cell is not null)
            {
                all.AddRange(cell);
            }
        }

        return all;
    }

    /// <summary>vmangos <c>incUnloadActiveLock</c>: an active object's spawn point is in this grid.</summary>
    public void IncrementUnloadActiveLock() => _unloadActiveLocks++;

    /// <summary>vmangos <c>decUnloadActiveLock</c>.</summary>
    public void DecrementUnloadActiveLock()
    {
        if (_unloadActiveLocks > 0)
        {
            _unloadActiveLocks--;
        }
    }

    internal void Add(CellCoord cell, WorldObject obj)
    {
        int index = Index(cell);
        (_cells[index] ??= []).Add(obj);
        JoinOrder.Insert(_orderedCells[index] ??= [], obj);
        ObjectCount++;
        if (obj is Player player)
        {
            JoinOrder.Insert(_playerCells[index] ??= [], player);
        }
    }

    internal void Remove(CellCoord cell, WorldObject obj)
    {
        int index = Index(cell);
        List<WorldObject>? list = _cells[index];
        if (list is not null && list.Remove(obj))
        {
            ObjectCount--;
            if (_orderedCells[index] is { } ordered)
            {
                JoinOrder.Remove(ordered, obj);
            }

            if (obj is Player player && _playerCells[index] is { } players)
            {
                JoinOrder.Remove(players, player);
            }
        }
    }

    internal void AppendObjects(int localX, int localY, List<WorldObject> results)
    {
        List<WorldObject>? list = _cells[(localX * GridDefines.MaxNumberOfCells) + localY];
        if (list is not null)
        {
            results.AddRange(list);
        }
    }

    internal void AppendPlayers(int localX, int localY, List<Player> results)
    {
        List<Player>? list = _playerCells[(localX * GridDefines.MaxNumberOfCells) + localY];
        if (list is { Count: > 0 })
        {
            results.AddRange(list);
        }
    }

    /// <summary>A cell's objects in join order (null or empty when it has none).</summary>
    internal List<WorldObject>? OrderedObjects(int localX, int localY) => _orderedCells[(localX * GridDefines.MaxNumberOfCells) + localY];

    /// <summary>A cell's players in join order (null or empty when it has none).</summary>
    internal List<Player>? OrderedPlayers(int localX, int localY) => _playerCells[(localX * GridDefines.MaxNumberOfCells) + localY];

    /// <summary>vmangos <c>GridInfo::UpdateTimeTracker</c>; true once the timer has passed.</summary>
    internal bool AdvanceTimer(long diffMs)
    {
        _timerMs -= diffMs;
        return _timerMs <= 0;
    }

    /// <summary>vmangos <c>Map::ResetGridExpiry(grid, factor)</c>.</summary>
    internal void ResetTimer(long expiryMs, float factor = 1.0f) => _timerMs = (long)(expiryMs * factor);

    private static int Index(CellCoord cell) => (cell.LocalX * GridDefines.MaxNumberOfCells) + cell.LocalY;
}
