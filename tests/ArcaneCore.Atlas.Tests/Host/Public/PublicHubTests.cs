using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Host.Public;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host.Public;

/// <summary>
/// The public sockets (design 0.3, 0.8, 0.11) driven through a real listener: a fake feed puts frames into the frame store, a manual clock paces the
/// hub, and ordinary client WebSockets read what a browser would. Covers the projection per tier, the byte budget (frames are dropped, never queued),
/// the message limits and the close rules.
/// </summary>
public sealed class PublicHubTests
{
    private const ulong ZedGuid = 0x1234_5678_90AB_CDEF;
    private const string ZedGuidDecimal = "1311768467294899695";
    private const ulong BoarGuid = 0x0000_0000_0000_00C5;

    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    private static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    /// <summary>Connects, sends the <c>auth</c> frame and reads the hello; the socket is then in the frame stream.</summary>
    private static async Task<ClientWebSocket> OpenAsync(PublicRig rig, Visitor visitor)
    {
        ClientWebSocket socket = await rig.ConnectAsync(visitor);
        await socket.SendJsonAsync(new JsonObject { ["op"] = "auth", ["csrf"] = visitor.Csrf });
        Assert.NotNull(await socket.ReceiveUntilAsync("hello"));
        return socket;
    }

    private static void AllowLinks(PublicRig rig, long seq)
        => rig.Runtime.Frames.SetLinks(0, new LinksSnapshot(true, seq, new HashSet<ulong>(), new HashSet<uint>(), new HashSet<uint>()));

    private static LiveFrame FrameWithZedAndABoar(long seq)
        => Frames.Frame(
            seq,
            Frames.Entity(ZedGuid, LiveKind.Player, "Zed", 4441.5, 5551.5),
            Frames.Entity(BoarGuid, LiveKind.Creature, "Boar", 300.25, 400.75, motion: "idle", entry: 5, home: new WorldPoint(7771, 8881, 10)));

    private static LiveFrame FrameOfBoars(long seq, int count)
        => Frames.Frame(seq, [.. Enumerable.Range(0, count).Select(i => Frames.Entity(1000UL + (ulong)i, LiveKind.Creature, "Dire Boar Number " + i, 100 + i, 200 + i, entry: 5))]);

    // ---- projection ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AGuestSocket_IsToldWaiting_ThenReceivesTheProjectedFrame_WithItsOwnOpaqueIds()
    {
        var time = new ManualTime();
        await using PublicRig rig = await PublicRig.StartAsync(time: time);
        Visitor visitor = await rig.VisitAsync();
        using ClientWebSocket socket = await OpenAsync(rig, visitor);

        JsonNode first = (await socket.ReceiveUntilAsync("event", "live.status"))!;
        Assert.False(first["data"]!["connected"]!.GetValue<bool>());
        Assert.Equal("waiting", first["data"]!["detail"]!.GetValue<string>());
        Assert.True(await WaitAsync(() => rig.World.Demanded.SequenceEqual(new uint[] { 0 })), "the first public map was not demanded for this socket");

        AllowLinks(rig, 1);
        rig.Runtime.Frames.Ingest(FrameWithZedAndABoar(1));
        string text = (await socket.ReceiveUntilAsync("event", "live.frame"))!.ToJsonString();

        Assert.DoesNotContain("Zed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("4441.5", text, StringComparison.Ordinal);
        Assert.DoesNotContain("5551.5", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEF", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ZedGuidDecimal, text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tg\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"home\"", text, StringComparison.Ordinal);
        Assert.Contains("\"Boar\"", text, StringComparison.Ordinal);
        PublicSession session = rig.Runtime.Sessions.Find(visitor.Cookie)!;
        Assert.Contains(GuestFrameProjector.OpaqueId(session.OpaqueKey, BoarGuid), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountsAtModeratorOrAbove_SeePlayersByName_GuestsAndLevelZeroAccountsDoNot()
    {
        var time = new ManualTime();
        await using PublicRig rig = await PublicRig.StartAsync(o => o.Login.AllowBelowMinActionSecurity = true, time: time);
        rig.World.Accounts["gm"] = ("pw", 1);
        rig.World.Accounts["plain"] = ("pw", 0);
        Visitor guest = await rig.VisitAsync();
        Visitor gm = await rig.LoginAsync(await rig.VisitAsync(), "gm", "pw");
        Visitor plain = await rig.LoginAsync(await rig.VisitAsync(), "plain", "pw");
        using ClientWebSocket guestSocket = await OpenAsync(rig, guest);
        using ClientWebSocket gmSocket = await OpenAsync(rig, gm);
        using ClientWebSocket plainSocket = await OpenAsync(rig, plain);

        AllowLinks(rig, 1);
        rig.Runtime.Frames.Ingest(FrameWithZedAndABoar(1));
        string guestText = (await guestSocket.ReceiveUntilAsync("event", "live.frame"))!.ToJsonString();
        string gmText = (await gmSocket.ReceiveUntilAsync("event", "live.frame"))!.ToJsonString();
        string plainText = (await plainSocket.ReceiveUntilAsync("event", "live.frame"))!.ToJsonString();

        Assert.Contains("\"Zed\"", gmText, StringComparison.Ordinal);
        Assert.Contains("4441.5", gmText, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEF", gmText, StringComparison.OrdinalIgnoreCase); // the staff view still has opaque ids
        Assert.DoesNotContain("\"tg\"", gmText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"home\"", gmText, StringComparison.Ordinal);
        foreach (string text in new[] { guestText, plainText })
        {
            Assert.DoesNotContain("Zed", text, StringComparison.Ordinal);
            Assert.DoesNotContain("4441.5", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheLiveCommands_AnswerStaffAndGuestsFromTheirOwnProjection()
    {
        var time = new ManualTime();
        await using PublicRig rig = await PublicRig.StartAsync(time: time);
        rig.World.Accounts["gm"] = ("pw", 1);
        Visitor guest = await rig.VisitAsync();
        Visitor gm = await rig.LoginAsync(await rig.VisitAsync(), "gm", "pw");
        AllowLinks(rig, 1);
        rig.Runtime.Frames.Ingest(FrameWithZedAndABoar(1));

        string guestAnswer = (await rig.CommandAsync(guest, "live.entities", Json("""{"map":0}""")))["data"]!.ToJsonString();
        string gmAnswer = (await rig.CommandAsync(gm, "live.entities", Json("""{"map":0}""")))["data"]!.ToJsonString();
        JsonNode found = (await rig.CommandAsync(gm, "live.find", Json("""{"name":"zed"}""")))["data"]!;

        Assert.DoesNotContain("Zed", guestAnswer, StringComparison.Ordinal);
        Assert.Contains("Zed", gmAnswer, StringComparison.Ordinal);
        Assert.Equal(1, found["count"]!.GetValue<int>());
    }

    // ---- byte budget -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFrameOverTheAddressesByteBudget_IsDropped_NotQueued_AndTheNextOneThatFitsArrives()
    {
        var time = new ManualTime();
        await using PublicRig rig = await PublicRig.StartAsync(o => { o.Guest.MaxBytesPerSecond = 4096; o.Guest.FeedHz = 2; }, time: time);
        Visitor visitor = await rig.VisitAsync();
        using ClientWebSocket socket = await OpenAsync(rig, visitor);
        AllowLinks(rig, 3);

        rig.Runtime.Frames.Ingest(FrameOfBoars(1, 2));
        JsonNode small = (await socket.ReceiveUntilAsync("event", "live.frame"))!;
        Assert.Equal(1, small["data"]!["seq"]!.GetValue<long>());

        time.Advance(TimeSpan.FromSeconds(1)); // past the frame interval; the budget is one second of burst again
        rig.Runtime.Frames.Ingest(FrameOfBoars(2, 80)); // roughly 9 KiB: more than the 4 KiB budget can ever pay for
        Assert.True(await WaitAsync(() => rig.Runtime.Stats.FramesDropped == 1), "the oversized frame was not counted as dropped");

        time.Advance(TimeSpan.FromSeconds(1));
        rig.Runtime.Frames.Ingest(FrameOfBoars(3, 2));
        JsonNode next = (await socket.ReceiveUntilAsync("event", "live.frame"))!;
        Assert.Equal(3, next["data"]!["seq"]!.GetValue<long>()); // seq 2 never reaches the socket, now or later
        Assert.Equal(1, rig.Runtime.Stats.FramesDropped);
    }

    [Fact]
    public async Task TheByteBudgetIsPerAddress_SoASecondSocketOfTheSameAddressCannotDoubleIt()
    {
        var time = new ManualTime();
        await using PublicRig rig = await PublicRig.StartAsync(o => { o.Guest.MaxBytesPerSecond = 8192; o.Guest.FeedHz = 2; }, time: time);
        using ClientWebSocket one = await OpenAsync(rig, await rig.VisitAsync());
        using ClientWebSocket two = await OpenAsync(rig, await rig.VisitAsync());
        AllowLinks(rig, 1);

        // A frame of about 3.3 KiB in an 8 KiB budget (reserve 2 KiB): one fits beside the reserve, two do not, and the margin on each side is
        // about 1 KiB. The hello and the status notes the hub charges first are timing dependent (a tick between the socket becoming ready and
        // the ingest adds a note per socket), so a frame sized to fit with a margin of tens of bytes flaked: a few extra notes dropped both.
        rig.Runtime.Frames.Ingest(FrameOfBoars(1, 27));
        Assert.True(await WaitAsync(() => rig.Runtime.Stats.FramesDropped == 1), "one of the two sockets should have had its frame dropped");

        bool oneGotIt = await GotFrameAsync(one);
        bool twoGotIt = await GotFrameAsync(two);
        Assert.Equal(1, new[] { oneGotIt, twoGotIt }.Count(g => g));
        Assert.Equal(1, rig.Runtime.Stats.FramesDropped);
    }

    [Fact]
    public async Task AnHttpAnswerOverTheAddressesByteBudget_IsRefusedWith429_AndTheSameAnswerPassesWithTheDefaultBudget()
    {
        var time = new ManualTime();
        await using PublicRig small = await PublicRig.StartAsync(o => o.Guest.MaxBytesPerSecond = 4096, time: time);
        await using PublicRig normal = await PublicRig.StartAsync(time: time);
        var args = Json("""{"map":0}""");
        foreach (PublicRig rig in new[] { small, normal })
        {
            AllowLinks(rig, 1);
            rig.Runtime.Frames.Ingest(FrameOfBoars(1, 60));
        }

        Visitor visitor = await normal.VisitAsync();
        Reply big = await normal.PostAsync("/api/public/command", visitor, new JsonObject { ["name"] = "live.entities", ["args"] = args.DeepClone() });
        Visitor smallVisitor = await small.VisitAsync();
        Reply refused = await small.PostAsync("/api/public/command", smallVisitor, new JsonObject { ["name"] = "live.entities", ["args"] = args.DeepClone() });
        Reply control = await small.PostAsync("/api/public/command", smallVisitor, new JsonObject { ["name"] = "guest.maps" });

        Assert.Equal(200, big.Status);
        Assert.True(big.Text.Length > 4096, $"the answer is {big.Text.Length} bytes: the budget below must be smaller than it");
        Assert.Equal(429, refused.Status);
        Assert.Equal("rate-limited", refused.Code);
        Assert.NotNull(refused.Header("Retry-After"));
        Assert.Equal(200, control.Status); // a small answer still fits: it is the budget, not the route
    }

    [Fact]
    public async Task TheByteBudgetIsSharedByHttpAndTheSockets()
    {
        var time = new ManualTime();
        await using PublicRig rig = await PublicRig.StartAsync(o => o.Guest.MaxBytesPerSecond = 4096, time: time);
        Visitor visitor = await rig.VisitAsync();

        // Spend the address's budget over HTTP with small answers until one is refused, then see a socket of the same address feel it.
        int served = 0;
        Reply last;
        do
        {
            last = await rig.PostAsync("/api/public/command", visitor, new JsonObject { ["name"] = "guest.help" });
            served += last.Status == 200 ? 1 : 0;
        }
        while (last.Status == 200 && served < 35);

        Assert.Equal(429, last.Status);
        Assert.True(served > 0);
        using ClientWebSocket socket = await OpenAsync(rig, visitor); // hello and status are charged but still delivered
        await socket.SendJsonAsync(new JsonObject { ["op"] = "command", ["id"] = 7, ["name"] = "guest.help" });
        JsonNode result = (await socket.ReceiveUntilAsync("result"))!;
        Assert.Equal(7, result["id"]!.GetValue<long>());
        Assert.False(result["result"]!["ok"]!.GetValue<bool>());
        Assert.Equal("rate-limited", result["result"]!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task AWebSocketCommandResultOverTheByteBudget_IsReplacedByARateLimitedRefusal()
    {
        var time = new ManualTime();
        await using PublicRig rig = await PublicRig.StartAsync(o => o.Guest.MaxBytesPerSecond = 4096, time: time);
        Visitor visitor = await rig.VisitAsync();
        using ClientWebSocket socket = await OpenAsync(rig, visitor);
        AllowLinks(rig, 1);
        rig.Runtime.Frames.Ingest(FrameOfBoars(1, 60));

        await socket.SendJsonAsync(new JsonObject { ["op"] = "command", ["id"] = 1, ["name"] = "live.entities", ["args"] = Json("""{"map":0}""") });
        JsonNode refused = (await socket.ReceiveUntilAsync("result"))!;

        Assert.Equal(1, refused["id"]!.GetValue<long>());
        Assert.False(refused["result"]!["ok"]!.GetValue<bool>());
        Assert.Equal("rate-limited", refused["result"]!["error"]!["code"]!.GetValue<string>());
        Assert.True(refused.ToJsonString().Length < 600);
    }

    [Fact]
    public async Task AGuestWhoseFrameWasDroppedForSize_IsToldSoInTheNextStatusNote()
    {
        var time = new ManualTime();
        await using PublicRig rig = await PublicRig.StartAsync(o => { o.Guest.MaxBytesPerSecond = 4096; o.Guest.FeedHz = 2; }, time: time);
        using ClientWebSocket socket = await OpenAsync(rig, await rig.VisitAsync());
        AllowLinks(rig, 1);

        rig.Runtime.Frames.Ingest(FrameOfBoars(1, 80)); // about 9 KiB: the budget can never pay for it
        Assert.True(await WaitAsync(() => rig.Runtime.Stats.FramesDropped == 1), "the oversized frame was not counted as dropped");

        DateTime end = DateTime.UtcNow.AddSeconds(10);
        string detail = string.Empty;
        while (detail != "throttled" && DateTime.UtcNow < end)
        {
            JsonNode status = (await socket.ReceiveUntilAsync("event", "live.status"))!;
            detail = status["data"]!["detail"]!.GetValue<string>();
        }

        Assert.Equal("throttled", detail);
    }

    private static async Task<bool> GotFrameAsync(ClientWebSocket socket)
    {
        try
        {
            return await socket.ReceiveUntilAsync("event", "live.frame", timeoutMs: 2500) is not null;
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            return false;
        }
    }

    // ---- messages ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASocketAnswersPing_RefusesBadMessages_AndRunsAllowlistedCommandsOnly()
    {
        await using PublicRig rig = await PublicRig.StartAsync();
        using ClientWebSocket socket = await OpenAsync(rig, await rig.VisitAsync());

        await socket.SendJsonAsync(new JsonObject { ["op"] = "ping" });
        Assert.NotNull(await socket.ReceiveUntilAsync("pong"));

        await socket.SendAsync(Encoding.UTF8.GetBytes("this is not json"), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Equal("bad-message", (await socket.ReceiveUntilAsync("error"))!["code"]!.GetValue<string>());

        await socket.SendJsonAsync(new JsonObject { ["op"] = "teleport" });
        Assert.Equal("bad-message", (await socket.ReceiveUntilAsync("error"))!["code"]!.GetValue<string>());

        await socket.SendJsonAsync(new JsonObject { ["op"] = "command", ["id"] = 7, ["name"] = "guest.status" });
        JsonNode ok = (await socket.ReceiveUntilAsync("result"))!;
        Assert.Equal(7, ok["id"]!.GetValue<int>());
        Assert.True(ok["result"]!["ok"]!.GetValue<bool>());

        await socket.SendJsonAsync(new JsonObject { ["op"] = "command", ["id"] = 8, ["name"] = "server.console", ["args"] = new JsonObject { ["line"] = ".server shutdown" } });
        JsonNode denied = (await socket.ReceiveUntilAsync("result"))!;
        Assert.Equal(8, denied["id"]!.GetValue<int>());
        Assert.Equal("not-available", denied["result"]!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChoosingAMap_IsLimitedToThePublicOnes_AndMovesTheDemand()
    {
        await using PublicRig rig = await PublicRig.StartAsync();
        using ClientWebSocket socket = await OpenAsync(rig, await rig.VisitAsync());

        await socket.SendJsonAsync(new JsonObject { ["op"] = "view", ["map"] = 389 });
        Assert.Equal("bad-args", (await socket.ReceiveUntilAsync("error"))!["code"]!.GetValue<string>());
        await socket.SendJsonAsync(new JsonObject { ["op"] = "view", ["map"] = 1 });
        JsonNode status = (await socket.ReceiveUntilAsync("event", "live.status", timeoutMs: 20_000))!;
        Assert.True(await WaitAsync(() => rig.World.Demanded.SequenceEqual(new uint[] { 0, 1 })));

        Assert.Equal(1, rig.World.ActiveDemand); // the first map's demand was released when the socket moved on
        Assert.NotNull(status);
    }

    [Fact]
    public async Task AMessageOverFourKilobytes_ClosesTheSocket()
    {
        await using PublicRig rig = await PublicRig.StartAsync();
        using ClientWebSocket socket = await OpenAsync(rig, await rig.VisitAsync());

        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"op\":\"ping\",\"pad\":\"" + new string('x', 5000) + "\"}"), WebSocketMessageType.Text, true, CancellationToken.None);

        Assert.Null(await socket.ReceiveTextAsync());
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.CloseStatus);
    }

    [Fact]
    public async Task MoreThanTenMessagesInASecond_AreAnsweredWithRateLimited()
    {
        await using PublicRig rig = await PublicRig.StartAsync();
        using ClientWebSocket socket = await OpenAsync(rig, await rig.VisitAsync());

        for (int i = 0; i < 25; i++)
        {
            await socket.SendJsonAsync(new JsonObject { ["op"] = "ping" });
        }

        JsonNode? limited = await socket.ReceiveUntilAsync("error");
        Assert.NotNull(limited);
        Assert.Equal("rate-limited", limited["code"]!.GetValue<string>());
    }

    // ---- connections -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SocketsPerAddress_AreCapped_AndASlotComesBackWhenASocketCloses()
    {
        await using PublicRig rig = await PublicRig.StartAsync(o => o.MaxConnectionsPerIp = 2);
        Visitor visitor = await rig.VisitAsync();
        ClientWebSocket first = await OpenAsync(rig, visitor);
        using ClientWebSocket second = await OpenAsync(rig, visitor);

        WebSocketException refused = await Assert.ThrowsAsync<WebSocketException>(() => rig.ConnectAsync(visitor));
        Assert.Contains("503", refused.Message, StringComparison.Ordinal);

        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        first.Dispose();
        Assert.True(await WaitAsync(() => rig.Server.Hub.Count == 1), "the closed socket kept its slot");
        using ClientWebSocket third = await OpenAsync(rig, visitor);
        Assert.Equal(2, rig.Server.Hub.Count);
    }

    [Fact]
    public async Task ASocketTheServerClosesButThePeerNeverAnswers_IsAbortedAfterTheGrace_AndFreesItsSlot()
    {
        await using PublicRig rig = await PublicRig.StartAsync();
        rig.Server.Hub.CloseGrace = TimeSpan.FromMilliseconds(300);
        Visitor visitor = await rig.VisitAsync();
        using ClientWebSocket socket = await OpenAsync(rig, visitor);
        Assert.Equal(1, rig.Server.Hub.Count);

        // The operator kicks the session. This client never reads again, so it never sends its close frame back.
        string reference = rig.Runtime.Sessions.Find(visitor.Cookie)!.Ref;
        IReadOnlyList<EndedSession> ended = rig.Runtime.Sessions.End(s => s.Ref == reference, "kicked");

        Assert.Single(ended);
        Assert.True(await WaitAsync(() => rig.Server.Hub.Count == 0), "the socket stayed in CloseSent and kept its connection slot");
        Assert.Equal(0, rig.Runtime.Stats.Sockets);
    }

    [Fact]
    public async Task LoggingIn_ClosesTheGuestSocket_AndTheSessionsSocketsNeverOutliveTheirSession()
    {
        await using PublicRig rig = await PublicRig.StartAsync();
        rig.World.Accounts["gm"] = ("pw", 1);
        Visitor guest = await rig.VisitAsync();
        using ClientWebSocket socket = await OpenAsync(rig, guest);

        await rig.LoginAsync(guest, "gm", "pw");

        Assert.Null(await socket.ReceiveTextAsync()); // closed by the server: the session it belonged to is gone
        Assert.Equal((WebSocketCloseStatus)1008, socket.CloseStatus);
    }
}
