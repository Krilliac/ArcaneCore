using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

/// <summary>The live client against an in-process fake feed over real named pipes (design section 9).</summary>
public sealed class LiveFeedTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static async Task<CommandException> FailureOf(Task<JsonObject> connect)
        => await Assert.ThrowsAsync<CommandException>(() => connect);

    [Fact]
    public async Task Hello_IsTheFirstLineTheClientSends_AndTheStatusReportsWhatTheServerSaid()
    {
        await using var rig = new LiveRig();
        (FakeFeedServer server, Task<JsonObject> connect) = await rig.BeginConnectAsync();

        JsonObject hello = await server.ReadAsync();
        await server.WriteAsync(Wire.Hello(1, 4242, "cafe1234", (0, 0, 2, 310), (1, 0, 0, 90)));
        JsonObject status = await connect.WaitAsync(FakeFeedServer.Patience);

        Assert.Equal("""{"op":"hello","protocol":1}""", hello.ToJsonString());
        Assert.True(status["connected"]!.GetValue<bool>());
        Assert.Equal(4242, status["pid"]!.GetValue<int>());
        Assert.Equal("cafe1234", status["sha"]!.GetValue<string>());
        Assert.Equal(rig.PipeName, status["pipe"]!.GetValue<string>());
        Assert.Equal(2, status["serverMaps"]!.AsArray().Count);
        Assert.Equal(310, status["serverMaps"]![0]!["creatures"]!.GetValue<int>());
        Assert.True(rig.Feed.IsConnected);
    }

    [Fact]
    public async Task ConnectingPublishesALiveStatusEvent_WithExactlyTheDocumentedFields()
    {
        await using var rig = new LiveRig();

        await rig.ConnectAsync(pid: 4242, Wire.Hello(1, 4242, "feedface"));
        JsonObject pushed = (await rig.Log.NextAsync(EventHub.Topics.LiveStatus))!.AsObject();

        Assert.Equal(new[] { "connected", "detail", "pid", "sha" }, pushed.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.True(pushed["connected"]!.GetValue<bool>());
        Assert.Equal(4242, pushed["pid"]!.GetValue<int>());
        Assert.Equal("feedface", pushed["sha"]!.GetValue<string>());
        Assert.Contains("4242", pushed["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHelloWithAnotherProtocol_IsRefusedNamingBothNumbers_AndNothingStaysConnected()
    {
        await using var rig = new LiveRig();
        (FakeFeedServer server, Task<JsonObject> connect) = await rig.BeginConnectAsync();

        await server.AnswerHelloAsync(Wire.Hello(protocol: 2));
        CommandException e = await FailureOf(connect);

        Assert.Equal(CommandException.Failed, e.Code);
        Assert.Contains("protocol 2", e.Message, StringComparison.Ordinal);
        Assert.Contains("speaks 1", e.Message, StringComparison.Ordinal);
        Assert.False(rig.Feed.IsConnected);
        Assert.Empty(rig.Log.Drain(EventHub.Topics.LiveStatus));
        Assert.Null(await server.ReadLineAsync());
        Assert.Contains("protocol", rig.Feed.Status()["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorInsteadOfHello_IsRefusedWithTheServersCodeAndMessage()
    {
        await using var rig = new LiveRig();
        (FakeFeedServer server, Task<JsonObject> connect) = await rig.BeginConnectAsync();

        await server.AnswerHelloAsync(Obj("""{"op":"error","code":"too-many-clients","message":"all 2 client slots are in use"}"""));
        CommandException e = await FailureOf(connect);

        Assert.Equal(CommandException.Failed, e.Code);
        Assert.Contains("too-many-clients", e.Message, StringComparison.Ordinal);
        Assert.Contains("all 2 client slots are in use", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHelloFromAnotherProcess_IsRefused()
    {
        await using var rig = new LiveRig();
        (FakeFeedServer server, Task<JsonObject> connect) = await rig.BeginConnectAsync(pid: 4242);

        await server.AnswerHelloAsync(Wire.Hello(pid: 99));
        CommandException e = await FailureOf(connect);

        Assert.Equal(CommandException.Failed, e.Code);
        Assert.Contains("process 99", e.Message, StringComparison.Ordinal);
        Assert.Contains("process 4242", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedHello_IsRefused()
    {
        await using var rig = new LiveRig();
        (FakeFeedServer server, Task<JsonObject> connect) = await rig.BeginConnectAsync();

        await server.AnswerHelloAsync(Obj("""{"op":"hello","sha":"x"}"""));
        CommandException e = await FailureOf(connect);

        Assert.Equal(CommandException.Failed, e.Code);
        Assert.Contains("malformed", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASilentPeer_FailsTheHandshakeAfterTheHelloTimeout()
    {
        await using var rig = new LiveRig(o => o with { HelloTimeout = TimeSpan.FromMilliseconds(300) });
        (FakeFeedServer server, Task<JsonObject> connect) = await rig.BeginConnectAsync();
        _ = await server.ReadAsync();

        CommandException e = await FailureOf(connect);

        Assert.Equal(CommandException.Failed, e.Code);
        Assert.Contains("did not answer hello", e.Message, StringComparison.Ordinal);
        Assert.False(rig.Feed.IsConnected);
    }

    [Fact]
    public async Task APeerThatHangsUpBeforeHello_IsUnavailable()
    {
        await using var rig = new LiveRig();
        (FakeFeedServer server, Task<JsonObject> connect) = await rig.BeginConnectAsync();
        _ = await server.ReadAsync();

        server.Drop();
        CommandException e = await FailureOf(connect);

        Assert.Equal(CommandException.Unavailable, e.Code);
        Assert.Contains("before answering hello", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoPipeAtAll_IsUnavailableAndSaysWhichPipe()
    {
        await using var rig = new LiveRig(o => o with { ConnectTimeout = TimeSpan.FromMilliseconds(300) });

        CommandException e = await Assert.ThrowsAsync<CommandException>(() => rig.Feed.ConnectAsync(4242, CancellationToken.None));

        Assert.Equal(CommandException.Unavailable, e.Code);
        Assert.Contains(rig.PipeName, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADiscoveryThatKnowsThePipeIsNotThere_FailsWithoutTryingToConnect()
    {
        await using var rig = new LiveRig(discovery: new GoneDiscovery());

        CommandException e = await Assert.ThrowsAsync<CommandException>(() => rig.Feed.ConnectAsync(4242, CancellationToken.None));

        Assert.Equal(CommandException.Unavailable, e.Code);
        Assert.Contains("is not running", e.Message, StringComparison.Ordinal);
    }

    private sealed class GoneDiscovery : IFeedDiscovery
    {
        public IReadOnlyList<int> FindFeeds() => [];

        public bool? PipeExists(string pipeName) => false;
    }

    [Fact]
    public async Task ConnectingWithoutAPid_UsesTheOnlyDiscoveredFeed()
    {
        await using var rig = new LiveRig(discovery: new FixedDiscovery(777));
        FakeFeedServer server = rig.NewServer();
        Task accept = server.AcceptAsync();
        Task<CommandResult> connect = rig.RunAsync("live.connect");
        await accept;

        await server.AnswerHelloAsync(Wire.Hello(pid: 777));
        CommandResult result = await connect.WaitAsync(FakeFeedServer.Patience);

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal(777, result.Data!["pid"]!.GetValue<int>());
    }

    [Fact]
    public async Task ConnectingWithoutAPid_WhenNoFeedRuns_IsUnavailable()
    {
        await using var rig = new LiveRig(discovery: new FixedDiscovery());

        CommandResult result = await rig.RunAsync("live.connect");

        Assert.False(result.Ok);
        Assert.Equal(CommandException.Unavailable, result.ErrorCode);
        Assert.Contains("no map feed found", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectingWithoutAPid_WhenSeveralFeedsRun_AsksForOne()
    {
        await using var rig = new LiveRig(discovery: new FixedDiscovery(10, 20));

        CommandResult result = await rig.RunAsync("live.connect");

        Assert.False(result.Ok);
        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        Assert.Contains("10, 20", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("\"abc\"")]
    [InlineData("2147483648")]
    public async Task ConnectRefusesANonsensePid(string pid)
    {
        await using var rig = new LiveRig();

        CommandResult result = await rig.RunAsync("live.connect", Obj($$"""{"pid":{{pid}}}"""));

        Assert.False(result.Ok);
        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
    }

    [Fact]
    public async Task Subscribe_SendsTheDocumentedMessage_AndTheDefaultsAreFourHertzEveryKindWithPaths()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();

        CommandResult custom = await rig.RunAsync("live.subscribe", Obj("""{"map":0,"instance":3,"hz":2.5,"kinds":["player","creature"],"paths":false}"""));
        JsonObject customLine = await server.ReadAsync();
        CommandResult defaults = await rig.RunAsync("live.subscribe", Obj("""{"map":1}"""));
        JsonObject defaultLine = await server.ReadAsync();

        Assert.True(custom.Ok, custom.ErrorMessage);
        Assert.Equal("""{"op":"subscribe","map":0,"instance":3,"hz":2.5,"kinds":["player","creature"],"paths":false}""", customLine.ToJsonString());
        Assert.True(defaults.Ok, defaults.ErrorMessage);
        Assert.Equal("""{"op":"subscribe","map":1,"instance":0,"hz":4,"kinds":["player","bot","creature","gameobject"],"paths":true}""", defaultLine.ToJsonString());
        Assert.Equal(2, defaults.Data!["subscriptions"]!.AsArray().Count);
    }

    [Fact]
    public async Task SubscribingTheSameMapAgain_ReplacesTheRememberedRequest()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();

        await rig.RunAsync("live.subscribe", Obj("""{"map":0,"hz":1}"""));
        _ = await server.ReadAsync();
        await rig.RunAsync("live.subscribe", Obj("""{"map":0,"hz":3}"""));
        _ = await server.ReadAsync();

        JsonArray subscriptions = rig.Feed.Status()["subscriptions"]!.AsArray();
        Assert.Equal(3.0, Assert.Single(subscriptions)!["hz"]!.GetValue<double>());
    }

    [Fact]
    public async Task Frames_LandInTheStoreAndAreForwardedToTheHub()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();
        await rig.Log.NextAsync(EventHub.Topics.LiveStatus);

        await server.WriteAsync(Wire.Frame(
            0, 0, 1, Wire.Entity("0x1A", "player", -9462.66, 16.19, 57.05, "Nathan"), Wire.Entity("2B", "creature", -9400, 10, 55), Wire.Entity("3C", "bot", -9300, 5, 50)));
        JsonObject pushed = (await rig.Log.NextAsync(EventHub.Topics.LiveFrame))!.AsObject();

        Assert.Equal(1, pushed["seq"]!.GetValue<long>());
        Assert.Equal(3, pushed["entities"]!.AsArray().Count);
        Assert.Equal("1A", pushed["entities"]![0]!["g"]!.GetValue<string>());
        Assert.Equal(3, rig.Feed.Store.EntityCount);
        Assert.True(rig.Feed.Store.TryGetEntity(0x1A, out LiveEntity? player));
        Assert.Equal("Nathan", player.Name);
        Assert.Equal(new WorldPoint(-9462.66, 16.19, 57.05), player.Position);

        JsonObject status = rig.Feed.Status();
        JsonObject frame = status["frames"]![0]!.AsObject();
        Assert.Equal(1, frame["seq"]!.GetValue<long>());
        Assert.Equal(3, frame["entities"]!.GetValue<int>());
        Assert.Equal(1, frame["byKind"]!["player"]!.GetValue<int>());
        Assert.Equal(2, frame["grids"]!.GetValue<int>());
        Assert.Equal(7, frame["counters"]!["deferredPathSearches"]!.GetValue<int>());
        Assert.Equal(3, status["entities"]!["total"]!.GetValue<int>());
        Assert.Equal(1, status["stats"]!["frames"]!.GetValue<long>());
    }

    [Fact]
    public async Task ANewFrame_ReplacesTheOldOne_AndAnEntityItOmitsDisappears()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();

        await server.WriteAsync(Wire.Frame(0, 0, 1, Wire.Entity("1", "creature", 0, 0, 0), Wire.Entity("2", "creature", 1, 1, 1)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);
        await server.WriteAsync(Wire.Frame(0, 0, 2, Wire.Entity("2", "creature", 5, 5, 5)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);

        Assert.False(rig.Feed.Store.TryGetEntity(1, out _));
        Assert.True(rig.Feed.Store.TryGetEntity(2, out LiveEntity? kept));
        Assert.Equal(new WorldPoint(5, 5, 5), kept.Position);
        Assert.Equal(2, rig.Feed.Store.Trail(2).Count);
    }

    [Fact]
    public async Task BadLines_AreCountedAndReported_AndTheConnectionKeepsWorking()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();
        JsonObject withBadEntities = Wire.Frame(
            0,
            0,
            2,
            Wire.Entity("A", "creature", 1, 2, 3),
            Obj("""{"g":"B","k":"vehicle","x":1,"y":2,"z":3}"""),
            Obj("""{"g":"C","k":"creature","x":1e999,"y":2,"z":3}"""),
            Wire.Entity("D", "bot", 4, 5, 6));

        await server.WriteLineAsync("this is not json");
        await server.WriteLineAsync("[1,2,3]");
        await server.WriteLineAsync("""{"seq":1}""");
        await server.WriteLineAsync("""{"op":"frame","seq":1}""");
        await server.WriteLineAsync("""{"op":"telemetry","data":1}""");
        await server.WriteLineAsync("{\"op\":\"frame\",\"map\":0,\"entities\":[");
        await server.WriteLineAsync("");
        await server.WriteLineAsync("\r");
        await server.WriteAsync(withBadEntities);
        JsonObject pushed = (await rig.Log.NextAsync(EventHub.Topics.LiveFrame))!.AsObject();

        JsonObject stats = rig.Feed.Status()["stats"]!.AsObject();
        Assert.Equal(5, stats["badMessages"]!.GetValue<long>());
        Assert.Equal(1, stats["unknownOps"]!.GetValue<long>());
        Assert.Equal(2, stats["badItems"]!.GetValue<long>());
        Assert.Equal(1, stats["frames"]!.GetValue<long>());
        Assert.NotNull(stats["lastProblem"]);
        Assert.Equal(new ulong[] { 0xA, 0xD }, rig.Feed.Store.Snapshot().Select(s => s.Entity.Id));
        Assert.Equal(2, pushed["entities"]!.AsArray().Count);
        Assert.True(rig.Feed.IsConnected);
    }

    [Fact]
    public async Task ALineOverEightMebibytes_IsSkipped_AndTheNextFrameStillArrives()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();

        byte[] huge = new byte[(8 * 1024 * 1024) + 10];
        Array.Fill(huge, (byte)'x');
        await server.WriteBytesAsync(huge);
        await server.WriteLineAsync(string.Empty);
        await server.WriteAsync(Wire.Frame(0, 0, 1, Wire.Entity("1", "creature", 1, 2, 3)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);

        JsonObject stats = rig.Feed.Status()["stats"]!.AsObject();
        Assert.Equal(1, stats["oversizeLines"]!.GetValue<long>());
        Assert.Contains("exceeded", stats["lastProblem"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, rig.Feed.Store.EntityCount);
        Assert.True(rig.Feed.IsConnected);
    }

    [Fact]
    public async Task ALineAtExactlyEightMebibytes_IsStillRead()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();
        JsonObject frame = Wire.Frame(0, 0, 1, Wire.Entity("1", "creature", 1, 2, 3));
        string head = frame.ToJsonString();
        // Pad the frame with an unknown string member until the line is exactly the limit.
        int padding = LiveProtocol.MaxLineBytes - head.Length - ",\"pad\":\"\"".Length;
        frame["pad"] = new string('p', padding);
        string line = frame.ToJsonString();
        Assert.Equal(LiveProtocol.MaxLineBytes, Encoding.UTF8.GetByteCount(line));

        await server.WriteLineAsync(line);
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);

        Assert.Equal(1, rig.Feed.Store.EntityCount);
        Assert.Equal(0, rig.Feed.Status()["stats"]!["oversizeLines"]!.GetValue<long>());
    }

    [Fact]
    public async Task AThrowingEventSubscriber_DoesNotEndTheConnection()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();
        using IDisposable bad = rig.Events.Subscribe((_, _) => throw new InvalidOperationException("a UI bug"));

        await server.WriteAsync(Wire.Frame(0, 0, 1, Wire.Entity("1", "creature", 1, 2, 3)));
        await server.WriteAsync(Wire.Frame(0, 0, 2, Wire.Entity("1", "creature", 4, 5, 6), Wire.Entity("2", "creature", 7, 8, 9)));
        await Wait.UntilAsync(() => rig.Feed.Store.EntityCount == 2, "both frames applied");

        Assert.True(rig.Feed.IsConnected);
        Assert.Equal(0, rig.Feed.Status()["stats"]!["badMessages"]!.GetValue<long>());
    }

    [Fact]
    public async Task ServerDisconnect_IsReported_ClearsTheStore_AndFailsWhatWasWaiting()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();
        await rig.Log.NextAsync(EventHub.Topics.LiveStatus);
        await server.WriteAsync(Wire.Frame(0, 0, 1, Wire.Entity("1", "creature", 1, 2, 3)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);
        Task<CommandResult> waiting = rig.RunAsync("live.query", Obj("""{"kind":"height","a":[1,2,3],"map":0}"""));
        _ = await server.ReadAsync();

        server.Drop();
        CommandResult query = await waiting.WaitAsync(FakeFeedServer.Patience);
        JsonObject pushed = (await rig.Log.NextAsync(EventHub.Topics.LiveStatus))!.AsObject();

        Assert.False(pushed["connected"]!.GetValue<bool>());
        Assert.Contains("server closed", pushed["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(query.Ok);
        Assert.Equal(CommandException.Unavailable, query.ErrorCode);
        Assert.Contains("closed", query.ErrorMessage, StringComparison.Ordinal);
        Assert.False(rig.Feed.IsConnected);
        Assert.Equal(0, rig.Feed.Store.EntityCount);
        CommandResult entities = await rig.RunAsync("live.entities");
        Assert.False(entities.Data!["connected"]!.GetValue<bool>());
        Assert.Equal(0, entities.Data["matched"]!.GetValue<int>());
        CommandResult status = await rig.RunAsync("live.status");
        Assert.False(status.Data!["connected"]!.GetValue<bool>());
        Assert.Contains("server closed", status.Data["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, status.Data["stats"]!["frames"]!.GetValue<long>());
    }

    [Fact]
    public async Task AfterTheServerDisconnects_Reconnecting_AsksAgainForWhatWasSubscribed()
    {
        await using var rig = new LiveRig();
        FakeFeedServer first = await rig.ConnectAsync();
        await rig.RunAsync("live.subscribe", Obj("""{"map":0,"hz":2}"""));
        _ = await first.ReadAsync();
        await rig.RunAsync("live.subscribe", Obj("""{"map":1,"instance":4,"kinds":["bot"],"paths":false}"""));
        _ = await first.ReadAsync();
        await first.WriteAsync(Wire.Frame(0, 0, 1, Wire.Entity("1", "creature", 1, 2, 3)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);
        first.Drop();
        await Wait.UntilAsync(() => !rig.Feed.IsConnected, "the drop is noticed");
        Assert.Equal(0, rig.Feed.Store.EntityCount);

        FakeFeedServer second = rig.NewServer();
        Task accept = second.AcceptAsync();
        Task<CommandResult> reconnect = rig.RunAsync("live.connect", Obj("""{"pid":4242}"""));
        await accept;
        await second.AnswerHelloAsync(Wire.Hello(pid: 4242, sha: "newer"));
        CommandResult result = await reconnect.WaitAsync(FakeFeedServer.Patience);
        JsonObject again1 = await second.ReadAsync();
        JsonObject again2 = await second.ReadAsync();
        await second.WriteAsync(Wire.Frame(0, 0, 1, Wire.Entity("7", "player", 9, 9, 9)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal("newer", result.Data!["sha"]!.GetValue<string>());
        Assert.Equal("""{"op":"subscribe","map":0,"instance":0,"hz":2,"kinds":["player","bot","creature","gameobject"],"paths":true}""", again1.ToJsonString());
        Assert.Equal("""{"op":"subscribe","map":1,"instance":4,"hz":4,"kinds":["bot"],"paths":false}""", again2.ToJsonString());
        Assert.Equal(new ulong[] { 7 }, rig.Feed.Store.Snapshot().Select(s => s.Entity.Id));
        Assert.Equal(2, result.Data["connections"]!.GetValue<int>());
    }

    [Fact]
    public async Task ConnectingWhileConnected_ReplacesTheOldConnection()
    {
        string nameA = FakeFeedServer.NewName();
        string nameB = FakeFeedServer.NewName();
        await using var rig = new LiveRig(o => o with { PipeNameFor = pid => pid == 1 ? nameA : nameB });
        FakeFeedServer a = rig.NewServer(nameA);
        FakeFeedServer b = rig.NewServer(nameB);
        await rig.ConnectAsync(pid: 1, Wire.Hello(pid: 1), a);
        await a.WriteAsync(Wire.Frame(0, 0, 1, Wire.Entity("1", "creature", 1, 2, 3)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);

        await rig.ConnectAsync(pid: 2, Wire.Hello(pid: 2), b);

        Assert.Null(await a.ReadLineAsync());
        Assert.Equal(2, rig.Feed.Status()["pid"]!.GetValue<int>());
        Assert.Equal(0, rig.Feed.Store.EntityCount);

        // Earlier events were consumed while waiting for the frame; what is left is the old connection ending and the new one starting.
        List<JsonNode?> statuses = rig.Log.Drain(EventHub.Topics.LiveStatus);
        Assert.Equal(2, statuses.Count);
        Assert.False(statuses[0]!["connected"]!.GetValue<bool>());
        Assert.Equal("reconnecting", statuses[0]!["detail"]!.GetValue<string>());
        Assert.Equal(1, statuses[0]!["pid"]!.GetValue<int>());
        Assert.True(statuses[1]!["connected"]!.GetValue<bool>());
        Assert.Equal(2, statuses[1]!["pid"]!.GetValue<int>());
    }

    [Fact]
    public async Task Disconnect_TellsTheServer_ClosesTheLine_AndForgetsTheSubscriptions()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();
        await rig.RunAsync("live.subscribe", Obj("""{"map":0}"""));
        _ = await server.ReadAsync();
        await server.WriteAsync(Wire.Frame(0, 0, 1, Wire.Entity("1", "creature", 1, 2, 3)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);

        CommandResult result = await rig.RunAsync("live.disconnect");
        JsonObject unsubscribe = await server.ReadAsync();
        string? end = await server.ReadLineAsync();

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.True(result.Data!["wasConnected"]!.GetValue<bool>());
        Assert.False(result.Data["connected"]!.GetValue<bool>());
        Assert.Equal("""{"op":"unsubscribe"}""", unsubscribe.ToJsonString());
        Assert.Null(end);
        Assert.Empty(result.Data["subscriptions"]!.AsArray());
        Assert.Equal(0, rig.Feed.Store.EntityCount);
        JsonObject closed = (await rig.Log.NextAsync(EventHub.Topics.LiveStatus))!.AsObject();
        Assert.False(closed["connected"]!.GetValue<bool>());
        Assert.Equal("disconnected", closed["detail"]!.GetValue<string>());

        CommandResult again = await rig.RunAsync("live.disconnect");
        Assert.True(again.Ok);
        Assert.False(again.Data!["wasConnected"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Query_RoundTripsByIdAndReturnsTheServersFields()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();

        Task<CommandResult> pending = rig.RunAsync("live.query", Obj("""{"kind":"los","a":[1,2,3],"b":[4,5,6],"map":1,"instance":2}"""));
        JsonObject asked = await server.ReadAsync();
        long id = asked["id"]!.GetValue<long>();
        await server.WriteAsync(Wire.Result(
            id,
            ("clear", JsonValue.Create(false)),
            ("hit", Wire.Point(3.5, 4.5, 5.5)),
            ("overlay", Obj("""{"segments":[{"from":[1,2,3],"to":[3.5,4.5,5.5]}]}"""))));
        CommandResult result = await pending.WaitAsync(FakeFeedServer.Patience);

        Assert.Equal("query", asked["op"]!.GetValue<string>());
        Assert.Equal("los", asked["kind"]!.GetValue<string>());
        Assert.Equal(1, asked["map"]!.GetValue<int>());
        Assert.Equal(2, asked["instance"]!.GetValue<int>());
        Assert.Equal("[1,2,3]", asked["a"]!.ToJsonString());
        Assert.Equal("[4,5,6]", asked["b"]!.ToJsonString());
        Assert.True(result.Ok, result.ErrorMessage);
        Assert.False(result.Data!["clear"]!.GetValue<bool>());
        Assert.Equal("[3.5,4.5,5.5]", result.Data["hit"]!.ToJsonString());
        Assert.NotNull(result.Data["overlay"]!["segments"]);
        Assert.Equal("los", result.Data["kind"]!.GetValue<string>());
        Assert.Equal(1, result.Data["map"]!.GetValue<int>());
        Assert.Null(result.Data.AsObject()["id"]);
        Assert.Null(result.Data.AsObject()["op"]);
        Assert.Null(result.Data.AsObject()["ok"]);
    }

    [Fact]
    public async Task Queries_AreCorrelatedById_WhateverOrderTheAnswersComeIn()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();

        Task<CommandResult> first = rig.RunAsync("live.query", Obj("""{"kind":"height","a":[10,0,0],"map":0}"""));
        JsonObject askedFirst = await server.ReadAsync();
        Task<CommandResult> second = rig.RunAsync("live.query", Obj("""{"kind":"height","a":[20,0,0],"map":0}"""));
        JsonObject askedSecond = await server.ReadAsync();
        await server.WriteAsync(Wire.Result(askedSecond["id"]!.GetValue<long>(), ("z", JsonValue.Create(222))));
        await server.WriteAsync(Wire.Result(askedFirst["id"]!.GetValue<long>(), ("z", JsonValue.Create(111))));

        CommandResult a = await first.WaitAsync(FakeFeedServer.Patience);
        CommandResult b = await second.WaitAsync(FakeFeedServer.Patience);

        Assert.NotEqual(askedFirst["id"]!.GetValue<long>(), askedSecond["id"]!.GetValue<long>());
        Assert.Equal(111, a.Data!["z"]!.GetValue<int>());
        Assert.Equal(222, b.Data!["z"]!.GetValue<int>());
    }

    [Fact]
    public async Task AQueryTheServerNeverAnswers_FailsWithATimeout_AndALateAnswerIsHarmless()
    {
        await using var rig = new LiveRig(o => o with { QueryTimeout = TimeSpan.FromMilliseconds(300) });
        FakeFeedServer server = await rig.ConnectAsync();

        Task<CommandResult> pending = rig.RunAsync("live.query", Obj("""{"kind":"undermap","a":[1,2,3],"map":0}"""));
        JsonObject asked = await server.ReadAsync();
        CommandResult result = await pending.WaitAsync(FakeFeedServer.Patience);
        await server.WriteAsync(Wire.Result(asked["id"]!.GetValue<long>(), ("under", JsonValue.Create(true))));
        await server.WriteAsync(Wire.Frame(0, 0, 1));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);

        Assert.False(result.Ok);
        Assert.Equal(CommandException.Failed, result.ErrorCode);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, rig.Feed.Status()["stats"]!["lateResults"]!.GetValue<long>());
        Assert.True(rig.Feed.IsConnected);
    }

    [Fact]
    public void TheDefaultQueryTimeoutIsFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), LiveOptions.Default.QueryTimeout);
    }

    [Fact]
    public async Task AQueryTheServerRefuses_FailsWithItsReason()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();

        Task<CommandResult> notOk = rig.RunAsync("live.query", Obj("""{"kind":"path","a":[1,2,3],"b":[4,5,6],"map":0}"""));
        JsonObject first = await server.ReadAsync();
        await server.WriteAsync(Obj($$"""{"op":"result","id":{{first["id"]}},"ok":false,"message":"map 0 has no loaded grid at a"}"""));
        CommandResult refused = await notOk.WaitAsync(FakeFeedServer.Patience);

        Task<CommandResult> errored = rig.RunAsync("live.query", Obj("""{"kind":"height","a":[1,2,3],"map":0}"""));
        JsonObject second = await server.ReadAsync();
        await server.WriteAsync(Obj($$"""{"op":"error","id":{{second["id"]}},"code":"bad-point","message":"coordinates are not finite"}"""));
        CommandResult error = await errored.WaitAsync(FakeFeedServer.Patience);

        Assert.False(refused.Ok);
        Assert.Equal(CommandException.Failed, refused.ErrorCode);
        Assert.Contains("map 0 has no loaded grid at a", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.False(error.Ok);
        Assert.Contains("bad-point", error.ErrorMessage, StringComparison.Ordinal);
        Assert.True(rig.Feed.IsConnected);
    }

    [Fact]
    public async Task AQueryWithoutAConnection_IsUnavailable()
    {
        await using var rig = new LiveRig();

        CommandResult result = await rig.RunAsync("live.query", Obj("""{"kind":"height","a":[1,2,3],"map":0}"""));

        Assert.False(result.Ok);
        Assert.Equal(CommandException.Unavailable, result.ErrorCode);
        Assert.Contains("live.connect", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQueryWithoutAMap_AsksTheOnlySubscribedOne_ElseTheViewsMap()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();

        Task<CommandResult> viewMap = rig.RunAsync("live.query", Obj("""{"kind":"height","a":[1,2,3]}"""));
        JsonObject first = await server.ReadAsync();
        await server.WriteAsync(Wire.Result(first["id"]!.GetValue<long>()));
        await viewMap.WaitAsync(FakeFeedServer.Patience);

        await rig.RunAsync("live.subscribe", Obj("""{"map":530,"instance":6}"""));
        _ = await server.ReadAsync();
        Task<CommandResult> subscribed = rig.RunAsync("live.query", Obj("""{"kind":"height","a":[1,2,3]}"""));
        JsonObject second = await server.ReadAsync();
        await server.WriteAsync(Wire.Result(second["id"]!.GetValue<long>()));
        await subscribed.WaitAsync(FakeFeedServer.Patience);

        await rig.RunAsync("live.subscribe", Obj("""{"map":1}"""));
        _ = await server.ReadAsync();
        CommandResult ambiguous = await rig.RunAsync("live.query", Obj("""{"kind":"height","a":[1,2,3]}"""));

        Assert.Equal(0, first["map"]!.GetValue<int>());
        Assert.Equal(0, first["instance"]!.GetValue<int>());
        Assert.Equal(530, second["map"]!.GetValue<int>());
        Assert.Equal(6, second["instance"]!.GetValue<int>());
        Assert.False(ambiguous.Ok);
        Assert.Equal(CommandException.BadArguments, ambiguous.ErrorCode);
    }

    [Fact]
    public async Task Follow_MovesTheCameraToTheEntity_AndAgainWithEveryFrameThatMovesIt()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();
        await server.WriteAsync(Wire.Frame(0, 0, 1, Wire.Entity("1A", "player", 100, 200, 30), Wire.Entity("2", "creature", 5, 5, 5)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);

        CommandResult follow = await rig.RunAsync("live.follow", Obj("""{"guid":"0x1a"}"""));
        Assert.True(follow.Ok, follow.ErrorMessage);
        Assert.True(follow.Data!["found"]!.GetValue<bool>());
        Assert.Equal("1A", follow.Data["following"]!.GetValue<string>());
        Assert.Equal("1A", rig.View.Current.FollowGuid);
        Assert.Equal(new WorldPoint(100, 200, 30), rig.View.Current.Camera.Target);

        Task moved = NextTarget(rig.View, new WorldPoint(110, 205, 31));
        await server.WriteAsync(Wire.Frame(0, 0, 2, Wire.Entity("1A", "player", 110, 205, 31), Wire.Entity("2", "creature", 6, 6, 6)));
        await moved.WaitAsync(FakeFeedServer.Patience);

        Assert.Equal(new WorldPoint(110, 205, 31), rig.View.Current.Camera.Target);
        Assert.Equal("1A", rig.View.Current.FollowGuid);
    }

    [Fact]
    public async Task Follow_IgnoresFramesThatDoNotMoveTheFollowedEntity()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();
        await server.WriteAsync(Wire.Frame(0, 0, 1, Wire.Entity("1A", "player", 100, 200, 30), Wire.Entity("2", "creature", 5, 5, 5)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);
        await rig.RunAsync("live.follow", Obj("""{"guid":"1A"}"""));

        // The user pans away; the followed entity stands still while another one moves: the camera must stay where the user put it.
        rig.View.Patch(Obj("""{"camera":{"target":[-50,-50,-50]}}"""), "ui");
        await server.WriteAsync(Wire.Frame(0, 0, 2, Wire.Entity("1A", "player", 100, 200, 30), Wire.Entity("2", "creature", 99, 99, 99)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);
        await server.WriteAsync(Wire.Frame(0, 0, 3, Wire.Entity("1A", "player", 100, 200, 30), Wire.Entity("2", "creature", 98, 98, 98)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);
        await server.WriteAsync(Wire.Frame(0, 0, 4));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);

        // A frame is pushed before its follow step runs, so the push of frame 4 proves frame 3 was fully handled.
        Assert.Equal(new WorldPoint(-50, -50, -50), rig.View.Current.Camera.Target);
    }

    [Fact]
    public async Task Follow_SwitchesTheViewToTheEntitysMap()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();
        await server.WriteAsync(Wire.Frame(1, 0, 1, Wire.Entity("1A", "player", -4000, 1000, 20)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);
        Assert.Equal(0u, rig.View.Current.MapId);

        CommandResult follow = await rig.RunAsync("live.follow", Obj("""{"guid":"1A"}"""));

        Assert.True(follow.Ok, follow.ErrorMessage);
        Assert.Equal(1u, rig.View.Current.MapId);
        Assert.Equal(new WorldPoint(-4000, 1000, 20), rig.View.Current.Camera.Target);
    }

    [Fact]
    public async Task Follow_OfAnEntityNotYetSeen_SetsTheViewAndMovesTheCameraWhenItAppears()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();

        CommandResult follow = await rig.RunAsync("live.follow", Obj("""{"guid":"BEEF"}"""));
        Task moved = NextTarget(rig.View, new WorldPoint(7, 8, 9));
        await server.WriteAsync(Wire.Frame(0, 0, 1, Wire.Entity("BEEF", "bot", 7, 8, 9)));
        await moved.WaitAsync(FakeFeedServer.Patience);

        Assert.True(follow.Ok, follow.ErrorMessage);
        Assert.False(follow.Data!["found"]!.GetValue<bool>());
        Assert.Contains("follows it as soon as", follow.Data["note"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("BEEF", rig.View.Current.FollowGuid);
    }

    [Fact]
    public async Task Follow_SetByTheUiThroughTheViewAlone_AlsoMovesTheCamera()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();
        rig.View.Patch(Obj("""{"follow":"c0de"}"""), "ui");

        Task moved = NextTarget(rig.View, new WorldPoint(1, 2, 3));
        await server.WriteAsync(Wire.Frame(0, 0, 1, Wire.Entity("0xC0DE", "creature", 1, 2, 3)));
        await moved.WaitAsync(FakeFeedServer.Patience);

        Assert.Equal(new WorldPoint(1, 2, 3), rig.View.Current.Camera.Target);
    }

    [Fact]
    public async Task Follow_WithNoGuid_StopsFollowing_AndLaterFramesLeaveTheCameraAlone()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();
        await server.WriteAsync(Wire.Frame(0, 0, 1, Wire.Entity("1A", "player", 100, 200, 30)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);
        await rig.RunAsync("live.follow", Obj("""{"guid":"1A"}"""));
        Assert.Equal("1A", rig.View.Current.FollowGuid);

        CommandResult stop = await rig.RunAsync("live.follow", Obj("""{"guid":null}"""));
        CommandResult stopAgain = await rig.RunAsync("live.follow");
        await server.WriteAsync(Wire.Frame(0, 0, 2, Wire.Entity("1A", "player", 900, 900, 90)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);
        await server.WriteAsync(Wire.Frame(0, 0, 3, Wire.Entity("1A", "player", 901, 901, 91)));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);
        await server.WriteAsync(Wire.Frame(0, 0, 4));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);

        Assert.True(stop.Ok, stop.ErrorMessage);
        Assert.True(stopAgain.Ok);
        Assert.Null(stop.Data!["following"]);
        Assert.Null(rig.View.Current.FollowGuid);
        Assert.Equal(new WorldPoint(100, 200, 30), rig.View.Current.Camera.Target);
    }

    [Fact]
    public async Task ADisposedClient_FailsFurtherConnects_AndStopsTheConnection()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();

        rig.Feed.Dispose();
        rig.Feed.Dispose();

        Assert.Null(await server.ReadLineAsync());
        Assert.False(rig.Feed.IsConnected);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Feed.ConnectAsync(4242, CancellationToken.None));
    }

    private static Task NextTarget(ViewStore view, WorldPoint expected)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(ViewState state, long version, string origin)
        {
            if (state.Camera.Target == expected)
            {
                done.TrySetResult();
            }
        }

        view.Changed += OnChanged;
        return done.Task.ContinueWith(_ => view.Changed -= OnChanged, TaskScheduler.Default);
    }
}
