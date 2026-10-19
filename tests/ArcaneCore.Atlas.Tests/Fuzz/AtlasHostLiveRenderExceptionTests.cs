using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host;
using ArcaneCore.Atlas.Host.Cli;
using ArcaneCore.Atlas.Live;
using ArcaneCore.Atlas.Render;
using ArcaneCore.Atlas.Tests.Host;
using ArcaneCore.Atlas.Tests.Live;
using ArcaneCore.Atlas.Tests.Render;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Fuzz;

public sealed class AtlasHostExceptionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void AtlasState_NullOrWhitespaceRoot_ThrowsArgumentException(string? root)
    {
        Assert.ThrowsAny<ArgumentException>(() => new AtlasState(root!));
    }

    [Fact]
    public void Discovery_NullArguments_ThrowArgumentNullException()
    {
        using var temp = new TempState();
        var record = new DiscoveryRecord(1234, 8080, "token-123", DateTimeOffset.UtcNow, "1.0", null, null);

        Assert.Throws<ArgumentNullException>(() => Discovery.FileFor(null!, 1234));
        Assert.Throws<ArgumentNullException>(() => Discovery.Write(null!, record));
        Assert.Throws<ArgumentNullException>(() => Discovery.Write(temp.State, null!));
        Assert.Throws<ArgumentNullException>(() => Discovery.Delete(null!, record));
        Assert.Throws<ArgumentNullException>(() => Discovery.Delete(temp.State, null!));
        Assert.Throws<ArgumentNullException>(() => Discovery.Live(null!));
        Assert.Throws<ArgumentNullException>(() => Discovery.IsAlive(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"str\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"pid\":\"not-int\",\"port\":80,\"token\":\"t\",\"started\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":-5,\"port\":80,\"token\":\"t\",\"started\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":1,\"port\":0,\"token\":\"t\",\"started\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":1,\"port\":65536,\"token\":\"t\",\"started\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":1,\"port\":80,\"token\":\"\",\"started\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":1,\"port\":80,\"token\":123,\"started\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"pid\":1,\"port\":80,\"token\":\"t\",\"started\":\"not-a-date\"}")]
    public void DiscoveryRecord_Parse_MalformedInputs_ReturnsNullWithoutThrowing(string json)
    {
        JsonNode? node = string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json);
        Assert.Null(DiscoveryRecord.Parse(node));
    }

    [Fact]
    public void Bookmarks_NullAndInvalidInputs_ThrowExpectedExceptions()
    {
        using var temp = new TempState();
        Assert.ThrowsAny<ArgumentException>(() => new BookmarkStore(null!, NullLoggerFactory.Instance));
        Assert.ThrowsAny<ArgumentException>(() => new BookmarkStore("   ", NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>(() => new BookmarkStore(temp.State.BookmarksFile, null!));

        var store = new BookmarkStore(temp.State.BookmarksFile, NullLoggerFactory.Instance);
        Assert.Throws<ArgumentNullException>(() => store.TryGet(null!, out _));
        Assert.Throws<ArgumentNullException>(() => store.Save(null!));
        Assert.Throws<ArgumentNullException>(() => store.Remove(null!));

        Assert.Throws<ArgumentNullException>(() => Bookmark.IsValidName(null!));
        Assert.False(Bookmark.IsValidName(""));
        Assert.False(Bookmark.IsValidName("   "));
        Assert.False(Bookmark.IsValidName("../escape"));
        Assert.False(Bookmark.IsValidName(new string('a', 65)));

        var badNameBookmark = new Bookmark("../bad", 0, new WorldPoint(0, 0, 0), 50, 0, -30, CameraMode.Orbit, null, null);
        CommandException badSave = Assert.Throws<CommandException>(() => store.Save(badNameBookmark));
        Assert.Equal(CommandException.BadArguments, badSave.Code);

        var builtInOverride = new Bookmark("goldshire-inn", 0, new WorldPoint(0, 0, 0), 50, 0, -30, CameraMode.Orbit, null, null);
        CommandException builtInSave = Assert.Throws<CommandException>(() => store.Save(builtInOverride));
        Assert.Equal(CommandException.BadArguments, builtInSave.Code);

        CommandException builtInRemove = Assert.Throws<CommandException>(() => store.Remove("goldshire-inn"));
        Assert.Equal(CommandException.BadArguments, builtInRemove.Code);
    }

    [Fact]
    public void BookmarkStore_CorruptJsonOnDisk_QuarantinesFileAndExposesLoadProblem()
    {
        using var temp = new TempState();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.State.BookmarksFile)!);
        File.WriteAllText(temp.State.BookmarksFile, "{corrupt json!!!");

        var store = new BookmarkStore(temp.State.BookmarksFile, NullLoggerFactory.Instance);
        Assert.NotNull(store.LoadProblem);
        Assert.NotEmpty(store.All());
    }

    [Fact]
    public void HostOrigin_NullOrWhitespaceLabel_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => HostOrigin.With(null!));
        Assert.ThrowsAny<ArgumentException>(() => HostOrigin.With(""));
        Assert.ThrowsAny<ArgumentException>(() => HostOrigin.With("   "));
    }

    [Fact]
    public void ChunkPicker_And_TileTraversal_EdgeCasesAndNonFiniteInputs_DoNotThrowUnhandled()
    {
        TileKey tile = Geo.TileAt(0, 0);
        GeometryChunk chunk = Geo.Quad(LayerIds.Terrain, tile, 0, 10, 0, 10, 5);

        Assert.Throws<ArgumentNullException>(() =>
            ChunkPicker.Nearest(null!, new WorldPoint(5, 5, 20), new WorldPoint(0, 0, -1), 100));

        Assert.Null(ChunkPicker.Nearest(chunk, new WorldPoint(5, 5, 20), new WorldPoint(0, 0, 0), 100));
        Assert.Null(ChunkPicker.Nearest(chunk, new WorldPoint(5, 5, 20), new WorldPoint(double.NaN, 0, -1), 100));
        Assert.Null(ChunkPicker.Nearest(chunk, new WorldPoint(double.PositiveInfinity, 5, 20), new WorldPoint(0, 0, -1), 100));
        Assert.Null(ChunkPicker.Nearest(chunk, new WorldPoint(5, 5, 20), new WorldPoint(0, 0, -1), 0));
        Assert.Null(ChunkPicker.Nearest(chunk, new WorldPoint(5, 5, 20), new WorldPoint(0, 0, -1), -10));
        Assert.Null(ChunkPicker.Nearest(chunk, new WorldPoint(5, 5, 20), new WorldPoint(0, 0, -1), double.NaN));

        Assert.Single(TileTraversal.Along(0, new WorldPoint(0, 0, 0), new WorldPoint(0, 0, 0), 100));
        Assert.Single(TileTraversal.Along(0, new WorldPoint(100, 100, 0), new WorldPoint(1, 0, 0), 0));
        Assert.Empty(TileTraversal.Along(0, new WorldPoint(0, 0, 0), new WorldPoint(1, 0, 0), -5));
        Assert.Empty(TileTraversal.Along(0, new WorldPoint(0, 0, 0), new WorldPoint(1, 0, 0), double.NaN));
        Assert.Empty(TileTraversal.Along(0, new WorldPoint(double.NaN, 0, 0), new WorldPoint(1, 0, 0), 100));
    }

    [Fact]
    public async Task HostModule_Commands_FuzzInvalidArguments_ReturnStructuredErrors()
    {
        await using HostRig rig = HostRig.Create(headless: true);

        CommandResult badMap = await rig.InvokeAsync("camera.goto", new JsonObject { ["map"] = -1, ["x"] = 0, ["y"] = 0, ["z"] = 0 });
        Assert.False(badMap.Ok);
        Assert.Equal(CommandException.BadArguments, badMap.ErrorCode);

        CommandResult nanX = await rig.InvokeAsync("camera.goto", new JsonObject { ["x"] = "NaN", ["y"] = 0, ["z"] = 0 });
        Assert.False(nanX.Ok);
        Assert.Equal(CommandException.BadArguments, nanX.ErrorCode);

        CommandResult badPitch = await rig.InvokeAsync("camera.goto", new JsonObject { ["x"] = 0, ["y"] = 0, ["z"] = 0, ["pitch"] = 95 });
        Assert.False(badPitch.Ok);
        Assert.Equal(CommandException.BadArguments, badPitch.ErrorCode);

        CommandResult badDist = await rig.InvokeAsync("camera.goto", new JsonObject { ["x"] = 0, ["y"] = 0, ["z"] = 0, ["distance"] = 0 });
        Assert.False(badDist.Ok);
        Assert.Equal(CommandException.BadArguments, badDist.ErrorCode);

        CommandResult badMode = await rig.InvokeAsync("camera.goto", new JsonObject { ["x"] = 0, ["y"] = 0, ["z"] = 0, ["mode"] = "teleport" });
        Assert.False(badMode.Ok);
        Assert.Equal(CommandException.BadArguments, badMode.ErrorCode);

        CommandResult badTile = await rig.InvokeAsync("camera.frame", new JsonObject { ["tile"] = "64,0" });
        Assert.False(badTile.Ok);
        Assert.Equal(CommandException.BadArguments, badTile.ErrorCode);

        CommandResult badLayer = await rig.InvokeAsync("layer.set", new JsonObject { ["id"] = "nonexistent.layer", ["visible"] = true });
        Assert.False(badLayer.Ok);
        Assert.Equal(CommandException.BadArguments, badLayer.ErrorCode);

        CommandResult badOpacity = await rig.InvokeAsync("layer.set", new JsonObject { ["id"] = LayerIds.Terrain, ["opacity"] = 1.5 });
        Assert.False(badOpacity.Ok);
        Assert.Equal(CommandException.BadArguments, badOpacity.ErrorCode);

        CommandResult badColor = await rig.InvokeAsync("layer.set", new JsonObject { ["id"] = LayerIds.Terrain, ["color"] = "not-a-hex-color" });
        Assert.False(badColor.Ok);
        Assert.Equal(CommandException.BadArguments, badColor.ErrorCode);

        CommandResult emptyViewSet = await rig.InvokeAsync("view.set", new JsonObject());
        Assert.False(emptyViewSet.Ok);
        Assert.Equal(CommandException.BadArguments, emptyViewSet.ErrorCode);

        CommandResult badCompare = await rig.InvokeAsync("view.set", new JsonObject { ["compare"] = "invalid-compare" });
        Assert.False(badCompare.Ok);
        Assert.Equal(CommandException.BadArguments, badCompare.ErrorCode);

        CommandResult badSplit = await rig.InvokeAsync("view.set", new JsonObject { ["split"] = 0.99 });
        Assert.False(badSplit.Ok);
        Assert.Equal(CommandException.BadArguments, badSplit.ErrorCode);

        CommandResult zeroRay = await rig.InvokeAsync("query.pick", new JsonObject
        {
            ["ray"] = new JsonObject
            {
                ["origin"] = new JsonArray(0, 0, 10),
                ["direction"] = new JsonArray(0, 0, 0),
            },
        });
        Assert.False(zeroRay.Ok);
        Assert.Equal(CommandException.BadArguments, zeroRay.ErrorCode);

        CommandResult badMaxDist = await rig.InvokeAsync("query.pick", new JsonObject
        {
            ["ray"] = new JsonObject
            {
                ["origin"] = new JsonArray(0, 0, 10),
                ["direction"] = new JsonArray(0, 0, -1),
            },
            ["maxDistance"] = 0,
        });
        Assert.False(badMaxDist.Ok);
        Assert.Equal(CommandException.BadArguments, badMaxDist.ErrorCode);

        CommandResult headlessUiCapture = await rig.InvokeAsync("capture", new JsonObject { ["source"] = "ui" });
        Assert.False(headlessUiCapture.Ok);
        Assert.Equal(CommandException.Unavailable, headlessUiCapture.ErrorCode);

        CommandResult missingBookmark = await rig.InvokeAsync("bookmark.go", new JsonObject { ["name"] = "does-not-exist" });
        Assert.False(missingBookmark.Ok);
        Assert.Equal(CommandException.BadArguments, missingBookmark.ErrorCode);
    }

    [Fact]
    public async Task AtlasServer_GuardClausesAndHttpFuzz_RespondCleanly()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => AtlasServer.StartAsync(null!));

        using var emptyCtx = new AtlasContext(new AtlasPaths(null, null), NullLoggerFactory.Instance, headless: true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            AtlasServer.StartAsync(emptyCtx, new AtlasServerOptions { Port = -1 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            AtlasServer.StartAsync(emptyCtx, new AtlasServerOptions { Port = 65536 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AtlasServer.StartAsync(emptyCtx));

        await using HostRig rig = await HostRig.StartAsync();

        using HttpResponseMessage unauth = await rig.Anonymous.PostAsync(
            "api/command",
            new StringContent("{\"name\":\"status\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        using HttpResponseMessage badJson = await rig.Http.PostAsync(
            "api/command",
            new StringContent("{broken json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);

        using HttpResponseMessage dupProps = await rig.Http.PostAsync(
            "api/command",
            new StringContent("{\"name\":\"status\",\"name\":\"layers.list\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, dupProps.StatusCode);

        using HttpResponseMessage missingCmd = await rig.Http.PostAsync(
            "api/command",
            new StringContent("{\"args\":{}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, missingCmd.StatusCode);

        using HttpResponseMessage traversal = await rig.Anonymous.GetAsync("..%2F..%2Fetc%2Fpasswd");
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);

        using var ws = new ClientWebSocket();
        Uri badWsUri = new($"ws://127.0.0.1:{rig.Server!.Port}/ws?token=wrong-token");
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ws.ConnectAsync(badWsUri, CancellationToken.None));
    }

    [Fact]
    public async Task McpServer_And_AtlasCli_NullGuardsAndFuzzInputs_NeverCrash()
    {
        var client = new FakeCommandClient();
        client.AddEcho();
        using var input = new InputPipe();
        using var output = new LineSink();
        using var log = new LineSink();

        Assert.Throws<ArgumentNullException>(() => new McpServer(null!, input, output, log));
        Assert.Throws<ArgumentNullException>(() => new McpServer(client, null!, output, log));
        Assert.Throws<ArgumentNullException>(() => new McpServer(client, input, null!, log));
        Assert.Throws<ArgumentNullException>(() => new McpServer(client, input, output, null!));

        await Assert.ThrowsAsync<ArgumentNullException>(() => AtlasCli.RunAsync(null!, new CliEnvironment(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AtlasCli.RunAsync([], null!, CancellationToken.None));

        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        session.Send("{\"jsonrpc\":\"2.0\",\"id\":10,\"id\":11,\"method\":\"ping\"}");
        JsonNode pingAfterDup = await session.RequestAsync(McpSession.Req(11, "ping"), 11);
        Assert.NotNull(pingAfterDup.At("result"));
        JsonNode dup = session.Messages().First(m => m["id"] is null);
        Assert.Equal(McpServer.ParseError, McpSession.ErrorCode(dup));

        StringBuilder deep = new("{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"tools/call\",\"params\":{\"name\":\"test_echo\",\"arguments\":");
        for (int i = 0; i < 80; i++)
        {
            deep.Append("{\"a\":");
        }

        deep.Append('1');
        for (int i = 0; i < 80; i++)
        {
            deep.Append('}');
        }

        deep.Append("}}");
        session.Send(deep.ToString());
        JsonNode alive = await session.RequestAsync(McpSession.Req(13, "ping"), 13);
        Assert.NotNull(alive.At("result"));
    }
}

public sealed class AtlasLiveExceptionTests
{
    [Fact]
    public void LiveModel_ValidationAndParsing_RejectsMalformedAndOutOfRangeInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LiveKinds.Name((LiveKind)99));
        Assert.False(LiveKinds.TryParse(null, out _));
        Assert.False(LiveKinds.TryParse("", out _));
        Assert.False(LiveKinds.TryParse("dragon", out _));

        Assert.False(LiveGuid.TryParse(null, out _));
        Assert.False(LiveGuid.TryParse("", out _));
        Assert.False(LiveGuid.TryParse("   ", out _));
        Assert.False(LiveGuid.TryParse("0x", out _));
        Assert.False(LiveGuid.TryParse("0xGGGG", out _));
        Assert.False(LiveGuid.TryParse("-1", out _));
        Assert.False(LiveGuid.TryParse("18446744073709551616", out _));
        Assert.Equal("2A", LiveGuid.Format(42));
        Assert.Equal("arcanecore-mapfeed-4242", LiveProtocol.PipeName(4242));
    }

    [Fact]
    public void LiveOptions_InvalidSettings_ThrowOnConstructingLiveFeed()
    {
        var events = new EventHub();
        var view = new ViewStore(ViewState.Default);

        Assert.Throws<ArgumentNullException>(() => new LiveFeed(null!, events, view, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new LiveFeed(LiveOptions.Default, null!, view, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new LiveFeed(LiveOptions.Default, events, null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new LiveFeed(LiveOptions.Default, events, view, null!));

        Assert.Throws<ArgumentNullException>(() =>
            new LiveFeed(LiveOptions.Default with { PipeNameFor = null! }, events, view, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            new LiveFeed(LiveOptions.Default with { Discovery = null! }, events, view, NullLogger.Instance));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LiveFeed(LiveOptions.Default with { ConnectTimeout = TimeSpan.Zero }, events, view, NullLogger.Instance));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LiveFeed(LiveOptions.Default with { HelloTimeout = TimeSpan.FromMilliseconds(-1) }, events, view, NullLogger.Instance));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LiveFeed(LiveOptions.Default with { QueryTimeout = TimeSpan.Zero }, events, view, NullLogger.Instance));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LiveFeed(LiveOptions.Default with { WriteTimeout = TimeSpan.Zero }, events, view, NullLogger.Instance));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LiveFeed(LiveOptions.Default with { MaxLineBytes = 512 }, events, view, NullLogger.Instance));
    }

    [Fact]
    public async Task LiveFeed_DisposedAndNullArguments_ThrowExpectedExceptions()
    {
        var feed = new LiveFeed(LiveOptions.Default, new EventHub(), new ViewStore(ViewState.Default), NullLogger.Instance);
        await Assert.ThrowsAsync<ArgumentNullException>(() => feed.SubscribeAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => feed.QueryAsync(null!, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => feed.ListEntities(null!));

        feed.Dispose();
        feed.Dispose();

        var sub = new LiveSubscription(0, 0, 4, LiveKinds.Names, true);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => feed.ConnectAsync(1234, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => feed.SubscribeAsync(sub, CancellationToken.None));
    }

    [Fact]
    public void LiveStore_NullFrameAndOutOfOrderSequences_AreHandledSafely()
    {
        var store = new LiveStore();
        Assert.Throws<ArgumentNullException>(() => store.Apply(null!));

        var frame2 = new LiveFrame(2, 100, 0, 0, [], null, [], 1000);
        var frame1 = new LiveFrame(1, 90, 0, 0, [], null, [], 900);
        var frame5 = new LiveFrame(5, 130, 0, 0, [], null, [], 1300);

        Assert.Equal(0, store.Apply(frame2));
        Assert.Equal(0, store.Apply(frame1));
        Assert.Equal(3, store.Apply(frame5));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"op\":\"hello\"}")]
    [InlineData("{\"op\":\"hello\",\"protocol\":-1}")]
    [InlineData("{\"op\":\"hello\",\"protocol\":\"one\"}")]
    [InlineData("{\"op\":\"hello\",\"protocol\":1,\"pid\":0}")]
    [InlineData("{\"op\":\"hello\",\"protocol\":1,\"pid\":-5}")]
    [InlineData("{\"op\":\"hello\",\"protocol\":1,\"pid\":100,\"sha\":123}")]
    [InlineData("{\"op\":\"hello\",\"protocol\":1,\"pid\":100,\"maps\":\"not-array\"}")]
    public void FrameParser_TryParseHello_MalformedPayloads_ReturnFalseWithProblem(string json)
    {
        JsonObject root = JsonNode.Parse(json, documentOptions: FrameParser.DocumentOptions)!.AsObject();
        bool ok = FrameParser.TryParseHello(root, out HelloInfo? hello, out string? problem);
        Assert.False(ok);
        Assert.Null(hello);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Theory]
    [InlineData("{\"op\":\"frame\",\"map\":-1,\"seq\":1,\"entities\":[]}")]
    [InlineData("{\"op\":\"frame\",\"map\":0,\"instance\":-1,\"seq\":1,\"entities\":[]}")]
    [InlineData("{\"op\":\"frame\",\"map\":0,\"seq\":-1,\"entities\":[]}")]
    [InlineData("{\"op\":\"frame\",\"map\":0,\"seq\":1,\"clockMs\":-1,\"entities\":[]}")]
    [InlineData("{\"op\":\"frame\",\"map\":0,\"seq\":1,\"entities\":\"not-array\"}")]
    [InlineData("{\"op\":\"frame\",\"map\":0,\"seq\":1,\"counters\":[],\"entities\":[]}")]
    [InlineData("{\"op\":\"frame\",\"map\":0,\"seq\":1,\"grids\":{},\"entities\":[]}")]
    public void FrameParser_TryParseFrame_MalformedTopLevel_ReturnsFalseWithProblem(string json)
    {
        JsonObject root = JsonNode.Parse(json, documentOptions: FrameParser.DocumentOptions)!.AsObject();
        bool ok = FrameParser.TryParseFrame(root, 1000, out ParsedFrame? parsed, out string? problem);
        Assert.False(ok);
        Assert.Null(parsed);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void FrameParser_TryParseFrame_SkipsMalformedEntitiesAndKeepsValidOnes()
    {
        const string json = """
        {
          "op": "frame",
          "map": 0,
          "instance": 0,
          "seq": 1,
          "clockMs": 250,
          "entities": [
            "not-an-object",
            {"g": "bad-guid", "k": "player", "x": 1, "y": 2, "z": 3},
            {"g": "0x1", "k": "unknown-kind", "x": 1, "y": 2, "z": 3},
            {"g": "0x2", "k": "player", "x": 1, "y": 2},
            {"g": "0x3", "k": "player", "x": "NaN", "y": 2, "z": 3},
            {"g": "0x4", "k": "bot", "x": 10, "y": 20, "z": 30, "n": "Thrall", "route": {"pts": [[4, 5, 6]], "next": 99, "cut": true, "flags": ["partial"]}}
          ]
        }
        """;

        JsonObject root = JsonNode.Parse(json, documentOptions: FrameParser.DocumentOptions)!.AsObject();
        Assert.True(FrameParser.TryParseFrame(root, 5000, out ParsedFrame? parsed, out string? problem), problem);
        Assert.NotNull(parsed);
        Assert.Equal(5, parsed.Rejected);
        LiveEntity entity = Assert.Single(parsed.Frame.Entities);
        Assert.Equal(4ul, entity.Id);
        Assert.Equal("Thrall", entity.Name);
        Assert.NotNull(entity.Route);
        WorldPoint waypoint = Assert.Single(entity.Route.Points);
        Assert.Equal(new WorldPoint(4, 5, 6), waypoint);
        Assert.Equal(1, entity.Route.Next);
    }

    [Fact]
    public async Task LiveCommands_FuzzInvalidArguments_ReturnBadArgumentsOrUnavailable()
    {
        await using var rig = new LiveRig();

        CommandResult badPid = await rig.RunAsync("live.connect", new JsonObject { ["pid"] = 0 });
        Assert.False(badPid.Ok);
        Assert.Equal(CommandException.BadArguments, badPid.ErrorCode);

        CommandResult negPid = await rig.RunAsync("live.connect", new JsonObject { ["pid"] = -42 });
        Assert.False(negPid.Ok);
        Assert.Equal(CommandException.BadArguments, negPid.ErrorCode);

        CommandResult badSubRate = await rig.RunAsync("live.subscribe", new JsonObject { ["map"] = 0, ["hz"] = 0 });
        Assert.False(badSubRate.Ok);
        Assert.Equal(CommandException.BadArguments, badSubRate.ErrorCode);

        CommandResult highSubRate = await rig.RunAsync("live.subscribe", new JsonObject { ["map"] = 0, ["hz"] = 61 });
        Assert.False(highSubRate.Ok);
        Assert.Equal(CommandException.BadArguments, highSubRate.ErrorCode);

        CommandResult badSubKind = await rig.RunAsync("live.subscribe", new JsonObject { ["map"] = 0, ["kinds"] = new JsonArray("spaceship") });
        Assert.False(badSubKind.Ok);
        Assert.Equal(CommandException.BadArguments, badSubKind.ErrorCode);

        CommandResult badFollow = await rig.RunAsync("live.follow", new JsonObject { ["guid"] = "not-a-guid" });
        Assert.False(badFollow.Ok);
        Assert.Equal(CommandException.BadArguments, badFollow.ErrorCode);

        CommandResult zeroFollow = await rig.RunAsync("live.follow", new JsonObject { ["guid"] = "0x0" });
        Assert.False(zeroFollow.Ok);
        Assert.Equal(CommandException.BadArguments, zeroFollow.ErrorCode);

        CommandResult badFilterKind = await rig.RunAsync("live.entities", new JsonObject { ["kind"] = "alien" });
        Assert.False(badFilterKind.Ok);
        Assert.Equal(CommandException.BadArguments, badFilterKind.ErrorCode);

        CommandResult radiusWithoutNear = await rig.RunAsync("live.entities", new JsonObject { ["radius"] = 50 });
        Assert.False(radiusWithoutNear.Ok);
        Assert.Equal(CommandException.BadArguments, radiusWithoutNear.ErrorCode);

        CommandResult badQueryKind = await rig.RunAsync("live.query", new JsonObject
        {
            ["kind"] = "explode",
            ["a"] = new JsonArray(0, 0, 0),
        });
        Assert.False(badQueryKind.Ok);
        Assert.Equal(CommandException.BadArguments, badQueryKind.ErrorCode);

        CommandResult losMissingB = await rig.RunAsync("live.query", new JsonObject
        {
            ["kind"] = "los",
            ["a"] = new JsonArray(0, 0, 0),
        });
        Assert.False(losMissingB.Ok);
        Assert.Equal(CommandException.BadArguments, losMissingB.ErrorCode);

        CommandResult heightWithB = await rig.RunAsync("live.query", new JsonObject
        {
            ["kind"] = "height",
            ["a"] = new JsonArray(0, 0, 0),
            ["b"] = new JsonArray(10, 10, 0),
        });
        Assert.False(heightWithB.Ok);
        Assert.Equal(CommandException.BadArguments, heightWithB.ErrorCode);

        CommandResult queryWhileDisconnected = await rig.RunAsync("live.query", new JsonObject
        {
            ["kind"] = "los",
            ["map"] = 0,
            ["a"] = new JsonArray(0, 0, 0),
            ["b"] = new JsonArray(10, 10, 0),
        });
        Assert.False(queryWhileDisconnected.Ok);
        Assert.Equal(CommandException.Unavailable, queryWhileDisconnected.ErrorCode);
    }
}

public sealed class AtlasRenderExceptionTests
{
    [Fact]
    public void Framebuffer_And_CpuRenderer_InvalidDimensions_ThrowArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Framebuffer(0, 100, Rgb.Background));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Framebuffer(100, 0, Rgb.Background));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Framebuffer(-1, 100, Rgb.Background));

        Assert.Throws<ArgumentOutOfRangeException>(() => CpuRenderer.ValidateSize(CpuRenderer.MinDimension - 1, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuRenderer.ValidateSize(64, CpuRenderer.MinDimension - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuRenderer.ValidateSize(CpuRenderer.MaxDimension + 1, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuRenderer.ValidateSize(64, CpuRenderer.MaxDimension + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuRenderer.ValidateSize(4096, 4096));

        var fb = new Framebuffer(8, 8, Rgb.Background);
        fb.FillRect(-10, -10, 20, 20, new Rgb(255, 0, 0), 0.5);
        Assert.Equal(8, fb.Width);
    }

    [Fact]
    public void ViewTransform_And_Rasterizer_NullGuardsAndDegenerateGeometry_NeverThrowUnhandled()
    {
        CameraState defaultCam = ViewState.Default.Camera;
        Assert.Throws<ArgumentNullException>(() => new ViewTransform(null!, 64, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewTransform(defaultCam, 0, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewTransform(defaultCam, 64, -1));

        var view = new ViewTransform(defaultCam, 32, 32);
        Assert.Throws<ArgumentNullException>(() => new Rasterizer(null!, in view));

        var fb = new Framebuffer(32, 32, Rgb.Background);
        var raster = new Rasterizer(fb, in view);
        var fill = new TriangleFill(true, true, new Rgb(200, 100, 50), 1.0);
        var stroke = new StrokeStyle(new Rgb(255, 255, 255), 1.0, Rasterizer.LineHalfWidth, true);

        var colinearA = new ViewVertex(0, 0, 10);
        var colinearB = new ViewVertex(1, 1, 10);
        var colinearC = new ViewVertex(2, 2, 10);
        raster.FillTriangle(in colinearA, in colinearB, in colinearC, in fill);

        var behind = new ViewVertex(0, 0, -5);
        raster.FillTriangle(in behind, in behind, in behind, in fill);

        raster.StrokeScreen(double.NaN, 0, 1, 10, 10, 1, in stroke);
        raster.StrokeScreen(0, 0, 1, double.PositiveInfinity, 10, 1, in stroke);

        TileKey tile = Views.TileOf(Views.TopCamera);
        var builder = new GeometryBuilder(LayerIds.Terrain, tile, new WorldPoint(0, 0, 0));
        uint v0 = builder.AddVertex(100, 200, 0);
        uint v1 = builder.AddVertex(100, 200, 0);
        uint v2 = builder.AddVertex(100, 200, 0);
        builder.AddTriangle(v0, v1, v2, 0);
        builder.AddLine(v0, v1, 0);
        builder.AddPoint(v0, 0);
        GeometryChunk degenerate = builder.Build();

        var rig = new RenderRig(new FakeSource().Add(LayerIds.Terrain, tile, degenerate));
        RenderedFrame rendered = rig.Frame(Views.Of(Views.TopCamera, (LayerIds.Terrain, Views.Style(mode: RenderMode.Both))), 32, 32);
        Assert.Equal(32, rendered.Buffer.Width);
    }

    [Fact]
    public void PngEncoder_InvalidDimensionsAndBufferLengths_ThrowExpectedExceptions()
    {
        byte[] valid = new byte[4 * 4 * 4];
        Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.Encode(valid, 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.Encode(valid, 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.Encode(valid, -1, 4));
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(new byte[10], 4, 4));

        byte[] png = PngEncoder.Encode(valid, 4, 4);
        Assert.True(png.Length > 8);
    }

    [Fact]
    public void OutputPaths_WorkingTreeAndExtensionGuards_EnforceSafety()
    {
        using var dir = new TempDirectory();
        Assert.ThrowsAny<ArgumentException>(() => OutputPaths.Resolve(null!, dir.File("a.png"), ".png", allowRelative: true));
        Assert.ThrowsAny<ArgumentException>(() => OutputPaths.Resolve("render.png", dir.File("a.png"), "", allowRelative: true));

        CommandException emptyOut = Assert.Throws<CommandException>(() =>
            OutputPaths.Resolve("render.png", "   ", ".png", allowRelative: true));
        Assert.Equal(CommandException.BadArguments, emptyOut.Code);

        CommandException relativeDisallowed = Assert.Throws<CommandException>(() =>
            OutputPaths.Resolve("render.png", "relative.png", ".png", allowRelative: false));
        Assert.Equal(CommandException.BadArguments, relativeDisallowed.Code);

        CommandException wrongExt = Assert.Throws<CommandException>(() =>
            OutputPaths.Resolve("render.png", dir.File("bad.jpg"), ".png", allowRelative: true));
        Assert.Equal(CommandException.BadArguments, wrongExt.Code);

        CommandException existingDir = Assert.Throws<CommandException>(() =>
            OutputPaths.Resolve("render.png", dir.Root, ".png", allowRelative: true));
        Assert.Equal(CommandException.BadArguments, existingDir.Code);

        string repoFile = Path.Combine(AppContext.BaseDirectory, "forbid.png");
        if (OutputPaths.FindWorkingTree(repoFile) is not null)
        {
            CommandException inRepo = Assert.Throws<CommandException>(() =>
                OutputPaths.Resolve("render.png", repoFile, ".png", allowRelative: true));
            Assert.Equal(CommandException.BadArguments, inRepo.Code);
        }
    }

    [Fact]
    public async Task RenderAndExportCommands_FuzzInvalidArguments_ReturnBadArguments()
    {
        using var dir = new TempDirectory();
        var source = new FakeSource().Serves(LayerIds.Terrain);
        var module = new TestModule(ctx => ctx.Layers.Add(source));
        using AtlasContext context = HostComposer.Compose(
            new AtlasPaths(null, null),
            NullLoggerFactory.Instance,
            headless: true,
            [new RenderModule(), module]);

        Task<CommandResult> RunAsync(string name, JsonObject args)
            => context.Commands.InvokeAsync(name, args, context.Services, CommandOrigin.Cli, headless: true, CancellationToken.None);

        CommandResult badWidth = await RunAsync("render.png", new JsonObject
        {
            ["out"] = dir.File("bad.png"),
            ["width"] = 0,
            ["height"] = 64,
        });
        Assert.False(badWidth.Ok);
        Assert.Equal(CommandException.BadArguments, badWidth.ErrorCode);

        CommandResult hugeHeight = await RunAsync("render.png", new JsonObject
        {
            ["out"] = dir.File("bad.png"),
            ["width"] = 64,
            ["height"] = 99999,
        });
        Assert.False(hugeHeight.Ok);
        Assert.Equal(CommandException.BadArguments, hugeHeight.ErrorCode);

        CommandResult badRadius = await RunAsync("export.obj", new JsonObject
        {
            ["out"] = dir.File("bad.obj"),
            ["map"] = 0,
            ["x"] = 0,
            ["y"] = 0,
            ["radius"] = 99,
        });
        Assert.False(badRadius.Ok);
        Assert.Equal(CommandException.BadArguments, badRadius.ErrorCode);

        CommandResult negRadius = await RunAsync("export.json", new JsonObject
        {
            ["out"] = dir.File("bad.json"),
            ["map"] = 0,
            ["x"] = 0,
            ["y"] = 0,
            ["radius"] = -1,
        });
        Assert.False(negRadius.Ok);
        Assert.Equal(CommandException.BadArguments, negRadius.ErrorCode);

        CommandResult partialCoords = await RunAsync("export.json", new JsonObject
        {
            ["out"] = dir.File("bad.json"),
            ["map"] = 0,
            ["x"] = 0,
        });
        Assert.False(partialCoords.Ok);
        Assert.Equal(CommandException.BadArguments, partialCoords.ErrorCode);

        CommandResult unknownExportLayer = await RunAsync("export.json", new JsonObject
        {
            ["out"] = dir.File("bad.json"),
            ["map"] = 0,
            ["x"] = 0,
            ["y"] = 0,
            ["layers"] = new JsonArray("not.a.layer"),
        });
        Assert.False(unknownExportLayer.Ok);
        Assert.Equal(CommandException.BadArguments, unknownExportLayer.ErrorCode);
    }
}
