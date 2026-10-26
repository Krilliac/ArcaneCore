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
        // (vmangos vmangos@0e3ff01 src/game/Maps/GridMapDefines.h:67-74 MAP_LIQUID_TYPE_*)
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

    [Fact]
    public void VMapLiquid_DrawsQuadsOn00To11Diagonal_Skips0x0FMask_AndMatchesWmoLiquidGetLiquidHeight()
    {
        // ref: vmangos@0e3ff01 src/game/vmap/WorldModel.cpp:158-204 WmoLiquid::GetLiquidHeight (flag & 0x0F == 0x0F, (0,0)-(1,1) diagonal)
        float[] heights =
        [
            10f, 12f, 14f,
            15f, 21f, 22f,
            18f, 19f, float.NaN,
        ];
        byte[] flags = [0x04, 0x8F, 0x04, 0x04];
        var liquid = new ArcaneCore.Game.Maps.Collision.VMaps.WmoLiquid(2, 2, System.Numerics.Vector3.Zero, 7, heights, flags);
        System.Numerics.Vector3[] verts = [new(0, 0, 0), new(10, 0, 0), new(10, 10, 0)];
        ArcaneCore.Game.Maps.Collision.VMaps.MeshTriangle[] tris = [new(0, 1, 2)];
        var group = new ArcaneCore.Game.Maps.Collision.VMaps.GroupModel(new(0, 0, 0), new(10, 10, 25), 0, 11, verts, tris, liquid: liquid);
        var model = new ArcaneCore.Game.Maps.Collision.VMaps.WorldModel(5, [group]);
        File.WriteAllBytes(Path.Combine(_root.Root, "vmaps", ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ModelFileName("pool.wmo")), model.ToBytes());

        WorldPoint origin = WorldGrid.Origin(Tile);
        var worldPos = new System.Numerics.Vector3((float)(origin.X - 100), (float)(origin.Y - 100), 50f);
        System.Numerics.Vector3 internalPos = ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ToInternal(worldPos);
        var wmoSpawn = new ArcaneCore.Game.Maps.Collision.VMaps.ModelSpawn(
            ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ModHasBound,
            1,
            10,
            internalPos,
            System.Numerics.Vector3.Zero,
            1f,
            internalPos - new System.Numerics.Vector3(20, 20, 5),
            internalPos + new System.Numerics.Vector3(20, 20, 30),
            "pool.wmo");
        var m2Spawn = wmoSpawn with { Flags = ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ModM2 | ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ModHasBound, Id = 11 };

        WriteVMapTree(Map, 2);
        WriteVMapTile(Tile, [(wmoSpawn, 0u), (m2Spawn, 1u)]);

        var source = new VMapLayerSource(new ServerData(_root.Paths));
        LayerBuild build = source.Build(LayerIds.VMapLiquid, Tile, CancellationToken.None);

        Assert.Equal(TileState.Ok, build.Status.State);
        GeometryChunk chunk = Assert.Single(build.Chunks);
        chunk.Validate();
        Assert.Equal(4, chunk.TriangleCount);
        Assert.Equal(1, (int)chunk.Stats["spawnsOfOtherLayers"]!);
        Assert.Equal(2, (int)chunk.Stats["liquidSquares"]!);
        JsonObject attr = Assert.Single(chunk.Attributes.Values);
        Assert.Equal("wmo liquid", (string)attr["kind"]!);
        Assert.Equal(2, (int)attr["squares"]!);
        Assert.Equal(1, (int)attr["squaresWithoutLiquid"]!);
        Assert.Equal(1, (int)attr["squaresWithBadHeight"]!);

        // Sample both sides of the (0,0)-(1,1) diagonal inside square (0,0) where the four corners (10, 12, 15, 21) are non-coplanar.
        var instance = new ArcaneCore.Game.Maps.Collision.VMaps.ModelInstance(wmoSpawn, model);
        foreach ((float dx, float dy) in new[] { (0.75f, 0.25f), (0.25f, 0.75f) })
        {
            var modelPt = new System.Numerics.Vector3(dx * VMapLayerSource.LiquidTileSize, dy * VMapLayerSource.LiquidTileSize, 0f);
            Assert.True(ServerRealDataTests.TryGetWmoLiquidHeight(liquid, modelPt, out float modelLiqZ));
            System.Numerics.Vector3 expectedWorld = ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ToWorld(
                instance.FromModel(new System.Numerics.Vector3(modelPt.X, modelPt.Y, modelLiqZ)));

            double? drawnZ = null;
            for (int t = 0; t < chunk.TriangleCount; t++)
            {
                WorldPoint a = chunk.Vertex(chunk.Triangles[t * 3]);
                WorldPoint b = chunk.Vertex(chunk.Triangles[(t * 3) + 1]);
                WorldPoint c = chunk.Vertex(chunk.Triangles[(t * 3) + 2]);
                if (ServerRealDataTests.ZAt(a, b, c, expectedWorld.X, expectedWorld.Y) is { } z)
                {
                    drawnZ = z;
                }
            }

            Assert.NotNull(drawnZ);
            Assert.Equal(expectedWorld.Z, drawnZ.Value, 2);
        }
    }

    [Fact]
    public void VMapOwnership_SyntheticTiles_CoversCentreOwner_FallbackOwner_AndOrphanOwner()
    {
        System.Numerics.Vector3[] verts = [new(0, 0, 0), new(2, 0, 0), new(2, 2, 0)];
        ArcaneCore.Game.Maps.Collision.VMaps.MeshTriangle[] tris = [new(0, 1, 2)];
        var model = new ArcaneCore.Game.Maps.Collision.VMaps.WorldModel(1, [new ArcaneCore.Game.Maps.Collision.VMaps.GroupModel(new(0, 0, 0), new(2, 2, 2), 0, 1, verts, tris)]);
        File.WriteAllBytes(Path.Combine(_root.Root, "vmaps", ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ModelFileName("prop.m2")), model.ToBytes());

        TileKey tileA = Tile;
        TileKey tileB = Tile with { Y = Tile.Y + 1 };
        TileKey tileFar = new(Map, 35, 35);
        WorldPoint originB = WorldGrid.Origin(tileB);
        WorldPoint originFar = WorldGrid.Origin(tileFar);

        // Border Y between tileA (Y=40) and tileB (Y=41) is originB.Y. Place bounds spanning the border with centre inside tileA (Y > originB.Y).
        var lowSpanWorld = new System.Numerics.Vector3((float)(originB.X - 200), (float)(originB.Y - 10), 0f);
        var highSpanWorld = new System.Numerics.Vector3((float)(originB.X - 100), (float)(originB.Y + 40), 10f);
        System.Numerics.Vector3 spanPosInternal = ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ToInternal((lowSpanWorld + highSpanWorld) * 0.5f);
        System.Numerics.Vector3 spanLowInternal = System.Numerics.Vector3.Min(
            ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ToInternal(lowSpanWorld),
            ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ToInternal(highSpanWorld));
        System.Numerics.Vector3 spanHighInternal = System.Numerics.Vector3.Max(
            ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ToInternal(lowSpanWorld),
            ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ToInternal(highSpanWorld));

        uint m2Flags = ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ModM2 | ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ModHasBound;
        var spawnCentre = new ArcaneCore.Game.Maps.Collision.VMaps.ModelSpawn(m2Flags, 1, 100, spanPosInternal, default, 1f, spanLowInternal, spanHighInternal, "prop.m2");
        var spawnFallback = spawnCentre with { Id = 200 };

        var farWorld = new System.Numerics.Vector3((float)(originFar.X - 100), (float)(originFar.Y - 100), 0f);
        System.Numerics.Vector3 farInternal = ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ToInternal(farWorld);
        var spawnOrphan = new ArcaneCore.Game.Maps.Collision.VMaps.ModelSpawn(
            m2Flags,
            1,
            300,
            farInternal,
            default,
            1f,
            farInternal - new System.Numerics.Vector3(5, 5, 5),
            farInternal + new System.Numerics.Vector3(5, 5, 5),
            "prop.m2");

        WriteVMapTree(Map, 3);
        WriteVMapTile(tileA, [(spawnCentre, 0u)]);
        WriteVMapTile(tileB, [(spawnCentre, 0u), (spawnFallback, 1u), (spawnOrphan, 2u)]);

        var source = new VMapLayerSource(new ServerData(_root.Paths));
        LayerBuild buildA = source.Build(LayerIds.VMapM2, tileA, CancellationToken.None);
        LayerBuild buildB = source.Build(LayerIds.VMapM2, tileB, CancellationToken.None);

        JsonObject statsA = buildA.Chunks[0].Stats;
        JsonObject statsB = buildB.Chunks[0].Stats;
        Assert.Equal(1, (int)statsA["spawnsDrawn"]!);
        Assert.Equal(0, (int)statsA["spawnsDrawnByAnotherTile"]!);

        // In tileB: spawn 100 is owned by tileA (spawnsDrawnByAnotherTile=1), spawn 200 is owned by fallback (spawnsOwnedByFallbackRule=1),
        // and spawn 300 is listed by no covering tile (spawnsListedByNoCoveringTile=1).
        Assert.Equal(2, (int)statsB["spawnsDrawn"]!);
        Assert.Equal(1, (int)statsB["spawnsDrawnByAnotherTile"]!);
        Assert.Equal(1, (int)statsB["spawnsOwnedByFallbackRule"]!);
        Assert.Equal(1, (int)statsB["spawnsListedByNoCoveringTile"]!);
    }

    [Fact]
    public void NavBv_ZeroNodesIsEmpty_AndRejectedBvTreeDrawsBoundingBox()
    {
        TileKey zeroTile = Tile;
        TileKey rejectedTile = Tile with { Y = Tile.Y + 1 };
        _root.WriteRaw(Path.Combine("mmaps", ArcaneCore.Game.Maps.Collision.MMaps.NavMeshFormat.TileFileName(Map, zeroTile.X, zeroTile.Y)), BuildSyntheticMmtile(bvNodeCount: 0, rootEscapeIndex: 0));
        _root.WriteRaw(Path.Combine("mmaps", ArcaneCore.Game.Maps.Collision.MMaps.NavMeshFormat.TileFileName(Map, rejectedTile.X, rejectedTile.Y)), BuildSyntheticMmtile(bvNodeCount: 1, rootEscapeIndex: -5));

        var source = new NavLayerSource(new ServerData(_root.Paths));
        LayerBuild zeroBuild = source.Build(LayerIds.NavBvRejected, zeroTile, CancellationToken.None);
        LayerBuild rejectedBuild = source.Build(LayerIds.NavBvRejected, rejectedTile, CancellationToken.None);

        Assert.Equal(TileState.Empty, zeroBuild.Status.State);
        Assert.Contains("the file stores no BV nodes", zeroBuild.Status.Detail, StringComparison.Ordinal);

        Assert.Equal(TileState.Ok, rejectedBuild.Status.State);
        GeometryChunk chunk = Assert.Single(rejectedBuild.Chunks);
        Assert.Equal(12, chunk.LineCount);
        Assert.Equal(1, (int)chunk.Stats["headerBvNodes"]!);
        Assert.Contains("failed the server's verification", rejectedBuild.Status.Detail, StringComparison.Ordinal);
    }

    private void WriteVMapTree(uint mapId, int slotCount)
    {
        var bounds = Enumerable.Repeat((System.Numerics.Vector3.Zero, System.Numerics.Vector3.One), slotCount).ToArray();
        ArcaneCore.Game.Maps.Collision.VMaps.BihTree tree = ArcaneCore.Game.Maps.Collision.VMaps.BihTree.Build(bounds, leafSize: 1);
        using var stream = File.Create(Path.Combine(_root.Root, "vmaps", ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.TreeFileName(mapId)));
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII);
        writer.Write(System.Text.Encoding.ASCII.GetBytes(ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.Magic));
        writer.Write((byte)1);
        writer.Write("NODE"u8);
        tree.Write(writer);
        writer.Write("GOBJ"u8);
    }

    private void WriteVMapTile(TileKey tile, IReadOnlyList<(ArcaneCore.Game.Maps.Collision.VMaps.ModelSpawn Spawn, uint Slot)> spawns)
    {
        using var stream = File.Create(Path.Combine(_root.Root, "vmaps", ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.TileFileName(tile.MapId, tile.X, tile.Y)));
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII);
        writer.Write(System.Text.Encoding.ASCII.GetBytes(ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.Magic));
        writer.Write((uint)spawns.Count);
        foreach ((ArcaneCore.Game.Maps.Collision.VMaps.ModelSpawn spawn, uint slot) in spawns)
        {
            spawn.Write(writer);
            writer.Write(slot);
        }
    }

    private static byte[] BuildSyntheticMmtile(int bvNodeCount, int rootEscapeIndex)
    {
        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            // dtMeshHeader (100 bytes): 15 ints + 3 floats + bmin(3) + bmax(3) + bvQuantFactor(1).
            int[] headerInts = [ArcaneCore.Game.Maps.Collision.MMaps.NavMeshFormat.DetourMagic, (int)ArcaneCore.Game.Maps.Collision.MMaps.NavMeshFormat.DetourVersion, 0, 0, 0, 0, 1, 4, 1, 0, 0, 0, bvNodeCount, 0, 1];
            foreach (int v in headerInts)
            {
                w.Write(v);
            }

            w.Write(2f);
            w.Write(0.5f);
            w.Write(1f);
            w.Write(0f);
            w.Write(0f);
            w.Write(0f);
            w.Write(10f);
            w.Write(10f);
            w.Write(10f);
            w.Write(1f);

            // 4 vertices (Recast x, y, z).
            foreach ((float x, float z) in new[] { (0f, 0f), (2f, 0f), (2f, 2f), (0f, 2f) })
            {
                w.Write(x);
                w.Write(0f);
                w.Write(z);
            }

            // 1 polygon (32 bytes): firstLink(u32), verts[6](u16), neis[6](u16), flags(u16), vertCount(u8), areaAndType(u8).
            w.Write(0u);
            ushort[] verts = [0, 1, 2, 3, 0, 0];
            foreach (ushort v in verts)
            {
                w.Write(v);
            }

            for (int i = 0; i < 6; i++)
            {
                w.Write((ushort)0);
            }

            w.Write((ushort)1);
            w.Write((byte)4);
            w.Write((byte)1);

            // 1 link (16 bytes).
            w.Write(new byte[16]);

            // Optional BV nodes (16 bytes each: bmin[3] u16, bmax[3] u16, i int32).
            for (int n = 0; n < bvNodeCount; n++)
            {
                for (int a = 0; a < 6; a++)
                {
                    w.Write((ushort)0);
                }

                w.Write(rootEscapeIndex);
            }
        }

        byte[] tileBytes = body.ToArray();
        using var file = new MemoryStream();
        using (var fw = new BinaryWriter(file))
        {
            fw.Write(ArcaneCore.Game.Maps.Collision.MMaps.NavMeshFormat.MmapMagic);
            fw.Write(ArcaneCore.Game.Maps.Collision.MMaps.NavMeshFormat.DetourVersion);
            fw.Write(ArcaneCore.Game.Maps.Collision.MMaps.NavMeshFormat.MmapVersion);
            fw.Write((uint)tileBytes.Length);
            fw.Write(0u);
            fw.Write(tileBytes);
        }

        return file.ToArray();
    }
}
