using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>The host's commands run through the registry exactly as HTTP, ctl, script and MCP reach them.</summary>
public sealed class HostCommandTests
{
    // The Goldshire inn (RealTerrainDataTests.cs:57) lies in tile (49, 31): x in (-9600, -9066.67], y in (0, 533.33].
    private static readonly TileKey Goldshire = Geo.TileAt(-9462.66, 16.1915);

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public async Task Status_ReportsModeModulesLayersCacheAndConnections()
    {
        await using HostRig rig = HostRig.Create();

        JsonNode data = await rig.DataAsync("status");

        Assert.Equal("serve", data.Str("mode"));
        Assert.Equal(AtlasVersion.Current, data.Str("version"));
        string[] modules = [.. data.At("modules")!.AsArray().Select(n => n!.GetValue<string>())];
        Assert.Contains("Host", modules);
        Assert.Contains("test", modules);
        Assert.Contains(LayerIds.Terrain, data.At("layers")!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(0, data.Num("cacheBytes"));
        Assert.Equal(0, data.Num("ui.connections"));
        Assert.Null(data.At("paths.terrainData"));
        Assert.Empty(data.At("problems")!.AsArray());
    }

    [Fact]
    public async Task Status_InAHeadlessContext_SaysHeadless_AndReportsBadDirectories()
    {
        string missing = Path.Combine(Path.GetTempPath(), "atlas-missing-" + Guid.NewGuid().ToString("N"));
        await using HostRig rig = HostRig.Create(headless: true, paths: new AtlasPaths(missing, null));

        JsonNode data = await rig.DataAsync("status");

        Assert.Equal("headless", data.Str("mode"));
        Assert.Equal(missing, data.Str("paths.terrainData"));
        Assert.Contains("does not exist", data.At("problems.0")!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ListsEverySectionThirteenCommand_AndDescribesOne()
    {
        await using HostRig rig = HostRig.Create();

        JsonNode all = await rig.DataAsync("help");
        string[] names = [.. all.At("commands")!.AsArray().Select(c => c.Str("name"))];
        string[] expected =
        [
            "status", "help", "maps.list", "view.get", "view.set", "camera.goto", "camera.frame", "layer.set", "layers.list",
            "bookmarks.list", "bookmark.go", "bookmark.add", "bookmark.remove", "tiles.status", "query.pick", "capture", "cache.clear",
            "session.join", "session.move", "session.leave", "sessions.list",
        ];
        foreach (string name in expected)
        {
            Assert.Contains(name, names);
        }

        JsonNode capture = await rig.DataAsync("help", Args("""{"command":"capture"}"""));
        Assert.Equal("capture", capture.Str("name"));
        Assert.Equal("object", capture.Str("schema.type"));
        Assert.Equal(1600, capture.Num("schema.properties.width.default"));
        Assert.Contains("width", capture.Str("usage"), StringComparison.Ordinal);
        Assert.Equal("width", capture.At("args.0")!.Str("name"));

        CommandResult unknown = await rig.InvokeAsync("help", Args("""{"command":"nope"}"""));
        Assert.False(unknown.Ok);
        Assert.Equal(CommandException.UnknownCommand, unknown.ErrorCode);
    }

    [Fact]
    public async Task LayersList_ReportsEveryCatalogLayer_AndWhichHaveASource()
    {
        await using HostRig rig = HostRig.Create();

        JsonNode data = await rig.DataAsync("layers.list");

        JsonArray layers = data.AsArray();
        Assert.Equal(LayerCatalog.All.Count, layers.Count);
        JsonNode terrain = layers.Single(l => l.Str("id") == LayerIds.Terrain)!;
        Assert.True(terrain.Flag("hasSource"));
        Assert.True(terrain.Flag("tiled"));
        Assert.Equal("terrain", terrain.Str("group"));
        Assert.False(layers.Single(l => l.Str("id") == LayerIds.NavPoly)!.Flag("hasSource"));
        Assert.NotNull(layers.Single(l => l.Str("id") == LayerIds.Liquid)!.At($"attributeColors.{ColorKeys.Water}"));
    }

    [Fact]
    public async Task ViewGet_IsTheDefaultView_AndViewSet_AppliesAMergePatch_AndPublishesTheViewEvent()
    {
        await using HostRig rig = HostRig.Create();
        JsonObject expected = ViewState.Default.ToJson();
        expected["version"] = 0;
        Assert.Equal(expected.ToJsonString(), (await rig.DataAsync("view.get")).ToJsonString());

        JsonNode set = await rig.DataAsync("view.set", Args("""{"camera":{"yaw":90,"mode":"fly"},"layers":{"terrain":{"opacity":0.5}}}"""));

        Assert.Equal(90, set.Num("view.camera.yaw"));
        Assert.Equal("fly", set.Str("view.camera.mode"));
        Assert.Equal(0.5, set.Num("view.layers.terrain.opacity"));
        Assert.Equal(ViewState.Default.Camera.Distance, set.Num("view.camera.distance"));
        JsonNode viewEvent = Assert.Single(rig.EventsOf(EventHub.Topics.View));
        Assert.Equal(1, viewEvent.Num("version"));
        Assert.Equal("http", viewEvent.Str("origin"));
        Assert.Equal(90, viewEvent.Num("view.camera.yaw"));
    }

    [Fact]
    public async Task ViewGet_IncludesFollowOriginWhileFollowing_AndOmitsItWhenNothingFollowed()
    {
        await using HostRig rig = HostRig.Create();

        // When nothing is followed, followOrigin is omitted and version is emitted.
        JsonNode initial = await rig.DataAsync("view.get");
        Assert.False(((JsonObject)initial).ContainsKey("followOrigin"));
        Assert.Equal(0, initial.Num("version"));

        // While something is followed, followOrigin reflects the owner.
        await rig.InvokeAsync("view.set", new JsonObject { ["follow"] = "1234ABCD" }, CommandOrigin.Cli);
        JsonNode following = await rig.DataAsync("view.get");
        Assert.Equal("1234ABCD", following.Str("follow"));
        Assert.Equal("cli", following.Str("followOrigin"));
        Assert.Equal(1, following.Num("version"));

        // When follow is cleared, followOrigin is omitted again.
        await rig.InvokeAsync("view.set", new JsonObject { ["follow"] = null }, CommandOrigin.Cli);
        JsonNode cleared = await rig.DataAsync("view.get");
        Assert.Null(cleared.At("follow"));
        Assert.False(((JsonObject)cleared).ContainsKey("followOrigin"));
        Assert.Equal(2, cleared.Num("version"));
    }

    [Fact]
    public async Task ViewSet_RejectsAnInvalidPatch_WithoutChangingOrPublishing()
    {
        await using HostRig rig = HostRig.Create();

        CommandResult badPitch = await rig.InvokeAsync("view.set", Args("""{"camera":{"pitch":120}}"""));
        CommandResult empty = await rig.InvokeAsync("view.set");
        CommandResult unknownLayer = await rig.InvokeAsync("view.set", Args("""{"layers":{"no.such":{"visible":true}}}"""));
        CommandResult badEnum = await rig.InvokeAsync("view.set", Args("""{"compare":"sideways"}"""));

        foreach (CommandResult result in new[] { badPitch, empty, unknownLayer, badEnum })
        {
            Assert.False(result.Ok);
            Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        }

        Assert.Equal(0, rig.Context.View.Version);
        Assert.Empty(rig.EventsOf(EventHub.Topics.View));
    }

    [Fact]
    public async Task EveryCommandResult_IsPublishedAsACommandEvent_WithItsOrigin()
    {
        await using HostRig rig = HostRig.Create();

        await rig.InvokeAsync("test.echo", Args("""{"text":"a"}"""), CommandOrigin.Mcp);
        await rig.InvokeAsync("no.such.command", null, CommandOrigin.Cli);

        IReadOnlyList<JsonNode> events = rig.EventsOf(EventHub.Topics.Command);
        Assert.Equal(2, events.Count);
        Assert.Equal("test.echo", events[0].Str("command"));
        Assert.True(events[0].Flag("ok"));
        Assert.Equal("mcp", events[0].Str("origin"));
        Assert.Equal("a", events[0].Str("data.echo"));
        Assert.False(events[1].Flag("ok"));
        Assert.Equal("cli", events[1].Str("origin"));
        Assert.Equal(CommandException.UnknownCommand, events[1].Str("error.code"));
    }

    [Fact]
    public async Task CameraGoto_WithAnExplicitPoint_MovesTheCameraAndReportsTheTile()
    {
        await using HostRig rig = HostRig.Create();

        JsonNode data = await rig.DataAsync("camera.goto", Args("""{"at":"-9462.66,16.1915,57.0459","map":0,"yaw":10,"pitch":30,"distance":50,"mode":"top"}"""));

        Assert.Equal("given", data.Str("zSource"));
        Assert.Equal(new double[] { 0, Goldshire.X, Goldshire.Y }, new[] { data.Num("tile.0"), data.Num("tile.1"), data.Num("tile.2") });
        Assert.Equal(-9462.66, data.Num("view.camera.target.0"), 6);
        Assert.Equal(57.0459, data.Num("view.camera.target.2"), 6);
        Assert.Equal(10, data.Num("view.camera.yaw"));
        Assert.Equal(30, data.Num("view.camera.pitch"));
        Assert.Equal(50, data.Num("view.camera.distance"));
        Assert.Equal("top", data.Str("view.camera.mode"));
    }

    [Fact]
    public async Task CameraGoto_WithoutZ_UsesTheTerrainHeightFromTheCache_OrKeepsTheCurrentZ()
    {
        await using HostRig rig = HostRig.Create();
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 61.5));

        JsonNode onTerrain = await rig.DataAsync("camera.goto", Args("""{"x":-9450,"y":20}"""));
        await rig.DataAsync("camera.goto", Args("""{"x":-9440,"y":25}"""));
        JsonNode offTerrain = await rig.DataAsync("camera.goto", Args("""{"x":-9300,"y":20}"""));
        JsonNode noTile = await rig.DataAsync("camera.goto", Args("""{"x":-9400,"y":-2000}"""));

        Assert.Equal("terrain", onTerrain.Str("zSource"));
        Assert.Equal(61.5, onTerrain.Num("view.camera.target.2"), 3);
        Assert.Equal("kept", offTerrain.Str("zSource"));
        Assert.Equal(61.5, offTerrain.Num("view.camera.target.2"), 3);
        Assert.Equal("kept", noTile.Str("zSource"));
        Assert.Equal(1, rig.Terrain.BuildsOf(LayerIds.Terrain, Goldshire));
    }

    [Fact]
    public async Task CameraGoto_RejectsAmbiguousOrImpossibleRequests()
    {
        await using HostRig rig = HostRig.Create();
        string[] requests =
        [
            "{}",
            """{"at":[1,2,3],"x":1}""",
            """{"x":5}""",
            """{"x":1e7,"y":0}""",
            """{"at":"1,2"}""",
            """{"at":[1,2,3],"mode":"sideways"}""",
            """{"at":[1,2,3],"pitch":"steep"}""",
        ];

        foreach (string request in requests)
        {
            CommandResult result = await rig.InvokeAsync("camera.goto", Args(request));
            Assert.False(result.Ok, request);
            Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        }

        Assert.Equal(0, rig.Context.View.Version);
    }

    [Fact]
    public async Task CameraGoto_WithOnlyAMap_ChangesTheMapAndLeavesTheCamera()
    {
        await using HostRig rig = HostRig.Create();

        JsonNode data = await rig.DataAsync("camera.goto", Args("""{"map":1}"""));

        Assert.Equal(1, data.Num("view.map"));
        Assert.Equal(ViewState.Default.Camera.Target.X, data.Num("view.camera.target.0"), 6);
    }

    [Fact]
    public async Task CameraFrame_Tile_CentresTheTileAndFitsItsDiagonal()
    {
        await using HostRig rig = HostRig.Create();
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9400, -9300, 250, 280, 44));

        JsonNode data = await rig.DataAsync("camera.frame", Args($$"""{"tile":"{{Goldshire.X}},{{Goldshire.Y}}"}"""));

        // Tile (49,31) covers x in (-9600, -9066.67], y in (0, 533.33]: its centre is about (-9333.33, 266.67).
        (double centreX, double centreY) = WorldGrid.Centre(Goldshire);
        Assert.Equal(-9333.33, centreX, 1);
        Assert.Equal(266.67, centreY, 1);
        Assert.Equal(centreX, data.Num("view.camera.target.0"), 6);
        Assert.Equal(centreY, data.Num("view.camera.target.1"), 6);
        Assert.Equal(44, data.Num("view.camera.target.2"), 3);
        double expected = 0.5 * Math.Sqrt(2) * 533.3333333 / Math.Sin(ViewState.Default.Camera.Fov * Math.PI / 360) * 1.05;
        Assert.Equal(expected, data.Num("view.camera.distance"), 1);
        Assert.Equal(expected, data.Num("distance"), 1);
    }

    [Fact]
    public async Task CameraFrame_Bounds_UsesTheBoxCentre_AndTheTileFormHonoursAMapPrefix()
    {
        await using HostRig rig = HostRig.Create();

        JsonNode bounds = await rig.DataAsync("camera.frame", Args("""{"bounds":{"min":[-9400,0,50],"max":[-9300,100,70]}}"""));
        JsonNode withMap = await rig.DataAsync("camera.frame", Args("""{"tile":"1,10,10"}"""));

        Assert.Equal(-9350, bounds.Num("view.camera.target.0"), 6);
        Assert.Equal(50, bounds.Num("view.camera.target.1"), 6);
        Assert.Equal(60, bounds.Num("view.camera.target.2"), 6);
        double radius = 0.5 * Math.Sqrt((100 * 100) + (100 * 100) + (20 * 20));
        Assert.Equal(radius / Math.Sin(ViewState.Default.Camera.Fov * Math.PI / 360) * 1.05, bounds.Num("view.camera.distance"), 3);
        Assert.Equal(1, withMap.Num("view.map"));
    }

    [Fact]
    public async Task CameraFrame_InTopMode_SizesTheViewFromTheLargerSide_AndInFlyMode_BacksTheTargetOff()
    {
        await using HostRig rig = HostRig.Create();
        await rig.DataAsync("view.set", Args("""{"camera":{"mode":"top"}}"""));
        JsonNode top = await rig.DataAsync("camera.frame", Args("""{"bounds":{"min":[-9400,0],"max":[-9300,200]}}"""));
        Assert.Equal(200.0 / 2 * 1.1, top.Num("view.camera.distance"), 6);

        await rig.DataAsync("view.set", Args("""{"camera":{"mode":"fly","yaw":0,"pitch":0}}"""));
        JsonNode fly = await rig.DataAsync("camera.frame", Args("""{"bounds":{"min":[-9400,0,50],"max":[-9300,100,70]}}"""));

        // Looking along +X with no pitch, the eye (= the target in fly mode) sits `distance` behind the box centre.
        double distance = fly.Num("distance");
        Assert.Equal(-9350 - distance, fly.Num("view.camera.target.0"), 3);
        Assert.Equal(50, fly.Num("view.camera.target.1"), 3);
        Assert.Equal(60, fly.Num("view.camera.target.2"), 3);
    }

    [Fact]
    public async Task CameraFrame_Guid_FollowsTheEntity_AndBadRequestsAreRefused()
    {
        await using HostRig rig = HostRig.Create();

        JsonNode follow = await rig.DataAsync("camera.frame", Args("""{"guid":"1f2a"}"""));
        Assert.Equal("1F2A", follow.Str("view.follow"));
        Assert.Equal("1f2a", follow.Str("following"));

        foreach (string request in new[] { "{}", """{"tile":"1,1","guid":"1"}""", """{"tile":"64,0"}""", """{"tile":"1.5,2"}""", """{"tile":"x"}""", """{"guid":"xyz"}""", """{"bounds":{"min":[1,2]}}""", """{"bounds":{"min":[0,0],"max":[1e7,1e7]}}""" })
        {
            CommandResult result = await rig.InvokeAsync("camera.frame", Args(request));
            Assert.False(result.Ok, request);
            Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        }
    }

    [Fact]
    public async Task LayerSet_ChangesOneLayersStyle_AndRefusesBadInput()
    {
        await using HostRig rig = HostRig.Create();

        JsonNode data = await rig.DataAsync("layer.set", Args("""{"id":"terrain","visible":false,"color":"#ff0000","opacity":0.25,"mode":"fill","colorByAttribute":true}"""));

        Assert.False(data.Flag("view.layers.terrain.visible"));
        Assert.Equal("#FF0000", data.Str("view.layers.terrain.color"));
        Assert.Equal(0.25, data.Num("view.layers.terrain.opacity"));
        Assert.Equal("fill", data.Str("view.layers.terrain.mode"));
        Assert.True(data.Flag("view.layers.terrain.colorByAttribute"));
        Assert.Equal(ViewState.Default.Layers[LayerIds.Liquid].Color, data.Str("view.layers.liquid.color"));

        foreach (string request in new[] { """{"id":"no.such","visible":true}""", """{"id":"terrain"}""", """{"id":"terrain","color":"red"}""", """{"id":"terrain","opacity":1.5}""", """{"id":"terrain","mode":"sparkly"}""", """{"visible":true}""" })
        {
            CommandResult result = await rig.InvokeAsync("layer.set", Args(request));
            Assert.False(result.Ok, request);
            Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        }

        Assert.Equal(1, rig.Context.View.Version);
    }

    [Fact]
    public async Task Bookmarks_ListBuiltInsFirst_AndGoMovesTheCamera()
    {
        await using HostRig rig = HostRig.Create();

        JsonNode list = await rig.DataAsync("bookmarks.list");
        JsonNode inn = list.AsArray().Single(b => b.Str("name") == "goldshire-inn")!;
        Assert.True(inn.Flag("builtin"));
        Assert.Contains("RealTerrainDataTests.cs:57", inn.Str("source"), StringComparison.Ordinal);
        Assert.Equal(BuiltInBookmarks.All.Count, list.AsArray().Count);

        await rig.DataAsync("camera.goto", Args("""{"map":1,"at":[0,0,0]}"""));
        JsonNode go = await rig.DataAsync("bookmark.go", Args("""{"name":"Goldshire-Inn"}"""));

        Assert.Equal(0, go.Num("view.map"));
        Assert.Equal(-9462.66, go.Num("view.camera.target.0"), 6);
        Assert.Equal(16.1915, go.Num("view.camera.target.1"), 6);
        Assert.Equal(57.0459, go.Num("view.camera.target.2"), 6);
        Assert.Equal("goldshire-inn", go.Str("bookmark.name"));
        Assert.Equal(2, rig.Context.View.Version);
    }

    [Fact]
    public async Task BookmarkAdd_SavesTheCurrentCamera_SurvivesARestart_AndRemoveDeletesIt()
    {
        await using HostRig rig = HostRig.Create();
        await rig.DataAsync("camera.goto", Args("""{"at":[-9450,20,60],"distance":77,"yaw":10,"pitch":20}"""));

        JsonNode added = await rig.DataAsync("bookmark.add", Args("""{"name":"My Spot","note":"near the inn"}"""));

        Assert.False(added.Flag("bookmark.builtin"));
        Assert.Equal(77, added.Num("bookmark.distance"));
        Assert.Equal(-9450, added.Num("bookmark.at.0"));
        var reopened = new BookmarkStore(rig.State.BookmarksFile, NullLoggerFactory.Instance);
        Bookmark saved = Assert.Single(reopened.All(), b => !b.BuiltIn);
        Assert.Equal("My Spot", saved.Name);
        Assert.Equal("near the inn", saved.Note);
        Assert.Equal(10, saved.Yaw);
        JsonNode file = JsonNode.Parse(await File.ReadAllTextAsync(rig.State.BookmarksFile))!;
        Assert.Equal(1, file.Num("version"));

        await rig.DataAsync("bookmark.add", Args("""{"name":"my spot","note":"replaced"}"""));
        JsonNode list = await rig.DataAsync("bookmarks.list");
        Assert.Equal(BuiltInBookmarks.All.Count + 1, list.AsArray().Count);
        Assert.Equal("replaced", list.AsArray().Last()!.Str("note"));

        JsonNode removed = await rig.DataAsync("bookmark.remove", Args("""{"name":"MY SPOT"}"""));
        Assert.Equal("MY SPOT", removed.Str("removed"));
        Assert.DoesNotContain(new BookmarkStore(rig.State.BookmarksFile, NullLoggerFactory.Instance).All(), b => !b.BuiltIn);
    }

    [Fact]
    public async Task Bookmarks_RefuseBuiltInNamesInvalidNamesAndUnknownNames()
    {
        await using HostRig rig = HostRig.Create();
        (string Command, string Request)[] bad =
        [
            ("bookmark.add", """{"name":"goldshire-inn"}"""),
            ("bookmark.add", """{"name":"-leading dash"}"""),
            ("bookmark.add", """{"name":"has<angle"}"""),
            ("bookmark.add", $$"""{"name":"{{new string('a', 65)}}"}"""),
            ("bookmark.add", """{"name":""}"""),
            ("bookmark.remove", """{"name":"goldshire-inn"}"""),
            ("bookmark.remove", """{"name":"never saved"}"""),
            ("bookmark.go", """{"name":"nowhere"}"""),
        ];

        foreach ((string command, string request) in bad)
        {
            CommandResult result = await rig.InvokeAsync(command, Args(request));
            Assert.False(result.Ok, $"{command} {request}");
            Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        }

        Assert.False(File.Exists(rig.State.BookmarksFile));
    }

    [Fact]
    public async Task BookmarkStore_WithACorruptFile_ReportsIt_AndKeepsTheFileAsideOnTheNextSave()
    {
        string root = Path.Combine(Path.GetTempPath(), "atlas-corrupt-" + Guid.NewGuid().ToString("N"));
        var state = new AtlasState(root);
        Directory.CreateDirectory(state.Root);
        await File.WriteAllTextAsync(state.BookmarksFile, "{ this is not json");
        try
        {
            using AtlasContext context = HostComposer.Compose(new AtlasPaths(null, null), NullLoggerFactory.Instance, headless: false, [new HostModule(state)]);
            BookmarkStore store = context.Services.Require<BookmarkStore>();

            Assert.NotNull(store.LoadProblem);
            Assert.Equal(BuiltInBookmarks.All.Count, store.All().Count);
            JsonNode status = StatusCommands.BuildStatus(context);
            Assert.Single(status.At("problems")!.AsArray(), p => p!.GetValue<string>().Contains("Saved bookmarks", StringComparison.Ordinal));

            store.Save(new Bookmark("fresh", 0, new WorldPoint(1, 2, 3), 10, 0, 45, CameraMode.Orbit, null, null));

            Assert.Null(store.LoadProblem);
            string aside = Assert.Single(Directory.GetFiles(state.Root, "bookmarks.json.unreadable-*"));
            Assert.Equal("{ this is not json", await File.ReadAllTextAsync(aside));
            Assert.Equal("fresh", Assert.Single(new BookmarkStore(state.BookmarksFile, NullLoggerFactory.Instance).All(), b => !b.BuiltIn).Name);
            Assert.Empty(StatusCommands.BuildStatus(context)["problems"]!.AsArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TilesStatus_ListsAvailableTilesWithoutBuilding_AndShowsBuiltStatusAfterwards()
    {
        await using HostRig rig = HostRig.Create();
        var rejected = new TileKey(0, Goldshire.X, Goldshire.Y + 1);
        var empty = new TileKey(0, Goldshire.X + 1, Goldshire.Y);
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60));
        rig.Terrain.AddRejected(LayerIds.Terrain, rejected, "bad header");
        rig.Terrain.AddEmpty(LayerIds.Terrain, empty);

        JsonNode before = await rig.DataAsync("tiles.status", Args("""{"map":0,"layer":"terrain"}"""));

        JsonArray tiles = before.At("tiles")!.AsArray();
        Assert.Equal(3, tiles.Count);
        Assert.Equal(new[] { Goldshire.X, Goldshire.X, Goldshire.X + 1 }, tiles.Select(t => (int)t.Num("tile.1")).ToArray());
        Assert.All(tiles, t => Assert.Equal("Unknown", t.Str("state")));
        Assert.Equal(0, rig.Terrain.Builds);

        rig.Context.Cache.Get(LayerIds.Terrain, Goldshire, CancellationToken.None);
        rig.Context.Cache.Get(LayerIds.Terrain, rejected, CancellationToken.None);
        JsonNode after = await rig.DataAsync("tiles.status", Args("""{"map":0}"""));

        JsonNode built = after.At("tiles")!.AsArray().Single(t => t.Str("layer") == LayerIds.Terrain && (int)t.Num("tile.1") == Goldshire.X && (int)t.Num("tile.2") == Goldshire.Y)!;
        Assert.Equal("Ok", built.Str("state"));
        Assert.True(built.Num("primitives") > 0);
        JsonNode refused = after.At("tiles")!.AsArray().Single(t => t.Str("layer") == LayerIds.Terrain && (int)t.Num("tile.1") == rejected.X && (int)t.Num("tile.2") == rejected.Y)!;
        Assert.Equal("Rejected", refused.Str("state"));
        Assert.Equal("bad header", refused.Str("detail"));
    }

    [Fact]
    public async Task TilesStatus_AroundATile_ClipsToTheGrid_AndMarksUnlistedTilesMissing()
    {
        await using HostRig rig = HostRig.Create();
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60));

        JsonNode around = await rig.DataAsync("tiles.status", Args($$"""{"map":0,"x":{{Goldshire.X}},"y":{{Goldshire.Y}},"radius":1}"""));
        JsonNode corner = await rig.DataAsync("tiles.status", Args("""{"map":0,"x":0,"y":0,"radius":2}"""));
        JsonNode global = await rig.DataAsync("tiles.status", Args("""{"map":0,"x":-1,"y":-1,"radius":3}"""));

        JsonArray tiles = around.At("tiles")!.AsArray();
        Assert.Equal(9, tiles.Count);
        Assert.Equal(1, tiles.Count(t => t.Str("state") == "Unknown"));
        Assert.Equal(8, tiles.Count(t => t.Str("state") == "Missing"));
        Assert.Equal(9, corner.At("tiles")!.AsArray().Count);
        Assert.Equal(-1, Assert.Single(global.At("tiles")!.AsArray()).Num("tile.1"));
    }

    [Fact]
    public async Task TilesStatus_RefusesBadRequests()
    {
        await using HostRig rig = HostRig.Create();
        (string Request, string Code)[] bad =
        [
            ("""{"x":1,"y":1}""", CommandException.BadArguments),
            ("""{"map":0,"x":1}""", CommandException.BadArguments),
            ("""{"map":0,"x":64,"y":0}""", CommandException.BadArguments),
            ("""{"map":0,"x":1,"y":1,"radius":17}""", CommandException.BadArguments),
            ("""{"map":0,"x":1,"y":1,"radius":-1}""", CommandException.BadArguments),
            ("""{"map":0,"layer":"nope"}""", CommandException.BadArguments),
            ("""{"map":0,"layer":"nav.poly"}""", CommandException.Unavailable),
            ("""{"map":-1}""", CommandException.BadArguments),
        ];

        foreach ((string request, string code) in bad)
        {
            CommandResult result = await rig.InvokeAsync("tiles.status", Args(request));
            Assert.False(result.Ok, request);
            Assert.Equal(code, result.ErrorCode);
        }
    }

    [Fact]
    public async Task MapsList_CombinesTheDirectoryAndDataFileNames_AndListsOnlyMapsThatHaveTiles()
    {
        string data = Path.Combine(Path.GetTempPath(), "atlas-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(data, "maps"));
        Directory.CreateDirectory(Path.Combine(data, "vmaps"));
        Directory.CreateDirectory(Path.Combine(data, "mmaps"));
        await File.WriteAllBytesAsync(Path.Combine(data, "maps", "0054040.map"), []);
        await File.WriteAllBytesAsync(Path.Combine(data, "vmaps", "007.vmtree"), []);
        await File.WriteAllBytesAsync(Path.Combine(data, "mmaps", "008.mmap"), []);
        await File.WriteAllBytesAsync(Path.Combine(data, "maps", "readme.txt"), []);
        try
        {
            await using HostRig rig = HostRig.Create(paths: new AtlasPaths(data, null));
            rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60));
            rig.Terrain.AddEmpty(LayerIds.Terrain, new TileKey(0, 49, 32));
            rig.Terrain.AddEmpty(LayerIds.Terrain, new TileKey(1, 10, 10));
            rig.Terrain.AddEmpty(LayerIds.Terrain, TileKey.Global(2));
            rig.Terrain.AddEmpty(LayerIds.Terrain, new TileKey(5, 40, 40));
            rig.Terrain.AddEmpty(LayerIds.Terrain, new TileKey(99, 1, 1));

            JsonArray maps = (await rig.DataAsync("maps.list")).AsArray();

            Assert.Equal(new[] { 0, 1, 2, 5 }, maps.Select(m => (int)m.Num("map")).ToArray());
            Assert.Equal("Eastern Kingdoms", maps[0]!.Str("name"));
            Assert.Equal("Azeroth", maps[0]!.Str("directory"));
            Assert.Equal(2, maps[0]!.Num("layers.terrain"));
            Assert.Equal(1, maps[1]!.Num("layers.terrain"));
            Assert.Equal("terrain", maps[2]!.At("global.0")!.GetValue<string>());
            Assert.Null(maps[2]!.At("layers.terrain"));
            Assert.Null(maps[3]!.At("name"));
            Assert.Equal(1, maps[3]!.Num("layers.terrain"));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task CacheClear_DropsCachedTiles_AndTellsTheUisToRefetch()
    {
        await using HostRig rig = HostRig.Create();
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60));
        rig.Context.Cache.Get(LayerIds.Terrain, Goldshire, CancellationToken.None);
        Assert.True(rig.Context.Cache.Bytes > 0);

        JsonNode cleared = await rig.DataAsync("cache.clear", Args("""{"layer":"terrain"}"""));

        Assert.Equal(1, cleared.Num("removed"));
        Assert.Equal(0, rig.Context.Cache.Bytes);
        JsonNode invalidated = Assert.Single(rig.EventsOf(EventHub.Topics.TileInvalidated));
        Assert.Equal("terrain", invalidated.Str("layer"));
        Assert.Null(invalidated.At("tile"));
        rig.Context.Cache.Get(LayerIds.Terrain, Goldshire, CancellationToken.None);
        Assert.Equal(2, rig.Terrain.BuildsOf(LayerIds.Terrain, Goldshire));

        CommandResult unknown = await rig.InvokeAsync("cache.clear", Args("""{"layer":"nope"}"""));
        Assert.Equal(CommandException.BadArguments, unknown.ErrorCode);
    }

    [Fact]
    public async Task Capture_Headless_WritesAPngOfTheRequestedSize()
    {
        await using HostRig rig = HostRig.Create();
        string shots = Path.Combine(rig.Directory, "shots");

        JsonNode data = await rig.DataAsync("capture", Args($$"""{"source":"headless","width":64,"height":32,"out":{{JsonValue.Create(shots)!.ToJsonString()}}}"""));

        string path = data.Str("path");
        Assert.StartsWith(shots, path, StringComparison.Ordinal);
        byte[] png = await File.ReadAllBytesAsync(path);
        Assert.Equal((64, 32), CaptureCommand.PngSize(png));
        Assert.Equal(png.Length, (int)data.Num("bytes"));
        Assert.Equal("headless", data.Str("source"));
        Assert.Equal(64, data.Num("width"));
        Assert.Equal(32, data.Num("height"));
        Assert.Equal(1, data.Num("stats.ms"));
    }

    [Fact]
    public async Task Capture_DefaultsToTheStateCapturesFolder_At1600By900_AndHonoursAnExactFilePath()
    {
        await using HostRig rig = HostRig.Create();

        JsonNode byDefault = await rig.DataAsync("capture");
        string exact = Path.Combine(rig.Directory, "named", "view.png");
        JsonNode named = await rig.DataAsync("capture", Args($$"""{"out":{{JsonValue.Create(exact)!.ToJsonString()}},"width":16,"height":16}"""));

        Assert.StartsWith(rig.State.CapturesDirectory, byDefault.Str("path"), StringComparison.Ordinal);
        Assert.Equal((1600, 900), CaptureCommand.PngSize(await File.ReadAllBytesAsync(byDefault.Str("path"))));
        Assert.Equal(exact, named.Str("path"));
        Assert.True(File.Exists(exact));
    }

    [Fact]
    public async Task Capture_AppliesTheViewPatchToACopy_AndLeavesTheSharedViewAlone()
    {
        await using HostRig rig = HostRig.Create();

        await rig.DataAsync("capture", Args("""{"source":"headless","width":16,"height":16,"view":{"camera":{"yaw":123}}}"""));

        Assert.Equal(123, rig.Renderer!.Views.Single().Camera.Yaw);
        Assert.Equal(ViewState.Default.Camera.Yaw, rig.Context.View.Current.Camera.Yaw);
        Assert.Equal(0, rig.Context.View.Version);
    }

    [Fact]
    public async Task Capture_RefusesBadSizesPathsSourcesAndPatches()
    {
        await using HostRig rig = HostRig.Create();
        string[] requests =
        [
            """{"width":15}""",
            """{"height":9000}""",
            """{"width":8192,"height":8192}""",
            """{"width":"wide"}""",
            """{"out":"notes.txt"}""",
            """{"source":"telepathy"}""",
            """{"view":{"camera":{"pitch":500}}}""",
        ];

        foreach (string request in requests)
        {
            CommandResult result = await rig.InvokeAsync("capture", Args(request));
            Assert.False(result.Ok, request);
            Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        }
    }

    [Fact]
    public async Task Capture_FromTheUi_NeedsAServeInstanceWithAConnectedUi()
    {
        await using HostRig headless = HostRig.Create(headless: true);
        await using HostRig noUi = HostRig.Create();
        await using HostRig noRenderer = HostRig.Create(renderer: false);

        CommandResult fromHeadless = await headless.InvokeAsync("capture", Args("""{"source":"ui"}"""));
        CommandResult withoutUi = await noUi.InvokeAsync("capture", Args("""{"source":"ui"}"""));
        CommandResult withoutRenderer = await noRenderer.InvokeAsync("capture");

        Assert.Equal(CommandException.Unavailable, fromHeadless.ErrorCode);
        Assert.Contains("serve", fromHeadless.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(CommandException.Unavailable, withoutUi.ErrorCode);
        Assert.Contains("no UI is connected", withoutUi.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(CommandException.Unavailable, withoutRenderer.ErrorCode);
        Assert.Contains("no headless renderer", withoutRenderer.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_RejectsARendererThatDoesNotReturnAPng()
    {
        await using HostRig rig = HostRig.Create(renderer: false, register: c => c.Services.Add<IHeadlessRenderer>(new NotAPngRenderer()));

        CommandResult result = await rig.InvokeAsync("capture", Args("""{"source":"headless","width":16,"height":16}"""));

        Assert.False(result.Ok);
        Assert.Equal(CommandException.Failed, result.ErrorCode);
        Assert.Contains("not a PNG", result.ErrorMessage, StringComparison.Ordinal);
        Assert.False(Directory.Exists(rig.State.CapturesDirectory));
    }

    private sealed class NotAPngRenderer : IHeadlessRenderer
    {
        public RenderedImage Render(ViewState view, int width, int height, CancellationToken cancellationToken)
            => new([1, 2, 3, 4], width, height, new JsonObject());
    }
}
