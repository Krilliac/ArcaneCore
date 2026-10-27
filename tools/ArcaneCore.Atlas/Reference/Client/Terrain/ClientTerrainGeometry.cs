using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Client.Terrain;

/// <summary>
/// Turns a parsed <see cref="AdtFile"/> into the renderable geometry of the <c>ref.terrain</c> and <c>ref.liquid</c>
/// layers. Stateless and thread-safe; every call builds its own <see cref="GeometryBuilder"/>.
/// </summary>
/// <remarks>
/// Both layers use the tile's north-east corner (<see cref="WorldGrid.Origin"/>, Z 0) as the chunk origin, so a vertex
/// is at most 534 yd from it and the 32-bit offsets are exact to a few micro-yards; Z is the stored height unchanged.
/// A tile is far below <see cref="GeometryWire.MaxChunkBytes"/> (about 3 MB for a full terrain tile), so each layer is a
/// single part. Triangles are wound counter-clockwise seen from above (X north, Y west, Z up).
/// </remarks>
internal static class ClientTerrainGeometry
{
    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:39-40 (16 chunks per tile edge, 8 squares per chunk edge)
    private const int SquaresPerChunk = 8;

    /// <summary>Slope threshold in degrees at or above which a terrain triangle is considered steep/unwalkable.</summary>
    public const double SteepSlopeDegrees = 60.0;

    private const int TotalSquaresPerTile = WorldGrid.MapResolution * WorldGrid.MapResolution;
    private const int MaxHighlightedSteepChunks = 8;

    /// <summary>The attribute id of the terrain triangles of the chunk at <paramref name="chunkIndex"/> (0..255).</summary>
    public static uint TerrainAttribute(int chunkIndex) => (uint)chunkIndex;

    /// <summary>
    /// The attribute id of the liquid triangles of chunk <paramref name="chunkIndex"/>, liquid layer
    /// <paramref name="kind"/>, dark water or not.
    /// </summary>
    public static uint LiquidAttribute(int chunkIndex, AdtLiquidKind kind, bool darkWater)
        => (uint)((((chunkIndex * 4) + (int)kind) * 2) + (darkWater ? 1 : 0));

    /// <summary>The lower-case name of a liquid kind as it appears in attribute tables and command results.</summary>
    public static string KindName(AdtLiquidKind kind) => kind switch
    {
        AdtLiquidKind.River => "river",
        AdtLiquidKind.Ocean => "ocean",
        AdtLiquidKind.Magma => "magma",
        _ => "slime",
    };

    /// <summary>The colour key of a liquid square: the dark-water bit selects the shared deep-water colour.</summary>
    public static string LiquidColorKey(AdtLiquidKind kind, bool darkWater)
        => darkWater ? ColorKeys.DeepWater : AdtLiquidLayer.ColorKeyOf(kind);

    /// <summary>
    /// Computes the terrain surface sample (height, unit normal, slope in degrees, steepness, and clamped liquid depth)
    /// at world (<paramref name="x"/>, <paramref name="y"/>) inside <paramref name="tile"/>.
    /// </summary>
    public static TerrainSurfaceSample? SampleSurfaceAt(AdtFile adt, TileKey tile, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(adt);
        if (AdtGeometry.SampleAt(adt, tile, x, y) is not { } sample)
        {
            return null;
        }

        IReadOnlyList<LiquidSample>? liquids = AdtGeometry.LiquidAt(adt, tile, x, y);
        double? liquidZ = null;
        if (liquids is { Count: > 0 })
        {
            double maxLevel = double.NegativeInfinity;
            for (int i = 0; i < liquids.Count; i++)
            {
                if (liquids[i].Level > maxLevel)
                {
                    maxLevel = liquids[i].Level;
                }
            }

            liquidZ = maxLevel;
        }

        if (sample.IsHole)
        {
            return new TerrainSurfaceSample(
                tile,
                sample,
                new WorldPoint(0, 0, 0),
                double.NaN,
                IsSteep: false,
                WalkableSlope: false,
                liquidZ,
                LiquidDepth: 0.0);
        }

        AdtChunk chunk = adt.Chunk(sample.ChunkRow, sample.ChunkColumn);
        int localRow = sample.SquareRow % SquaresPerChunk;
        int localColumn = sample.SquareColumn % SquaresPerChunk;
        (WorldPoint normal, double slopeDegrees) = TriangleNormalAndSlope(chunk, localRow, localColumn, sample.Triangle);
        bool isSteep = slopeDegrees >= SteepSlopeDegrees;
        double liquidDepth = liquidZ is { } lz ? Math.Max(0.0, lz - sample.Z) : 0.0;

        return new TerrainSurfaceSample(
            tile,
            sample,
            normal,
            slopeDegrees,
            isSteep,
            WalkableSlope: !isSteep,
            liquidZ,
            liquidDepth);
    }

    /// <summary>
    /// Computes the unit upward surface normal and slope angle in degrees for one of the four fan triangles (1..4) of
    /// square (<paramref name="localRow"/>, <paramref name="localColumn"/>) in <paramref name="chunk"/>.
    /// </summary>
    public static (WorldPoint Normal, double SlopeDegrees) TriangleNormalAndSlope(AdtChunk chunk, int localRow, int localColumn, int triangle)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        double h1 = chunk.OuterHeight(localRow, localColumn);
        double h2 = chunk.OuterHeight(localRow + 1, localColumn);
        double h3 = chunk.OuterHeight(localRow, localColumn + 1);
        double h4 = chunk.OuterHeight(localRow + 1, localColumn + 1);
        double h5 = 2.0 * chunk.InnerHeight(localRow, localColumn);

        double a;
        double b;
        switch (triangle)
        {
            case 1:
                a = h2 - h1;
                b = h5 - h1 - h2;
                break;
            case 2:
                a = h5 - h1 - h3;
                b = h3 - h1;
                break;
            case 3:
                a = h2 + h4 - h5;
                b = h4 - h2;
                break;
            case 4:
                a = h4 - h3;
                b = h3 + h4 - h5;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(triangle), triangle, "Triangle index must be 1..4.");
        }

        // In square coordinates, fx advances along -X by SquareSize and fy advances along -Y by SquareSize:
        // dZ/dX = -a / SquareSize, dZ/dY = -b / SquareSize, so the upward normal (-dZ/dX, -dZ/dY, 1) is (a / s, b / s, 1).
        double nxRaw = a / WorldGrid.SquareSize;
        double nyRaw = b / WorldGrid.SquareSize;
        double len = Math.Sqrt((nxRaw * nxRaw) + (nyRaw * nyRaw) + 1.0);
        double nx = nxRaw / len;
        double ny = nyRaw / len;
        double nz = 1.0 / len;
        double slopeDegrees = Math.Acos(Math.Clamp(nz, -1.0, 1.0)) * (180.0 / Math.PI);
        return (new WorldPoint(nx, ny, nz), slopeDegrees);
    }

    /// <summary>
    /// Summarizes elevation min/max/mean, steep-triangle counts, hole square counts, visible liquid square coverage,
    /// distinct <c>AreaId</c> values, and the steepest chunks across <paramref name="adt"/>.
    /// </summary>
    public static TerrainTileAnalysis AnalyzeTile(TileKey tile, AdtFile adt)
    {
        ArgumentNullException.ThrowIfNull(adt);
        double minElevation = double.PositiveInfinity;
        double maxElevation = double.NegativeInfinity;
        double sumTriangleElevation = 0.0;
        int totalTriangles = 0;
        int steepTriangles = 0;
        int holeSquares = 0;
        int liquidSquares = 0;
        var areaIds = new SortedSet<uint>();
        var chunkSteepness = new List<TerrainChunkSteepness>(adt.Chunks.Count);

        foreach (AdtChunk chunk in adt.Chunks)
        {
            areaIds.Add(chunk.AreaId);
            int chunkSteep = 0;
            int chunkTriangles = 0;
            double chunkMaxSlope = 0.0;
            double chunkMinZ = double.PositiveInfinity;
            double chunkMaxZ = double.NegativeInfinity;

            for (int y = 0; y < SquaresPerChunk; y++)
            {
                for (int x = 0; x < SquaresPerChunk; x++)
                {
                    bool hasVisibleLiquid = false;
                    for (int l = 0; l < chunk.LiquidLayers.Count; l++)
                    {
                        if (chunk.LiquidLayers[l].IsVisible(y, x))
                        {
                            hasVisibleLiquid = true;
                            break;
                        }
                    }

                    if (hasVisibleLiquid)
                    {
                        liquidSquares++;
                    }

                    if (chunk.IsHoleSquare(y, x))
                    {
                        holeSquares++;
                        continue;
                    }

                    double h1 = chunk.OuterHeight(y, x);
                    double h2 = chunk.OuterHeight(y + 1, x);
                    double h3 = chunk.OuterHeight(y, x + 1);
                    double h4 = chunk.OuterHeight(y + 1, x + 1);
                    double hc = chunk.InnerHeight(y, x);

                    double sqMin = Math.Min(hc, Math.Min(Math.Min(h1, h2), Math.Min(h3, h4)));
                    double sqMax = Math.Max(hc, Math.Max(Math.Max(h1, h2), Math.Max(h3, h4)));
                    minElevation = Math.Min(minElevation, sqMin);
                    maxElevation = Math.Max(maxElevation, sqMax);
                    chunkMinZ = Math.Min(chunkMinZ, sqMin);
                    chunkMaxZ = Math.Max(chunkMaxZ, sqMax);

                    sumTriangleElevation += ((h1 + h2 + hc) / 3.0)
                        + ((h1 + h3 + hc) / 3.0)
                        + ((h2 + h4 + hc) / 3.0)
                        + ((h3 + h4 + hc) / 3.0);

                    for (int tri = 1; tri <= 4; tri++)
                    {
                        (_, double slope) = TriangleNormalAndSlope(chunk, y, x, tri);
                        totalTriangles++;
                        chunkTriangles++;
                        if (slope > chunkMaxSlope)
                        {
                            chunkMaxSlope = slope;
                        }

                        if (slope >= SteepSlopeDegrees)
                        {
                            steepTriangles++;
                            chunkSteep++;
                        }
                    }
                }
            }

            if (chunkSteep > 0)
            {
                chunkSteepness.Add(new TerrainChunkSteepness(
                    chunk.Index,
                    chunk.Row,
                    chunk.Column,
                    chunkSteep,
                    chunkTriangles,
                    chunkMaxSlope,
                    chunkMinZ,
                    chunkMaxZ));
            }
        }

        chunkSteepness.Sort(static (a, b) =>
        {
            int cmp = b.SteepTriangles.CompareTo(a.SteepTriangles);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = b.MaxSlopeDegrees.CompareTo(a.MaxSlopeDegrees);
            return cmp != 0 ? cmp : a.ChunkIndex.CompareTo(b.ChunkIndex);
        });

        if (chunkSteepness.Count > MaxHighlightedSteepChunks)
        {
            chunkSteepness.RemoveRange(MaxHighlightedSteepChunks, chunkSteepness.Count - MaxHighlightedSteepChunks);
        }

        double finalMin = totalTriangles > 0 ? minElevation : double.NaN;
        double finalMax = totalTriangles > 0 ? maxElevation : double.NaN;
        double finalMean = totalTriangles > 0 ? sumTriangleElevation / totalTriangles : double.NaN;
        double steepRatio = totalTriangles > 0 ? (double)steepTriangles / totalTriangles : 0.0;
        double liquidRatio = (double)liquidSquares / TotalSquaresPerTile;

        return new TerrainTileAnalysis(
            tile,
            finalMin,
            finalMax,
            finalMean,
            totalTriangles,
            steepTriangles,
            steepRatio,
            holeSquares,
            liquidSquares,
            liquidRatio,
            [.. areaIds],
            chunkSteepness);
    }

    /// <summary>
    /// The terrain of <paramref name="adt"/> as the server would interpolate it: for every square the hole bitmap does
    /// not cut out, four triangles around the centre sample. Throws <see cref="InvalidDataException"/> when a stored
    /// height sums to a value that is not finite. Honours <paramref name="cancellationToken"/> once per chunk.
    /// </summary>
    /// <param name="tile">The server tile the ADT belongs to.</param>
    /// <param name="adt">The parsed file.</param>
    /// <param name="areaName">AreaTable.dbc name lookup (may return null).</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    public static GeometryChunk BuildTerrain(TileKey tile, AdtFile adt, Func<uint, string?> areaName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adt);
        ArgumentNullException.ThrowIfNull(areaName);
        var builder = new GeometryBuilder(LayerIds.RefTerrain, tile, WorldGrid.Origin(tile));

        // Corner (row, column) of the tile's 129x129 grid; chunks share an edge corner only when both store the same height.
        var corners = new Dictionary<(int Row, int Column, int HeightBits), uint>();
        int drawnSquares = 0;
        int holeSquares = 0;
        int flatChunks = 0;
        float minZ = float.PositiveInfinity;
        float maxZ = float.NegativeInfinity;

        foreach (AdtChunk chunk in adt.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!chunk.HasHeights)
            {
                flatChunks++;
            }

            uint attribute = TerrainAttribute(chunk.Index);
            for (int y = 0; y < SquaresPerChunk; y++)
            {
                for (int x = 0; x < SquaresPerChunk; x++)
                {
                    if (chunk.IsHoleSquare(y, x))
                    {
                        holeSquares++;
                        continue;
                    }

                    if (!builder.HasAttribute(attribute))
                    {
                        builder.DescribeAttribute(attribute, () => DescribeChunk(chunk, areaName));
                    }

                    uint p1 = Corner(builder, corners, tile, chunk, y, x);
                    uint p2 = Corner(builder, corners, tile, chunk, y + 1, x);
                    uint p3 = Corner(builder, corners, tile, chunk, y, x + 1);
                    uint p4 = Corner(builder, corners, tile, chunk, y + 1, x + 1);
                    float centreHeight = chunk.InnerHeight(y, x);
                    CheckFinite(centreHeight, chunk, "centre", y, x);
                    uint centre = builder.AddVertex(AdtGeometry.InnerVertex(tile, chunk, y, x));

                    // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:414-456 (fan around the centre: edges 1-2, 1-3, 2-4, 3-4)
                    builder.AddTriangle(p1, p2, centre, attribute);
                    builder.AddTriangle(p1, centre, p3, attribute);
                    builder.AddTriangle(p2, p4, centre, attribute);
                    builder.AddTriangle(p3, centre, p4, attribute);
                    drawnSquares++;
                    minZ = Math.Min(minZ, centreHeight);
                    maxZ = Math.Max(maxZ, centreHeight);
                }
            }
        }

        JsonObject stats = builder.Stats;
        stats["source"] = "adt";
        stats["chunks"] = adt.Chunks.Count;
        stats["squares"] = drawnSquares;
        stats["holeSquares"] = holeSquares;
        stats["chunksWithoutHeights"] = flatChunks;
        stats["positionMismatches"] = AdtGeometry.CountPositionMismatches(adt, tile);
        stats["doodads"] = adt.Doodads.Count;
        stats["wmos"] = adt.WmoObjects.Count;
        if (drawnSquares > 0)
        {
            stats["minZ"] = minZ;
            stats["maxZ"] = maxZ;
        }

        return builder.Build();
    }

    /// <summary>
    /// Every visible liquid square of every MCLQ layer of <paramref name="adt"/> as two triangles, layers kept apart (the
    /// extractor's merge is not applied). Null when no layer shows any square. Throws
    /// <see cref="InvalidDataException"/> when a shown square stores a height that is not finite.
    /// </summary>
    /// <param name="tile">The server tile the ADT belongs to.</param>
    /// <param name="adt">The parsed file.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    public static GeometryChunk? BuildLiquid(TileKey tile, AdtFile adt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adt);
        WorldPoint origin = WorldGrid.Origin(tile);
        var builder = new GeometryBuilder(LayerIds.RefLiquid, tile, origin);
        var corners = new Dictionary<(int Kind, int Row, int Column, int HeightBits), uint>();
        int[] layersByKind = new int[4];
        int[] squaresByKind = new int[4];
        int darkSquares = 0;
        int stackedChunks = 0;
        float minLevel = float.PositiveInfinity;
        float maxLevel = float.NegativeInfinity;

        foreach (AdtChunk chunk in adt.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.LiquidLayers.Count > 1)
            {
                stackedChunks++;
            }

            foreach (AdtLiquidLayer layer in chunk.LiquidLayers)
            {
                int kindIndex = (int)layer.Kind;
                layersByKind[kindIndex]++;
                for (int y = 0; y < SquaresPerChunk; y++)
                {
                    for (int x = 0; x < SquaresPerChunk; x++)
                    {
                        if (!layer.IsVisible(y, x))
                        {
                            continue;
                        }

                        bool dark = layer.IsDarkWater(y, x);
                        uint attribute = LiquidAttribute(chunk.Index, layer.Kind, dark);
                        if (!builder.HasAttribute(attribute))
                        {
                            builder.DescribeAttribute(attribute, () => DescribeLiquid(chunk, layer, dark));
                        }

                        // Only the four corners of a shown square are read: the client leaves the rest uninitialised.
                        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:607-620 (only the corners of a visible sub cell are read)
                        uint a = LiquidCorner(builder, corners, tile, chunk, layer, y, x);
                        uint b = LiquidCorner(builder, corners, tile, chunk, layer, y + 1, x);
                        uint c = LiquidCorner(builder, corners, tile, chunk, layer, y, x + 1);
                        uint d = LiquidCorner(builder, corners, tile, chunk, layer, y + 1, x + 1);
                        builder.AddTriangle(a, b, c, attribute);
                        builder.AddTriangle(b, d, c, attribute);
                        squaresByKind[kindIndex]++;
                        darkSquares += dark ? 1 : 0;
                        foreach (float h in (ReadOnlySpan<float>)[layer.Height(y, x), layer.Height(y + 1, x), layer.Height(y, x + 1), layer.Height(y + 1, x + 1)])
                        {
                            minLevel = Math.Min(minLevel, h);
                            maxLevel = Math.Max(maxLevel, h);
                        }
                    }
                }
            }
        }

        if (builder.IsEmpty)
        {
            return null;
        }

        JsonObject stats = builder.Stats;
        stats["source"] = "adt";
        stats["merged"] = false;
        stats["stackedChunks"] = stackedChunks;
        stats["darkWaterSquares"] = darkSquares;
        stats["minLevel"] = minLevel;
        stats["maxLevel"] = maxLevel;
        var layerCounts = new JsonObject();
        var squareCounts = new JsonObject();
        foreach (AdtLiquidKind kind in Enum.GetValues<AdtLiquidKind>())
        {
            layerCounts[KindName(kind)] = layersByKind[(int)kind];
            squareCounts[KindName(kind)] = squaresByKind[(int)kind];
        }

        stats["layers"] = layerCounts;
        stats["squares"] = squareCounts;
        return builder.Build();
    }

    private static uint Corner(GeometryBuilder builder, Dictionary<(int Row, int Column, int HeightBits), uint> corners, TileKey tile, AdtChunk chunk, int y, int x)
    {
        float height = chunk.OuterHeight(y, x);
        CheckFinite(height, chunk, "corner", y, x);
        var key = ((chunk.Row * SquaresPerChunk) + y, (chunk.Column * SquaresPerChunk) + x, BitConverter.SingleToInt32Bits(height));
        if (!corners.TryGetValue(key, out uint id))
        {
            id = builder.AddVertex(AdtGeometry.OuterVertex(tile, chunk, y, x));
            corners[key] = id;
        }

        return id;
    }

    private static uint LiquidCorner(
        GeometryBuilder builder,
        Dictionary<(int Kind, int Row, int Column, int HeightBits), uint> corners,
        TileKey tile,
        AdtChunk chunk,
        AdtLiquidLayer layer,
        int y,
        int x)
    {
        float height = layer.Height(y, x);
        if (!float.IsFinite(height))
        {
            throw new InvalidDataException($"ADT MCNK {chunk.Index}: the {KindName(layer.Kind)} liquid layer shows a square at corner ({y}, {x}) whose height {height} is not finite.");
        }

        int row = (chunk.Row * SquaresPerChunk) + y;
        int column = (chunk.Column * SquaresPerChunk) + x;
        var key = ((int)layer.Kind, row, column, BitConverter.SingleToInt32Bits(height));
        if (!corners.TryGetValue(key, out uint id))
        {
            WorldPoint origin = WorldGrid.Origin(tile);
            id = builder.AddVertex(origin.X - (row * WorldGrid.SquareSize), origin.Y - (column * WorldGrid.SquareSize), height);
            corners[key] = id;
        }

        return id;
    }

    private static void CheckFinite(float height, AdtChunk chunk, string what, int y, int x)
    {
        if (!float.IsFinite(height))
        {
            throw new InvalidDataException($"ADT MCNK {chunk.Index}: the {what} height at ({y}, {x}) is {height}, not a finite number (base {chunk.BaseHeight} plus a stored offset).");
        }
    }

    private static JsonObject DescribeChunk(AdtChunk chunk, Func<uint, string?> areaName)
    {
        var liquids = new JsonArray();
        foreach (AdtLiquidLayer layer in chunk.LiquidLayers)
        {
            liquids.Add(KindName(layer.Kind));
        }

        return new JsonObject
        {
            ["chunk"] = new JsonArray(chunk.Row, chunk.Column),
            ["areaId"] = chunk.AreaId,
            ["area"] = areaName(chunk.AreaId),
            ["flags"] = "0x" + chunk.Flags.ToString("X", CultureInfo.InvariantCulture),
            ["holeSquares"] = chunk.HoleSquares,
            ["hasHeights"] = chunk.HasHeights,
            ["textureLayers"] = chunk.TextureLayerCount,
            ["liquid"] = liquids,
        };
    }

    private static JsonObject DescribeLiquid(AdtChunk chunk, AdtLiquidLayer layer, bool dark) => new()
    {
        ["colorKey"] = LiquidColorKey(layer.Kind, dark),
        ["liquid"] = KindName(layer.Kind),
        ["darkWater"] = dark,
        ["chunk"] = new JsonArray(chunk.Row, chunk.Column),
        ["areaId"] = chunk.AreaId,
        ["layerMin"] = FiniteOrNull(layer.MinHeight),
        ["layerMax"] = FiniteOrNull(layer.MaxHeight),
        ["layersInChunk"] = chunk.LiquidLayers.Count,
    };

    // A layer's stored min/max are not validated by the reader; JSON cannot carry NaN or infinity.
    private static JsonNode? FiniteOrNull(float value) => float.IsFinite(value) ? JsonValue.Create(value) : null;
}
