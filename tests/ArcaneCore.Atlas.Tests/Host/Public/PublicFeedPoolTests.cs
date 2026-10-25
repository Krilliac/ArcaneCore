using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Host.Public;
using ArcaneCore.Atlas.Live;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host.Public;

/// <summary>The public host's feed pool (design 0.10 and 0.11) against a fake world feed.</summary>
public sealed class PublicFeedPoolTests
{
    private sealed class Rig
    {
        public Rig(int capacity = 2, Action<PublicOptions>? edit = null, bool inProcess = false)
        {
            Options = new PublicOptions();
            edit?.Invoke(Options);
            Store = new PublicFrameStore(Options, Time);
            Pool = new PublicFeedPool(Feed, Options, capacity, Store, Time, NullLogger.Instance, requireQuietWorld: inProcess);
        }

        public ManualTime Time { get; } = new();

        public FakeFeed Feed { get; } = new();

        public PublicOptions Options { get; }

        public PublicFrameStore Store { get; }

        public PublicFeedPool Pool { get; }
    }

    private static LiveFrame FrameOf(long seq, params LiveEntity[] entities) => Frames.Frame(seq, entities);

    [Fact]
    public async Task TheFirstThingSentOnAConnection_IsTheRoleOp_AndNothingElseBeforeTheAck()
    {
        var rig = new Rig();

        await rig.Pool.StartAsync(CancellationToken.None);

        FakeConnection connection = Assert.Single(rig.Feed.Opened);
        JsonObject role = Assert.Single(connection.Sent);
        Assert.Equal("role", (string?)role["op"]);
        Assert.Equal("web", (string?)role["role"]);
        Assert.Equal(1, rig.Pool.ConnectionCount);

        using IDisposable? demand = await rig.Pool.DemandAsync(0, CancellationToken.None);
        Assert.Equal(["role", "subscribe"], connection.Ops);
        Assert.Equal([0u], rig.Pool.SubscribedMaps);
    }

    [Fact]
    public async Task AHelloWithToolsTrue_IsRefused_FatalIsRaised_AndNothingIsSent()
    {
        var rig = new Rig();
        rig.Feed.Tools = true;
        string? fatal = null;
        rig.Pool.Fatal += reason => fatal = reason;

        PublicFeedException e = await Assert.ThrowsAsync<PublicFeedException>(() => rig.Pool.StartAsync(CancellationToken.None));

        Assert.Contains("tools:true", e.Message, StringComparison.Ordinal);
        Assert.NotNull(fatal);
        Assert.Empty(Assert.Single(rig.Feed.Opened).Sent);
        Assert.Equal(0, rig.Pool.ConnectionCount);
    }

    [Fact]
    public async Task InProcessMode_AsksTheWorldForCaps_AfterTheRoleAck_AndAcceptsAQuietWorld()
    {
        var rig = new Rig(inProcess: true);

        await rig.Pool.StartAsync(CancellationToken.None);

        FakeConnection connection = Assert.Single(rig.Feed.Opened);
        Assert.Equal(["role", "caps"], connection.Ops);
        Assert.Equal(1, rig.Pool.ConnectionCount);
    }

    [Theory]
    [InlineData(true, false, "actions:true")]
    [InlineData(false, true, "liveServer:true")]
    public async Task InProcessMode_RefusesAWorldWhoseCapsSayActionsOrLive_WhateverTheOperatorStated(bool actions, bool live, string expected)
    {
        var rig = new Rig(inProcess: true, edit: o => { o.Feed.WorldActionsEnabled = false; o.Feed.LiveServer = false; });
        rig.Feed.CapsActions = actions;
        rig.Feed.CapsLiveServer = live;
        string? fatal = null;
        rig.Pool.Fatal += reason => fatal = reason;

        PublicFeedException e = await Assert.ThrowsAsync<PublicFeedException>(() => rig.Pool.StartAsync(CancellationToken.None));

        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
        Assert.Contains(expected, fatal, StringComparison.Ordinal);
        Assert.Equal(0, rig.Pool.ConnectionCount);
        Assert.Equal(["role", "caps"], Assert.Single(rig.Feed.Opened).Ops);
    }

    [Fact]
    public async Task InProcessMode_RefusesAWorldWhoseCapsAnswerIsMissingOrBroken()
    {
        foreach (JsonObject answer in new JsonObject[]
        {
            new() { ["op"] = "result", ["ok"] = true, ["kind"] = "caps" },
            new() { ["op"] = "result", ["ok"] = true, ["kind"] = "caps", ["actions"] = false },
            new() { ["op"] = "result", ["ok"] = true, ["kind"] = "caps", ["actions"] = "no", ["liveServer"] = false },
            new() { ["op"] = "result", ["ok"] = false, ["kind"] = "caps", ["code"] = "unknown-op" },
        })
        {
            var rig = new Rig(inProcess: true);
            rig.Feed.CapsAnswer = answer;

            await Assert.ThrowsAsync<PublicFeedException>(() => rig.Pool.StartAsync(CancellationToken.None));

            Assert.Equal(0, rig.Pool.ConnectionCount);
        }
    }

    [Fact]
    public async Task InProcessMode_ChecksCapsOnAReconnectToo_AndRaisesFatalWhenTheWorldTurnedActionsOn()
    {
        var rig = new Rig(capacity: 1, inProcess: true);
        await rig.Pool.StartAsync(CancellationToken.None);
        using IDisposable? demand = await rig.Pool.DemandAsync(0, CancellationToken.None);
        string? fatal = null;
        rig.Pool.Fatal += reason => fatal = reason;

        // The world is restarted with its action channel on; the pooled connection is lost and the pool reconnects.
        rig.Feed.CapsActions = true;
        rig.Feed.Opened[0].Close("restart");
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        await rig.Pool.MaintainAsync(CancellationToken.None);

        Assert.NotNull(fatal);
        Assert.Contains("actions:true", fatal, StringComparison.Ordinal);
        Assert.Equal(2, rig.Feed.Opened.Count);
        Assert.Equal(0, rig.Pool.ConnectionCount);
        Assert.Empty(rig.Pool.SubscribedMaps);
    }

    [Fact]
    public async Task SeparateMode_DoesNotAskForCaps()
    {
        var rig = new Rig();

        await rig.Pool.StartAsync(CancellationToken.None);

        Assert.DoesNotContain("caps", Assert.Single(rig.Feed.Opened).Ops);
    }

    [Fact]
    public async Task ARoleThatTheWorldDoesNotAcknowledgeAsWeb_StopsTheStart()
    {
        var rig = new Rig();
        rig.Feed.RoleAnswer = new JsonObject { ["op"] = "result", ["ok"] = false, ["kind"] = "role", ["code"] = "role-refused" };

        PublicFeedException e = await Assert.ThrowsAsync<PublicFeedException>(() => rig.Pool.StartAsync(CancellationToken.None));

        Assert.Contains("role-refused", e.Message, StringComparison.Ordinal);
        Assert.Equal(0, rig.Pool.ConnectionCount);
    }

    [Fact]
    public async Task AnUnreachableWorld_IsAStartFailure()
    {
        var rig = new Rig();
        rig.Feed.OpenFailures = 1;

        await Assert.ThrowsAsync<PublicFeedException>(() => rig.Pool.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OneConnectionServesOneMap_AndAMapBeyondTheCapacityIsBusy()
    {
        var rig = new Rig(capacity: 2);
        await rig.Pool.StartAsync(CancellationToken.None);

        using IDisposable? a = await rig.Pool.DemandAsync(0, CancellationToken.None);
        using IDisposable? b = await rig.Pool.DemandAsync(1, CancellationToken.None);
        using IDisposable? c = await rig.Pool.DemandAsync(2, CancellationToken.None);
        using IDisposable? again = await rig.Pool.DemandAsync(0, CancellationToken.None);

        Assert.Equal(2, rig.Feed.Opened.Count);
        Assert.Equal([0u, 1u], rig.Pool.SubscribedMaps);
        Assert.False(rig.Store.Status(0).Busy);
        Assert.True(rig.Store.Status(2).Busy);
        Assert.All(rig.Feed.Opened, o => Assert.Equal("role", o.Ops.First()));
    }

    [Fact]
    public async Task AMapIsReleasedSixtySecondsAfterItsLastViewer_NotBefore()
    {
        var rig = new Rig();
        await rig.Pool.StartAsync(CancellationToken.None);
        IDisposable? first = await rig.Pool.DemandAsync(0, CancellationToken.None);
        IDisposable? second = await rig.Pool.DemandAsync(0, CancellationToken.None);
        first!.Dispose();
        rig.Time.Advance(TimeSpan.FromSeconds(100));
        await rig.Pool.MaintainAsync(CancellationToken.None);
        Assert.Equal([0u], rig.Pool.SubscribedMaps); // one viewer is left

        second!.Dispose();
        second.Dispose(); // a second dispose changes nothing
        rig.Time.Advance(TimeSpan.FromSeconds(59));
        await rig.Pool.MaintainAsync(CancellationToken.None);
        Assert.Equal([0u], rig.Pool.SubscribedMaps);
        Assert.DoesNotContain("unsubscribe", rig.Feed.Opened[0].Ops);

        rig.Time.Advance(TimeSpan.FromSeconds(2));
        await rig.Pool.MaintainAsync(CancellationToken.None);
        Assert.Empty(rig.Pool.SubscribedMaps);
        Assert.Equal(["role", "subscribe", "unsubscribe"], rig.Feed.Opened[0].Ops);

        using IDisposable? viewer = await rig.Pool.DemandAsync(0, CancellationToken.None);
        Assert.Equal([0u], rig.Pool.SubscribedMaps); // the connection is reused
        Assert.Single(rig.Feed.Opened);
    }

    [Fact]
    public async Task ALostConnection_IsReplacedAtMostOncePerTenSecondsPerMap()
    {
        var rig = new Rig();
        await rig.Pool.StartAsync(CancellationToken.None);
        using IDisposable? viewer = await rig.Pool.DemandAsync(0, CancellationToken.None);
        rig.Feed.Opened[0].Close("lost");

        rig.Time.Advance(TimeSpan.FromSeconds(2));
        await rig.Pool.MaintainAsync(CancellationToken.None);
        Assert.Single(rig.Feed.Opened); // inside the interval: nothing new
        Assert.Empty(rig.Pool.SubscribedMaps);

        rig.Time.Advance(TimeSpan.FromSeconds(9));
        await rig.Pool.MaintainAsync(CancellationToken.None);
        Assert.Equal(2, rig.Feed.Opened.Count);
        Assert.Equal(["role", "subscribe"], rig.Feed.Opened[1].Ops);
        Assert.Equal([0u], rig.Pool.SubscribedMaps);
    }

    [Fact]
    public async Task Frames_AreProjectedForGuests_AndTheLinksAnswerHidesLinkedEntities()
    {
        var rig = new Rig();
        rig.Feed.Linked = [500UL];
        await rig.Pool.StartAsync(CancellationToken.None);
        using IDisposable? viewer = await rig.Pool.DemandAsync(0, CancellationToken.None);
        FakeConnection connection = rig.Feed.Opened[0];
        LiveEntity pet = Frames.Entity(500, LiveKind.Creature, "Wolf", 7771.5, 10.5, entry: 17);
        LiveEntity boar = Frames.Entity(501, LiveKind.Creature, "Boar", 30.5, 40.5, entry: 5);

        connection.PushFrame(FrameOf(1, pet, boar));
        connection.PushFrame(FrameOf(2, pet, boar));

        Assert.Contains("links", connection.Ops);
        PublicMapStatus status = rig.Store.Status(0);
        Assert.True(status.Connected);
        Assert.False(status.Reduced);
        Assert.Equal([501UL], status.Projection!.Entities.Select(e => e.Guid).ToArray());
    }

    [Fact]
    public async Task AWorldWithoutLinks_GivesReducedGuestData()
    {
        var rig = new Rig();
        rig.Feed.LinksSupported = false;
        await rig.Pool.StartAsync(CancellationToken.None);
        using IDisposable? viewer = await rig.Pool.DemandAsync(0, CancellationToken.None);
        FakeConnection connection = rig.Feed.Opened[0];
        LiveEntity bot = Frames.Entity(700, LiveKind.Bot, "Bot", 1.5, 2.5);
        LiveEntity boar = Frames.Entity(501, LiveKind.Creature, "Boar", 30.5, 40.5, entry: 5);

        connection.PushFrame(FrameOf(1, bot, boar));
        connection.PushFrame(FrameOf(2, bot, boar));

        PublicMapStatus status = rig.Store.Status(0);
        Assert.True(status.Reduced);
        Assert.Empty(status.Projection!.Entities); // no template set is known, so no creature is shown
    }

    [Fact]
    public async Task AWorldThatNeverAnswersLinks_IsAskedOncePerBackoffWindow_NotOncePerFrame()
    {
        var rig = new Rig();
        rig.Feed.LinksSupported = false;
        rig.Feed.LinksSilent = true; // a daemon whose refusal of the op carries no id: the query times out
        await rig.Pool.StartAsync(CancellationToken.None);
        using IDisposable? viewer = await rig.Pool.DemandAsync(0, CancellationToken.None);
        FakeConnection connection = rig.Feed.Opened[0];
        LiveEntity boar = Frames.Entity(501, LiveKind.Creature, "Boar", 30.5, 40.5, entry: 5);

        for (int seq = 1; seq <= 8; seq++)
        {
            connection.PushFrame(FrameOf(seq, boar));
        }

        Assert.Equal(1, connection.Ops.Count(o => o == "links")); // each unanswered ask is a refused line on an id-less daemon

        // A timeout is not final: the world may only have been slow. One ask per back-off window (1 min, then 2 min), never a permanent stop.
        rig.Time.Advance(TimeSpan.FromSeconds(59));
        connection.PushFrame(FrameOf(9, boar));
        Assert.Equal(1, connection.Ops.Count(o => o == "links"));
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        connection.PushFrame(FrameOf(10, boar));
        connection.PushFrame(FrameOf(11, boar));
        Assert.Equal(2, connection.Ops.Count(o => o == "links"));
        rig.Time.Advance(TimeSpan.FromSeconds(119));
        connection.PushFrame(FrameOf(12, boar));
        Assert.Equal(2, connection.Ops.Count(o => o == "links"));
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        connection.PushFrame(FrameOf(13, boar));
        Assert.Equal(3, connection.Ops.Count(o => o == "links"));

        // The back-off doubles up to a cap of 10 minutes: 1, 2, 4, 8, then 10 (not 16).
        foreach (int minutes in new[] { 4, 8, 10, 10 })
        {
            rig.Time.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
            int before = connection.Ops.Count(o => o == "links");
            connection.PushFrame(FrameOf(100 + minutes, boar));
            Assert.Equal(before, connection.Ops.Count(o => o == "links")); // still inside the window
            rig.Time.Advance(TimeSpan.FromSeconds(2));
            connection.PushFrame(FrameOf(200 + minutes, boar));
            Assert.Equal(before + 1, connection.Ops.Count(o => o == "links"));
        }
    }

    [Fact]
    public async Task TheLinksBackoff_StartsAgainAtOneMinuteAfterASuccessfulAnswer()
    {
        var rig = new Rig();
        rig.Feed.LinksSupported = false;
        rig.Feed.LinksSilent = true;
        await rig.Pool.StartAsync(CancellationToken.None);
        using IDisposable? viewer = await rig.Pool.DemandAsync(0, CancellationToken.None);
        FakeConnection connection = rig.Feed.Opened[0];
        LiveEntity boar = Frames.Entity(501, LiveKind.Creature, "Boar", 30.5, 40.5, entry: 5);
        int Asked() => connection.Ops.Count(o => o == "links");

        connection.PushFrame(FrameOf(1, boar)); // timeout: 1 minute
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        connection.PushFrame(FrameOf(2, boar)); // timeout: 2 minutes
        Assert.Equal(2, Asked());
        rig.Feed.LinksSupported = true;
        rig.Time.Advance(TimeSpan.FromSeconds(121));
        connection.PushFrame(FrameOf(3, boar)); // answered: the back-off is forgotten
        Assert.Equal(3, Asked());
        connection.PushFrame(FrameOf(4, boar));
        Assert.Equal(4, Asked()); // an answer is followed by another ask on the next frame

        rig.Feed.LinksSupported = false;
        connection.PushFrame(FrameOf(5, boar)); // timeout again: back to 1 minute, not 4
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        int before = Asked();
        connection.PushFrame(FrameOf(6, boar));
        Assert.Equal(before + 1, Asked());
    }

    [Fact]
    public async Task ALinksAnswerThatSaysNotOk_IsAskedAgainAfterAShortBackoff_NotOnTheNextFrame()
    {
        var rig = new Rig();
        rig.Feed.LinksBusy = true;
        await rig.Pool.StartAsync(CancellationToken.None);
        using IDisposable? viewer = await rig.Pool.DemandAsync(0, CancellationToken.None);
        FakeConnection connection = rig.Feed.Opened[0];
        LiveEntity boar = Frames.Entity(501, LiveKind.Creature, "Boar", 30.5, 40.5, entry: 5);

        connection.PushFrame(FrameOf(1, boar));
        connection.PushFrame(FrameOf(2, boar));
        Assert.Equal(1, connection.Ops.Count(o => o == "links"));
        rig.Time.Advance(TimeSpan.FromSeconds(6));
        connection.PushFrame(FrameOf(3, boar));
        Assert.Equal(2, connection.Ops.Count(o => o == "links"));
    }

    [Fact]
    public async Task AWorldThatAnswersUnknownOpToLinks_IsAskedOncePerConnection_NotOncePerFrame()
    {
        var rig = new Rig();
        rig.Feed.LinksSupported = false;
        await rig.Pool.StartAsync(CancellationToken.None);
        using IDisposable? viewer = await rig.Pool.DemandAsync(0, CancellationToken.None);
        FakeConnection connection = rig.Feed.Opened[0];
        LiveEntity boar = Frames.Entity(501, LiveKind.Creature, "Boar", 30.5, 40.5, entry: 5);

        for (int seq = 1; seq <= 8; seq++)
        {
            connection.PushFrame(FrameOf(seq, boar));
        }

        Assert.Equal(1, connection.Ops.Count(o => o == "links")); // eight refused lines would have closed the connection after five
    }

    [Fact]
    public async Task FramesOfAMapNobodySubscribed_AreIgnored()
    {
        var rig = new Rig();
        await rig.Pool.StartAsync(CancellationToken.None);
        using IDisposable? viewer = await rig.Pool.DemandAsync(0, CancellationToken.None);

        rig.Feed.Opened[0].PushFrame(Frames.Frame(1) with { Map = 1 });

        Assert.Null(rig.Store.Status(1).Projection);
    }

    [Fact]
    public async Task TwoWorldErrorsInAMinute_PauseGuestDerivedRequests_ButNotLoginOrLogout()
    {
        var rig = new Rig();
        await rig.Pool.StartAsync(CancellationToken.None);
        FakeConnection connection = rig.Feed.Opened[0];
        int InfoCalls() => connection.Ops.Count(o => o == "info.public");

        await rig.Pool.InfoPublicAsync(1, false, false, CancellationToken.None);
        connection.ServerErrors = 1;
        await rig.Pool.InfoPublicAsync(1, false, false, CancellationToken.None);
        Assert.Equal(2, InfoCalls());

        connection.ServerErrors = 2;
        await rig.Pool.InfoPublicAsync(1, false, false, CancellationToken.None); // sent, and this is the second strike
        Assert.Equal(3, InfoCalls());
        Assert.Null(await rig.Pool.InfoPublicAsync(1, false, false, CancellationToken.None));
        Assert.Equal(3, InfoCalls()); // paused

        await rig.Pool.LoginAsync("user", "pw", null, "1.2.3.4", 0, CancellationToken.None);
        await rig.Pool.EndTicketAsync("t", true, CancellationToken.None);
        Assert.Contains("login", connection.Ops);
        Assert.Contains("logout", connection.Ops);

        rig.Time.Advance(TimeSpan.FromSeconds(61));
        await rig.Pool.InfoPublicAsync(1, false, false, CancellationToken.None);
        Assert.Equal(4, InfoCalls());
    }

    [Fact]
    public async Task AGuestDerivedLineOverFourKilobytes_IsRefusedBeforeItIsSent()
    {
        var rig = new Rig();
        await rig.Pool.StartAsync(CancellationToken.None);

        JsonObject? answer = await rig.Pool.CharactersAsync(new string('x', 5000), CancellationToken.None);

        Assert.Null(answer);
        Assert.DoesNotContain("characters", rig.Feed.Opened[0].Ops);
    }

    [Fact]
    public async Task InfoPublic_SendsTheTypedPolicyFlags()
    {
        var rig = new Rig();
        await rig.Pool.StartAsync(CancellationToken.None);

        await rig.Pool.InfoPublicAsync(0x1234, showPlayers: true, showNames: true, CancellationToken.None);
        await rig.Pool.InfoPublicAsync(0x1234, showPlayers: false, showNames: true, CancellationToken.None);

        JsonObject[] asked = [.. rig.Feed.Opened[0].Sent.Where(m => (string?)m["op"] == "info.public")];
        Assert.Equal("shown", (string?)asked[0]["players"]);
        Assert.True((bool)asked[0]["names"]!);
        Assert.Equal("hidden", (string?)asked[1]["players"]);
        Assert.False((bool)asked[1]["names"]!); // names are only ever on when players are shown
    }

    [Fact]
    public async Task Login_MapsTheWorldsAnswers()
    {
        var rig = new Rig();
        Assert.Equal(WorldLoginKind.Unavailable, (await rig.Pool.LoginAsync("u", "p", null, "1.1.1.1", 0, CancellationToken.None)).Kind); // not started
        await rig.Pool.StartAsync(CancellationToken.None);

        WorldLoginResult failed = await rig.Pool.LoginAsync("u", "p", null, "1.1.1.1", 0, CancellationToken.None);
        rig.Feed.LoginAnswer = new JsonObject { ["op"] = "result", ["ok"] = false, ["kind"] = "login", ["code"] = "web-login-off" };
        WorldLoginResult off = await rig.Pool.LoginAsync("u", "p", null, "1.1.1.1", 0, CancellationToken.None);
        rig.Feed.LoginAnswer = new JsonObject { ["op"] = "result", ["ok"] = true, ["kind"] = "login", ["ticket"] = "tk", ["account"] = "STAFF", ["security"] = 2, ["expiresIn"] = 3600 };
        WorldLoginResult ok = await rig.Pool.LoginAsync("staff", "pw", "123456", "9.9.9.9", 1, CancellationToken.None);

        Assert.Equal(WorldLoginKind.Failed, failed.Kind);
        Assert.Equal(WorldLoginKind.Off, off.Kind);
        Assert.Equal(WorldLoginKind.Ok, ok.Kind);
        Assert.Equal("tk", ok.Ticket);
        Assert.Equal("STAFF", ok.Account);
        Assert.Equal(2, ok.Security);
        JsonObject sent = rig.Feed.Opened[0].Sent.Last(m => (string?)m["op"] == "login");
        Assert.Equal("staff", (string?)sent["user"]);
        Assert.Equal("123456", (string?)sent["code"]);
        Assert.Equal("9.9.9.9", (string?)sent["ip"]);
        Assert.Equal(1, (int?)sent["floor"]);
    }
}
