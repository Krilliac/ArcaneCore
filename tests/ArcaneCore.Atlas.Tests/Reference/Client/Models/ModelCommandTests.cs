using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Client.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

public sealed class ModelInfoTests
{
    [Fact]
    public void ModelInfo_DescribesAWmo_WithItsGroupsSetsAndTheExtractorsSkipRules()
    {
        var world = new ModelWorld();
        var root = new SyntheticWmoRoot();
        root.GroupNames.AddRange(["lobby", "cellar"]);
        root.DoodadSets.Add(("Default", 0, 2));
        root.Doodads.Add((ModelWorld.CubeStoredPath, Vector3.Zero, Quaternion.Identity, 1f));
        root.Doodads.Add((ModelWorld.CubeStoredPath, Vector3.UnitX, Quaternion.Identity, 1f));
        SyntheticWmoGroup lobby = ModelFixtures.WallGroup(0f);
        lobby.DoodadReferences.Add(1);
        lobby.Triangles.Add(new SyntheticTriangle(0, 1, 2, 0x04));
        SyntheticWmoGroup cellar = ModelFixtures.WallGroup(1f, groupFlags: 0x80);
        cellar.NameOffset = (uint)"lobby\0".Length;
        world.AddWmo(ModelWorld.WallPath, root, lobby, cellar);

        JsonObject info = world.Queries.ModelInfo("world/wmo/test/wall.wmo");

        Assert.Equal("wmo", info["kind"]!.GetValue<string>());
        Assert.Equal(17u, info["version"]!.GetValue<uint>());
        Assert.Equal("memory", info["archive"]!.GetValue<string>());
        Assert.Equal(2u, info["header"]!["groups"]!.GetValue<uint>());
        JsonArray groups = info["groups"]!.AsArray();
        Assert.Equal(2, groups.Count);
        Assert.Equal("lobby", groups[0]!["name"]!.GetValue<string>());
        Assert.Equal(3, groups[0]!["triangles"]!.GetValue<int>());
        Assert.Equal(2, groups[0]!["collidableTriangles"]!.GetValue<int>());
        Assert.Equal(1, groups[0]!["renderOnlyTriangles"]!.GetValue<int>());
        Assert.False(groups[0]!["extractorSkipped"]!.GetValue<bool>());
        Assert.True(groups[1]!["extractorSkipped"]!.GetValue<bool>());
        Assert.Equal("unreachable", groups[1]!["skipReason"]!.GetValue<string>());
        Assert.Equal(1, info["extractor"]!["keptGroups"]!.GetValue<int>());
        Assert.Equal(1, info["extractor"]!["skippedGroups"]!.GetValue<int>());
        Assert.Equal(3, info["extractor"]!["triangles"]!.GetValue<int>());
        JsonObject set = info["doodadSets"]![0]!.AsObject();
        Assert.Equal("Default", set["name"]!.GetValue<string>());
        Assert.Equal(1, set["referencedByKeptGroups"]!.GetValue<int>());
        Assert.Equal(2, info["doodads"]!.GetValue<int>());
    }

    [Fact]
    public void ModelInfo_DescribesAnM2_AndMapsAMdxNameToTheM2File()
    {
        var world = new ModelWorld();
        world.AddCube();

        JsonObject info = world.Queries.ModelInfo(ModelWorld.CubeStoredPath);

        Assert.Equal("m2", info["kind"]!.GetValue<string>());
        Assert.Equal(ModelWorld.CubePath, info["path"]!.GetValue<string>());
        Assert.Equal(8, info["vertices"]!.GetValue<int>());
        Assert.Equal(12, info["renderTriangles"]!.GetValue<int>());
        Assert.Equal(12, info["boundingTriangles"]!.GetValue<int>());
        Assert.True(info["hasCollisionHull"]!.GetValue<bool>());
        Assert.Equal(-1.0, info["hullBoundsMin"]![0]!.GetValue<double>());
        Assert.Equal(1.0, info["hullBoundsMax"]![2]!.GetValue<double>());
    }

    [Fact]
    public void ModelInfo_ReportsAnM2WithoutAHull()
    {
        var world = new ModelWorld();
        world.AddCube(withHull: false);

        JsonObject info = world.Queries.ModelInfo(ModelWorld.CubePath);

        Assert.False(info["hasCollisionHull"]!.GetValue<bool>());
        Assert.Equal(0, info["boundingTriangles"]!.GetValue<int>());
    }

    [Fact]
    public void ModelInfo_Failures_UseTheDocumentedCodes()
    {
        var world = new ModelWorld();
        world.Files.Add("World\\Doodads\\Test\\Bad.m2", [1, 2, 3]);

        CommandException missing = Assert.Throws<CommandException>(() => world.Queries.ModelInfo("World\\wmo\\Test\\Gone.wmo"));
        CommandException corrupt = Assert.Throws<CommandException>(() => world.Queries.ModelInfo("World\\Doodads\\Test\\Bad.m2"));
        CommandException extension = Assert.Throws<CommandException>(() => world.Queries.ModelInfo("World\\Doodads\\Test\\Cube.blp"));
        CommandException unavailable = Assert.Throws<CommandException>(() => new ModelWorld(withFiles: false).Queries.ModelInfo(ModelWorld.WallPath));

        Assert.Equal(CommandException.BadArguments, missing.Code);
        Assert.Contains("Gone.wmo", missing.Message, StringComparison.Ordinal);
        Assert.Equal(CommandException.Failed, corrupt.Code);
        Assert.Equal(CommandException.BadArguments, extension.Code);
        Assert.Equal(CommandException.Unavailable, unavailable.Code);
    }
}

public sealed class LineOfSightTests
{
    private static readonly WorldPoint WallSpot = new(-9300, 100, 50);
    private static readonly WorldPoint CubeSpot = new(-9250, 100, 50);

    private static ModelWorld WallWorld(ushort wmoFlags = 0, uint groupFlags = 0)
    {
        var world = new ModelWorld();
        world.AddWall(groupFlags: groupFlags);
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, WallSpot, ModelWorld.Goldshire, flags: wmoFlags));
        return world;
    }

    private static double[] Point(JsonNode? node) => [.. node!.AsArray().Select(n => n!.GetValue<double>())];

    [Fact]
    public void AWallBetweenTheEndpoints_BlocksSpellAndMovement_AndTheHitIsOnThePlane()
    {
        ModelWorld world = WallWorld();

        JsonObject result = world.Queries.LineOfSight(0, new WorldPoint(-9290, 103, 48), new WorldPoint(-9310, 103, 48), false, CancellationToken.None);

        foreach (string kind in new[] { "spell", "movement" })
        {
            JsonObject outcome = result[kind]!.AsObject();
            Assert.False(outcome["clear"]!.GetValue<bool>(), kind);
            JsonObject hit = outcome["hit"]!.AsObject();
            double[] point = Point(hit["point"]);
            Assert.Equal(-9300, point[0], 3);
            Assert.Equal(103, point[1], 3);
            Assert.Equal(48, point[2], 3);
            Assert.Equal(0.5, hit["t"]!.GetValue<double>(), 6);
            Assert.Equal("wmo", hit["kind"]!.GetValue<string>());
            Assert.Equal("collide", hit["class"]!.GetValue<string>());
            Assert.Equal(1u, hit["uniqueId"]!.GetValue<uint>());
            Assert.Equal(0, hit["group"]!.GetValue<int>());
            Assert.Equal(ModelWorld.WallPath, hit["model"]!.GetValue<string>());
        }

        Assert.Equal(20.0, result["length"]!.GetValue<double>(), 6);
        JsonArray tiles = result["tiles"]!.AsArray();
        Assert.Single(tiles);
        Assert.Equal("Ok", tiles[0]!["state"]!.GetValue<string>());
        JsonObject overlay = result["overlay"]!.AsObject();
        Assert.Equal(3, overlay["points"]!.AsArray().Count);
        Assert.Equal(2, overlay["segments"]!.AsArray().Count);
        Assert.True(overlay["segments"]![1]!["dashed"]!.GetValue<bool>());
    }

    [Fact]
    public void ASegmentThatMissesTheWall_IsClearAndDrawsOneSegment()
    {
        ModelWorld world = WallWorld();

        JsonObject result = world.Queries.LineOfSight(0, new WorldPoint(-9290, 103, 60), new WorldPoint(-9310, 103, 60), false, CancellationToken.None);

        Assert.True(result["spell"]!["clear"]!.GetValue<bool>());
        Assert.True(result["movement"]!["clear"]!.GetValue<bool>());
        Assert.Null(result["spell"]!["hit"]);
        Assert.Single(result["overlay"]!["segments"]!.AsArray());
    }

    [Fact]
    public void AnM2Hull_BlocksMovementButNotSpells()
    {
        var world = new ModelWorld();
        world.AddCube();
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.M2(4, CubeSpot, ModelWorld.Goldshire));

        JsonObject result = world.Queries.LineOfSight(0, new WorldPoint(-9240, 100.3, 50.2), new WorldPoint(-9260, 100.3, 50.2), false, CancellationToken.None);

        Assert.True(result["spell"]!["clear"]!.GetValue<bool>());
        JsonObject hit = result["movement"]!["hit"]!.AsObject();
        Assert.Equal("m2", hit["kind"]!.GetValue<string>());
        Assert.Equal("mddf", hit["source"]!.GetValue<string>());
        Assert.Equal(-9249, Point(hit["point"])[0], 3);
        Assert.Equal(0.45, hit["t"]!.GetValue<double>(), 6);
    }

    [Fact]
    public void AnM2WithoutAHull_NeverBlocks()
    {
        var world = new ModelWorld();
        world.AddCube(withHull: false);
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.M2(4, CubeSpot, ModelWorld.Goldshire));

        JsonObject result = world.Queries.LineOfSight(0, new WorldPoint(-9240, 100.3, 50.2), new WorldPoint(-9260, 100.3, 50.2), true, CancellationToken.None);

        Assert.True(result["movement"]!["clear"]!.GetValue<bool>());
    }

    [Fact]
    public void ExtractorDroppedGeometry_IsIgnoredUnlessAsked()
    {
        ModelWorld destructible = WallWorld(wmoFlags: 1);
        ModelWorld unreachable = WallWorld(groupFlags: 0x80);
        var a = new WorldPoint(-9290, 103, 48);
        var b = new WorldPoint(-9310, 103, 48);

        foreach (ModelWorld world in new[] { destructible, unreachable })
        {
            JsonObject ignored = world.Queries.LineOfSight(0, a, b, false, CancellationToken.None);
            JsonObject included = world.Queries.LineOfSight(0, a, b, true, CancellationToken.None);

            Assert.True(ignored["spell"]!["clear"]!.GetValue<bool>());
            Assert.False(ignored["extractorSkippedIncluded"]!.GetValue<bool>());
            Assert.False(included["spell"]!["clear"]!.GetValue<bool>());
            Assert.True(included["extractorSkippedIncluded"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void ARenderOnlyTriangle_StillBlocksAndIsLabelled()
    {
        var world = new ModelWorld();
        world.AddWall(flags: 0x04);
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, WallSpot, ModelWorld.Goldshire));

        JsonObject result = world.Queries.LineOfSight(0, new WorldPoint(-9290, 103, 48), new WorldPoint(-9310, 103, 48), false, CancellationToken.None);

        Assert.Equal("render-only", result["spell"]!["hit"]!["class"]!.GetValue<string>());
        Assert.Equal(4, result["spell"]!["hit"]!["mopyFlags"]!.GetValue<int>());
    }

    [Fact]
    public void ASegmentCrossingTwoTiles_ReadsBothTiles_AndFindsTheWallInTheSecond()
    {
        var world = new ModelWorld();
        world.AddWall();
        var wallSpot = new WorldPoint(-9050, 100, 50);
        world.Placements.Add(ModelWorld.Neighbour, ModelWorld.Wmo(2, wallSpot, ModelWorld.Neighbour));

        JsonObject result = world.Queries.LineOfSight(0, new WorldPoint(-9100, 103, 48), new WorldPoint(-9000, 103, 48), false, CancellationToken.None);

        JsonArray tiles = result["tiles"]!.AsArray();
        Assert.Equal(2, tiles.Count);
        Assert.Equal(49, tiles[0]!["tile"]![0]!.GetValue<int>());
        Assert.Equal("Missing", tiles[0]!["state"]!.GetValue<string>());
        Assert.Equal(48, tiles[1]!["tile"]![0]!.GetValue<int>());
        Assert.Equal("Ok", tiles[1]!["state"]!.GetValue<string>());
        Assert.Equal(-9050, Point(result["movement"]!["hit"]!["point"])[0], 3);
    }

    [Fact]
    public void ARejectedTile_IsReportedButDoesNotStopTheQuery()
    {
        ModelWorld world = WallWorld();
        world.Placements.Corrupt[ModelWorld.Neighbour] = "MODF is truncated";

        JsonObject result = world.Queries.LineOfSight(0, new WorldPoint(-9100, 103, 48), new WorldPoint(-9000, 103, 48), false, CancellationToken.None);

        string state = result["tiles"]![1]!["state"]!.GetValue<string>();
        Assert.StartsWith("Rejected", state, StringComparison.Ordinal);
        Assert.Contains("MODF is truncated", state, StringComparison.Ordinal);
    }

    [Fact]
    public void BadArguments_AreRefusedWithBadArgs()
    {
        ModelWorld world = WallWorld();
        var inside = new WorldPoint(-9300, 100, 50);

        Assert.Equal(CommandException.BadArguments, Assert.Throws<CommandException>(
            () => world.Queries.LineOfSight(0, new WorldPoint(double.NaN, 0, 0), inside, false, CancellationToken.None)).Code);
        Assert.Equal(CommandException.BadArguments, Assert.Throws<CommandException>(
            () => world.Queries.LineOfSight(0, inside, new WorldPoint(1e9, 0, 0), false, CancellationToken.None)).Code);
        CommandException many = Assert.Throws<CommandException>(
            () => world.Queries.LineOfSight(0, new WorldPoint(17000, 17000, 0), new WorldPoint(-17000, -17000, 0), false, CancellationToken.None));
        Assert.Equal(CommandException.BadArguments, many.Code);
        Assert.Contains("limit 64", many.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutServices_TheQueryIsUnavailable()
    {
        var a = new WorldPoint(-9290, 103, 48);
        var b = new WorldPoint(-9310, 103, 48);

        CommandException noFiles = Assert.Throws<CommandException>(
            () => new ModelWorld(withFiles: false).Queries.LineOfSight(0, a, b, false, CancellationToken.None));
        CommandException noPlacements = Assert.Throws<CommandException>(
            () => new ModelWorld(withPlacements: false).Queries.LineOfSight(0, a, b, false, CancellationToken.None));

        Assert.Equal(CommandException.Unavailable, noFiles.Code);
        Assert.Equal(CommandException.Unavailable, noPlacements.Code);
    }

    [Fact]
    public void ACancelledQuery_Throws()
    {
        ModelWorld world = WallWorld();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => world.Queries.LineOfSight(0, new WorldPoint(-9290, 103, 48), new WorldPoint(-9310, 103, 48), false, cts.Token));
    }

    [Fact]
    public void SegmentTriangle_HitsInsideEdgesAndEndpoints_AndNothingOutside()
    {
        var v0 = new Vec3d(0, 0, 0);
        var v1 = new Vec3d(4, 0, 0);
        var v2 = new Vec3d(0, 4, 0);

        Assert.Equal(0.5, ReferenceModelQueries.SegmentTriangle(new Vec3d(1, 1, -1), new Vec3d(1, 1, 1), v0, v1, v2)!.Value, 9);
        Assert.Equal(0.5, ReferenceModelQueries.SegmentTriangle(new Vec3d(1, 1, 1), new Vec3d(1, 1, -1), v0, v1, v2)!.Value, 9);
        Assert.Equal(1.0, ReferenceModelQueries.SegmentTriangle(new Vec3d(1, 1, 1), new Vec3d(1, 1, 0), v0, v1, v2)!.Value, 9);
        Assert.Null(ReferenceModelQueries.SegmentTriangle(new Vec3d(3, 3, -1), new Vec3d(3, 3, 1), v0, v1, v2));
        Assert.Null(ReferenceModelQueries.SegmentTriangle(new Vec3d(1, 1, 1), new Vec3d(1, 1, 0.5), v0, v1, v2));
        Assert.Null(ReferenceModelQueries.SegmentTriangle(new Vec3d(-1, 0, 0), new Vec3d(5, 0, 0), v0, v1, v2));
    }

    [Fact]
    public void TilesCrossed_ListsEveryTileTheSegmentTouches_InOrder()
    {
        Assert.Equal(
            new[] { new TileKey(0, 49, 31) },
            ReferenceModelQueries.TilesCrossed(0, new WorldPoint(-9300, 100, 0), new WorldPoint(-9200, 120, 0)));
        Assert.Equal(
            new[] { new TileKey(0, 49, 31), new TileKey(0, 48, 31) },
            ReferenceModelQueries.TilesCrossed(0, new WorldPoint(-9100, 100, 0), new WorldPoint(-9000, 100, 0)));
        Assert.Equal(
            new[] { new TileKey(0, 49, 31), new TileKey(0, 49, 32) },
            ReferenceModelQueries.TilesCrossed(0, new WorldPoint(-9300, 100, 0), new WorldPoint(-9300, -100, 0)));
    }

    // The grid cell in double precision (the server's TileOf rounds through float, which can differ exactly on a boundary).
    private static TileKey CellOf(double x, double y)
        => new(0, (int)Math.Floor(32 - (x / WorldGrid.TileSize)), (int)Math.Floor(32 - (y / WorldGrid.TileSize)));

    [Fact]
    public void TilesCrossed_CoversEverySampledPoint_AndStepsOneTileAtATime()
    {
        var random = new SplitMix(5875);
        for (int i = 0; i < 300; i++)
        {
            var a = new WorldPoint(random.Range(-13000, 13000), random.Range(-13000, 13000), 0);
            var b = new WorldPoint(a.X + random.Range(-3000, 3000), a.Y + random.Range(-3000, 3000), 0);

            IReadOnlyList<TileKey> tiles = ReferenceModelQueries.TilesCrossed(0, a, b);

            Assert.Equal(CellOf(a.X, a.Y), tiles[0]);
            Assert.Equal(CellOf(b.X, b.Y), tiles[^1]);
            for (int step = 1; step < tiles.Count; step++)
            {
                int distance = Math.Abs(tiles[step].X - tiles[step - 1].X) + Math.Abs(tiles[step].Y - tiles[step - 1].Y);
                Assert.Equal(1, distance);
            }

            for (int sample = 0; sample <= 200; sample++)
            {
                double t = sample / 200.0;
                TileKey at = CellOf(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
                Assert.True(tiles.Contains(at), $"segment {i} sample {sample}: {at} is not in the crossed list");
            }
        }
    }
}

public sealed class ModelModuleTests
{
    private sealed class OtherLaneModule : IAtlasModule
    {
        public string Name => "Other.Lane";

        public int Order => ModuleOrder.Foundation;

        public void Register(AtlasContext context)
            => context.Commands.Register(new CommandDefinition("ref.query.los", "Another lane's line of sight.", [], (_, _) => Task.FromResult<JsonNode?>("other")));
    }

    private static AtlasContext Compose(params IAtlasModule[] modules)
        => AtlasModules.Compose(new AtlasPaths(null, null), NullLoggerFactory.Instance, headless: true, modules);

    [Fact]
    public void TheModule_IsDiscovered_AndRegistersItsLayersCommandsAndServices()
    {
        IReadOnlyList<IAtlasModule> modules = AtlasModules.Discover(typeof(ClientModelsModule).Assembly);
        Assert.Single(modules, m => m is ClientModelsModule);
        Assert.Equal(modules.Count, modules.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count());

        using AtlasContext context = Compose(new ClientModelsModule());

        Assert.Empty(context.Problems);
        Assert.Equal(new[] { LayerIds.RefWmo, LayerIds.RefM2Collision, LayerIds.RefM2Render }, context.Layers.Available);
        Assert.True(context.Commands.TryGet("ref.model.info", out CommandDefinition info));
        Assert.True(context.Commands.TryGet("ref.query.los", out CommandDefinition los));
        Assert.Equal("path", Assert.Single(info.Args).Name);
        Assert.Equal(new[] { "map", "a", "b", "includeSkipped" }, los.Args.Select(a => a.Name));
        Assert.NotNull(context.Services.Get<ClientModelCache>());
        Assert.NotNull(context.Services.Get<ReferenceModelQueries>());
    }

    [Fact]
    public void WhenAnotherLaneOwnsACommandName_TheModuleKeepsItsLayersAndOtherCommand_AndReportsTheProblem()
    {
        using AtlasContext context = Compose(new OtherLaneModule(), new ClientModelsModule());

        Assert.Contains(context.Problems, p => p.Contains("ref.query.los", StringComparison.Ordinal));
        Assert.True(context.Commands.TryGet("ref.model.info", out _));
        Assert.True(context.Commands.TryGet("ref.query.los", out CommandDefinition los));
        Assert.Empty(los.Args);
        Assert.Equal(3, context.Layers.Available.Count);
    }

    [Fact]
    public async Task TheCommands_RunThroughTheRegistry_WithTypedArguments()
    {
        using AtlasContext context = Compose(new ClientModelsModule());
        var world = new ModelWorld();
        world.AddWall();
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, new WorldPoint(-9300, 100, 50), ModelWorld.Goldshire));
        context.Services.Add<IClientFileSource>(world.Files);
        context.Services.Add<IClientPlacementSource>(world.Placements);

        CommandResult json = await context.Commands.InvokeAsync(
            "ref.query.los",
            JsonNode.Parse("""{"map":0,"a":[-9290,103,48],"b":[-9310,103,48]}""")!.AsObject(),
            context.Services,
            CommandOrigin.Script,
            headless: true,
            CancellationToken.None);
        CommandResult cli = await context.Commands.InvokeAsync(
            "ref.query.los",
            JsonNode.Parse("""{"map":"0","a":"-9290,103,60","b":"-9310,103,60","includeSkipped":"true"}""")!.AsObject(),
            context.Services,
            CommandOrigin.Cli,
            headless: true,
            CancellationToken.None);
        CommandResult info = await context.Commands.InvokeAsync(
            "ref.model.info",
            new JsonObject { ["path"] = ModelWorld.WallPath },
            context.Services,
            CommandOrigin.Script,
            headless: true,
            CancellationToken.None);
        CommandResult missingArg = await context.Commands.InvokeAsync(
            "ref.query.los",
            new JsonObject { ["map"] = 0 },
            context.Services,
            CommandOrigin.Script,
            headless: true,
            CancellationToken.None);

        Assert.True(json.Ok, json.ErrorMessage);
        Assert.False(json.Data!["movement"]!["clear"]!.GetValue<bool>());
        Assert.True(cli.Ok, cli.ErrorMessage);
        Assert.True(cli.Data!["movement"]!["clear"]!.GetValue<bool>());
        Assert.True(cli.Data["extractorSkippedIncluded"]!.GetValue<bool>());
        Assert.True(info.Ok, info.ErrorMessage);
        Assert.Equal("wmo", info.Data!["kind"]!.GetValue<string>());
        Assert.False(missingArg.Ok);
        Assert.Equal(CommandException.BadArguments, missingArg.ErrorCode);
    }

    [Fact]
    public async Task WithoutClientFiles_TheCommandsAnswerUnavailable_NotFailed()
    {
        using AtlasContext context = Compose(new ClientModelsModule());

        CommandResult info = await context.Commands.InvokeAsync(
            "ref.model.info",
            new JsonObject { ["path"] = ModelWorld.WallPath },
            context.Services,
            CommandOrigin.Script,
            headless: true,
            CancellationToken.None);
        CommandResult los = await context.Commands.InvokeAsync(
            "ref.query.los",
            JsonNode.Parse("""{"map":0,"a":[1,2,3],"b":[4,5,6]}""")!.AsObject(),
            context.Services,
            CommandOrigin.Script,
            headless: true,
            CancellationToken.None);

        Assert.Equal(CommandException.Unavailable, info.ErrorCode);
        Assert.Equal(CommandException.Unavailable, los.ErrorCode);
    }

    [Fact]
    public void TheLayers_ReportMissing_UntilTheClientServicesAppear()
    {
        using AtlasContext context = Compose(new ClientModelsModule());

        TileStatus before = context.Cache.Peek(LayerIds.RefWmo, ModelWorld.Goldshire);
        var world = new ModelWorld();
        world.AddWall();
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, new WorldPoint(-9300, 100, 50), ModelWorld.Goldshire));
        context.Services.Add<IClientFileSource>(world.Files);
        context.Services.Add<IClientPlacementSource>(world.Placements);
        LayerBuild after = context.Cache.Get(LayerIds.RefWmo, ModelWorld.Goldshire, CancellationToken.None);

        Assert.Equal(TileState.Missing, before.State);
        Assert.Equal(TileState.Ok, after.Status.State);
    }
}
