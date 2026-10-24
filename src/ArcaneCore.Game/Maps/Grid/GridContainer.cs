using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Maps.Grid;

/// <summary>
/// The spatial index and grid lifecycle of one map — vmangos' <c>Map</c> grid half
/// (<c>EnsureGridCreated</c>, <c>EnsureGridLoadedAtEnter</c>, <c>AddToGrid</c>/<c>RemoveFromGrid</c>,
/// relocation, <c>ActiveObjectsNearGrid</c>, <c>UnloadGrid</c>) plus the grid state machine of
/// GridStates.cpp.
/// <para>
/// Objects live in 33⅓-yard cells; queries visit only the cells a circle touches
/// (<c>Cell::Visit</c>). Players and objects marked active keep the grids around them loaded;
/// a grid with nothing active nearby goes Active → Idle → Removal and is unloaded once its
/// expiry timer runs out.
/// </para>
/// <para>Thread affinity: world thread (owned by its map).</para>
/// </summary>
public sealed partial class GridContainer
{
    /// <summary>Above this many cells a query walks the object list instead (radius ≳ 1700 yards).</summary>
    private const int MaxCellsPerQuery = 100 * 100;

    private readonly Grid?[] _grids = new Grid?[GridDefines.MaxNumberOfGrids * GridDefines.MaxNumberOfGrids];
    private readonly Dictionary<WorldObject, CellCoord> _cells = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<WorldObject> _active = new(ReferenceEqualityComparer.Instance);

    // Objects at a non-finite position: indexed in a clamped cell, but also returned by every
    // query so callers' exact distance tests decide (a NaN distance compares false either way).
    private readonly HashSet<WorldObject> _unplaced = new(ReferenceEqualityComparer.Instance);
    private readonly List<Grid> _loaded = [];
    private float _visibilityDistance;
    private GridLifecycleCounters _counters;

    // Grids GridApproaching was raised for and when (the container's update clock), so a prefetch is asked for once per expiry.
    private readonly Dictionary<int, long> _approached = [];
    private long _clockMs;

    public GridContainer(MapOptions options, float visibilityDistance)
    {
        Options = options;
        _visibilityDistance = visibilityDistance;
    }

    public MapOptions Options { get; }

    /// <summary>
    /// Raised when a grid is created (vmangos <c>EnsureGridCreated</c>, which also loads the
    /// grid's terrain tile).
    /// </summary>
    public event Action<Grid>? GridCreated;

    /// <summary>
    /// Raised when a grid is created and its objects are to be loaded (vmangos
    /// <c>ObjectGridLoader::LoadN</c> in <c>EnsureGridLoaded</c>). Content spawning hooks in here.
    /// </summary>
    public event Action<Grid>? GridLoaded;

    /// <summary>
    /// Raised before a grid is unloaded (vmangos <c>ObjectGridUnloader</c>). Handlers remove or
    /// move their objects; whatever is still in the grid afterwards is removed by the map.
    /// </summary>
    public event Action<Grid>? GridUnloading;

    /// <summary>
    /// Raised when an object entered a cell from which a grid that does not exist yet is within reach
    /// (<see cref="MapOptions.GridPrefetchDistance"/>): its tiles may be read ahead of <see cref="GridCreated"/>. At most once per grid
    /// every two minutes; nothing else depends on it.
    /// </summary>
    public event Action<GridCoord>? GridApproaching;

    /// <summary>Raised after a grid was unloaded and dropped.</summary>
    public event Action<GridCoord>? GridUnloaded;

    /// <summary>Removes an object left in a grid being unloaded (set by the owning map).</summary>
    internal Action<WorldObject>? EvictObject { get; set; }

    /// <summary>The visibility distance used by <see cref="ActiveObjectsNearGrid"/> (vmangos <c>GetVisibilityDistance</c>).</summary>
    public float VisibilityDistance
    {
        get => _visibilityDistance;
        set => _visibilityDistance = value;
    }

    public int LoadedGridCount => _loaded.Count;

    public IReadOnlyList<Grid> LoadedGrids => _loaded;

    public int ObjectCount => _cells.Count;

    /// <summary>Every indexed object (allocation-free enumeration of the index keys).</summary>
    public IEnumerable<WorldObject> Objects => _cells.Keys;

    /// <summary>The largest bounding radius of any object added or moved so far (pads distance queries).</summary>
    public float MaxBoundingRadius { get; private set; }

    /// <summary>Cumulative grid lifecycle work of this container (diagnostics; world thread).</summary>
    internal GridLifecycleCounters Counters => _counters;

    public Grid? GetGrid(GridCoord coord) => _grids[coord.Id];

    public bool IsGridLoaded(GridCoord coord) => _grids[coord.Id] is { ObjectDataLoaded: true };

    public bool Contains(WorldObject obj) => _cells.ContainsKey(obj);

    public bool IsActive(WorldObject obj) => obj.IsGridActiveObject;

    // One bit per cell of the map (1024 x 1024), allocated on the first mark; the words set last time are cleared next time.
    private ulong[]? _activeCells;
    private readonly List<int> _activeCellWords = [];

    /// <summary>Cells marked by the last <see cref="MarkActiveCells"/> (diagnostics, tests).</summary>
    public int ActiveCellCount { get; private set; }

    /// <summary>
    /// vmangos Map::UpdateActiveCellsSynch / MarkCellsAroundObject (Maps/Map.cpp:743-760, 815-838): mark every cell a circle of
    /// <see cref="MapOptions.GridActivationDistance"/> touches (Cell::CalculateCellArea) around each player and active object
    /// (Map::AddToActive: <see cref="SetActive"/>). Objects in other cells are not updated this tick
    /// (<see cref="IsInActiveCell"/>).
    /// </summary>
    public void MarkActiveCells()
    {
        ulong[] bits = _activeCells ??= new ulong[(GridDefines.TotalNumberOfCellsPerMap * GridDefines.TotalNumberOfCellsPerMap) / 64];
        foreach (int word in _activeCellWords)
        {
            bits[word] = 0;
        }

        _activeCellWords.Clear();
        int marked = 0;
        float radius = Options.GridActivationDistance;
        foreach (WorldObject obj in _active)
        {
            if (!float.IsFinite(obj.X) || !float.IsFinite(obj.Y))
            {
                continue;
            }

            CellArea area = GridDefines.CalculateCellArea(obj.X, obj.Y, radius);
            for (int x = area.Low.X; x <= area.High.X; x++)
            {
                for (int y = area.Low.Y; y <= area.High.Y; y++)
                {
                    int id = (y * GridDefines.TotalNumberOfCellsPerMap) + x;
                    ulong mask = 1UL << (id & 63);
                    ref ulong word = ref bits[id >> 6];
                    if ((word & mask) != 0)
                    {
                        continue;
                    }

                    if (word == 0)
                    {
                        _activeCellWords.Add(id >> 6);
                    }

                    word |= mask;
                    marked++;
                }
            }
        }

        ActiveCellCount = marked;
    }

    /// <summary>
    /// Whether the cell the object is indexed in (or, for an object out of the index such as a dead creature waiting for its respawn
    /// at home, the cell of its position: vmangos keeps it in its cell's container) was marked by the last <see cref="MarkActiveCells"/>.
    /// </summary>
    public bool IsInActiveCell(WorldObject obj)
    {
        if (_activeCells is not { } bits)
        {
            return false;
        }

        int id = obj.GridCellId;
        if (id < 0)
        {
            if (!float.IsFinite(obj.X) || !float.IsFinite(obj.Y))
            {
                return false;
            }

            CellCoord cell = GridDefines.ComputeCellCoord(obj.X, obj.Y);
            if ((uint)cell.X >= GridDefines.TotalNumberOfCellsPerMap || (uint)cell.Y >= GridDefines.TotalNumberOfCellsPerMap)
            {
                return false;
            }

            id = (cell.Y * GridDefines.TotalNumberOfCellsPerMap) + cell.X;
        }

        return (bits[id >> 6] & (1UL << (id & 63))) != 0;
    }

    /// <summary>The cell an object is indexed in, or null when it is not in this container.</summary>
    public CellCoord? CellOf(WorldObject obj) => _cells.TryGetValue(obj, out CellCoord cell) ? cell : null;

    /// <summary>
    /// Index an object at its position. Players load and activate their grid
    /// and every grid within the activation distance (a non-player active object only its own) (vmangos <c>Map::Add(Player*)</c> →
    /// <c>EnsureGridLoadedAtEnter</c> + <c>LoadMapCellsAround</c>); other objects only ensure
    /// their grid exists (vmangos <c>Map::Add&lt;T&gt;</c> → <c>EnsureGridCreated</c>).
    /// </summary>
    public void Add(WorldObject obj, bool active)
    {
        if (_cells.ContainsKey(obj))
        {
            throw new InvalidOperationException($"{obj.Guid} is already in the grid index");
        }

        CellCoord cell = GridDefines.ComputeCellCoord(obj.X, obj.Y);
        Grid grid = active ? EnsureGridLoadedAtEnter(cell.Grid) : EnsureGridCreated(cell.Grid);
        grid.Add(cell, obj);
        _cells[obj] = cell;
        obj.GridCellId = (cell.Y * GridDefines.TotalNumberOfCellsPerMap) + cell.X;
        TrackRadius(obj);
        TrackPlacement(obj);

        if (active)
        {
            _active.Add(obj);
            obj.IsGridActiveObject = true;
            grid.ActiveObjectCount++;
            if (obj is Player)
            {
                LoadGridsAround(obj.X, obj.Y, Options.GridActivationDistance); // a non-player active object pins only its own grid (SetActive)
            }
        }
        else
        {
            obj.IsGridActiveObject = false;
        }

        Approach(obj, active && obj is Player);
    }

    /// <summary>Remove an object from the index (vmangos <c>RemoveFromGrid</c>).</summary>
    public bool Remove(WorldObject obj)
    {
        if (!_cells.Remove(obj, out CellCoord cell))
        {
            return false;
        }

        obj.GridCellId = -1;
        obj.IsGridActiveObject = false;
        _unplaced.Remove(obj);
        Grid? grid = _grids[cell.Grid.Id];
        grid?.Remove(cell, obj);
        if (_active.Remove(obj) && grid is not null)
        {
            grid.ActiveObjectCount--;
        }

        return true;
    }

    /// <summary>Whether <see cref="Relocate"/> would move <paramref name="obj"/> to another cell (no change made).</summary>
    internal bool ChangesCell(WorldObject obj)
        => float.IsFinite(obj.X) && float.IsFinite(obj.Y) && _cells.TryGetValue(obj, out CellCoord cell) && GridDefines.ComputeCellCoord(obj.X, obj.Y) != cell;

    /// <summary>
    /// Whether <see cref="Relocate"/> would create, load or approach a grid that is not already in memory (and therefore fire
    /// <see cref="GridLoaded"/> or <see cref="GridApproaching"/>, which touch shared spawn/terrain/navmesh state).
    /// </summary>
    internal bool RelocateTouchesGridLifecycle(WorldObject obj)
    {
        if (!float.IsFinite(obj.X) || !float.IsFinite(obj.Y) || !_cells.TryGetValue(obj, out CellCoord oldCell))
        {
            return false;
        }

        CellCoord newCell = GridDefines.ComputeCellCoord(obj.X, obj.Y);
        if (newCell == oldCell)
        {
            return false;
        }

        if (obj is Player)
        {
            return true;
        }

        bool active = _active.Contains(obj);
        if (newCell.Grid != oldCell.Grid)
        {
            Grid? targetGrid = _grids[newCell.Grid.Id];
            if (targetGrid is null || (active && !targetGrid.ObjectDataLoaded))
            {
                return true;
            }
        }

        float prefetch = Options.GridPrefetchDistance;
        if (GridApproaching is not null && prefetch > 0)
        {
            float radius = prefetch / 2;
            CellArea area = GridDefines.CalculateCellArea(obj.X, obj.Y, radius);
            for (int gx = area.Low.Grid.X; gx <= area.High.Grid.X; gx++)
            {
                for (int gy = area.Low.Grid.Y; gy <= area.High.Grid.Y; gy++)
                {
                    var coord = new GridCoord(gx, gy);
                    if (_grids[coord.Id] is null
                        && (!_approached.TryGetValue(coord.Id, out long at) || _clockMs - at >= TilePrefetchCache<object>.ExpiryMs))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Re-index an object after it moved (vmangos <c>Map::PlayerRelocation</c> /
    /// <c>CreatureCellRelocation</c>). Moving into another grid loads it for active objects; an
    /// active object entering a different cell re-activates its grid (expiry × 0.1); only a player
    /// also loads the grids within the activation distance. Returns true when the cell changed.
    /// </summary>
    public bool Relocate(WorldObject obj)
    {
        if (!_cells.TryGetValue(obj, out CellCoord oldCell))
        {
            return false;
        }

        TrackRadius(obj);
        TrackPlacement(obj);
        CellCoord newCell = GridDefines.ComputeCellCoord(obj.X, obj.Y);
        if (newCell == oldCell)
        {
            return false;
        }

        bool active = _active.Contains(obj);
        Grid oldGrid = _grids[oldCell.Grid.Id]!;
        oldGrid.Remove(oldCell, obj);
        if (active)
        {
            oldGrid.ActiveObjectCount--;
        }

        Grid newGrid = oldCell.Grid == newCell.Grid ? oldGrid
            : active ? EnsureGridLoadedAtEnter(newCell.Grid)
            : EnsureGridCreated(newCell.Grid);
        newGrid.Add(newCell, obj);
        _cells[obj] = newCell;
        obj.GridCellId = (newCell.Y * GridDefines.TotalNumberOfCellsPerMap) + newCell.X;

        if (active)
        {
            newGrid.ActiveObjectCount++;
            if (newGrid.State != GridState.Active)
            {
                newGrid.ResetTimer(Options.EffectiveCleanUpDelayMs, 0.1f);
                newGrid.State = GridState.Active;
            }

            // Only a player loads the grids around it (vmangos Player::LoadMapCellsAround); an active creature or game object
            // loads the grid it walks into (Map::CreatureCellRelocation, Maps/Map.cpp:1486-1509) and nothing more.
            if (obj is Player)
            {
                LoadGridsAround(obj.X, obj.Y, Options.GridActivationDistance);
            }
        }

        Approach(obj, active && obj is Player);
        return true;
    }

    /// <summary>Raise <see cref="GridApproaching"/> for the grids within reach of an object that do not exist yet.</summary>
    private void Approach(WorldObject obj, bool active)
    {
        float prefetch = Options.GridPrefetchDistance;
        if (GridApproaching is null || !(prefetch > 0) || !float.IsFinite(obj.X) || !float.IsFinite(obj.Y))
        {
            return;
        }

        float radius = active ? Options.GridActivationDistance + prefetch : prefetch / 2;
        CellArea area = GridDefines.CalculateCellArea(obj.X, obj.Y, radius);
        for (int gx = area.Low.Grid.X; gx <= area.High.Grid.X; gx++)
        {
            for (int gy = area.Low.Grid.Y; gy <= area.High.Grid.Y; gy++)
            {
                var coord = new GridCoord(gx, gy);
                if (_grids[coord.Id] is not null
                    || (_approached.TryGetValue(coord.Id, out long at) && _clockMs - at < TilePrefetchCache<object>.ExpiryMs))
                {
                    continue;
                }

                _approached[coord.Id] = _clockMs;
                GridApproaching(coord);
            }
        }
    }

    /// <summary>
    /// Mark a non-player object active or not (vmangos <c>Map::AddToActive</c> / <c>RemoveFromActive</c>, Maps/Map.cpp:1925-1963):
    /// the object's own grid is loaded and pinned (<c>EnsureGridLoadedAtEnter</c>, <c>incUnloadActiveLock</c>) and the object is
    /// updated wherever it stands. Unlike a player it loads no grid around it: the cells around it are updated only where their
    /// grid is already loaded (<c>Map::UpdateCellsAroundObject</c> visits with <c>SetNoCreate</c>, Map.cpp:736;
    /// <see cref="MarkActiveCells"/> marks cells without loading). Like vmangos' <c>Map::ActiveObjectsNearGrid</c>
    /// (Map.cpp:1887-1921), it still keeps an already loaded grid within visibility range from unloading.
    /// </summary>
    public void SetActive(WorldObject obj, bool active)
    {
        if (!_cells.TryGetValue(obj, out CellCoord cell))
        {
            throw new InvalidOperationException($"{obj.Guid} is not in the grid index");
        }

        Grid grid = _grids[cell.Grid.Id]!;
        if (active && _active.Add(obj))
        {
            obj.IsGridActiveObject = true;
            grid.ActiveObjectCount++;
            if (!grid.ObjectDataLoaded || grid.State != GridState.Active)
            {
                EnsureGridLoadedAtEnter(cell.Grid);
                grid.ResetTimer(Options.EffectiveCleanUpDelayMs, 0.1f);
                grid.State = GridState.Active;
            }
        }
        else if (!active && _active.Remove(obj))
        {
            obj.IsGridActiveObject = false;
            grid.ActiveObjectCount--;
        }
    }

    /// <summary>
    /// Load every grid a circle touches (vmangos <c>Player::LoadMapCellsAround</c> /
    /// <c>Map::ForceLoadGridsAroundPosition</c>: a visit with <c>dont_load = false</c>).
    /// </summary>
    public void LoadGridsAround(float x, float y, float radius)
    {
        CellArea area = GridDefines.CalculateCellArea(x, y, radius);
        GridCoord low = area.Low.Grid;
        GridCoord high = area.High.Grid;
        for (int gx = low.X; gx <= high.X; gx++)
        {
            for (int gy = low.Y; gy <= high.Y; gy++)
            {
                EnsureGridLoadedAtEnter(new GridCoord(gx, gy));
            }
        }
    }

    /// <summary>
    /// Append every object in the cells a circle touches to <paramref name="results"/>
    /// (vmangos <c>Cell::Visit</c> with <c>dont_load = true</c>: grids that are not loaded are
    /// skipped). The caller still applies its exact distance test; an object may appear twice
    /// (objects at a non-finite position are always included).
    /// </summary>
    public void CollectObjects(float x, float y, float radius, List<WorldObject> results)
    {
        if (!float.IsFinite(radius) || !float.IsFinite(x) || !float.IsFinite(y))
        {
            results.AddRange(_cells.Keys);
            return;
        }

        int start = results.Count;
        results.AddRange(_unplaced);
        CellArea area = GridDefines.CalculateCellArea(x, y, radius);
        long cells = (long)(area.High.X - area.Low.X + 1) * (area.High.Y - area.Low.Y + 1);
        if (cells > MaxCellsPerQuery)
        {
            results.RemoveRange(start, results.Count - start);
            results.AddRange(_cells.Keys);
            return;
        }

        for (int cx = area.Low.X; cx <= area.High.X; cx++)
        {
            int gx = cx / GridDefines.MaxNumberOfCells;
            for (int cy = area.Low.Y; cy <= area.High.Y; cy++)
            {
                Grid? grid = _grids[(gx * GridDefines.MaxNumberOfGrids) + (cy / GridDefines.MaxNumberOfCells)];
                grid?.AppendObjects(cx % GridDefines.MaxNumberOfCells, cy % GridDefines.MaxNumberOfCells, results);
            }
        }
    }

    private readonly List<List<WorldObject>> _orderedObjectSources = [];
    private readonly List<List<Player>> _orderedPlayerSources = [];

    /// <summary>
    /// <see cref="CollectObjects"/> in map-join order without a sort: the area's join-ordered cells are merged into the
    /// empty <paramref name="results"/>, which comes out ordered and distinct. Unplaced objects (non-finite positions)
    /// go to <paramref name="extras"/> unordered. False, with nothing added, for the cases <see cref="CollectObjects"/>
    /// answers by walking everything (a non-finite query or an oversized area); the caller then collects and sorts.
    /// </summary>
    internal bool TryCollectObjectsInJoinOrder(float x, float y, float radius, List<WorldObject> results, List<WorldObject> extras, out CellArea area)
    {
        area = default;
        if (results.Count != 0 || !float.IsFinite(radius) || !float.IsFinite(x) || !float.IsFinite(y))
        {
            return false;
        }

        area = GridDefines.CalculateCellArea(x, y, radius);
        if ((long)(area.High.X - area.Low.X + 1) * (area.High.Y - area.Low.Y + 1) > MaxCellsPerQuery)
        {
            return false;
        }

        // Not reentrant, and need not be: nothing runs between gathering the cells and merging them.
        List<List<WorldObject>> sources = _orderedObjectSources;
        sources.Clear();
        for (int cx = area.Low.X; cx <= area.High.X; cx++)
        {
            int gx = cx / GridDefines.MaxNumberOfCells;
            for (int cy = area.Low.Y; cy <= area.High.Y; cy++)
            {
                Grid? grid = _grids[(gx * GridDefines.MaxNumberOfGrids) + (cy / GridDefines.MaxNumberOfCells)];
                if (grid?.OrderedObjects(cx % GridDefines.MaxNumberOfCells, cy % GridDefines.MaxNumberOfCells) is { Count: > 0 } cell)
                {
                    sources.Add(cell);
                }
            }
        }

        JoinOrder.Merge(sources, results);
        sources.Clear();
        extras.AddRange(_unplaced);
        return true;
    }

    /// <summary><see cref="TryCollectObjectsInJoinOrder"/> for <see cref="CollectPlayers"/>.</summary>
    internal bool TryCollectPlayersInJoinOrder(float x, float y, float radius, List<Player> results, List<Player> extras, out CellArea area)
    {
        area = default;
        if (results.Count != 0 || !float.IsFinite(radius) || !float.IsFinite(x) || !float.IsFinite(y))
        {
            return false;
        }

        area = GridDefines.CalculateCellArea(x, y, radius);
        if ((long)(area.High.X - area.Low.X + 1) * (area.High.Y - area.Low.Y + 1) > MaxCellsPerQuery)
        {
            return false;
        }

        List<List<Player>> sources = _orderedPlayerSources;
        sources.Clear();
        for (int cx = area.Low.X; cx <= area.High.X; cx++)
        {
            int gx = cx / GridDefines.MaxNumberOfCells;
            for (int cy = area.Low.Y; cy <= area.High.Y; cy++)
            {
                Grid? grid = _grids[(gx * GridDefines.MaxNumberOfGrids) + (cy / GridDefines.MaxNumberOfCells)];
                if (grid?.OrderedPlayers(cx % GridDefines.MaxNumberOfCells, cy % GridDefines.MaxNumberOfCells) is { Count: > 0 } cell)
                {
                    sources.Add(cell);
                }
            }
        }

        JoinOrder.Merge(sources, results);
        sources.Clear();
        foreach (WorldObject obj in _unplaced)
        {
            if (obj is Player player)
            {
                extras.Add(player);
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a join-ordered collect over <paramref name="area"/> already returned <paramref name="obj"/>: it is indexed
    /// in a cell of the area whose grid exists. False means it may be missing and must be added as an extra.
    /// </summary>
    internal bool IsCollectedIn(WorldObject obj, CellArea area)
        => _cells.TryGetValue(obj, out CellCoord cell) && area.Contains(cell) && _grids[cell.Grid.Id] is not null;

    /// <summary>
    /// Append every player in the cells a circle touches to <paramref name="results"/> — the
    /// players-only visit vmangos <c>Map::UpdateObjectVisibility</c> (Map.cpp) makes with a
    /// <c>WorldTypeMapContainer</c> visitor, which skips the cells' creatures and game objects.
    /// Same cells and same caveats as <see cref="CollectObjects"/>: the caller applies its exact
    /// distance test, and a player may appear twice (players at a non-finite position are always
    /// included).
    /// </summary>
    public void CollectPlayers(float x, float y, float radius, List<Player> results)
    {
        CellArea area = default;
        bool walkAll = !float.IsFinite(radius) || !float.IsFinite(x) || !float.IsFinite(y);
        if (!walkAll)
        {
            area = GridDefines.CalculateCellArea(x, y, radius);
            walkAll = (long)(area.High.X - area.Low.X + 1) * (area.High.Y - area.Low.Y + 1) > MaxCellsPerQuery;
        }

        if (walkAll)
        {
            foreach (WorldObject obj in _cells.Keys)
            {
                if (obj is Player player)
                {
                    results.Add(player);
                }
            }

            return;
        }

        foreach (WorldObject obj in _unplaced)
        {
            if (obj is Player unplaced)
            {
                results.Add(unplaced);
            }
        }

        for (int cx = area.Low.X; cx <= area.High.X; cx++)
        {
            int gx = cx / GridDefines.MaxNumberOfCells;
            for (int cy = area.Low.Y; cy <= area.High.Y; cy++)
            {
                Grid? grid = _grids[(gx * GridDefines.MaxNumberOfGrids) + (cy / GridDefines.MaxNumberOfCells)];
                grid?.AppendPlayers(cx % GridDefines.MaxNumberOfCells, cy % GridDefines.MaxNumberOfCells, results);
            }
        }
    }

    /// <summary>
    /// Advance every loaded grid's state machine (vmangos <c>Map::Update</c> →
    /// <c>MapManager::UpdateGridState</c>, GridStates.cpp):
    /// Active — every expiry/10 ms, if nothing active is in or near the grid it goes Idle,
    /// otherwise its timer restarts at expiry × 0.1; Idle — restart the timer at the full expiry
    /// and go to Removal; Removal — unless locked, once the timer passes unload the grid, or
    /// restart the timer when something active is near.
    /// </summary>
    public void Update(long diffMs)
    {
        _clockMs += diffMs;
        long expiry = Options.EffectiveCleanUpDelayMs;
        for (int i = _loaded.Count - 1; i >= 0; i--)
        {
            if (i >= _loaded.Count)
            {
                continue; // an unload handler removed several grids
            }

            Grid grid = _loaded[i];
            switch (grid.State)
            {
                case GridState.Active:
                    if (grid.AdvanceTimer(diffMs))
                    {
                        if (grid.ActiveObjectCount == 0 && !ActiveObjectsNearGrid(grid.Coord))
                        {
                            grid.State = GridState.Idle;
                        }
                        else
                        {
                            grid.ResetTimer(expiry, 0.1f);
                        }
                    }

                    break;

                case GridState.Idle:
                    grid.ResetTimer(expiry);
                    grid.State = GridState.Removal;
                    break;

                case GridState.Removal:
                    if (!grid.IsUnloadLocked && grid.AdvanceTimer(diffMs) && !UnloadGrid(grid.Coord, force: false))
                    {
                        grid.ResetTimer(expiry);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Whether a player or active object stands within visibility range (in cells, plus one)
    /// of a grid's borders (vmangos <c>Map::ActiveObjectsNearGrid</c>).
    /// </summary>
    public bool ActiveObjectsNearGrid(GridCoord coord)
    {
        int cellRange = (int)MathF.Ceiling(_visibilityDistance / GridDefines.SizeOfGridCell) + 1;
        int minX = Math.Max(0, (coord.X * GridDefines.MaxNumberOfCells) - cellRange);
        int minY = Math.Max(0, (coord.Y * GridDefines.MaxNumberOfCells) - cellRange);
        int maxX = Math.Min(GridDefines.TotalNumberOfCellsPerMap - 1, (coord.X * GridDefines.MaxNumberOfCells) + GridDefines.MaxNumberOfCells + cellRange);
        int maxY = Math.Min(GridDefines.TotalNumberOfCellsPerMap - 1, (coord.Y * GridDefines.MaxNumberOfCells) + GridDefines.MaxNumberOfCells + cellRange);

        _counters.NearChecks++;
        foreach (WorldObject obj in _active)
        {
            _counters.NearCheckObjects++;
            CellCoord p = GridDefines.ComputeCellCoord(obj.X, obj.Y);
            if (p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Unload a grid (vmangos <c>Map::UnloadGrid</c>): refused while something active is near
    /// unless forced; otherwise <see cref="GridUnloading"/> runs, every object still in the grid
    /// is evicted, and the grid is dropped.
    /// </summary>
    public bool UnloadGrid(GridCoord coord, bool force)
    {
        Grid? grid = _grids[coord.Id];
        if (grid is null)
        {
            return true;
        }

        if (!force && ActiveObjectsNearGrid(coord))
        {
            return false;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        GridUnloading?.Invoke(grid);
        foreach (WorldObject obj in grid.AllObjects())
        {
            if (EvictObject is { } evict)
            {
                evict(obj);
            }

            Remove(obj);
        }

        _grids[coord.Id] = null;
        _loaded.Remove(grid);
        GridUnloaded?.Invoke(coord);
        _counters.Unloaded++;
        _counters.UnloadMicros += GridLifecycleCounters.MicrosSince(start);
        return true;
    }

    /// <summary>Unload every grid (vmangos <c>Map::UnloadAll</c>).</summary>
    public void UnloadAll()
    {
        foreach (Grid grid in _loaded.ToArray())
        {
            UnloadGrid(grid.Coord, force: true);
        }
    }

    /// <summary>vmangos <c>Map::EnsureGridCreated</c>: create the grid (Idle) if it does not exist.</summary>
    private Grid EnsureGridCreated(GridCoord coord)
    {
        Grid? grid = _grids[coord.Id];
        if (grid is null)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            _approached.Remove(coord.Id);
            grid = new Grid(coord, Options.EffectiveCleanUpDelayMs, Options.GridUnload) { State = GridState.Idle };
            _grids[coord.Id] = grid;
            _loaded.Add(grid);
            GridCreated?.Invoke(grid);
            _counters.Created++;
            _counters.CreateMicros += GridLifecycleCounters.MicrosSince(start);
        }

        return grid;
    }

    /// <summary>
    /// vmangos <c>Map::EnsureGridLoadedAtEnter</c>: create the grid, and if its objects were
    /// not loaded yet, load them (<see cref="GridLoaded"/>), restart its timer at expiry × 0.1
    /// and make it Active.
    /// </summary>
    private Grid EnsureGridLoadedAtEnter(GridCoord coord)
    {
        Grid grid = EnsureGridCreated(coord);
        if (!grid.ObjectDataLoaded)
        {
            // "it's important to set it loaded before loading!" (vmangos EnsureGridLoaded)
            grid.ObjectDataLoaded = true;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            GridLoaded?.Invoke(grid);
            _counters.Loaded++;
            _counters.LoadMicros += GridLifecycleCounters.MicrosSince(start);
            grid.ResetTimer(Options.EffectiveCleanUpDelayMs, 0.1f);
            grid.State = GridState.Active;
        }

        return grid;
    }

    private void TrackPlacement(WorldObject obj)
    {
        if (float.IsFinite(obj.X) && float.IsFinite(obj.Y))
        {
            _unplaced.Remove(obj);
        }
        else
        {
            _unplaced.Add(obj);
        }
    }

    /// <summary>
    /// Raise <see cref="MaxBoundingRadius"/> to cover <paramref name="obj"/>. A non-finite
    /// radius makes it infinite, which turns every query into a full walk (still exact).
    /// </summary>
    internal void TrackRadius(WorldObject obj)
    {
        float radius = obj.BoundingRadius;
        if (!float.IsFinite(radius))
        {
            MaxBoundingRadius = float.PositiveInfinity;
        }
        else if (radius > MaxBoundingRadius)
        {
            MaxBoundingRadius = radius;
        }
    }
}

/// <summary>
/// Cumulative grid lifecycle work of a <see cref="GridContainer"/> (diagnostics): grids created (terrain and collision tiles
/// loaded by the handlers), grids whose objects were loaded (spawns), grids unloaded (handlers and evictions), the time those
/// handlers took, and the activity checks (<see cref="GridContainer.ActiveObjectsNearGrid"/>) with the active objects they visited.
/// </summary>
internal struct GridLifecycleCounters
{
    public long Created;
    public long Loaded;
    public long Unloaded;
    public long CreateMicros;
    public long LoadMicros;
    public long UnloadMicros;
    public long NearChecks;
    public long NearCheckObjects;

    public static GridLifecycleCounters operator -(GridLifecycleCounters a, GridLifecycleCounters b) => new()
    {
        Created = a.Created - b.Created, Loaded = a.Loaded - b.Loaded, Unloaded = a.Unloaded - b.Unloaded,
        CreateMicros = a.CreateMicros - b.CreateMicros, LoadMicros = a.LoadMicros - b.LoadMicros, UnloadMicros = a.UnloadMicros - b.UnloadMicros,
        NearChecks = a.NearChecks - b.NearChecks, NearCheckObjects = a.NearCheckObjects - b.NearCheckObjects,
    };

    internal static long MicrosSince(long start)
        => (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
}
