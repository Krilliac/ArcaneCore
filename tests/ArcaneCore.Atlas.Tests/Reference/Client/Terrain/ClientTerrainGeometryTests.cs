using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

public sealed class ClientTerrainGeometryTests
{
    private static readonly TileKey Tile = new(0, 33, 31);

    private static AdtFile Parse(AdtSpec spec) => AdtFile.Parse(SyntheticTerrain.Adt(spec).Bytes);

    private static GeometryChunk Terrain(AdtFile adt, Func<uint, string?>? names = null)
        => ClientTerrainGeometry.BuildTerrain(Tile, adt, names ?? (_ => null), CancellationToken.None);

    private static double NormalZ(GeometryChunk chunk, int triangle)
    {
        WorldPoint a = chunk.Vertex(chunk.Triangles[triangle * 3]);
        WorldPoint b = chunk.Vertex(chunk.Triangles[(triangle * 3) + 1]);
        WorldPoint c = chunk.Vertex(chunk.Triangles[(triangle * 3) + 2]);
        return ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
    }

    private static int ChunkIndexOf(double x, double y)
    {
        WorldPoint origin = WorldGrid.Origin(Tile);
        double chunkSize = WorldGrid.TileSize / 16;
        int row = (int)Math.Floor((origin.X - x) / chunkSize);
        int column = (int)Math.Floor((origin.Y - y) / chunkSize);
        return (row * 16) + column;
    }

    [Fact]
    public void Terrain_OfAContinuousPlane_HasTheFullGridOfFans()
    {
        AdtFile adt = Parse(SyntheticTerrain.Plane(Tile, 100f, 0.01f, -0.02f));

        GeometryChunk chunk = Terrain(adt);

        // 129 x 129 corners (chunk edges shared) + 128 x 128 centres; four triangles per square.
        Assert.Equal((129 * 129) + (128 * 128), chunk.VertexCount);
        Assert.Equal(128 * 128 * 4, chunk.TriangleCount);

        // Every square contributes its four spokes; the square outlines are the 129 x 128 horizontal and vertical edges.
        Assert.Equal((2 * 129 * 128) + (128 * 128 * 4), chunk.LineCount);
        Assert.Equal(0, chunk.PointCount);
        Assert.Equal(LayerIds.RefTerrain, chunk.LayerId);
        Assert.Equal(Tile, chunk.Tile);
        Assert.Equal(WorldGrid.Origin(Tile), chunk.Origin);
        chunk.Validate();
    }

    [Fact]
    public void Terrain_VerticesLieOnThePlaneInsideTheTile()
    {
        const float a = 100f;
        const float slopeX = 0.01f;
        const float slopeY = -0.02f;
        AdtFile adt = Parse(SyntheticTerrain.Plane(Tile, a, slopeX, slopeY));
        (double minX, double maxX, double minY, double maxY) = WorldGrid.Bounds(Tile);

        GeometryChunk chunk = Terrain(adt);

        for (uint i = 0; i < chunk.VertexCount; i++)
        {
            WorldPoint p = chunk.Vertex(i);
            Assert.InRange(p.X, minX - 1e-3, maxX + 1e-3);
            Assert.InRange(p.Y, minY - 1e-3, maxY + 1e-3);
            Assert.Equal(a + (slopeX * p.X) + (slopeY * p.Y), p.Z, 0.02);
        }
    }

    [Fact]
    public void Terrain_TrianglesFaceUpAndCarryTheirChunkIndex()
    {
        AdtFile adt = Parse(SyntheticTerrain.Plane(Tile, 5f, 0.003f, 0.002f));

        GeometryChunk chunk = Terrain(adt);

        for (int t = 0; t < chunk.TriangleCount; t++)
        {
            Assert.True(NormalZ(chunk, t) > 0, $"triangle {t} faces down");
            WorldPoint a = chunk.Vertex(chunk.Triangles[t * 3]);
            WorldPoint b = chunk.Vertex(chunk.Triangles[(t * 3) + 1]);
            WorldPoint c = chunk.Vertex(chunk.Triangles[(t * 3) + 2]);
            int index = ChunkIndexOf((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3);
            Assert.Equal((uint)index, chunk.TriangleAttributes[t]);
        }

        Assert.Equal(256, chunk.Attributes.Count);
    }

    [Fact]
    public void Terrain_SkipsHoleSquaresAndDescribesOnlyChunksThatDrawSomething()
    {
        var spec = new AdtSpec { Tile = Tile };
        spec.Holes[0] = 0xFFFF;
        spec.Holes[1] = 0x0001;
        AdtFile adt = Parse(spec);

        GeometryChunk chunk = Terrain(adt);

        Assert.Equal(((128 * 128) - 64 - 4) * 4, chunk.TriangleCount);
        Assert.False(chunk.Attributes.ContainsKey(0));
        Assert.True(chunk.Attributes.ContainsKey(1));
        Assert.Equal(255, chunk.Attributes.Count);
        Assert.Equal(68, chunk.Stats["holeSquares"].AsInt());
        Assert.Equal((128 * 128) - 68, chunk.Stats["squares"].AsInt());
    }

    [Fact]
    public void Terrain_KeepsEachChunksOwnValueWhereNeighboursDisagreeOnASharedEdge()
    {
        var spec = new AdtSpec { Tile = Tile, BaseHeight = (row, _) => row == 0 ? 10f : 20f };
        AdtFile adt = Parse(spec);

        GeometryChunk chunk = Terrain(adt);

        // The row of corners between chunk rows 0 and 1 exists twice: once at 10 (north chunk) and once at 20.
        Assert.Equal((129 * 129) + (128 * 128) + 129, chunk.VertexCount);
        Assert.Equal((2 * 129 * 128) + (128 * 128 * 4) + 128, chunk.LineCount);
        WorldPoint origin = WorldGrid.Origin(Tile);
        double boundaryX = origin.X - (8 * WorldGrid.SquareSize);
        var heightsOnTheEdge = new HashSet<double>();
        for (uint i = 0; i < chunk.VertexCount; i++)
        {
            WorldPoint p = chunk.Vertex(i);
            if (Math.Abs(p.X - boundaryX) < 1e-3)
            {
                heightsOnTheEdge.Add(p.Z);
            }
        }

        Assert.Equal(new double[] { 10, 20 }, heightsOnTheEdge.Order());
    }

    [Fact]
    public void Terrain_ChunkWithoutHeightsIsFlatAtItsBaseAndCounted()
    {
        var spec = new AdtSpec { Tile = Tile, BaseHeight = (row, col) => row == 2 && col == 3 ? 77f : 0f };
        spec.WithoutHeights.Add((2 * 16) + 3);
        AdtFile adt = Parse(spec);

        GeometryChunk chunk = Terrain(adt);

        Assert.Equal(1, chunk.Stats["chunksWithoutHeights"].AsInt());
        Assert.True(chunk.Attributes.ContainsKey((uint)((2 * 16) + 3)));
        Assert.False(chunk.Attributes[(uint)((2 * 16) + 3)]["hasHeights"]!.GetValue<bool>());
        int flat = 0;
        for (int t = 0; t < chunk.TriangleCount; t++)
        {
            if (chunk.TriangleAttributes[t] == (uint)((2 * 16) + 3))
            {
                flat++;
                for (int k = 0; k < 3; k++)
                {
                    Assert.Equal(77.0, chunk.Vertex(chunk.Triangles[(t * 3) + k]).Z);
                }
            }
        }

        Assert.Equal(256, flat);
    }

    [Fact]
    public void Terrain_DescribesEachChunkWithItsAreaFlagsAndLiquid()
    {
        var spec = new AdtSpec { Tile = Tile };
        spec.Areas[21] = 12;
        spec.Liquid[21] = [new LiquidLayerSpec(AdtLiquidKind.Slime, 0, 0, (_, _) => 0f, (_, _) => AdtLiquidLayer.HiddenSquare)];
        spec.Holes[21] = 0x000F;
        AdtFile adt = Parse(spec);

        GeometryChunk chunk = Terrain(adt, id => id == 12 ? "Elwynn Forest" : null);

        JsonObject description = chunk.Attributes[21];
        Assert.Equal(1, description["chunk"]![0].AsInt());
        Assert.Equal(5, description["chunk"]![1].AsInt());
        Assert.Equal(12u, description["areaId"].AsUInt());
        Assert.Equal("Elwynn Forest", description["area"]!.GetValue<string>());
        Assert.Equal("0x20", description["flags"]!.GetValue<string>());
        Assert.Equal(16, description["holeSquares"].AsInt());
        Assert.Equal("slime", description["liquid"]![0]!.GetValue<string>());
        Assert.Null(chunk.Attributes[22]["area"]);
    }

    [Fact]
    public void Terrain_RefusesAHeightThatSumsToInfinity()
    {
        var spec = new AdtSpec
        {
            Tile = Tile,
            BaseHeight = (row, col) => row == 4 && col == 4 ? float.MaxValue : 0f,
            Outer = (row, col, y, x) => row == 4 && col == 4 && y == 2 && x == 2 ? float.MaxValue : 0f,
        };
        AdtFile adt = Parse(spec);

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => Terrain(adt));

        Assert.Contains("MCNK 68", e.Message, StringComparison.Ordinal);
        Assert.Contains("not a finite", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Terrain_HonoursCancellation()
    {
        AdtFile adt = Parse(new AdtSpec { Tile = Tile });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => ClientTerrainGeometry.BuildTerrain(Tile, adt, _ => null, cts.Token));
        Assert.Throws<OperationCanceledException>(() => ClientTerrainGeometry.BuildLiquid(Tile, adt, cts.Token));
    }

    [Fact]
    public void Terrain_RoundTripsThroughTheWireFormat()
    {
        AdtFile adt = Parse(SyntheticTerrain.Plane(Tile, 3f, 0.01f, 0.01f));
        GeometryChunk chunk = Terrain(adt, id => $"area {id}");

        byte[] wire = GeometryWire.Write(chunk);
        GeometryChunk back = GeometryWire.Read(wire);

        Assert.True(wire.Length < GeometryWire.MaxChunkBytes / 8, $"{wire.Length} bytes");
        Assert.Equal(chunk.VertexCount, back.VertexCount);
        Assert.Equal(chunk.TriangleCount, back.TriangleCount);
        Assert.Equal(chunk.LineCount, back.LineCount);
        Assert.Equal(chunk.Positions, back.Positions);
        Assert.Equal(chunk.TriangleAttributes, back.TriangleAttributes);
        Assert.Equal(chunk.Attributes.Count, back.Attributes.Count);
    }

    private static AdtSpec LiquidSpec(Action<AdtSpec> arrange)
    {
        var spec = new AdtSpec { Tile = Tile };
        arrange(spec);
        return spec;
    }

    [Fact]
    public void Liquid_DrawsTwoTrianglesPerVisibleSquareOnSharedVertices()
    {
        AdtSpec spec = LiquidSpec(s => s.Liquid[0] =
        [
            new LiquidLayerSpec(AdtLiquidKind.River, 5, 6, (y, x) => 5f + (y * 0.5f), (y, x) => y < 2 && x < 2 ? (byte)0 : AdtLiquidLayer.HiddenSquare),
        ]);
        AdtFile adt = Parse(spec);

        GeometryChunk? chunk = ClientTerrainGeometry.BuildLiquid(Tile, adt, CancellationToken.None);

        Assert.NotNull(chunk);
        Assert.Equal(LayerIds.RefLiquid, chunk.LayerId);
        Assert.Equal(8, chunk.TriangleCount);
        Assert.Equal(9, chunk.VertexCount);

        // A 2 x 2 block of squares: 6 horizontal + 6 vertical outline edges + 4 diagonals.
        Assert.Equal(16, chunk.LineCount);
        WorldPoint origin = WorldGrid.Origin(Tile);
        for (uint i = 0; i < chunk.VertexCount; i++)
        {
            WorldPoint p = chunk.Vertex(i);
            int y = (int)Math.Round((origin.X - p.X) / WorldGrid.SquareSize);
            int x = (int)Math.Round((origin.Y - p.Y) / WorldGrid.SquareSize);
            Assert.InRange(y, 0, 2);
            Assert.InRange(x, 0, 2);
            Assert.Equal(5.0 + (y * 0.5), p.Z, 1e-6);
        }

        for (int t = 0; t < chunk.TriangleCount; t++)
        {
            Assert.True(NormalZ(chunk, t) > 0, $"liquid triangle {t} faces down");
        }

        Assert.Equal(0, chunk.Stats["stackedChunks"].AsInt());
        Assert.Equal(4, chunk.Stats["squares"]!["river"].AsInt());
        Assert.False(chunk.Stats["merged"]!.GetValue<bool>());
    }

    [Fact]
    public void Liquid_ColoursByKindAndMarksDarkWaterDeep()
    {
        AdtSpec spec = LiquidSpec(s =>
        {
            s.Liquid[0] = [new LiquidLayerSpec(AdtLiquidKind.River, 0, 0, (_, _) => 1f, (y, x) => y == 0 && x == 0 ? (byte)0 : AdtLiquidLayer.HiddenSquare)];
            s.Liquid[1] = [new LiquidLayerSpec(AdtLiquidKind.Ocean, 0, 0, (_, _) => 2f, (y, x) => y == 0 && x == 0 ? (byte)0 : AdtLiquidLayer.HiddenSquare)];
            s.Liquid[2] = [new LiquidLayerSpec(AdtLiquidKind.Magma, 0, 0, (_, _) => 3f, (y, x) => y == 0 && x == 0 ? (byte)0 : AdtLiquidLayer.HiddenSquare)];
            s.Liquid[3] = [new LiquidLayerSpec(AdtLiquidKind.Slime, 0, 0, (_, _) => 4f, (y, x) => y == 0 && x == 0 ? (byte)0 : AdtLiquidLayer.HiddenSquare)];
            s.Liquid[4] = [new LiquidLayerSpec(AdtLiquidKind.Ocean, 0, 0, (_, _) => 5f, (y, x) => y == 0 && x == 0 ? (byte)0x80 : AdtLiquidLayer.HiddenSquare)];
        });
        AdtFile adt = Parse(spec);

        GeometryChunk? chunk = ClientTerrainGeometry.BuildLiquid(Tile, adt, CancellationToken.None);

        Assert.NotNull(chunk);
        GeometryChunk liquid = chunk;
        Assert.Equal(10, chunk.TriangleCount);
        string KeyOf(int index, AdtLiquidKind kind, bool dark)
            => liquid.Attributes[ClientTerrainGeometry.LiquidAttribute(index, kind, dark)]["colorKey"]!.GetValue<string>();

        Assert.Equal(ColorKeys.Water, KeyOf(0, AdtLiquidKind.River, false));
        Assert.Equal(ColorKeys.Ocean, KeyOf(1, AdtLiquidKind.Ocean, false));
        Assert.Equal(ColorKeys.Magma, KeyOf(2, AdtLiquidKind.Magma, false));
        Assert.Equal(ColorKeys.Slime, KeyOf(3, AdtLiquidKind.Slime, false));
        Assert.Equal(ColorKeys.DeepWater, KeyOf(4, AdtLiquidKind.Ocean, true));
        Assert.True(chunk.Attributes[ClientTerrainGeometry.LiquidAttribute(4, AdtLiquidKind.Ocean, true)]["darkWater"]!.GetValue<bool>());
        Assert.Equal(1, chunk.Stats["darkWaterSquares"].AsInt());
        Assert.Equal(1.0, chunk.Stats["minLevel"].AsDouble());
        Assert.Equal(5.0, chunk.Stats["maxLevel"].AsDouble());
    }

    [Fact]
    public void Liquid_KeepsStackedLayersOfOneChunkApart()
    {
        AdtSpec spec = LiquidSpec(s => s.Liquid[7] =
        [
            new LiquidLayerSpec(AdtLiquidKind.River, 10, 10, (_, _) => 10f, (y, x) => y == 3 && x == 3 ? (byte)0 : AdtLiquidLayer.HiddenSquare),
            new LiquidLayerSpec(AdtLiquidKind.Magma, 20, 20, (_, _) => 20f, (y, x) => y == 3 && x == 3 ? (byte)0 : AdtLiquidLayer.HiddenSquare),
        ]);
        AdtFile adt = Parse(spec);

        GeometryChunk? chunk = ClientTerrainGeometry.BuildLiquid(Tile, adt, CancellationToken.None);

        Assert.NotNull(chunk);

        // Two surfaces over the same square at 10 and 20: both are drawn, neither is merged away.
        Assert.Equal(4, chunk.TriangleCount);
        Assert.Equal(8, chunk.VertexCount);
        Assert.Equal(1, chunk.Stats["stackedChunks"].AsInt());
        Assert.Equal(new[] { 10.0, 20.0 }, Enumerable.Range(0, chunk.VertexCount).Select(i => chunk.Vertex((uint)i).Z).Distinct().Order());
        Assert.Equal(1, chunk.Stats["layers"]!["river"].AsInt());
        Assert.Equal(1, chunk.Stats["layers"]!["magma"].AsInt());
        Assert.Equal(0, chunk.Stats["layers"]!["ocean"].AsInt());
    }

    [Fact]
    public void Liquid_IsNullWhenEverySquareIsHidden()
    {
        AdtSpec spec = LiquidSpec(s => s.Liquid[3] = [new LiquidLayerSpec(AdtLiquidKind.River, 1, 1, (_, _) => 1f, (_, _) => AdtLiquidLayer.HiddenSquare)]);
        AdtFile adt = Parse(spec);

        Assert.Null(ClientTerrainGeometry.BuildLiquid(Tile, adt, CancellationToken.None));
        Assert.Null(ClientTerrainGeometry.BuildLiquid(Tile, Parse(new AdtSpec { Tile = Tile }), CancellationToken.None));
    }

    [Fact]
    public void Liquid_ReadsOnlyTheCornersOfVisibleSquares()
    {
        // The client leaves hidden squares' vertices uninitialised, so NaN there must not matter...
        AdtSpec hidden = LiquidSpec(s => s.Liquid[3] =
        [
            new LiquidLayerSpec(AdtLiquidKind.River, 1, 1, (y, x) => y == 0 && x == 0 ? float.NaN : 1f, (y, x) => y == 0 && x == 0 ? AdtLiquidLayer.HiddenSquare : (y == 5 && x == 5 ? (byte)0 : AdtLiquidLayer.HiddenSquare)),
        ]);

        // ...while the same NaN under a visible square is a corrupt file.
        AdtSpec shown = LiquidSpec(s => s.Liquid[3] =
        [
            new LiquidLayerSpec(AdtLiquidKind.River, 1, 1, (y, x) => y == 0 && x == 1 ? float.NaN : 1f, (y, x) => y == 0 && x == 0 ? (byte)0 : AdtLiquidLayer.HiddenSquare),
        ]);

        GeometryChunk? ok = ClientTerrainGeometry.BuildLiquid(Tile, Parse(hidden), CancellationToken.None);
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => ClientTerrainGeometry.BuildLiquid(Tile, Parse(shown), CancellationToken.None));

        Assert.NotNull(ok);
        Assert.Equal(2, ok.TriangleCount);
        Assert.Contains("MCNK 3", e.Message, StringComparison.Ordinal);
        Assert.Contains("not finite", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Liquid_TolerantOfNonFiniteLayerMinMaxInTheAttributeTable()
    {
        AdtSpec spec = LiquidSpec(s => s.Liquid[3] = [new LiquidLayerSpec(AdtLiquidKind.River, float.NaN, float.PositiveInfinity, (_, _) => 1f, (y, x) => y == 0 && x == 0 ? (byte)0 : AdtLiquidLayer.HiddenSquare)]);

        GeometryChunk? chunk = ClientTerrainGeometry.BuildLiquid(Tile, Parse(spec), CancellationToken.None);

        Assert.NotNull(chunk);
        byte[] wire = GeometryWire.Write(chunk);
        Assert.NotEmpty(wire);
        JsonObject description = chunk.Attributes[ClientTerrainGeometry.LiquidAttribute(3, AdtLiquidKind.River, false)];
        Assert.Null(description["layerMin"]);
        Assert.Null(description["layerMax"]);
    }
}
