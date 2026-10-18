using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Server;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Server;

/// <summary>The terrain layers of the server view, on synthetic <c>.map</c> files written at runtime.</summary>
public sealed class TerrainLayerSourceTests : IDisposable
{
    private const uint Map = 1;
    private static readonly TileKey Tile = new(Map, 30, 40);
    private readonly TempTerrainRoot _root = new();

    public void Dispose() => _root.Dispose();

    private static float Corner(int r, int c) => (r * 0.5f) + (c * 0.25f) + 10f;

    // A centre that is not the mean of its corners, so a wrong fan (two triangles, or a swapped diagonal) gives other heights.
    private static float Centre(int r, int c) => (((r + 0.5f) * 0.5f) + ((c + 0.5f) * 0.25f) + 10f) + ((r + c) % 3 * 0.75f);

    private static ushort[] HolesAt(params (int Cell, ushort Mask)[] holes)
    {
        var mask = new ushort[256];
        foreach ((int cell, ushort m) in holes)
        {
            mask[cell] = m;
        }

        return mask;
    }

    private TerrainLayerSource Source(out ServerData data)
    {
        data = new ServerData(_root.Paths);
        return new TerrainLayerSource(data);
    }

    [Fact]
    public void Terrain_FloatTile_IsFourTrianglesPerSquare_HolesOmitted_AndMatchesGetHeight()
    {
        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:472-480 hole bit (holeRow * 4 + holeCol) of cell (row / 8, col / 8) covers 2 x 2 squares
        // (vmangos GridMap.cpp:41-42 holetab_h/holetab_v, :274-284 isHole). Cell 0 bit 0: squares (0..1, 0..1); cell 5 * 16 + 7 bit 15: squares (46..47, 62..63).
        byte[] file = new SyntheticMapFile
        {
            V9 = SyntheticMapFile.Grid(129, Corner),
            V8 = SyntheticMapFile.Grid(128, Centre),
            Holes = HolesAt((0, 0x0001), ((5 * 16) + 7, 0x8000)),
        }.Build();
        _root.WriteMap(Map, Tile.X, Tile.Y, file);
        TerrainLayerSource source = Source(out ServerData data);

        LayerBuild build = source.Build(LayerIds.Terrain, Tile, CancellationToken.None);

        Assert.Equal(TileState.Ok, build.Status.State);
        GeometryChunk chunk = Assert.Single(build.Chunks);
        chunk.Validate();
        Assert.Equal((128 * 128 * 4) - (8 * 4), chunk.TriangleCount);
        Assert.Equal(8, (int)chunk.Stats["holeSquares"]!);

        // Every triangle is a piece of the server's interpolation: its centroid height is TerrainTile.GetHeight at the centroid.
        TerrainTile terrain = data.TerrainFile(Tile).Value!;
        double worst = 0;
        for (int t = 0; t < chunk.TriangleCount; t++)
        {
            WorldPoint a = chunk.Vertex(chunk.Triangles[t * 3]);
            WorldPoint b = chunk.Vertex(chunk.Triangles[(t * 3) + 1]);
            WorldPoint c = chunk.Vertex(chunk.Triangles[(t * 3) + 2]);
            double x = (a.X + b.X + c.X) / 3;
            double y = (a.Y + b.Y + c.Y) / 3;
            double z = (a.Z + b.Z + c.Z) / 3;
            float server = terrain.GetHeight((float)x, (float)y);
            worst = Math.Max(worst, Math.Abs(server - z));
        }

        Assert.True(worst < 2e-3, $"largest centroid height difference {worst}");

        // The squares under the holes have no triangle: the server answers the invalid height there.
        WorldPoint origin = WorldGrid.Origin(Tile);
        float inHole = terrain.GetHeight((float)(origin.X - (0.5 * WorldGrid.SquareSize)), (float)(origin.Y - (0.5 * WorldGrid.SquareSize)));
        Assert.False(TerrainLayerSource.IsUsable(inHole));
    }

    [Fact]
    public void Holes_FloatTile_OutlinesEachHoleSquareBlock()
    {
        _root.WriteMap(Map, Tile.X, Tile.Y, new SyntheticMapFile
        {
            V9 = SyntheticMapFile.Grid(129, Corner),
            V8 = SyntheticMapFile.Grid(128, Centre),
            Holes = HolesAt((0, 0x0001)),
        }.Build());
        TerrainLayerSource source = Source(out _);

        LayerBuild build = source.Build(LayerIds.TerrainHoles, Tile, CancellationToken.None);

        Assert.Equal(TileState.Ok, build.Status.State);
        GeometryChunk chunk = Assert.Single(build.Chunks);
        Assert.Equal(4, (int)chunk.Stats["holeSquares"]!);

        // A 2 x 2 block: two filled triangles per square, and only the outer outline (8 unit edges), not the inner edges.
        Assert.Equal(8, chunk.TriangleCount);
        Assert.Equal(8, chunk.LineCount);
    }

    [Fact]
    public void Terrain_PackedTile_KeepsMaskedSquares_BecauseTheServerIgnoresTheMaskThere()
    {
        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:244 only float storage consults the hole mask (vmangos GridMap.cpp:301 isHole
        // in getHeightFromFloat, absent from getHeightFromUint16)
        _root.WriteMap(Map, Tile.X, Tile.Y, new SyntheticMapFile
        {
            GridHeight = 0,
            GridMaxHeight = 100,
            V9Packed = new ushort[129 * 129],
            V8Packed = new ushort[128 * 128],
            Holes = HolesAt((0, 0x0001)),
        }.Build());
        TerrainLayerSource source = Source(out _);

        GeometryChunk chunk = Assert.Single(source.Build(LayerIds.Terrain, Tile, CancellationToken.None).Chunks);

        Assert.Equal(128 * 128 * 4, chunk.TriangleCount);
        Assert.Equal("packed", (string)chunk.Stats["storage"]!);
        Assert.Equal(4, (int)chunk.Stats["holeMaskSquaresIgnoredByServer"]!);
        Assert.Equal(TileState.Empty, source.Build(LayerIds.TerrainHoles, Tile, CancellationToken.None).Status.State);
    }

    [Fact]
    public void FlatTile_TerrainIsEmpty_AndNoHeightOutlinesTheTileAtGridHeight()
    {
        _root.WriteMap(Map, Tile.X, Tile.Y, new SyntheticMapFile { GridHeight = 42.5f, GridMaxHeight = 42.5f }.Build());
        TerrainLayerSource source = Source(out _);

        LayerBuild terrain = source.Build(LayerIds.Terrain, Tile, CancellationToken.None);
        LayerBuild flat = source.Build(LayerIds.TerrainNoHeight, Tile, CancellationToken.None);

        Assert.Equal(TileState.Empty, terrain.Status.State);
        Assert.Contains("stores no heights", terrain.Status.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Ok, flat.Status.State);
        GeometryChunk chunk = Assert.Single(flat.Chunks);
        Assert.Equal(4, chunk.LineCount);
        for (uint v = 0; v < chunk.VertexCount; v++)
        {
            Assert.Equal(42.5, chunk.Vertex(v).Z, 3);
        }
    }

    [Fact]
    public void Liquid_DrawsOneQuadPerSquare_AttributedByType()
    {
        // ref: src/ArcaneCore.Game/Maps/Terrain/LiquidStatus.cs:36-50 magma 1, ocean 2, slime 4, water 8, deep 0x10
        // (vmangos D:\refs\vmangos\src\game\Maps\GridMapDefines.h:67-74 MAP_LIQUID_TYPE_*)
        var types = new byte[256];
        types[0] = 0x08;
        types[1] = 0x01;
        _root.WriteMap(Map, Tile.X, Tile.Y, new SyntheticMapFile
        {
            V9 = SyntheticMapFile.Grid(129, Corner),
            V8 = SyntheticMapFile.Grid(128, Centre),
            LiquidTypes = types,
            LiquidLevel = 500f,
        }.Build());
        TerrainLayerSource source = Source(out _);

        LayerBuild build = source.Build(LayerIds.Liquid, Tile, CancellationToken.None);

        Assert.Equal(TileState.Ok, build.Status.State);
        GeometryChunk chunk = Assert.Single(build.Chunks);
        Assert.Equal(2 * 64 * 2, chunk.TriangleCount);
        var keys = chunk.TriangleAttributes.Distinct().Select(a => (string)chunk.Attributes[a]["colorKey"]!).Order().ToArray();
        Assert.Equal(["magma", "water"], keys);
        for (uint v = 0; v < chunk.VertexCount; v++)
        {
            Assert.Equal(500.0, chunk.Vertex(v).Z, 3);
        }
    }

    [Fact]
    public void Liquid_ColorKeys_FollowTheServerTypeBits()
    {
        Assert.Equal("water", TerrainLayerSource.LiquidColorKey(0x08));
        Assert.Equal("ocean", TerrainLayerSource.LiquidColorKey(0x02));
        Assert.Equal("magma", TerrainLayerSource.LiquidColorKey(0x01));
        Assert.Equal("slime", TerrainLayerSource.LiquidColorKey(0x04));
        Assert.Equal("deep", TerrainLayerSource.LiquidColorKey(0x10 | 0x02));
        Assert.Null(TerrainLayerSource.LiquidColorKey(0));
    }

    [Fact]
    public void Area_DrawsBordersBetweenCellsWithDifferentFlags()
    {
        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:105-110 the 16 x 16 area map; GetAreaFlag reads it by cell (row from X, column from Y)
        var flags = Enumerable.Repeat((ushort)7, 256).ToArray();
        flags[0] = 5;
        _root.WriteMap(Map, Tile.X, Tile.Y, new SyntheticMapFile
        {
            AreaFlags = flags,
            V9 = SyntheticMapFile.Grid(129, Corner),
            V8 = SyntheticMapFile.Grid(128, Centre),
        }.Build());
        TerrainLayerSource source = Source(out _);

        LayerBuild build = source.Build(LayerIds.Area, Tile, CancellationToken.None);

        Assert.Equal(TileState.Ok, build.Status.State);
        GeometryChunk chunk = Assert.Single(build.Chunks);

        // Cell (0, 0) meets (1, 0) and (0, 1): two borders of eight squares each.
        Assert.Equal(16, chunk.LineCount);
        Assert.Equal(2, (int)chunk.Stats["distinctAreaFlags"]!);
    }

    [Fact]
    public void MissingAndRejectedFiles_AreReportedAsSuch_NeverAsEmpty()
    {
        _root.WriteMap(Map, Tile.X + 1, Tile.Y, [0x4D, 0x41, 0x50, 0x53, 0, 0, 0, 0]);
        TerrainLayerSource source = Source(out _);

        LayerBuild missing = source.Build(LayerIds.Terrain, Tile, CancellationToken.None);
        LayerBuild rejected = source.Build(LayerIds.Terrain, Tile with { X = Tile.X + 1 }, CancellationToken.None);

        Assert.Equal(TileState.Missing, missing.Status.State);
        Assert.Empty(missing.Chunks);
        Assert.Equal(TileState.Rejected, rejected.Status.State);
        Assert.False(string.IsNullOrWhiteSpace(rejected.Status.Detail));
        Assert.Equal(TileState.NotApplicable, source.Build(LayerIds.Terrain, TileKey.Global(Map), CancellationToken.None).Status.State);
    }

    [Fact]
    public void AvailableTiles_ListsTheMapFilesOfTheMap()
    {
        _root.WriteMap(Map, Tile.X, Tile.Y, new SyntheticMapFile { GridHeight = 1 }.Build());
        _root.WriteMap(Map + 1, 2, 3, new SyntheticMapFile { GridHeight = 1 }.Build());
        TerrainLayerSource source = Source(out _);

        Assert.Equal([Tile], source.AvailableTiles(LayerIds.Terrain, Map));
        Assert.Equal(TileState.Missing, source.PeekStatus(LayerIds.Terrain, Tile with { Y = Tile.Y + 1 }).State);
    }

    [Fact]
    public void Tiles_Layer_ReportsWhichFilesExist()
    {
        _root.WriteMap(Map, Tile.X, Tile.Y, new SyntheticMapFile { V9 = SyntheticMapFile.Grid(129, Corner), V8 = SyntheticMapFile.Grid(128, Centre) }.Build());
        var source = new TilesLayerSource(new ServerData(_root.Paths));

        LayerBuild build = source.Build(LayerIds.Tiles, Tile, CancellationToken.None);

        Assert.Equal(TileState.Ok, build.Status.State);
        GeometryChunk chunk = Assert.Single(build.Chunks);
        Assert.True(chunk.LineCount >= 4);
        Assert.Equal(TilesLayerSource.TerrainBit, (int)chunk.Stats["bits"]! & TilesLayerSource.TerrainBit);
        Assert.Equal(0, (int)chunk.Stats["bits"]! & (TilesLayerSource.ModelBit | TilesLayerSource.NavBit));
        JsonObject attribute = chunk.Attributes[chunk.LineAttributes[0]];
        Assert.NotNull(attribute["terrain"]);
    }
}
