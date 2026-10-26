using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Client.Terrain;

/// <summary>
/// What <see cref="AdtGeometry.SampleAt"/> found: the client terrain at one world position, interpolated the way the
/// server interpolates its own height grid.
/// </summary>
/// <param name="IsHole">True when the square is cut out by the chunk's hole bitmap (then <paramref name="Z"/> is NaN).</param>
/// <param name="Z">Interpolated ground height, or NaN for a hole.</param>
/// <param name="ChunkRow">MCNK row 0..15.</param>
/// <param name="ChunkColumn">MCNK column 0..15.</param>
/// <param name="SquareRow">Square row inside the tile, 0..127 (advances toward -X).</param>
/// <param name="SquareColumn">Square column inside the tile, 0..127 (advances toward -Y).</param>
/// <param name="Triangle">The fan triangle 1..4 that holds the point (the server's numbering), or 0 for a hole.</param>
/// <param name="FractionRow">Position inside the square along the row axis, 0..1.</param>
/// <param name="FractionColumn">Position inside the square along the column axis, 0..1.</param>
public readonly record struct TerrainSample(
    bool IsHole,
    double Z,
    int ChunkRow,
    int ChunkColumn,
    int SquareRow,
    int SquareColumn,
    int Triangle,
    double FractionRow,
    double FractionColumn);

/// <summary>One visible liquid layer at one position, as <see cref="AdtGeometry.LiquidAt"/> found it.</summary>
/// <param name="Kind">The liquid.</param>
/// <param name="SquareFlags">The layer's raw flag byte for the square.</param>
/// <param name="IsDarkWater">True when the flag byte carries the dark-water bit.</param>
/// <param name="Level">Surface height at the position, over the triangles <c>ref.liquid</c> draws.</param>
/// <param name="HeightRowColumn">Stored height at the square's first-row, first-column corner.</param>
/// <param name="HeightNextRow">Stored height at the corner one row further (toward -X).</param>
/// <param name="HeightNextColumn">Stored height at the corner one column further (toward -Y).</param>
/// <param name="HeightNextBoth">Stored height at the far corner.</param>
public readonly record struct LiquidSample(
    AdtLiquidKind Kind,
    byte SquareFlags,
    bool IsDarkWater,
    double Level,
    float HeightRowColumn,
    float HeightNextRow,
    float HeightNextColumn,
    float HeightNextBoth);

/// <summary>
/// Where the samples of an <see cref="AdtFile"/> sit in world coordinates, and the height interpolation over them.
/// </summary>
/// <remarks>
/// A tile's corner with the largest X and Y is <see cref="WorldGrid.Origin"/>. Sample (y, x) of the chunk at (row,
/// column) lies at X = originX - (row * 8 + y) * squareSize and Y = originY - (column * 8 + x) * squareSize; the
/// square-centre samples sit half a square further on both axes. Heights are world Z. The square size is the tile size
/// over 128 (4.1667 yd).
/// </remarks>
public static class AdtGeometry
{
    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:411-452 (chunk i, j and sample y, x fill V9[i*8+y][j*8+x] and V8 the same way)
    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:24-26 (TILESIZE 533.33333, CHUNKSIZE = TILESIZE / 16, UNITSIZE = CHUNKSIZE / 8)
    // ref: WoW-RE-Research formats/terrain-adt.md:372-386 (samples walk south = decreasing X and east = decreasing Y by UNIT_SIZE)
    // ref: Worldforge src/coords.hpp:60-68 (chunk corner = (32 - block) * TILE - index * CHUNK per axis)
    // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:399-407 (server row = (int)(128 * (32 - x / G)), column the same from y)
    private const int Resolution = WorldGrid.MapResolution;

    /// <summary>World position and height of corner sample (<paramref name="y"/>, <paramref name="x"/>) of <paramref name="chunk"/>.</summary>
    public static WorldPoint OuterVertex(TileKey tile, AdtChunk chunk, int y, int x)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        WorldPoint origin = WorldGrid.Origin(tile);
        return new WorldPoint(
            origin.X - (((chunk.Row * 8) + y) * WorldGrid.SquareSize),
            origin.Y - (((chunk.Column * 8) + x) * WorldGrid.SquareSize),
            chunk.OuterHeight(y, x));
    }

    /// <summary>World position and height of square-centre sample (<paramref name="y"/>, <paramref name="x"/>) of <paramref name="chunk"/>.</summary>
    public static WorldPoint InnerVertex(TileKey tile, AdtChunk chunk, int y, int x)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        WorldPoint origin = WorldGrid.Origin(tile);
        return new WorldPoint(
            origin.X - ((((chunk.Row * 8) + y) + 0.5) * WorldGrid.SquareSize),
            origin.Y - ((((chunk.Column * 8) + x) + 0.5) * WorldGrid.SquareSize),
            chunk.InnerHeight(y, x));
    }

    /// <summary>
    /// How many chunks have a header position that differs from the position the tile naming predicts by more than
    /// <paramref name="tolerance"/> yards on either axis (0 for a correctly named file).
    /// </summary>
    public static int CountPositionMismatches(AdtFile adt, TileKey tile, double tolerance = 0.01)
    {
        ArgumentNullException.ThrowIfNull(adt);
        WorldPoint origin = WorldGrid.Origin(tile);
        double chunkSize = WorldGrid.TileSize / AdtFile.ChunksPerSide;
        int mismatches = 0;
        foreach (AdtChunk chunk in adt.Chunks)
        {
            double expectedX = origin.X - (chunk.Row * chunkSize);
            double expectedY = origin.Y - (chunk.Column * chunkSize);
            if (Math.Abs(chunk.BaseX - expectedX) > tolerance || Math.Abs(chunk.BaseY - expectedY) > tolerance)
            {
                mismatches++;
            }
        }

        return mismatches;
    }

    /// <summary>
    /// The client terrain at world (<paramref name="x"/>, <paramref name="y"/>) inside <paramref name="tile"/>, or null
    /// when the position is not inside that tile. The square holding the point is a fan of four triangles around its
    /// centre sample; the triangle and plane are chosen exactly as the server's <c>GetHeight</c> does. Only the chunk
    /// that owns the square is used, so a vertex shared by two chunks keeps that chunk's own value.
    /// </summary>
    public static TerrainSample? SampleAt(AdtFile adt, TileKey tile, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(adt);
        if (!TryLocate(tile, x, y, out int row, out int column, out double fx, out double fy))
        {
            return null;
        }

        int chunkRow = row / 8;
        int chunkColumn = column / 8;
        int localRow = row % 8;
        int localColumn = column % 8;
        AdtChunk chunk = adt.Chunk(chunkRow, chunkColumn);
        if (chunk.IsHoleSquare(localRow, localColumn))
        {
            return new TerrainSample(true, double.NaN, chunkRow, chunkColumn, row, column, 0, fx, fy);
        }

        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:414-456 (h5 = 2 * centre; four planes a * x + b * y + c)
        // ref: vmangos@0e3ff01 src/game/Maps/GridMap.cpp:286-437 (GridMap::getHeightFromFloat, the original; isHole at :274-284)
        double h5 = 2.0 * chunk.InnerHeight(localRow, localColumn);
        double a;
        double b;
        double c;
        int triangle;
        if (fx + fy < 1)
        {
            double h1 = chunk.OuterHeight(localRow, localColumn);
            if (fx > fy)
            {
                double h2 = chunk.OuterHeight(localRow + 1, localColumn);
                a = h2 - h1;
                b = h5 - h1 - h2;
                c = h1;
                triangle = 1;
            }
            else
            {
                double h3 = chunk.OuterHeight(localRow, localColumn + 1);
                a = h5 - h1 - h3;
                b = h3 - h1;
                c = h1;
                triangle = 2;
            }
        }
        else if (fx > fy)
        {
            double h2 = chunk.OuterHeight(localRow + 1, localColumn);
            double h4 = chunk.OuterHeight(localRow + 1, localColumn + 1);
            a = h2 + h4 - h5;
            b = h4 - h2;
            c = h5 - h4;
            triangle = 3;
        }
        else
        {
            double h3 = chunk.OuterHeight(localRow, localColumn + 1);
            double h4 = chunk.OuterHeight(localRow + 1, localColumn + 1);
            a = h4 - h3;
            b = h3 + h4 - h5;
            c = h5 - h4;
            triangle = 4;
        }

        return new TerrainSample(false, (a * fx) + (b * fy) + c, chunkRow, chunkColumn, row, column, triangle, fx, fy);
    }

    /// <summary>
    /// Every visible liquid layer of the square holding world (<paramref name="x"/>, <paramref name="y"/>) inside
    /// <paramref name="tile"/>, in storage order (river, ocean, magma, slime); empty when no layer shows that square.
    /// Null when the position is not inside the tile. The level follows the two triangles the <c>ref.liquid</c> layer
    /// draws for the square (the diagonal runs from the next-row corner to the next-column corner).
    /// </summary>
    public static IReadOnlyList<LiquidSample>? LiquidAt(AdtFile adt, TileKey tile, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(adt);
        if (!TryLocate(tile, x, y, out int row, out int column, out double fx, out double fy))
        {
            return null;
        }

        AdtChunk chunk = adt.Chunk(row / 8, column / 8);
        int localRow = row % 8;
        int localColumn = column % 8;
        var samples = new List<LiquidSample>();
        foreach (AdtLiquidLayer layer in chunk.LiquidLayers)
        {
            if (!layer.IsVisible(localRow, localColumn))
            {
                continue;
            }

            float h00 = layer.Height(localRow, localColumn);
            float h10 = layer.Height(localRow + 1, localColumn);
            float h01 = layer.Height(localRow, localColumn + 1);
            float h11 = layer.Height(localRow + 1, localColumn + 1);
            double level = fx + fy <= 1
                ? h00 + (fx * (h10 - (double)h00)) + (fy * (h01 - (double)h00))
                : h11 + ((1 - fy) * (h10 - (double)h11)) + ((1 - fx) * (h01 - (double)h11));
            samples.Add(new LiquidSample(layer.Kind, layer.SquareFlags(localRow, localColumn), layer.IsDarkWater(localRow, localColumn), level, h00, h10, h01, h11));
        }

        return samples;
    }

    private static bool TryLocate(TileKey tile, double x, double y, out int row, out int column, out double fx, out double fy)
    {
        row = 0;
        column = 0;
        fx = 0;
        fy = 0;
        if (!tile.IsInGrid || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return false;
        }

        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:399-407 (row from X, column from Y, 128 squares per tile)
        double rowCoordinate = (Resolution * (32 - (x / WorldGrid.TileSize))) - (tile.X * Resolution);
        double columnCoordinate = (Resolution * (32 - (y / WorldGrid.TileSize))) - (tile.Y * Resolution);
        const double slack = 1e-6;
        if (rowCoordinate < -slack || rowCoordinate > Resolution + slack || columnCoordinate < -slack || columnCoordinate > Resolution + slack)
        {
            return false;
        }

        row = Math.Clamp((int)Math.Floor(rowCoordinate), 0, Resolution - 1);
        column = Math.Clamp((int)Math.Floor(columnCoordinate), 0, Resolution - 1);
        fx = Math.Clamp(rowCoordinate - row, 0, 1);
        fy = Math.Clamp(columnCoordinate - column, 0, 1);
        return true;
    }
}
