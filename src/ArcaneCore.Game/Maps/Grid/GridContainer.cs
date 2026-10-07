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
public sealed class GridContainer
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

    public Grid? GetGrid(GridCoord coord) => _grids[coord.Id];

    public bool IsGridLoaded(GridCoord coord) => _grids[coord.Id] is { ObjectDataLoaded: true };

    public bool Contains(WorldObject obj) => _cells.ContainsKey(obj);

    public bool IsActive(WorldObject obj) => _active.Contains(obj);

    /// <summary>The cell an object is indexed in, or null when it is not in this container.</summary>
    public CellCoord? CellOf(WorldObject obj) => _cells.TryGetValue(obj, out CellCoord cell) ? cell : null;

    /// <summary>
    /// Index an object at its position. Active objects (players) load and activate their grid
    /// and every grid within the activation distance (vmangos <c>Map::Add(Player*)</c> →
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
        TrackRadius(obj);
        TrackPlacement(obj);

        if (active)
        {
            _active.Add(obj);
            grid.ActiveObjectCount++;
            LoadGridsAround(obj.X, obj.Y, Options.GridActivationDistance);
        }
    }

    /// <summary>Remove an object from the index (vmangos <c>RemoveFromGrid</c>).</summary>
    public bool Remove(WorldObject obj)
    {
        if (!_cells.Remove(obj, out CellCoord cell))
        {
            return false;
        }

        _unplaced.Remove(obj);
        Grid? grid = _grids[cell.Grid.Id];
        grid?.Remove(cell, obj);
        if (_active.Remove(obj) && grid is not null)
        {
            grid.ActiveObjectCount--;
        }

        return true;
    }

    /// <summary>
    /// Re-index an object after it moved (vmangos <c>Map::PlayerRelocation</c> /
    /// <c>CreatureCellRelocation</c>). Moving into another grid loads it for active objects; an
    /// active object entering a different cell re-activates its grid (expiry × 0.1) and loads
    /// the grids within the activation distance. Returns true when the cell changed.
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

        if (active)
        {
            newGrid.ActiveObjectCount++;
            if (newGrid.State != GridState.Active)
            {
                newGrid.ResetTimer(Options.EffectiveCleanUpDelayMs, 0.1f);
                newGrid.State = GridState.Active;
            }

            LoadGridsAround(obj.X, obj.Y, Options.GridActivationDistance);
        }

        return true;
    }

    /// <summary>
    /// Mark a non-player object active or not (vmangos <c>Map::AddToActive</c> /
    /// <c>RemoveFromActive</c>): active objects keep the grids around them loaded, like players.
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
            grid.ActiveObjectCount++;
            if (!grid.ObjectDataLoaded || grid.State != GridState.Active)
            {
                EnsureGridLoadedAtEnter(cell.Grid);
                grid.ResetTimer(Options.EffectiveCleanUpDelayMs, 0.1f);
                grid.State = GridState.Active;
            }

            LoadGridsAround(obj.X, obj.Y, Options.GridActivationDistance);
        }
        else if (!active && _active.Remove(obj))
        {
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

        foreach (WorldObject obj in _active)
        {
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
            grid = new Grid(coord, Options.EffectiveCleanUpDelayMs, Options.GridUnload) { State = GridState.Idle };
            _grids[coord.Id] = grid;
            _loaded.Add(grid);
            GridCreated?.Invoke(grid);
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
            GridLoaded?.Invoke(grid);
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
