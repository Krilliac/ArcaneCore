using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Host.Public;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host.Public;

/// <summary>
/// The byte budget as three message classes (essential, interactive, droppable): the session routes and error replies are never refused for bytes,
/// a command does not run while an address is in debt, frames leave headroom for command answers, and a flood of refused socket messages is bounded.
/// </summary>
public sealed class PublicByteBudgetTests
{
    private static readonly string LoopbackKey = PublicAddresses.Key(IPAddress.Loopback);

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

    private static async Task<ClientWebSocket> OpenAsync(PublicRig rig, Visitor visitor)
    {
        ClientWebSocket socket = await rig.ConnectAsync(visitor);
        await socket.SendJsonAsync(new JsonObject { ["op"] = "auth", ["csrf"] = visitor.Csrf });
        Assert.NotNull(await socket.ReceiveUntilAsync("hello"));
        return socket;
    }

    private static void AllowLinks(PublicRig rig, long seq)
        => rig.Runtime.Frames.SetLinks(0, new LinksSnapshot(true, seq, new HashSet<ulong>(), new HashSet<uint>(), new HashSet<uint>()));

    private static LiveFrame Boars(long seq, int count)
        => Frames.Frame(seq, [.. Enumerable.Range(0, count).Select(i => Frames.Entity(1000UL + (ulong)i, LiveKind.Creature, "Dire Boar Number " + i, 100 + i, 200 + i, entry: 5))]);

    private static void Options(PublicOptions o)
    {
        o.Guest.MaxBytesPerSecond = 4096;
        o.Guest.FeedHz = 2;
        o.Login.PerIpPerMinute = 1000;
        o.Login.PerAccountPer15Min = 1000;
    }

    /// <summary>Drains the address's budget with real traffic (a frame, then command answers until one is refused), then pushes it into debt with essential messages.</summary>
    private static async Task<(Visitor Visitor, ClientWebSocket Socket)> DrainAsync(PublicRig rig)
    {
        Visitor visitor = await rig.VisitAsync();
        ClientWebSocket socket = await OpenAsync(rig, visitor);
        AllowLinks(rig, 1);
        rig.Runtime.Frames.Ingest(Boars(1, 4));
        Assert.NotNull(await socket.ReceiveUntilAsync("event", "live.frame"));

        Reply last;
        int guard = 0;
        do
        {
            last = await rig.PostAsync("/api/public/command", visitor, new JsonObject { ["name"] = "guest.help" });
        }
        while (last.Status == 200 && ++guard < 30);

        Assert.Equal(429, last.Status);
        for (int i = 0; i < 4 && !rig.Runtime.Limits.InDebt(LoopbackKey, out _); i++)
        {
            rig.Runtime.Limits.ChargeEssentialBytes(LoopbackKey, PublicRateLimits.MaxEssentialBytes);
        }

        Assert.True(rig.Runtime.Limits.InDebt(LoopbackKey, out _), "the address should be in debt now");
        return (visitor, socket);
    }

    [Fact]
    public async Task ASocketDrainingTheBudget_DoesNotStopLogin_AndTheIssuedSessionIsUsable()
    {
        var time = new ManualTime();
        await using PublicRig rig = await PublicRig.StartAsync(Options, time: time);
        rig.World.Accounts["gm"] = ("pw", 1);
        (Visitor guest, ClientWebSocket socket) = await DrainAsync(rig);
        using ClientWebSocket held = socket;

        Reply login = await rig.PostAsync("/api/public/login", guest, new { user = "gm", password = "pw" });

        Assert.Equal(200, login.Status);
        Assert.False(string.IsNullOrEmpty(login.Json!["csrf"]?.GetValue<string>()));
        string cookie = Assert.Single(login.SetCookies);
        Assert.StartsWith("atlas-dev=", cookie, StringComparison.Ordinal);
        var account = new Visitor(rig.CookieOf(login)!, login.Json["csrf"]!.GetValue<string>());
        Reply me = await rig.GetAsync("/api/public/me", account);
        Assert.Equal(200, me.Status);
        Assert.Equal("account", me.Json!["tier"]!.GetValue<string>());
        Assert.Equal(401, (await rig.GetAsync("/api/public/me", guest)).Status);
    }

    [Fact]
    public async Task ALogoutAfterTheBudgetIsDrained_StillWorks()
    {
        var time = new ManualTime();
        await using PublicRig rig = await PublicRig.StartAsync(Options, time: time);
        rig.World.Accounts["gm"] = ("pw", 1);
        Visitor guest = await rig.VisitAsync();
        Visitor account = await rig.LoginAsync(guest, "gm", "pw");
        rig.Runtime.Limits.ChargeEssentialBytes(LoopbackKey, PublicRateLimits.MaxEssentialBytes);
        rig.Runtime.Limits.ChargeEssentialBytes(LoopbackKey, PublicRateLimits.MaxEssentialBytes);
        rig.Runtime.Limits.ChargeEssentialBytes(LoopbackKey, PublicRateLimits.MaxEssentialBytes);
        Assert.True(rig.Runtime.Limits.InDebt(LoopbackKey, out _));

        Reply logout = await rig.PostAsync("/api/public/logout", account, null);

        Assert.Equal(200, logout.Status);
        Assert.Equal("guest", logout.Json!["tier"]!.GetValue<string>());
        Assert.NotNull(rig.CookieOf(logout));
        Assert.Single(rig.World.EndedTickets);
    }

    [Fact]
    public async Task WhileInDebt_StatusAndMeStillAnswer200_SoThePageCanStart()
    {
        var time = new ManualTime();
        await using PublicRig rig = await PublicRig.StartAsync(Options, time: time);
        Visitor visitor = await rig.VisitAsync();
        for (int i = 0; i < 4; i++)
        {
            rig.Runtime.Limits.ChargeEssentialBytes(LoopbackKey, PublicRateLimits.MaxEssentialBytes);
        }

        Assert.True(rig.Runtime.Limits.InDebt(LoopbackKey, out _));
        Reply status = await rig.GetAsync("/api/public/status");
        Reply me = await rig.GetAsync("/api/public/me", visitor);

        Assert.Equal(200, status.Status);
        Assert.Equal(200, me.Status);
    }

    [Fact]
    public async Task AFloodOfRefusedMessages_GetsAtMostTwoErrorRepliesPerSecond_AndThenTheSocketCloses()
    {
        var time = new ManualTime();
        await using PublicRig rig = await PublicRig.StartAsync(Options, time: time);
        Visitor visitor = await rig.VisitAsync();
        using ClientWebSocket socket = await rig.ConnectAsync(visitor);
        await socket.SendJsonAsync(new JsonObject { ["op"] = "auth", ["csrf"] = visitor.Csrf });

        for (int i = 0; i < 200; i++)
        {
            try
            {
                await socket.SendJsonAsync(new JsonObject { ["op"] = "ping" });
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                break; // the server has already closed
            }
        }

        int pongs = 0;
        int errors = 0;
        int bytes = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var buffer = new byte[8192];
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            bytes += result.Count;
            string text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            pongs += text.Contains("\"pong\"", StringComparison.Ordinal) ? 1 : 0;
            errors += text.Contains("\"error\"", StringComparison.Ordinal) ? 1 : 0;
        }

        string seen = $"pongs={pongs} errors={errors} bytes={bytes} close={socket.CloseStatus}/{socket.CloseStatusDescription}";
        Assert.True(socket.CloseStatus == WebSocketCloseStatus.PolicyViolation && socket.CloseStatusDescription == "flood", seen);
        Assert.True(pongs == 10, seen);
        Assert.True(errors is >= 1 and <= 2, seen);
        Assert.True(bytes < 2048, seen);
    }

    [Fact]
    public async Task FramesLeaveHeadroom_SoAnInteractiveAnswerStillFitsAfterAFrame()
    {
        var time = new ManualTime();
        await using PublicRig rig = await PublicRig.StartAsync(o => { Options(o); o.Guest.MaxBytesPerSecond = 8192; o.Guest.FeedHz = 10; }, time: time);
        Visitor visitor = await rig.VisitAsync();
        using ClientWebSocket socket = await OpenAsync(rig, visitor);
        AllowLinks(rig, 1);

        rig.Runtime.Frames.Ingest(Boars(1, 15));
        JsonNode first = (await socket.ReceiveUntilAsync("event", "live.frame"))!;
        int size = Encoding.UTF8.GetByteCount(first.ToJsonString());
        Assert.InRange(size, 1536, 2048); // the sizing this test relies on

        // Leave the budget just able to pay for the next frame if nothing were held back, and no more: a frame then eats the room a command answer needs.
        double wanted = size + 100;
        double balance = rig.Runtime.Limits.ByteBalance(LoopbackKey);
        double refill = 8192 * 0.11;
        int burn = (int)(balance + refill - wanted);
        Assert.True(burn > 0, $"balance {balance} is already below the sizing target");
        Assert.True(rig.Runtime.Limits.AllowBytes(LoopbackKey, burn));
        time.Advance(TimeSpan.FromMilliseconds(110)); // past the 100 ms frame interval
        long dropped = rig.Runtime.Stats.FramesDropped;
        rig.Runtime.Frames.Ingest(Boars(2, 15));
        Assert.True(await WaitAsync(() => rig.Runtime.Stats.FramesDropped == dropped + 1), "the second frame should be dropped to keep the reserve");

        Reply answer = await rig.PostAsync("/api/public/command", visitor, new JsonObject { ["name"] = "guest.maps" });

        Assert.Equal(200, answer.Status);
    }

    [Fact]
    public async Task InDebt_ACommandIsRefusedBeforeItRuns()
    {
        var time = new ManualTime();
        await using PublicRig rig = await PublicRig.StartAsync(Options, time: time);
        rig.World.InfoAnswer = Json("""{"ok":true,"level":12}""");
        Visitor visitor = await rig.VisitAsync();
        AllowLinks(rig, 1);
        rig.Runtime.Frames.Ingest(Boars(1, 1));
        PublicSession session = rig.Runtime.Sessions.Find(visitor.Cookie)!;
        string id = GuestFrameProjector.OpaqueId(session.OpaqueKey, 1000);
        var pick = new JsonObject { ["name"] = "pick.public", ["args"] = Json($$"""{"map":0,"id":"{{id}}"}""") };
        for (int i = 0; i < 4; i++)
        {
            rig.Runtime.Limits.ChargeEssentialBytes(LoopbackKey, PublicRateLimits.MaxEssentialBytes);
        }

        Reply refused = await rig.PostAsync("/api/public/command", visitor, (JsonObject)pick.DeepClone());

        Assert.Equal(429, refused.Status);
        Assert.Equal("rate-limited", refused.Code);
        Assert.NotNull(refused.Header("Retry-After"));
        Assert.Empty(rig.World.InfoAsked); // the world was not asked

        time.Advance(TimeSpan.FromSeconds(3));
        Reply accepted = await rig.PostAsync("/api/public/command", visitor, (JsonObject)pick.DeepClone());
        Assert.Equal(200, accepted.Status);
        Assert.Single(rig.World.InfoAsked);
    }
}
