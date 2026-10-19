using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Diff;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Reference.Mpq;
using ArcaneCore.Atlas.Reference.Navmesh;
using ArcaneCore.Atlas.Server;
using ArcaneCore.Atlas.Tests.Reference.Client.Models;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Navmesh;
using ArcaneCore.Atlas.Tests.Server;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using DotRecast.Detour;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Fuzz;

/// <summary>
/// Exhaustive exception contract and deterministic seeded fuzz tests across <c>Core</c>, <c>Server</c>,
/// <c>Diff</c>, and <c>Reference</c> (<c>Mpq</c>, <c>Dbc</c>, <c>Client.Terrain</c>, <c>Client.Models</c>, <c>Navmesh</c>).
/// </summary>
public sealed class AtlasCoreServerDiffExceptionTests
{
    public static TheoryData<int> Seeds => [1, 42, 20251004];

    // -------------------------------------------------------------------------
    // Core: WorldGrid, CameraMath, GeometryBuilder, GeometryChunk, GeometryWire
    // -------------------------------------------------------------------------

    [Fact]
    public void WorldGrid_And_CameraMath_Guards_ThrowExpectedExceptions()
    {
        const double halfExtent = 32 * WorldGrid.TileSize;
        var zeroPoint = new WorldPoint(0, 0, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldGrid.Around(0u, 0.0, 0.0, -1).ToList());
        Assert.Null(WorldGrid.TileOf(0, double.NaN, 0));
        Assert.Null(WorldGrid.TileOf(0, 0, double.PositiveInfinity));
        Assert.Null(WorldGrid.TileOf(0, halfExtent + WorldGrid.TileSize + 1.0, 0));
        Assert.Null(WorldGrid.TileOf(0, 0, -halfExtent - 1.0));
        Assert.False(new WorldPoint(double.NaN, 0, 0).IsFinite);
        Assert.False(new WorldPoint(0, double.PositiveInfinity, 0).IsFinite);
        Assert.False(new WorldPoint(0, 0, double.NegativeInfinity).IsFinite);

        Assert.Throws<ArgumentNullException>(() => CameraMath.Direction(null!));
        Assert.Throws<ArgumentNullException>(() => CameraMath.Eye(null!));
        Assert.Throws<ArgumentNullException>(() => CameraMath.Up(null!));
        Assert.Throws<ArgumentNullException>(() => CameraMath.Planes(null!));
        Assert.Throws<ArgumentNullException>(() => CameraMath.View(null!));
        Assert.Throws<ArgumentNullException>(() => CameraMath.Projection(null!, 1.0));
        Assert.Throws<ArgumentNullException>(() => CameraMath.RayThrough(null!, 0, 0, 0, 1.0));
        Assert.Throws<ArgumentNullException>(() => CameraMath.Project(null!, zeroPoint, 1.0));

        CameraState cam = ViewState.Default.Camera;
        foreach (double badAspect in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraMath.Projection(cam, badAspect));
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraMath.Project(cam, zeroPoint, badAspect));
        }
    }

    [Fact]
    public void GeometryBuilder_GeometryChunk_And_GeometryWire_Guards_ThrowExpectedExceptions()
    {
        var tile = new TileKey(0, 32, 32);
        var origin = WorldGrid.Origin(tile);

        var builder = new GeometryBuilder(LayerIds.Terrain, tile, origin);
        Assert.Throws<ArgumentException>(() => builder.AddVertex(double.NaN, 0, 0));
        Assert.Throws<ArgumentException>(() => builder.AddVertex(0, double.PositiveInfinity, 0));
        Assert.Throws<ArgumentException>(() => builder.AddVertex(0, 0, double.NegativeInfinity));

        Assert.Throws<ArgumentNullException>(() => GeometryWire.Write(null!));

        // Direct GeometryChunk.Validate failure paths
        var badOriginChunk = new GeometryChunk
        {
            LayerId = LayerIds.Terrain,
            Tile = tile,
            Origin = new WorldPoint(double.NaN, 0, 0),
            Positions = [],
        };
        Assert.Throws<InvalidDataException>(badOriginChunk.Validate);

        var badPosLengthChunk = new GeometryChunk
        {
            LayerId = LayerIds.Terrain,
            Tile = tile,
            Origin = origin,
            Positions = [1f, 2f],
        };
        Assert.Throws<InvalidDataException>(badPosLengthChunk.Validate);

        var badTriIndicesChunk = new GeometryChunk
        {
            LayerId = LayerIds.Terrain,
            Tile = tile,
            Origin = origin,
            Positions = [0f, 0f, 0f],
            Triangles = [0u, 0u],
        };
        Assert.Throws<InvalidDataException>(badTriIndicesChunk.Validate);

        var badTriAttrChunk = new GeometryChunk
        {
            LayerId = LayerIds.Terrain,
            Tile = tile,
            Origin = origin,
            Positions = [0f, 0f, 0f],
            Triangles = [0u, 0u, 0u],
        };
        Assert.Throws<InvalidDataException>(badTriAttrChunk.Validate);

        var badLineIndicesChunk = new GeometryChunk
        {
            LayerId = LayerIds.Terrain,
            Tile = tile,
            Origin = origin,
            Positions = [0f, 0f, 0f],
            Lines = [0u],
        };
        Assert.Throws<InvalidDataException>(badLineIndicesChunk.Validate);

        var badLineAttrChunk = new GeometryChunk
        {
            LayerId = LayerIds.Terrain,
            Tile = tile,
            Origin = origin,
            Positions = [0f, 0f, 0f],
            Lines = [0u, 0u],
        };
        Assert.Throws<InvalidDataException>(badLineAttrChunk.Validate);

        var badPointAttrChunk = new GeometryChunk
        {
            LayerId = LayerIds.Terrain,
            Tile = tile,
            Origin = origin,
            Positions = [0f, 0f, 0f],
            Points = [0u],
        };
        Assert.Throws<InvalidDataException>(badPointAttrChunk.Validate);

        var outOfRangeTriVertexChunk = new GeometryChunk
        {
            LayerId = LayerIds.Terrain,
            Tile = tile,
            Origin = origin,
            Positions = [0f, 0f, 0f],
            Triangles = [0u, 0u, 5u],
            TriangleAttributes = [1u],
        };
        Assert.Throws<InvalidDataException>(outOfRangeTriVertexChunk.Validate);

        var outOfRangeLineVertexChunk = new GeometryChunk
        {
            LayerId = LayerIds.Terrain,
            Tile = tile,
            Origin = origin,
            Positions = [0f, 0f, 0f],
            Lines = [0u, 5u],
            LineAttributes = [1u],
        };
        Assert.Throws<InvalidDataException>(outOfRangeLineVertexChunk.Validate);

        var outOfRangePointVertexChunk = new GeometryChunk
        {
            LayerId = LayerIds.Terrain,
            Tile = tile,
            Origin = origin,
            Positions = [0f, 0f, 0f],
            Points = [5u],
            PointAttributes = [1u],
        };
        Assert.Throws<InvalidDataException>(outOfRangePointVertexChunk.Validate);
    }

    // -------------------------------------------------------------------------
    // Core: LayerCatalog, ViewState, ViewStore, ClientFiles, Modules, Services
    // -------------------------------------------------------------------------

    [Fact]
    public void LayerCatalog_ViewState_ViewStore_And_ClientFiles_Guards_ThrowExpectedExceptions()
    {
        Assert.Throws<KeyNotFoundException>(() => LayerCatalog.Get("no.such.layer"));
        Assert.False(LayerCatalog.TryGet("no.such.layer", out _));
        Assert.Throws<ArgumentNullException>(() => LayerStyle.For(null!));

        Assert.Throws<ArgumentNullException>(() => ViewState.FromJson(null!));
        ViewState view = ViewState.Default;
        Assert.Throws<ArgumentNullException>(() => view.Apply(null!));

        string[] invalidViewPatches =
        [
            """{"map":-1}""",
            """{"layers":123}""",
            """{"layers":{"unknown.layer":{"visible":true}}}""",
            """{"layers":{"terrain":123}}""",
            """{"layers":{"terrain":{"opacity":-0.1}}}""",
            """{"layers":{"terrain":{"opacity":1.1}}}""",
            """{"layers":{"terrain":{"color":"not-hex"}}}""",
            """{"camera":123}""",
            """{"camera":{"mode":"warp"}}""",
            """{"camera":{"target":[1,2]}}""",
            """{"camera":{"distance":0}}""",
            """{"camera":{"distance":-5}}""",
            """{"camera":{"pitch":90.0}}""",
            """{"camera":{"pitch":-90.0}}""",
            """{"camera":{"fov":4.9}}""",
            """{"camera":{"fov":150.1}}""",
            """{"compare":"invalid"}""",
            """{"split":0.01}""",
            """{"loadRadius":99}""",
            """{"loadRadius":1.5}""",
            """{"theme":"neon"}""",
            """{"follow":"not_hex_guid!"}""",
        ];

        foreach (string raw in invalidViewPatches)
        {
            var patch = JsonNode.Parse(raw)!.AsObject();
            CommandException ex = Assert.Throws<CommandException>(() => ViewState.Default.Apply(patch));
            Assert.Equal(CommandException.BadArguments, ex.Code);
        }

        Assert.Throws<ArgumentNullException>(() => new ViewStore(null!));
        var store = new ViewStore(ViewState.Default);
        Assert.Throws<ArgumentNullException>(() => store.Set(null!, "test"));
        Assert.Throws<ArgumentNullException>(() => store.Patch(null!, "test"));

        Assert.Throws<ArgumentNullException>(() => ClientPath.Key(null!));
        var memFiles = new MemoryClientFiles();
        Assert.Throws<ArgumentNullException>(() => memFiles.Add(null!, []));
        Assert.Throws<ArgumentNullException>(() => memFiles.Add("DBFilesClient\\Map.dbc", null!));

        Assert.Throws<ArgumentException>(() => new DirectoryClientFiles(""));
        Assert.Throws<ArgumentException>(() => new DirectoryClientFiles("   "));
        Assert.Throws<DirectoryNotFoundException>(() => new DirectoryClientFiles(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        using var tempRoot = new TempTerrainRoot();
        var dirFiles = new DirectoryClientFiles(tempRoot.Root);
        Assert.False(dirFiles.Exists(""));
        Assert.False(dirFiles.Exists("../outside.dbc"));
        Assert.False(dirFiles.Exists(Path.GetFullPath(tempRoot.Root)));
        Assert.Null(dirFiles.Read("../outside.dbc"));
    }

    [Fact]
    public void Modules_Services_EventHub_And_LayerCache_Guards_ThrowExpectedExceptions()
    {
        var paths = new AtlasPaths(null, null);
        Assert.Throws<ArgumentNullException>(() => new AtlasContext(null!, NullLoggerFactory.Instance, headless: true));
        Assert.Throws<ArgumentNullException>(() => new AtlasContext(paths, null!, headless: true));

        var context = new AtlasContext(paths, NullLoggerFactory.Instance, headless: true);
        Assert.Throws<ArgumentException>(() => context.ReportProblem(""));
        Assert.Throws<ArgumentException>(() => context.ReportProblem("   "));

        Assert.Throws<ArgumentNullException>(() => context.Layers.Add(null!));
        Assert.Throws<ArgumentException>(() => context.Layers.Add(new StubLayerSource(["not.in.catalog"])));

        var terrainSource = new StubLayerSource([LayerIds.Terrain]);
        context.Layers.Add(terrainSource);
        Assert.Throws<ArgumentException>(() => context.Layers.Add(new StubLayerSource([LayerIds.Terrain])));
        context.Layers.Freeze();
        Assert.Throws<InvalidOperationException>(() => context.Layers.Add(new StubLayerSource([LayerIds.Tiles])));

        Assert.Throws<ArgumentNullException>(() => context.Services.Add<string>(null!));
        context.Services.Add("hello");
        Assert.Throws<ArgumentException>(() => context.Services.Add("duplicate"));
        Assert.Throws<InvalidOperationException>(() => context.Services.Require<Uri>());

        Assert.Throws<ArgumentNullException>(() => AtlasModules.Discover(null!));
        Assert.Throws<ArgumentNullException>(() => AtlasModules.Compose(paths, NullLoggerFactory.Instance, true, (IEnumerable<IAtlasModule>)null!));

        AtlasContext badModuleCtx = AtlasModules.Compose(paths, NullLoggerFactory.Instance, true, [new ThrowingModule()]);
        Assert.Contains(badModuleCtx.Problems, p => p.Contains("throwing.module", StringComparison.Ordinal));

        var hub = new EventHub();
        Assert.Throws<ArgumentNullException>(() => hub.Subscribe(null!));
        Assert.Throws<ArgumentException>(() => hub.Publish("", new JsonObject()));
        Assert.Throws<ArgumentException>(() => hub.Publish("   ", new JsonObject()));

        var cache = new LayerCache(context.Layers, maxBytes: 1024);
        var validTile = new TileKey(0, 32, 32);
        var invalidTile = new TileKey(0, -5, 99);
        Assert.Throws<KeyNotFoundException>(() => cache.Get("unknown.layer", validTile, CancellationToken.None));
        Assert.Throws<KeyNotFoundException>(() => cache.GetWire("unknown.layer", validTile, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => cache.Get(LayerIds.Terrain, invalidTile, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => cache.GetWire(LayerIds.Terrain, invalidTile, CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // Core.Commands: CommandInvocation, CommandRegistry, CommandSchema
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CommandInvocation_CommandRegistry_And_CommandSchema_Guards_ThrowExpectedExceptions()
    {
        var services = new AtlasServices();
        var dummyDef = new CommandDefinition("test.cmd", "summary", [], (_, _) => Task.FromResult<JsonNode?>(new JsonObject()));
        var args = new JsonObject
        {
            ["str"] = "abc",
            ["int"] = 42,
            ["floatInt"] = 42.5,
            ["num"] = 3.5,
            ["bool"] = true,
            ["arr"] = new JsonArray(1.0, 2.0, 3.0),
            ["shortArr"] = new JsonArray(1.0, 2.0),
            ["badArr"] = new JsonArray("a", "b", "c"),
            ["strList"] = new JsonArray("one", "two"),
            ["badStrList"] = new JsonArray("one", 2),
            ["obj"] = new JsonObject { ["x"] = 1.0, ["y"] = 2.0, ["z"] = 3.0 },
            ["negMap"] = -1,
        };

        var inv = new CommandInvocation(dummyDef, args, services, CommandOrigin.Cli, headless: true);
        CommandException missingSvc = Assert.Throws<CommandException>(() => inv.Service<Uri>());
        Assert.Equal(CommandException.Unavailable, missingSvc.Code);

        Assert.Throws<CommandException>(() => inv.GetString("missing"));
        Assert.Throws<CommandException>(() => inv.GetString("int"));
        Assert.Throws<CommandException>(() => inv.OptionalString("int"));

        Assert.Throws<CommandException>(() => inv.GetInt("missing"));
        Assert.Throws<CommandException>(() => inv.GetInt("str"));
        Assert.Throws<CommandException>(() => inv.GetInt("floatInt"));

        Assert.Throws<CommandException>(() => inv.GetNumber("missing"));
        Assert.Throws<CommandException>(() => inv.GetNumber("str"));

        Assert.Throws<CommandException>(() => inv.OptionalBool("str"));

        Assert.Equal(new WorldPoint(1, 2, 3), inv.GetPoint("arr"));
        Assert.Throws<CommandException>(() => inv.GetPoint("missing"));
        Assert.Throws<CommandException>(() => inv.GetPoint("shortArr"));
        Assert.Throws<CommandException>(() => inv.GetPoint("badArr"));
        Assert.Throws<CommandException>(() => inv.GetPoint("str"));

        Assert.Equal(["one", "two"], inv.GetStringList("strList"));
        Assert.Throws<CommandException>(() => inv.GetStringList("int"));
        Assert.Throws<CommandException>(() => inv.GetStringList("badStrList"));

        Assert.NotNull(inv.OptionalObject("obj"));
        Assert.Throws<CommandException>(() => inv.OptionalObject("str"));
        Assert.Throws<CommandException>(() => inv.GetMapId("negMap"));

        var reg = new CommandRegistry();
        Assert.Throws<ArgumentNullException>(() => reg.Register((CommandDefinition)null!));
        Assert.Throws<ArgumentNullException>(() => reg.Register((ICommandModule)null!));
        Assert.Throws<ArgumentException>(() => reg.Register(new CommandDefinition("", "s", [], (_, _) => Task.FromResult<JsonNode?>(new JsonObject()))));
        var validDef = new CommandDefinition(
            "ui.only",
            "UI command",
            [new CommandArg("a", ArgType.String, "desc", Required: true)],
            (_, _) => Task.FromResult<JsonNode?>(new JsonObject()),
            Surfaces: CommandSurfaces.Instance);
        reg.Register(validDef);
        Assert.Throws<ArgumentException>(() => reg.Register(validDef));

        var throwDef = new CommandDefinition(
            "boom",
            "Throws InvalidOperationException",
            [],
            (_, _) => throw new InvalidOperationException("unexpected failure"));
        reg.Register(throwDef);
        reg.Freeze();
        Assert.Throws<InvalidOperationException>(() => reg.Register(new CommandDefinition("other", "s", [], (_, _) => Task.FromResult<JsonNode?>(new JsonObject()))));

        await Assert.ThrowsAsync<ArgumentNullException>(() => reg.InvokeAsync("ui.only", null, null!, CommandOrigin.Cli, true, CancellationToken.None));
        CommandResult emptyCmd = await reg.InvokeAsync("  ", null, services, CommandOrigin.Cli, true, CancellationToken.None);
        Assert.Equal(CommandException.UnknownCommand, emptyCmd.ErrorCode);

        CommandResult missingRequiredArg = await reg.InvokeAsync("ui.only", null, services, CommandOrigin.Ui, false, CancellationToken.None);
        Assert.Equal(CommandException.BadArguments, missingRequiredArg.ErrorCode);

        CommandResult unknownCmd = await reg.InvokeAsync("nope", null, services, CommandOrigin.Cli, true, CancellationToken.None);
        Assert.Equal(CommandException.UnknownCommand, unknownCmd.ErrorCode);

        CommandResult headlessBlocked = await reg.InvokeAsync("ui.only", null, services, CommandOrigin.Ui, true, CancellationToken.None);
        Assert.Equal(CommandException.Unavailable, headlessBlocked.ErrorCode);

        CommandResult failedCmd = await reg.InvokeAsync("boom", null, services, CommandOrigin.Cli, true, CancellationToken.None);
        Assert.Equal(CommandException.Failed, failedCmd.ErrorCode);

        Assert.Throws<ArgumentNullException>(() => CommandSchema.InputSchema(null!));
        Assert.Throws<ArgumentNullException>(() => CommandSchema.Help(null!));
        var badArgDef = new CommandDefinition("bad.arg", "s", [new CommandArg("x", (ArgType)999, "d")], (_, _) => Task.FromResult<JsonNode?>(new JsonObject()));
        Assert.Throws<ArgumentOutOfRangeException>(() => CommandSchema.InputSchema(badArgDef));
    }

    // -------------------------------------------------------------------------
    // Server: OwnerThread, ParseCache, ServerData, ServerEngine, ServerOverlay,
    //         ReportJson, ServerLayerSource, ServerCommands
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Server_Subsystems_Guards_ThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentException>(() => new OwnerThread(""));
        Assert.Throws<ArgumentException>(() => new OwnerThread("   "));

        var owner = new OwnerThread("test-owner");
        await Assert.ThrowsAsync<ArgumentNullException>(() => owner.RunAsync<int>(null!, CancellationToken.None));
        owner.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.RunAsync(() => 1, CancellationToken.None));

        var parseCache = new ParseCache<string, byte[]>(maxCost: 10);
        Assert.Throws<ArgumentNullException>(() => parseCache.GetOrLoad("k", null!));
        Assert.Throws<InvalidDataException>(() => parseCache.GetOrLoad("k", () => throw new InvalidDataException("corrupt")));
        Assert.Equal(0, parseCache.Stats.Entries);
        parseCache.GetOrLoad("a", () => (ParsedFile<byte[]>.Ok(new byte[6], "a"), 6L));
        parseCache.GetOrLoad("b", () => (ParsedFile<byte[]>.Ok(new byte[6], "b"), 6L));
        Assert.Equal(1, parseCache.Stats.Entries);
        parseCache.Clear();
        Assert.Equal(0, parseCache.Stats.Entries);

        Assert.Throws<ArgumentNullException>(() => new VMapTileData(null!));
        Assert.Throws<ArgumentNullException>(() => new ServerData(null!));

        using var root = new TempTerrainRoot();
        var serverData = new ServerData(root.Paths);
        Assert.Throws<ArgumentNullException>(() => serverData.ModelFile(null!));
        foreach (string unsafeModel in new[] { "", "..", "a/b.vmo", "a\\b.vmo", "C:b.vmo" })
        {
            Assert.Equal(TileState.Rejected, serverData.ModelFile(unsafeModel).State);
        }

        Assert.Throws<ArgumentNullException>(() => new ServerEngine(null!, NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>(() => new ServerEngine(serverData, null!));

        using var engine = new ServerEngine(serverData, NullLoggerFactory.Instance);
        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.QueryPathAsync(0, Vector3.Zero, Vector3.Zero, default, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.RunOnOwnerAsync<int>(null!, CancellationToken.None));

        await Assert.ThrowsAsync<ServerEngineException>(() => engine.QueryHeightAsync(0, new Vector3(float.NaN, 0, 0), 50f, CancellationToken.None));
        await Assert.ThrowsAsync<ServerEngineException>(() => engine.QueryHeightAsync(0, Vector3.Zero, 0f, CancellationToken.None));
        await Assert.ThrowsAsync<ServerEngineException>(() => engine.QueryLosAsync(0, new Vector3(float.NaN, 0, 0), Vector3.Zero, CancellationToken.None));
        await Assert.ThrowsAsync<ServerEngineException>(() => engine.QueryFloorStackAsync(0, float.NaN, 0f, -100f, 100f, CancellationToken.None));
        await Assert.ThrowsAsync<ServerEngineException>(() => engine.QueryFloorStackAsync(0, 0f, 0f, 100f, 100f, CancellationToken.None));
        await Assert.ThrowsAsync<ServerEngineException>(() => engine.QueryPathAsync(0, Vector3.Zero, Vector3.Zero, default, new PathOptions { MaxPoints = 1 }, CancellationToken.None));
        await Assert.ThrowsAsync<ServerEngineException>(() => engine.QueryPathAsync(0, Vector3.Zero, Vector3.Zero, default, new PathOptions { MaxSearchNodes = 0 }, CancellationToken.None));
        await Assert.ThrowsAsync<ServerEngineException>(() => engine.QueryPathAsync(0, Vector3.Zero, Vector3.Zero, default, new PathOptions { EndTolerance = float.NaN }, CancellationToken.None));

        var overlay = new ServerOverlay();
        Assert.Throws<ArgumentException>(() => ServerOverlay.Triple(new Vector3(float.NaN, 0, 0)));
        Assert.Throws<ArgumentNullException>(() => overlay.Polyline(null!));
        Assert.Throws<ArgumentException>(() => overlay.Polyline([Vector3.Zero]));
        Assert.Throws<ArgumentException>(() => ReportJson.Number(float.NaN));
        Assert.Throws<ArgumentException>(() => ReportJson.Number(float.PositiveInfinity));

        Assert.Throws<ArgumentNullException>(() => new TilesLayerSource(null!));
        Assert.Throws<ArgumentNullException>(() => new TerrainLayerSource(null!));
        Assert.Throws<ArgumentNullException>(() => new VMapLayerSource(null!));
        Assert.Throws<ArgumentNullException>(() => new NavLayerSource(null!));
        Assert.Throws<ArgumentNullException>(() => VMapLayerSource.WorldBounds(null!));

        var tilesSource = new TilesLayerSource(serverData);
        var invalidTile = new TileKey(0, -2, 32);
        Assert.Throws<ArgumentNullException>(() => tilesSource.AvailableTiles(null!, 0));
        Assert.Throws<ArgumentException>(() => tilesSource.AvailableTiles(LayerIds.Terrain, 0));
        Assert.Throws<ArgumentException>(() => tilesSource.PeekStatus(LayerIds.Tiles, invalidTile));
        Assert.Throws<ArgumentException>(() => tilesSource.Build(LayerIds.Tiles, invalidTile, CancellationToken.None));

        Assert.Throws<ArgumentNullException>(() => ServerCommands.Create(null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => ServerCommands.Create(engine, null!));
    }

    // -------------------------------------------------------------------------
    // Diff: TerrainDiff, ModelDiff, NavDiff, DiffLayerSource, DiffCommands, DiffModule
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Diff_Subsystems_Guards_ThrowExpectedExceptions()
    {
        var globalTile = TileKey.Global(0);
        var invalidTile = new TileKey(0, -2, 99);

        Assert.Throws<ArgumentNullException>(() => new DiffLayerSource(null!));
        var services = new AtlasServices();
        var diffSource = new DiffLayerSource(services);
        Assert.Throws<ArgumentException>(() => diffSource.AvailableTiles(LayerIds.Terrain, 0));
        Assert.Throws<ArgumentException>(() => diffSource.PeekStatus(LayerIds.Terrain, new TileKey(0, 32, 32)));
        Assert.Throws<ArgumentException>(() => diffSource.PeekStatus(LayerIds.DiffTerrain, invalidTile));
        Assert.Throws<ArgumentException>(() => diffSource.Build(LayerIds.DiffTerrain, invalidTile, CancellationToken.None));
        Assert.Equal(TileState.NotApplicable, diffSource.PeekStatus(LayerIds.DiffTerrain, globalTile).State);
        Assert.Equal(TileState.NotApplicable, diffSource.PeekStatus(LayerIds.DiffNav, globalTile).State);
        Assert.Equal(TileState.NotApplicable, diffSource.Build(LayerIds.DiffTerrain, globalTile, CancellationToken.None).Status.State);
        Assert.Equal(TileState.NotApplicable, diffSource.Build(LayerIds.DiffNav, globalTile, CancellationToken.None).Status.State);

        var commands = new CommandRegistry();
        Assert.Throws<ArgumentNullException>(() => DiffCommands.Register(null!, services));
        Assert.Throws<ArgumentNullException>(() => DiffCommands.Register(commands, null!));
        Assert.Throws<ArgumentNullException>(() => new DiffModule().Register(null!));

        DiffCommands.Register(commands, services);
        CommandResult badRadius = await commands.InvokeAsync(
            DiffCommands.TerrainCommand,
            new JsonObject { ["map"] = 0, ["x"] = 32, ["y"] = 32, ["radius"] = 5 },
            services,
            CommandOrigin.Cli,
            true,
            CancellationToken.None);
        Assert.Equal(CommandException.BadArguments, badRadius.ErrorCode);

        CommandResult badTileX = await commands.InvokeAsync(
            DiffCommands.NavCommand,
            new JsonObject { ["map"] = 0, ["x"] = 64, ["y"] = 32 },
            services,
            CommandOrigin.Cli,
            true,
            CancellationToken.None);
        Assert.Equal(CommandException.BadArguments, badTileX.ErrorCode);
    }

    // -------------------------------------------------------------------------
    // Reference: Mpq, Dbc, Client.Terrain, Client.Models, Navmesh
    // -------------------------------------------------------------------------

    [Fact]
    public void Mpq_And_Dbc_Guards_ThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => MpqCrypto.HashString((string)null!, 0));
        Assert.Throws<IndexOutOfRangeException>(() => MpqCrypto.HashString("abc", 0x500u));
        Assert.Throws<ArgumentNullException>(() => MpqCrypto.FileKey(null!, 0, 0, false));

        using var tempRoot = new TempTerrainRoot();
        string badMpqPath = Path.Combine(tempRoot.Root, "corrupt.mpq");
        File.WriteAllBytes(badMpqPath, new byte[16]);
        Assert.Throws<ArgumentException>(() => MpqArchive.Open(""));
        Assert.Throws<ArgumentException>(() => MpqArchive.Open("   "));
        Assert.Throws<InvalidDataException>(() => MpqArchive.Open(badMpqPath));

        Assert.Throws<ArgumentException>(() => MpqFileSystem.Open(""));
        Assert.Throws<DirectoryNotFoundException>(() => MpqFileSystem.Open(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        Assert.Throws<ArgumentException>(() => MpqFileSystem.HasArchives(""));
        Assert.Throws<ArgumentNullException>(() => MpqFileSystem.LoadOrder(null!, out _));
        Assert.Throws<ArgumentNullException>(() => MpqScanner.Scan(null!, sectors: false));

        Assert.Throws<InvalidDataException>(() => MpqCompression.Decompress(ReadOnlyMemory<byte>.Empty, new byte[10]));
        Assert.Throws<NotSupportedException>(() => MpqCompression.Decompress(new byte[] { 0x40, 1, 2, 3 }, new byte[10]));
        Assert.Throws<InvalidDataException>(() => PkwareExplode.Decompress([0, 4], new byte[10]));
        Assert.Throws<InvalidDataException>(() => MpqSparse.Decompress([1, 2, 3], new byte[10]));
        Assert.Throws<ArgumentNullException>(() => ClientOutputPath.Check(null!, false, []));
        Assert.Throws<ArgumentNullException>(() => ClientOutputPath.Check("C:\\out.bin", false, null!));
        Assert.Throws<CommandException>(() => ClientOutputPath.Check("relative/path.bin", false, []));
        Assert.Throws<CommandException>(() => ClientOutputPath.Check(@"\\server\share\file.bin", false, []));
        Assert.Throws<CommandException>(() => ClientOutputPath.Check(tempRoot.Root, false, []));
        string insideRoot = Path.Combine(tempRoot.Root, "sub", "out.bin");
        Assert.Throws<CommandException>(() => ClientOutputPath.Check(insideRoot, true, [tempRoot.Root]));
        Assert.Throws<ArgumentNullException>(() => ClientOutputPath.FindWorkingTree(null!));
        Assert.Throws<ArgumentNullException>(() => ClientOutputPath.ResolveLinks(null!));
        Assert.Throws<ArgumentNullException>(() => ClientCommands.Register(null!));

        Assert.Throws<ArgumentNullException>(() => WdbcFile.Parse(null!));
        Assert.Throws<InvalidDataException>(() => WdbcFile.Parse(new byte[10]));
        Assert.Throws<ArgumentNullException>(() => ClientMapDirectory.Load(null!));
        Assert.Throws<ArgumentNullException>(() => ClientMapDirectory.FromTables(null!, null!));
        Assert.Throws<FileNotFoundException>(() => ClientMapDirectory.Load(new MemoryClientFiles()));

        // Valid minimal 1-record, 2-field WDBC to exercise cell and string bounds guards
        byte[] wdbcBytes = new byte[WdbcFile.HeaderSize + 8 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(wdbcBytes.AsSpan(0), WdbcFile.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(wdbcBytes.AsSpan(4), 1u);  // 1 record
        BinaryPrimitives.WriteUInt32LittleEndian(wdbcBytes.AsSpan(8), 2u);  // 2 fields
        BinaryPrimitives.WriteUInt32LittleEndian(wdbcBytes.AsSpan(12), 8u); // recordSize = 8
        BinaryPrimitives.WriteUInt32LittleEndian(wdbcBytes.AsSpan(16), 4u); // stringBlockSize = 4
        BinaryPrimitives.WriteUInt32LittleEndian(wdbcBytes.AsSpan(20), 99u); // field 0: out-of-range string offset
        BinaryPrimitives.WriteUInt32LittleEndian(wdbcBytes.AsSpan(24), 1u);  // field 1: offset 1 (unterminated)
        wdbcBytes[28] = 0;
        wdbcBytes[29] = (byte)'A';
        wdbcBytes[30] = (byte)'B';
        wdbcBytes[31] = (byte)'C';

        WdbcFile wdbc = WdbcFile.Parse(wdbcBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => wdbc.GetUInt32(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => wdbc.GetUInt32(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => wdbc.GetUInt32(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => wdbc.GetUInt32(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => wdbc.GetLocalizedString(0, 0));
        Assert.Throws<InvalidDataException>(() => wdbc.GetString(0, 0));
        Assert.Throws<InvalidDataException>(() => wdbc.GetString(0, 1));
    }

    [Fact]
    public void ClientTerrain_ClientModels_And_Navmesh_Guards_ThrowExpectedExceptions()
    {
        var validTile = new TileKey(0, 32, 32);
        var globalTile = TileKey.Global(0);
        var invalidTile = new TileKey(0, -3, 99);
        Assert.Throws<ArgumentException>(() => ClientMapPaths.WdtPath(""));
        Assert.Throws<ArgumentException>(() => ClientMapPaths.WdtPath("../bad"));
        Assert.Throws<ArgumentException>(() => ClientMapPaths.WdtPath("a/b"));
        Assert.Throws<ArgumentException>(() => ClientMapPaths.AdtPath("Azeroth", globalTile));

        Assert.Throws<InvalidDataException>(() => WdtFile.Parse([]));
        Assert.Throws<InvalidDataException>(() => WdtFile.Parse(new byte[16]));
        WdtFile wdt = WdtFile.Parse(SyntheticTerrain.Wdt([(32, 32)]));
        Assert.False(wdt.HasTile(-1, 0));
        Assert.False(wdt.HasTile(0, 64));
        Assert.Equal(0u, wdt.TileFlags(-1, 0));
        Assert.Equal(0u, wdt.TileFlags(0, 64));

        Assert.Throws<InvalidDataException>(() => AdtFile.Parse([]));
        BuiltAdt builtAdt = SyntheticTerrain.Adt(SyntheticTerrain.Plane(validTile, 10f, 0f, 0f));
        AdtFile adt = AdtFile.Parse(builtAdt.Bytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => adt.Chunk(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => adt.Chunk(0, 16));
        AdtChunk chunk0 = adt.Chunk(0, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk0.OuterHeight(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk0.OuterHeight(0, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk0.InnerHeight(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk0.InnerHeight(0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk0.IsHoleSquare(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk0.IsHoleSquare(0, 8));
        Assert.Equal(ColorKeys.Slime, AdtLiquidLayer.ColorKeyOf((AdtLiquidKind)99));
        Assert.Equal(AdtLiquidLayer.McnkSlime, AdtLiquidLayer.FlagOf((AdtLiquidKind)99));

        Assert.Throws<ArgumentNullException>(() => AdtGeometry.OuterVertex(validTile, null!, 0, 0));
        Assert.Throws<ArgumentNullException>(() => AdtGeometry.InnerVertex(validTile, null!, 0, 0));
        Assert.Throws<ArgumentNullException>(() => AdtGeometry.CountPositionMismatches(null!, validTile));
        Assert.Throws<ArgumentNullException>(() => AdtGeometry.SampleAt(null!, validTile, 0, 0));
        Assert.Throws<ArgumentNullException>(() => AdtGeometry.LiquidAt(null!, validTile, 0, 0));
        Assert.Throws<ArgumentNullException>(() => ClientTerrainGeometry.BuildTerrain(validTile, null!, _ => "Area", CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => ClientTerrainGeometry.BuildTerrain(validTile, adt, null!, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => ClientTerrainGeometry.BuildLiquid(validTile, null!, CancellationToken.None));

        Assert.Throws<ArgumentNullException>(() => new ClientTerrainService(null!, () => null, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new ClientTerrainService(() => null, null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new ClientTerrainService(() => null, () => null, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientTerrainService(() => null, () => null, NullLogger.Instance, cacheCapacity: 0));

        var terrainService = new ClientTerrainService(() => null, () => null, NullLogger.Instance);
        Assert.Throws<ArgumentNullException>(() => new ClientPlacementSource(null!));
        var placementSource = new ClientPlacementSource(terrainService);
        Assert.Throws<ArgumentException>(() => placementSource.Placements(invalidTile));

        Assert.Throws<ArgumentNullException>(() => new ClientTerrainLayerSource(null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new ClientTerrainLayerSource(terrainService, null!));
        Assert.Throws<ArgumentNullException>(() => ClientTerrainCommands.Register(null!));

        var loadCache = new LoadCache<string, byte[]>(capacity: 4);
        Assert.Throws<ArgumentNullException>(() => loadCache.GetOrLoad("k", null!));

        Assert.Throws<ArgumentOutOfRangeException>(() => WowChunks.Scan(new byte[16], -1, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => WowChunks.Scan(new byte[16], 8, 24));
        Assert.Throws<InvalidDataException>(() => WowChunks.Required(new byte[16], new WowChunk("MVER", 8, 8, Truncated: true), "test"));

        Assert.Throws<InvalidDataException>(() => WmoRootFile.Parse([]));
        Assert.Throws<ArgumentException>(() => WmoRootFile.GroupFilePath("", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WmoRootFile.GroupFilePath("World\\wmo\\wall.wmo", -1));
        Assert.Equal("World\\wmo\\wall_1000.wmo", WmoRootFile.GroupFilePath("World\\wmo\\wall.wmo", 1000));
        Assert.Throws<InvalidDataException>(() => WmoGroupFile.Parse([]));
        Assert.Throws<InvalidDataException>(() => M2ModelFile.Parse([]));

        Assert.Throws<ArgumentNullException>(() => ClientModelNames.ClientFilePath(null!));
        Assert.Throws<ArgumentNullException>(() => ClientModelNames.PlainName(null!));
        Assert.Throws<ArgumentNullException>(() => ClientModelNames.FixNameCase(null!));
        Assert.Throws<ArgumentNullException>(() => ClientModelNames.FixNameSpaces(null!));
        Assert.Throws<ArgumentNullException>(() => PlacementTransform.ForM2(null!));
        Assert.Throws<ArgumentNullException>(() => PlacementTransform.ForWmo(null!, out _));
        Assert.Throws<ArgumentNullException>(() => PlacementTransform.ForWmoDoodad(default, null!));

        var modelCache = new ClientModelCache();
        Assert.Throws<ArgumentNullException>(() => modelCache.GetWmo(null!, "a.wmo"));
        Assert.Throws<ArgumentException>(() => modelCache.GetWmo(new MemoryClientFiles(), ""));
        Assert.Throws<ArgumentNullException>(() => modelCache.GetM2(null!, "a.m2"));
        Assert.Throws<ArgumentException>(() => modelCache.GetM2(new MemoryClientFiles(), ""));

        var emptyServices = new AtlasServices();
        Assert.Throws<ArgumentNullException>(() => new ClientModelLayerSource(null!, modelCache, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new ClientModelLayerSource(emptyServices, null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new ClientModelLayerSource(emptyServices, modelCache, null!));

        var refQueries = new ReferenceModelQueries(emptyServices, modelCache);
        Assert.Throws<ArgumentNullException>(() => refQueries.ModelInfo(null!));
        Assert.Throws<CommandException>(() => refQueries.ModelInfo("World\\wmo\\wall.wmo"));
        Assert.Throws<CommandException>(() => refQueries.LineOfSight(0, new WorldPoint(0, 0, 0), new WorldPoint(0, 0, 0), false, CancellationToken.None));
        Assert.Throws<CommandException>(() => refQueries.FloorStack(0, 0, 0, -100, 100, false, CancellationToken.None));

        Assert.Throws<InvalidDataException>(() => MmapParamsReader.Parse(new byte[12]));
        byte[] badParams = new byte[MmapParamsReader.ByteSize];
        BinaryPrimitives.WriteSingleLittleEndian(badParams.AsSpan(0), float.NaN);
        Assert.Throws<InvalidDataException>(() => MmapParamsReader.Parse(badParams));

        Assert.Throws<ArgumentNullException>(() => MmapTileReader.Parse(null!));
        Assert.Throws<InvalidDataException>(() => MmapTileReader.Parse(new byte[8]));

        Assert.Throws<ArgumentNullException>(() => new ReferenceNavMeshService(null, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReferenceNavMeshService(null, NullLogger.Instance, maxResidentTilesPerMap: 8));
        using var tempRoot = new TempTerrainRoot();
        var navService = new ReferenceNavMeshService(tempRoot.Root, NullLogger.Instance);
        navService.Dispose();
        Assert.Throws<ObjectDisposedException>(() => navService.OpenMap(0));

        using var liveNavService = new ReferenceNavMeshService(null, NullLogger.Instance);
        Assert.Throws<ArgumentNullException>(() => new ReferenceNavLayerSource(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReferenceNavLayerSource(liveNavService, maxChunkBytes: 512));

        var navLayer = new ReferenceNavLayerSource(liveNavService);
        Assert.Throws<ArgumentException>(() => navLayer.AvailableTiles(LayerIds.Terrain, 0));
        Assert.Throws<ArgumentException>(() => navLayer.PeekStatus(LayerIds.RefNavPoly, invalidTile));
        Assert.Throws<ArgumentException>(() => navLayer.Build(LayerIds.RefNavPoly, invalidTile, CancellationToken.None));
        Assert.Equal(TileState.NotApplicable, navLayer.PeekStatus(LayerIds.RefNavPoly, globalTile).State);

        Assert.Throws<ArgumentNullException>(() => NavQuery.FindPath(null!, Vector3.Zero, Vector3.Zero, new PathOptions()));
        Assert.Throws<ArgumentNullException>(() => NavQuery.Nearest(null!, Vector3.Zero, new NavQueryFilter(NavTerrain.Ground, NavTerrain.Empty, true), NavSearchBox.Ladder, null, out _));
    }

    // -------------------------------------------------------------------------
    // Deterministic Seeded Fuzzers
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_CommandInvocation_And_ViewState_RejectArbitraryMalformedJsonGracefully(int seed)
    {
        var rng = new Random(seed);
        var services = new AtlasServices();
        var def = new CommandDefinition("fuzz.cmd", "s", [], (_, _) => Task.FromResult<JsonNode?>(new JsonObject()));
        for (int i = 0; i < 64; i++)
        {
            var obj = new JsonObject
            {
                ["a"] = rng.Next(4) switch
                {
                    0 => JsonValue.Create(rng.Next(-500, 500)),
                    1 => JsonValue.Create("str_" + rng.Next(10)),
                    2 => new JsonArray(rng.NextDouble() * 100.0, rng.NextDouble() * 100.0),
                    _ => new JsonObject { ["x"] = rng.NextDouble() },
                },
            };

            var inv = new CommandInvocation(def, obj, services, CommandOrigin.Cli, headless: true);
            TryCatchCommand(() => _ = inv.GetString("a"));
            TryCatchCommand(() => _ = inv.GetInt("a"));
            TryCatchCommand(() => _ = inv.GetNumber("a"));
            TryCatchCommand(() => _ = inv.GetBool("a", false));
            TryCatchCommand(() => _ = inv.GetPoint("a"));
            TryCatchCommand(() => _ = inv.GetStringList("a"));
            TryCatchCommand(() => _ = inv.OptionalObject("a"));
            TryCatchCommand(() => ViewState.Default.Apply(obj));
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_ParseCache_LoadCache_And_LayerCache_MaintainCapAndNeverCacheFailures(int seed)
    {
        var rng = new Random(seed);
        var parseCache = new ParseCache<int, byte[]>(maxCost: 128);
        var loadCache = new LoadCache<int, byte[]>(capacity: 4);

        for (int i = 0; i < 80; i++)
        {
            int key = rng.Next(0, 16);
            int size = rng.Next(1, 96);
            bool fail = rng.Next(4) == 0;

            if (fail)
            {
                int failKey = 1000 + i;
                Assert.Throws<InvalidOperationException>(() =>
                    parseCache.GetOrLoad(failKey, () => throw new InvalidOperationException("simulated failure")));
                Assert.False(parseCache.TryPeek(failKey, out _));

                Assert.Throws<InvalidOperationException>(() =>
                    loadCache.GetOrLoad(failKey, () => throw new InvalidOperationException("simulated failure")));
                Assert.False(loadCache.TryPeek(failKey, out _));
            }
            else
            {
                ParsedFile<byte[]> val = parseCache.GetOrLoad(key, () => (ParsedFile<byte[]>.Ok(new byte[size], "f"), size));
                Assert.True(val.IsOk);

                bool wasCached = loadCache.TryPeek(key, out byte[]? existing);
                byte[] loaded = loadCache.GetOrLoad(key, () => new byte[size], keep: b => b.Length % 2 == 0);
                Assert.Equal(wasCached ? existing!.Length : size, loaded.Length);
            }

            Assert.True(parseCache.Stats.Cost <= Math.Max(128, size));
            Assert.True(loadCache.Count <= 4);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_TerrainDiff_ModelDiff_And_NavDiff_ClassifyCorruptAndMissingInputsDeterministically(int seed)
    {
        var rng = new Random(seed);
        using var root = new TempTerrainRoot();
        var serverData = new ServerData(root.Paths);
        var files = new MemoryClientFiles();
        var maps = SyntheticTerrain.Maps(new ClientMap(0, "Azeroth", "Eastern Kingdoms", 0));
        var terrainService = new ClientTerrainService(() => files, () => maps, NullLogger.Instance);
        var placementSource = new ClientPlacementSource(terrainService);
        using var navService = new ReferenceNavMeshService(root.Paths.MMapsDirectory, NullLogger.Instance);

        var services = new AtlasServices();
        services.Add(serverData);
        services.Add<IClientFileSource>(files);
        services.Add<IMapDirectory>(maps);
        services.Add(terrainService);
        services.Add<IClientPlacementSource>(placementSource);
        services.Add(navService);

        for (int i = 0; i < 16; i++)
        {
            var tile = new TileKey(0, rng.Next(0, 64), rng.Next(0, 64));
            byte[] garbage = new byte[rng.Next(4, 128)];
            rng.NextBytes(garbage);

            if (rng.Next(2) == 0)
            {
                root.WriteMap(tile.MapId, tile.X, tile.Y, garbage);
            }

            if (rng.Next(2) == 0)
            {
                files.Add(ClientMapPaths.AdtPath("Azeroth", tile), garbage);
            }

            LayerBuild tDiff = TerrainDiff.BuildLayer(services, tile, CancellationToken.None);
            Assert.Equal(tile, tDiff.Status.Tile);

            LayerBuild mDiff = ModelDiff.BuildLayer(services, tile, CancellationToken.None);
            Assert.Equal(tile, mDiff.Status.Tile);

            LayerBuild nDiff = NavDiff.BuildLayer(services, tile, CancellationToken.None);
            Assert.Equal(tile, nDiff.Status.Tile);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_ReferenceModelQueries_And_NavQuery_HandleRandomCoordinatesWithoutUnhandledExceptions(int seed)
    {
        var rng = new Random(seed);
        var tile = ModelWorld.Goldshire;
        var world = new ModelWorld();
        world.AddCube(half: 4f, withHull: true);
        world.AddAdt(tile);
        (double cx, double cy) = WorldGrid.Centre(tile);
        world.Placements.Add(tile, ModelWorld.M2(1, new WorldPoint(cx, cy, 20.0), tile));
        var queries = new ReferenceModelQueries(world.Services, world.Cache);

        using var map = new SyntheticMap(mapId: 0);
        map.Add(new TileSpec { TerrainX = tile.X, TerrainY = tile.Y, Cells = 2 });
        using var navService = new ReferenceNavMeshService(Path.Combine(map.Root, "mmaps"), NullLogger.Instance);
        ReferenceNavMap navMap = navService.OpenMap(0).Map!;
        Assert.Throws<ArgumentNullException>(() => navMap.Read<int>(null!, _ => 1));
        Assert.Throws<ArgumentNullException>(() => navMap.Read<int>([tile], null!));
        Assert.Throws<ArgumentException>(() => navMap.Read([new TileKey(99, tile.X, tile.Y)], _ => 1));
        Assert.Throws<ArgumentException>(() => navMap.Read([TileKey.Global(0)], _ => 1));

        for (int i = 0; i < 24; i++)
        {
            var from = new WorldPoint(
                cx + ((rng.NextDouble() - 0.5) * 80.0),
                cy + ((rng.NextDouble() - 0.5) * 80.0),
                (rng.NextDouble() * 60.0) - 10.0);
            var to = new WorldPoint(
                cx + ((rng.NextDouble() - 0.5) * 80.0),
                cy + ((rng.NextDouble() - 0.5) * 80.0),
                (rng.NextDouble() * 60.0) - 10.0);

            JsonObject los = queries.LineOfSight(tile.MapId, from, to, includeExtractorSkipped: true, CancellationToken.None);
            Assert.NotNull(los["spell"]);

            JsonObject floors = queries.FloorStack(tile.MapId, from.X, from.Y, -200.0, 200.0, includeExtractorSkipped: true, CancellationToken.None);
            Assert.NotNull(floors["surfaces"]);

            NavPathAnswer path = navMap.Read(
                [tile],
                view => NavQuery.FindPath(
                    view,
                    new Vector3((float)from.X, (float)from.Y, (float)from.Z),
                    new Vector3((float)to.X, (float)to.Y, (float)to.Z),
                    new PathOptions { MaxPoints = 64, MaxSearchNodes = 64 }));
            Assert.NotEmpty(path.Outcome);
        }
    }

    private static void TryCatchCommand(Action action)
    {
        try
        {
            action();
        }
        catch (CommandException)
        {
            // Expected for invalid fuzzed arguments.
        }
    }

    private sealed class StubLayerSource : ILayerSource
    {
        public StubLayerSource(IReadOnlyList<string> layers) => Layers = layers;
        public IReadOnlyList<string> Layers { get; }
        public IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId) => [];
        public TileStatus PeekStatus(string layerId, TileKey tile) => new(layerId, tile, TileState.Empty);
        public LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken)
            => new(new TileStatus(layerId, tile, TileState.Empty), []);
    }

    private sealed class ThrowingModule : IAtlasModule
    {
        public string Name => "throwing.module";
        public int Order => ModuleOrder.Layers;
        public void Register(AtlasContext context) => throw new InvalidOperationException("module boom");
    }

}
