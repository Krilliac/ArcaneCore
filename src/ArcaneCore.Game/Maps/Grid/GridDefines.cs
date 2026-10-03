namespace ArcaneCore.Game.Maps.Grid;

/// <summary>
/// The map partition every MaNGOS-family core uses: a map is 64 × 64 grids of 533⅓ yards,
/// each split into cells. Values verified against vmangos and cmangos-classic
/// <c>GridDefines.h</c>, which agree on every constant used here.
/// <para>
/// Reference discrepancy: vmangos and cmangos-classic use <b>16 × 16</b> cells per grid
/// (<c>MAX_NUMBER_OF_CELLS 16</c>, 33⅓-yard cells); TrinityCore and older MaNGOS use 8 × 8.
/// The two 1.12 servers win. The cell size only changes how finely space is indexed, never
/// which objects a query returns.
/// </para>
/// <para>
/// Reference discrepancy: vmangos' <c>MaNGOS::Compute</c> returns <c>(y_val, x_val)</c>,
/// cmangos-classic returns <c>(x_val, y_val)</c>. This is an internal naming convention of
/// each core; ArcaneCore follows cmangos-classic (<see cref="CellCoord.X"/> comes from world X).
/// </para>
/// </summary>
public static class GridDefines
{
    /// <summary><c>MAX_NUMBER_OF_GRIDS</c>.</summary>
    public const int MaxNumberOfGrids = 64;

    /// <summary><c>SIZE_OF_GRIDS</c> (yards).</summary>
    public const float SizeOfGrids = 533.33333f;

    /// <summary><c>CENTER_GRID_ID</c>.</summary>
    public const int CenterGridId = MaxNumberOfGrids / 2;

    /// <summary><c>CENTER_GRID_OFFSET</c>.</summary>
    public const float CenterGridOffset = SizeOfGrids / 2;

    /// <summary><c>MAX_NUMBER_OF_CELLS</c> (per grid side) — 16 in vmangos and cmangos-classic.</summary>
    public const int MaxNumberOfCells = 16;

    /// <summary><c>SIZE_OF_GRID_CELL</c> (yards).</summary>
    public const float SizeOfGridCell = SizeOfGrids / MaxNumberOfCells;

    /// <summary><c>CENTER_GRID_CELL_ID</c>.</summary>
    public const int CenterGridCellId = MaxNumberOfCells * MaxNumberOfGrids / 2;

    /// <summary><c>CENTER_GRID_CELL_OFFSET</c>.</summary>
    public const float CenterGridCellOffset = SizeOfGridCell / 2;

    /// <summary><c>TOTAL_NUMBER_OF_CELLS_PER_MAP</c> (per map side).</summary>
    public const int TotalNumberOfCellsPerMap = MaxNumberOfGrids * MaxNumberOfCells;

    /// <summary>The highest z a height search starts from (vmangos/cmangos <c>MAX_HEIGHT</c>, GridMap.h).</summary>
    public const float MaxHeight = 100000.0f;

    /// <summary><c>MAP_RESOLUTION</c>: height samples per terrain tile side.</summary>
    public const int MapResolution = 128;

    /// <summary><c>MAP_SIZE</c> (yards).</summary>
    public const float MapSize = SizeOfGrids * MaxNumberOfGrids;

    /// <summary><c>MAP_HALFSIZE</c> (yards).</summary>
    public const float MapHalfSize = MapSize / 2;

    /// <summary>
    /// Largest search radius a cell visit accepts (vmangos <c>MAX_VISIBILITY_DISTANCE</c> =
    /// <c>SIZE_OF_GRIDS</c>, ObjectDefines.h; <c>Cell::Visit</c> clamps to it).
    /// </summary>
    public const float MaxVisibilityDistance = SizeOfGrids;

    /// <summary>
    /// The grid holding a world position (<c>MaNGOS::ComputeGridPair</c>): computed in
    /// double precision "for having same result as same mySQL calculations", then clamped
    /// into range like vmangos' <c>CoordPair</c> constructor.
    /// </summary>
    public static GridCoord ComputeGridCoord(float x, float y)
    {
        (int gx, int gy) = Compute(x, y, CenterGridOffset, SizeOfGrids, CenterGridId);
        return new GridCoord(Clamp(gx, MaxNumberOfGrids), Clamp(gy, MaxNumberOfGrids));
    }

    /// <summary>The cell (map-wide index) holding a world position (<c>MaNGOS::ComputeCellPair</c>).</summary>
    public static CellCoord ComputeCellCoord(float x, float y)
    {
        (int cx, int cy) = Compute(x, y, CenterGridCellOffset, SizeOfGridCell, CenterGridCellId);
        return new CellCoord(Clamp(cx, TotalNumberOfCellsPerMap), Clamp(cy, TotalNumberOfCellsPerMap));
    }

    /// <summary>
    /// The cells a circle touches (<c>Cell::CalculateCellArea</c>): the cells of the two
    /// corners of its bounding square, each normalized into the map. A radius of 0 or less
    /// gives just the centre cell.
    /// </summary>
    public static CellArea CalculateCellArea(float x, float y, float radius)
    {
        if (radius <= 0.0f)
        {
            CellCoord centre = ComputeCellCoord(x, y);
            return new CellArea(centre, centre);
        }

        return new CellArea(ComputeCellCoord(x - radius, y - radius), ComputeCellCoord(x + radius, y + radius));
    }

    /// <summary>
    /// A finite coordinate inside the map (<c>MaNGOS::IsValidMapCoord</c>):
    /// |c| ≤ <c>MAP_HALFSIZE</c> − 0.5.
    /// </summary>
    public static bool IsValidMapCoord(float c) => float.IsFinite(c) && MathF.Abs(c) <= MapHalfSize - 0.5f;

    /// <summary>A valid height (<c>MaNGOS::IsValidZCoord</c>: |z| ≤ 400000).</summary>
    public static bool IsValidZCoord(float z) => float.IsFinite(z) && MathF.Abs(z) <= 400000f;

    /// <summary>
    /// A valid position (<c>MaNGOS::IsValidMapCoord(x, y, z, o)</c>): x, y inside the map, a
    /// valid z, and a finite orientation with |o| ≤ 4π.
    /// </summary>
    public static bool IsValidMapCoord(float x, float y, float z, float orientation)
        => IsValidMapCoord(x) && IsValidMapCoord(y) && IsValidZCoord(z)
           && float.IsFinite(orientation) && MathF.Abs(orientation) <= 4 * MathF.PI;

    private static (int X, int Y) Compute(float x, float y, float centerOffset, float size, int centerValue)
    {
        double xOffset = ((double)x - centerOffset) / size;
        double yOffset = ((double)y - centerOffset) / size;

        // C++ int(...) truncates toward zero; so does a C# (int) cast.
        int xVal = (int)(xOffset + centerValue + 0.5);
        int yVal = (int)(yOffset + centerValue + 0.5);
        return (xVal, yVal);
    }

    private static int Clamp(int value, int limit) => value < 0 ? 0 : value >= limit ? limit - 1 : value;
}

/// <summary>A grid index pair (vmangos <c>GridPair</c>), each 0..63.</summary>
public readonly record struct GridCoord(int X, int Y)
{
    /// <summary>A unique id within a map (vmangos <c>NGridType</c> id = x * 64 + y).</summary>
    public int Id => (X * GridDefines.MaxNumberOfGrids) + Y;

    public override string ToString() => $"[{X},{Y}]";
}

/// <summary>A map-wide cell index pair (vmangos <c>CellPair</c>), each 0..1023.</summary>
public readonly record struct CellCoord(int X, int Y)
{
    /// <summary>The grid this cell belongs to.</summary>
    public GridCoord Grid => new(X / GridDefines.MaxNumberOfCells, Y / GridDefines.MaxNumberOfCells);

    /// <summary>Cell position inside its grid (0..15).</summary>
    public int LocalX => X % GridDefines.MaxNumberOfCells;

    /// <summary>Cell position inside its grid (0..15).</summary>
    public int LocalY => Y % GridDefines.MaxNumberOfCells;

    public override string ToString() => $"[{X},{Y}]";
}

/// <summary>An inclusive rectangle of cells (vmangos <c>CellArea</c>).</summary>
public readonly record struct CellArea(CellCoord Low, CellCoord High)
{
    /// <summary>Whether the area is a single cell (vmangos <c>CellArea::operator!</c>).</summary>
    public bool IsSingleCell => Low == High;

    /// <summary>Whether a cell lies inside the area.</summary>
    public bool Contains(CellCoord cell) => cell.X >= Low.X && cell.X <= High.X && cell.Y >= Low.Y && cell.Y <= High.Y;
}
