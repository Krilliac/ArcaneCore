using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Diff;
using ArcaneCore.Atlas.Host;
using ArcaneCore.Atlas.Live;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Reference.Navmesh;
using ArcaneCore.Atlas.Server;
using ArcaneCore.Atlas.Tests.Reference.Client.Models;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Navmesh;
using ArcaneCore.Atlas.Tests.Server;
using ArcaneCore.Game.Maps.Collision.VMaps;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Diff;

public sealed class DiffTests
{
    [Fact]
    public void DiffModule_RegistersLayersAndCommands()
    {
        var context = new AtlasContext(new AtlasPaths(null, null), NullLoggerFactory.Instance, headless: true);
        new DiffModule().Register(context);

        Assert.True(context.Layers.TryGet(LayerIds.DiffTerrain, out _));
        Assert.True(context.Layers.TryGet(LayerIds.DiffModels, out _));
        Assert.True(context.Layers.TryGet(LayerIds.DiffNav, out _));
        Assert.True(context.Commands.TryGet(DiffCommands.TerrainCommand, out _));
        Assert.True(context.Commands.TryGet(DiffCommands.ModelsCommand, out _));
        Assert.True(context.Commands.TryGet(DiffCommands.NavCommand, out _));
    }

    [Fact]
    public void GeometryBuilder_AddEdge_DeduplicatesBothDirections()
    {
        var tile = new TileKey(0, 32, 32);
        var builder = new GeometryBuilder(LayerIds.DiffTerrain, tile, WorldGrid.Origin(tile));
        uint v0 = builder.AddVertex(10, 20, 30);
        uint v1 = builder.AddVertex(15, 25, 35);

        builder.AddEdge(v0, v1, 1);
        builder.AddEdge(v1, v0, 1);
        builder.AddEdge(v0, v0, 1);

        GeometryChunk chunk = builder.Build();
        Assert.Equal(1, chunk.LineCount);
    }

    [Fact]
    public async Task TerrainDiff_MatchingPlane_ReturnsEmptyLayerAndZeroDiffs()
    {
        using var root = new TempTerrainRoot();
        var tile = new TileKey(0, 32, 32);
        BuiltAdt adt = SyntheticTerrain.Adt(SyntheticTerrain.Plane(tile, 42f, 0f, 0f));

        var mapFile = new SyntheticMapFile
        {
            GridHeight = 42f,
            GridMaxHeight = 42f,
            V9 = SyntheticMapFile.Grid(129, (_, _) => 42f),
            V8 = SyntheticMapFile.Grid(128, (_, _) => 42f),
        };
        root.WriteMap(tile.MapId, tile.X, tile.Y, mapFile.Build());

        var files = new MemoryClientFiles();
        SyntheticTerrain.AddMap(files, "Azeroth", SyntheticTerrain.Wdt([(tile.X, tile.Y)]), (tile, adt.Bytes));
        var maps = SyntheticTerrain.Maps(new ClientMap(0, "Azeroth", "Eastern Kingdoms", 0));

        var context = BuildDiffContext(root.Paths, files, maps);
        Assert.True(context.Layers.TryGet(LayerIds.DiffTerrain, out ILayerSource source));
        LayerBuild build = source.Build(LayerIds.DiffTerrain, tile, CancellationToken.None);

        Assert.Equal(TileState.Empty, build.Status.State);
        Assert.Empty(build.Chunks);

        CommandResult cmd = await InvokeAsync(context, DiffCommands.TerrainCommand, new JsonObject
        {
            ["map"] = 0,
            ["x"] = tile.X,
            ["y"] = tile.Y,
        });
        Assert.True(cmd.Ok, cmd.ErrorMessage);
        JsonObject data = cmd.Data!.AsObject();
        Assert.Equal(1, data["tilesCompared"]!.GetValue<int>());
        Assert.Equal(0L, data["unexplainedCount"]!.GetValue<long>());
        Assert.Equal(0L, data["expectedCount"]!.GetValue<long>());
        Assert.Equal(37120L, data["totals"]!["exactSamples"]!.GetValue<long>());
    }

    [Fact]
    public async Task TerrainDiff_ClassifiesExpectedClampAndNonFloatHoleMaskAndFlagsUnexplained()
    {
        using var root = new TempTerrainRoot();
        var tileExpected = new TileKey(0, 32, 32);
        var specExpected = new AdtSpec
        {
            Tile = tileExpected,
            BaseHeight = (_, _) => 0f,
            Outer = (r, c, y, x) => (r == 0 && c == 0 && y == 0 && x == 0) ? -600f : -500f,
            Inner = (_, _, _, _) => -500f,
        };
        specExpected.Holes[0] = 0x0001;
        BuiltAdt adtExpected = SyntheticTerrain.Adt(specExpected);

        var holes = new ushort[256];
        holes[0] = 0x0001;
        var flatClampedMap = new SyntheticMapFile
        {
            GridHeight = -500f,
            GridMaxHeight = -500f,
            Holes = holes,
        };
        root.WriteMap(tileExpected.MapId, tileExpected.X, tileExpected.Y, flatClampedMap.Build());

        var tileMismatch = new TileKey(0, 32, 33);
        BuiltAdt adtMismatch = SyntheticTerrain.Adt(SyntheticTerrain.Plane(tileMismatch, 10f, 0f, 0f));
        float[] v9 = SyntheticMapFile.Grid(129, (r, c) => (r == 0 && c == 0) ? 25f : 10f);
        float[] v8 = SyntheticMapFile.Grid(128, (_, _) => 10f);
        var mismatchHoles = new ushort[256];
        mismatchHoles[0] = 0x0001;
        var mismatchMap = new SyntheticMapFile
        {
            GridHeight = 10f,
            GridMaxHeight = 25f,
            V9 = v9,
            V8 = v8,
            Holes = mismatchHoles,
        };
        root.WriteMap(tileMismatch.MapId, tileMismatch.X, tileMismatch.Y, mismatchMap.Build());

        var files = new MemoryClientFiles();
        SyntheticTerrain.AddMap(
            files,
            "Azeroth",
            SyntheticTerrain.Wdt([(tileExpected.X, tileExpected.Y), (tileMismatch.X, tileMismatch.Y)]),
            (tileExpected, adtExpected.Bytes),
            (tileMismatch, adtMismatch.Bytes));
        var maps = SyntheticTerrain.Maps(new ClientMap(0, "Azeroth", "Eastern Kingdoms", 0));

        var context = BuildDiffContext(root.Paths, files, maps);
        Assert.True(context.Layers.TryGet(LayerIds.DiffTerrain, out ILayerSource source));

        LayerBuild expectedBuild = source.Build(LayerIds.DiffTerrain, tileExpected, CancellationToken.None);
        Assert.Equal(TileState.Ok, expectedBuild.Status.State);
        Assert.Single(expectedBuild.Chunks);
        Assert.Equal(0L, expectedBuild.Chunks[0].Stats["unexplainedCount"]!.GetValue<long>());
        Assert.Equal(1L, expectedBuild.Chunks[0].Stats["clampedSamples"]!.GetValue<long>());
        Assert.Equal(4L, expectedBuild.Chunks[0].Stats["nonFloatHoleMaskIgnored"]!.GetValue<long>());

        LayerBuild mismatchBuild = source.Build(LayerIds.DiffTerrain, tileMismatch, CancellationToken.None);
        Assert.Equal(TileState.Ok, mismatchBuild.Status.State);
        Assert.Single(mismatchBuild.Chunks);
        Assert.Equal(1L, mismatchBuild.Chunks[0].Stats["unexplainedHeightMismatches"]!.GetValue<long>());
        Assert.Equal(4L, mismatchBuild.Chunks[0].Stats["holeMismatches"]!.GetValue<long>());

        CommandResult cmd = await InvokeAsync(context, DiffCommands.TerrainCommand, new JsonObject
        {
            ["map"] = 0,
            ["x"] = tileMismatch.X,
            ["y"] = tileMismatch.Y,
        });
        Assert.True(cmd.Ok, cmd.ErrorMessage);
        Assert.Equal(5L, cmd.Data!["unexplainedCount"]!.GetValue<long>());
    }

    [Fact]
    public async Task ModelDiff_ClassifiesExpectedRenderOnlyAndFlagsUnmatchedSpawns()
    {
        using var root = new TempTerrainRoot();
        var tile = ModelWorld.Goldshire;
        var world = new ModelWorld();
        (double cx, double cy) = WorldGrid.Centre(tile);
        var pos = new WorldPoint(cx, cy, 20.0);

        // Group 0: 1 collidable triangle (0x20) + 1 render-only triangle (0x04, kept by server precise mode).
        // Group 1: unreachable (0x80), skipped by extractor.
        var g0 = new SyntheticWmoGroup
        {
            GroupId = 10,
            Vertices =
            {
                new Vector3(0, 0, 0),
                new Vector3(4, 0, 0),
                new Vector3(0, 4, 0),
                new Vector3(4, 4, 0),
            },
            Triangles =
            {
                new SyntheticTriangle(0, 1, 2, 0x20),
                new SyntheticTriangle(1, 3, 2, 0x04),
            },
        };
        var g1 = new SyntheticWmoGroup
        {
            GroupId = 11,
            Flags = WmoGroupFlags.Unreachable,
            Vertices =
            {
                new Vector3(0, 0, 5),
                new Vector3(2, 0, 5),
                new Vector3(0, 2, 5),
            },
            Triangles = { new SyntheticTriangle(0, 1, 2, 0x20) },
        };
        world.AddWmo(ModelWorld.WallPath, new SyntheticWmoRoot { WmoId = 77 }, g0, g1);
        world.AddAdt(tile);

        ClientPlacement clientWmo = ModelWorld.Wmo(100, pos, tile);
        world.Placements.Add(tile, clientWmo);

        // Write matching server .vmo, .vmtree, and .vmtile.
        var serverGroup = new GroupModel(
            new Vector3(0, 0, 0),
            new Vector3(4, 4, 0),
            0,
            10,
            [.. g0.Vertices],
            [new MeshTriangle(0, 1, 2), new MeshTriangle(1, 3, 2)]);
        var serverWmo = new WorldModel(77, [serverGroup]);
        root.WriteRaw(Path.Combine("vmaps", VMapFormat.ModelFileName("Wall.wmo")), serverWmo.ToBytes());

        Vector3 internalPos = VMapFormat.ToInternal(new Vector3((float)pos.X, (float)pos.Y, (float)pos.Z));
        var spawn = new ModelSpawn(
            VMapFormat.ModHasBound,
            0,
            100,
            internalPos,
            Vector3.Zero,
            1f,
            internalPos - new Vector3(10, 10, 10),
            internalPos + new Vector3(10, 10, 10),
            "Wall.wmo");
        WriteVMapTreeAndTile(root, tile, [(spawn, 0u)], slotCount: 1);

        var context = new AtlasContext(root.Paths, NullLoggerFactory.Instance, headless: true);
        context.Services.Add(new ServerData(root.Paths));
        context.Services.Add<IClientFileSource>(world.Files);
        context.Services.Add<IClientPlacementSource>(world.Placements);
        context.Services.Add(world.Cache);
        new DiffModule().Register(context);

        Assert.True(context.Layers.TryGet(LayerIds.DiffModels, out ILayerSource modelDiffSource));
        LayerBuild build = modelDiffSource.Build(LayerIds.DiffModels, tile, CancellationToken.None);
        Assert.Equal(TileState.Ok, build.Status.State);
        Assert.Single(build.Chunks);
        Assert.Equal(0L, build.Chunks[0].Stats["unexplainedCount"]!.GetValue<long>());
        Assert.Equal(1L, build.Chunks[0].Stats["skippedWmoGroups"]!.GetValue<long>());
        Assert.Equal(1L, build.Chunks[0].Stats["renderOnlyTrianglesKept"]!.GetValue<long>());

        // Now add an unmatched client M2 placement and verify it is flagged as unexplained.
        world.AddCube();
        ClientPlacement extraM2 = ModelWorld.M2(200, new WorldPoint(cx + 20, cy + 20, 20), tile);
        world.Placements.Add(tile, extraM2);

        CommandResult cmd = await InvokeAsync(context, DiffCommands.ModelsCommand, new JsonObject
        {
            ["map"] = tile.MapId,
            ["x"] = tile.X,
            ["y"] = tile.Y,
        });
        Assert.True(cmd.Ok, cmd.ErrorMessage);
        Assert.Equal(1L, cmd.Data!["unexplainedCount"]!.GetValue<long>());
        Assert.Equal(1L, cmd.Data!["totals"]!["unmatchedClientInstances"]!.GetValue<long>());
    }

    [Fact]
    public async Task NavDiff_MatchesCleanPortalsAndClassifiesOffMeshAndRejectedBvTree()
    {
        using var map = new SyntheticMap(mapId: 0);
        var tileA = new TileKey(0, 30, 30);
        var tileB = new TileKey(0, 30, 31);
        map.Add(new TileSpec { TerrainX = tileA.X, TerrainY = tileA.Y, Cells = 2 });
        map.Add(new TileSpec { TerrainX = tileB.X, TerrainY = tileB.Y, Cells = 2 });

        var paths = new AtlasPaths(map.Root, null);
        var context = BuildNavDiffContext(paths);
        Assert.True(context.Layers.TryGet(LayerIds.DiffNav, out ILayerSource source));

        LayerBuild cleanBuild = source.Build(LayerIds.DiffNav, tileA, CancellationToken.None);
        Assert.Equal(TileState.Empty, cleanBuild.Status.State);
        Assert.Empty(cleanBuild.Chunks);

        // Tile with an off-mesh connection and a BV-tree leaf missing polygon 1 (so server TryVerifyBvTree rejects it while DotRecast loads it).
        var tileC = new TileKey(0, 32, 32);
        Vector3 startRc = SyntheticNav.CellCenterRecast(tileC.X, tileC.Y, 2, 0, 0, 10f);
        Vector3 endRc = SyntheticNav.CellCenterRecast(tileC.X, tileC.Y, 2, 1, 1, 10f);
        BuiltTile built = SyntheticNav.Build(new TileSpec
        {
            TerrainX = tileC.X,
            TerrainY = tileC.Y,
            Cells = 2,
            OffMesh = [new OffMeshSpec(startRc, endRc, 1.5f, Bidirectional: true)],
        });

        // Overwrite every BV node in built.File to be a leaf pointing at polygon 0 (valid for DotRecast's ValidateIndices,
        // but rejected by the server's TryVerifyBvTree because polygon 1..3 are missing from the tree).
        byte[] patched = (byte[])built.File.Clone();
        for (int i = 0; i < built.BvNodeCount; i++)
        {
            int nodeOffset = built.Layout.Bv + (i * 16);
            BinaryPrimitives.WriteInt32LittleEndian(patched.AsSpan(nodeOffset + 12, 4), 0);
        }

        map.WriteRaw(tileC.X, tileC.Y, patched);

        var context2 = BuildNavDiffContext(paths);
        Assert.True(context2.Layers.TryGet(LayerIds.DiffNav, out ILayerSource source2));
        LayerBuild expectedBuild = source2.Build(LayerIds.DiffNav, tileC, CancellationToken.None);
        Assert.Equal(TileState.Ok, expectedBuild.Status.State);
        Assert.Single(expectedBuild.Chunks);
        Assert.Equal(0L, expectedBuild.Chunks[0].Stats["unexplainedCount"]!.GetValue<long>());
        Assert.True(expectedBuild.Chunks[0].Stats["bvTreeRejectedByServer"]!.GetValue<bool>());
        Assert.Equal(1, expectedBuild.Chunks[0].Stats["offMeshConnectionsIgnored"]!.GetValue<int>());

        CommandResult cmd = await InvokeAsync(context2, DiffCommands.NavCommand, new JsonObject
        {
            ["map"] = 0,
            ["x"] = tileC.X,
            ["y"] = tileC.Y,
        });
        Assert.True(cmd.Ok, cmd.ErrorMessage);
        Assert.Equal(0L, cmd.Data!["unexplainedCount"]!.GetValue<long>());
        Assert.Equal(2L, cmd.Data!["expectedCount"]!.GetValue<long>());
    }

    [Fact]
    public async Task RefQueryFloorstack_ReturnsTerrainWmoAndM2HitsInDescendingOrder()
    {
        var tile = ModelWorld.Goldshire;
        var world = new ModelWorld();
        (double cx, double cy) = WorldGrid.Centre(tile);

        // Horizontal floor WMO at z = 50 and M2 cube centred at z = 30 (top at z = 32, bottom at z = 28).
        var floorGroup = new SyntheticWmoGroup
        {
            GroupId = 1,
            Vertices =
            {
                new Vector3(-10, -10, 0),
                new Vector3(10, -10, 0),
                new Vector3(-10, 10, 0),
                new Vector3(10, 10, 0),
            },
            Triangles =
            {
                new SyntheticTriangle(0, 1, 2, 0x20),
                new SyntheticTriangle(1, 3, 2, 0x20),
            },
        };
        world.AddWmo(ModelWorld.WallPath, new SyntheticWmoRoot { WmoId = 1 }, floorGroup);
        world.AddCube(half: 2f, withHull: true);
        world.AddAdt(tile);

        world.Placements.Add(
            tile,
            ModelWorld.Wmo(1, new WorldPoint(cx, cy, 50.0), tile),
            ModelWorld.M2(2, new WorldPoint(cx, cy, 30.0), tile));

        BuiltAdt adt = SyntheticTerrain.Adt(SyntheticTerrain.Plane(tile, 12f, 0f, 0f));
        SyntheticTerrain.AddMap(
            world.Files,
            "Azeroth",
            SyntheticTerrain.Wdt([(tile.X, tile.Y)]),
            (tile, adt.Bytes));
        var terrainService = new ClientTerrainService(() => world.Files, () => world.Services.Get<IMapDirectory>(), NullLogger.Instance);
        world.Services.Add(terrainService);

        var context = new AtlasContext(new AtlasPaths(null, null), NullLoggerFactory.Instance, headless: true);
        context.Services.Add<IClientFileSource>(world.Files);
        context.Services.Add<IClientPlacementSource>(world.Placements);
        context.Services.Add(world.Services.Get<IMapDirectory>()!);
        context.Services.Add(terrainService);
        new ClientModelsModule().Register(context);

        CommandResult result = await InvokeAsync(context, "ref.query.floorstack", new JsonObject
        {
            ["map"] = tile.MapId,
            ["x"] = cx,
            ["y"] = cy,
        });
        Assert.True(result.Ok, result.ErrorMessage);
        JsonArray surfaces = result.Data!["surfaces"]!.AsArray();
        Assert.True(surfaces.Count >= 3);
        Assert.Contains(surfaces, s => s!["kind"]!.GetValue<string>() == "terrain" && Math.Abs(s["z"]!.GetValue<double>() - 12.0) < 0.05);
        Assert.Contains(surfaces, s => s!["kind"]!.GetValue<string>() == "model" && s["modelKind"]!.GetValue<string>() == "m2");
        Assert.Contains(surfaces, s => s!["kind"]!.GetValue<string>() == "model" && s["modelKind"]!.GetValue<string>() == "wmo" && Math.Abs(s["z"]!.GetValue<double>() - 50.0) < 0.05);
    }

    [Fact]
    public async Task StatusAndCameraFrame_IntegrateWithLiveFeedAndStore()
    {
        var context = new AtlasContext(new AtlasPaths(null, null), NullLoggerFactory.Instance, headless: false);
        new LiveModule().Register(context);
        StatusCommands.Register(context.Commands);
        ViewCommands.Register(context.Commands);

        CommandResult status = await InvokeAsync(context, "status", new JsonObject());
        Assert.True(status.Ok, status.ErrorMessage);
        Assert.NotNull(status.Data!["live"]);
        Assert.False(status.Data!["live"]!["connected"]!.GetValue<bool>());

        LiveFeed feed = context.Services.Get<LiveFeed>()!;
        var entityPos = new WorldPoint(-9462.5, 62.25, 56.0);
        var entity = new LiveEntity(
            0xABCDUL,
            LiveKind.Player,
            1,
            "TestBot",
            1,
            0,
            entityPos,
            0.0,
            100.0,
            "idle",
            null,
            null,
            null,
            null);
        feed.Store.Apply(new LiveFrame(1, 1000, 1, 0, [(49, 31)], null, [entity], Stopwatch.GetTimestamp()));

        CommandResult frame = await InvokeAsync(
            context,
            "camera.frame",
            new JsonObject { ["guid"] = "0xabcd" },
            headless: false);
        Assert.True(frame.Ok, frame.ErrorMessage);
        Assert.Equal("ABCD", context.View.Current.FollowGuid);
        Assert.Equal(1u, context.View.Current.MapId);
        Assert.Equal(entityPos.X, context.View.Current.Camera.Target.X, 3);
        Assert.Equal(entityPos.Y, context.View.Current.Camera.Target.Y, 3);
        Assert.Equal(entityPos.Z, context.View.Current.Camera.Target.Z, 3);
    }

    private static AtlasContext BuildDiffContext(AtlasPaths paths, IClientFileSource files, IMapDirectory maps)
    {
        var context = new AtlasContext(paths, NullLoggerFactory.Instance, headless: true);
        context.Services.Add(new ServerData(paths));
        context.Services.Add(files);
        context.Services.Add(maps);
        new ClientTerrainModule().Register(context);
        new DiffModule().Register(context);
        return context;
    }

    private static AtlasContext BuildNavDiffContext(AtlasPaths paths)
    {
        var context = new AtlasContext(paths, NullLoggerFactory.Instance, headless: true);
        context.Services.Add(new ServerData(paths));
        new ReferenceNavmeshModule().Register(context);
        new DiffModule().Register(context);
        return context;
    }

    private static void WriteVMapTreeAndTile(TempTerrainRoot root, TileKey tile, IReadOnlyList<(ModelSpawn Spawn, uint Slot)> spawns, int slotCount)
    {
        var bounds = Enumerable.Range(0, Math.Max(1, slotCount))
            .Select(_ => (Vector3.Zero, Vector3.One))
            .ToArray();
        BihTree bih = BihTree.Build(bounds, leafSize: 1);

        using var treeStream = new MemoryStream();
        using (var w = new BinaryWriter(treeStream, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes(VMapFormat.Magic));
            w.Write((byte)1);
            w.Write("NODE"u8);
            bih.Write(w);
            w.Write("GOBJ"u8);
        }

        root.WriteRaw(Path.Combine("vmaps", VMapFormat.TreeFileName(tile.MapId)), treeStream.ToArray());

        using var tileStream = new MemoryStream();
        using (var w = new BinaryWriter(tileStream, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes(VMapFormat.Magic));
            w.Write((uint)spawns.Count);
            foreach ((ModelSpawn s, uint slot) in spawns)
            {
                s.Write(w);
                w.Write(slot);
            }
        }

        root.WriteRaw(Path.Combine("vmaps", VMapFormat.TileFileName(tile.MapId, tile.X, tile.Y)), tileStream.ToArray());
    }

    private static Task<CommandResult> InvokeAsync(AtlasContext context, string command, JsonObject args, bool headless = true)
        => context.Commands.InvokeAsync(command, args, context.Services, CommandOrigin.Cli, headless, CancellationToken.None);
}
