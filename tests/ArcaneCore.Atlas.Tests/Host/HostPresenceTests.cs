using System.Net.WebSockets;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host;
using ArcaneCore.Atlas.Tests.Host.Public;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>
/// Several UIs on one host: the <c>presence</c> op, the <c>ui.presence</c> / <c>ui.presence.left</c> events, the hello
/// <c>name</c> and <c>sessions</c>, and <c>status.ui.sessions</c>, plus the <c>cameraChanged</c> flag of the view event and
/// <c>bookmark.add</c> with an explicit camera. Nothing here sleeps to "let something happen": the rate limit is switched off
/// (or driven by a manual clock) wherever a test sends more than one message per connection.
/// </summary>
public sealed class HostPresenceTests
{
    /// <summary>
    /// A host whose presence rate limit is switched off (or set to <paramref name="interval"/> on <paramref name="time"/>), so
    /// tests that are not about the limit never depend on how fast the machine is.
    /// </summary>
    private static Task<HostRig> StartAsync(TimeSpan? interval = null, TimeProvider? time = null)
        => HostRig.StartAsync(new AtlasServerOptions { PresenceMinInterval = interval ?? TimeSpan.Zero, Time = time ?? TimeProvider.System });

    /// <summary>
    /// Returns once the host has processed everything this client sent so far: a presence message without fields is always
    /// invalid, invalid messages are answered before the rate limit is consulted, and one connection is read in order.
    /// </summary>
    private static async Task BarrierAsync(UiClient ui)
    {
        await ui.SendTextAsync("""{"op":"presence"}""");
        await ui.ReceiveUntilAsync(m => m.Str("op") == "error", "the barrier's bad-request");
    }

    private static JsonObject Presence(uint map = 0, double x = 1, double y = 2, double z = 3, double yaw = 45, string mode = "orbit", string? name = null)
    {
        var message = new JsonObject { ["op"] = "presence", ["map"] = map, ["at"] = new JsonArray(x, y, z), ["yaw"] = yaw, ["mode"] = mode };
        if (name is not null)
        {
            message["name"] = name;
        }

        return message;
    }

    private static JsonObject Raw(string json) => JsonNode.Parse(json)!.AsObject();

    private static bool IsPresence(JsonNode m) => m.Str("op") == "event" && m.Str("topic") == UiHub.PresenceTopic;

    private static bool IsLeft(JsonNode m) => m.Str("op") == "event" && m.Str("topic") == UiHub.PresenceLeftTopic;

    /// <summary>Reads frames until one matches and returns every frame read, the match last.</summary>
    private static async Task<List<JsonNode>> CollectUntilAsync(UiClient ui, Func<JsonNode, bool> match)
    {
        var seen = new List<JsonNode>();
        while (true)
        {
            JsonNode message = await ui.ReceiveAsync();
            seen.Add(message);
            if (match(message))
            {
                return seen;
            }
        }
    }

    private static Task<JsonNode> ReceivePresenceOfAsync(UiClient ui, string id, Func<JsonNode, bool>? where = null)
        => ui.ReceiveEventAsync(UiHub.PresenceTopic, d => d.Str("id") == id && (where?.Invoke(d) ?? true));

    [Fact]
    public async Task TwoClientsConnectedAtOnce_GetDifferentIds_AndTheHubCountsBoth()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);

        Assert.NotEqual(a.ConnectionId, b.ConnectionId);
        Assert.Equal(2, rig.Server!.Hub.ConnectionCount);
        Assert.Equal(2, (await rig.DataAsync("status")).Num("ui.connections"));
    }

    [Fact]
    public async Task Hello_CarriesTheDefaultName_AndNoSessionsWhenAlone()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);

        Assert.Equal("session " + a.ConnectionId[..4], a.Hello.Str("name"));
        JsonArray sessions = Assert.IsType<JsonArray>(a.Hello.At("sessions"));
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task APublishedPresence_ReachesTheOtherClient_WithEveryField_AndNotTheSender()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);

        await a.SendAsync(Presence(map: 1, x: -9462.66, y: 16.1915, z: 57.0459, yaw: 123.5, mode: "fly"));
        JsonNode seen = await ReceivePresenceOfAsync(b, a.ConnectionId);

        Assert.Equal(a.ConnectionId, seen.Str("data.id"));
        Assert.Equal(a.Hello.Str("name"), seen.Str("data.name"));
        Assert.Equal(1, seen.Num("data.map"));
        Assert.Equal(-9462.66, seen.Num("data.at.0"));
        Assert.Equal(16.1915, seen.Num("data.at.1"));
        Assert.Equal(57.0459, seen.Num("data.at.2"));
        Assert.Equal(123.5, seen.Num("data.yaw"));
        Assert.Equal("fly", seen.Str("data.mode"));

        // B answers after it saw A's message, so anything the host would have echoed to A is queued before B's presence.
        await b.SendAsync(Presence(x: 7));
        List<JsonNode> toA = await CollectUntilAsync(a, IsPresence);
        Assert.All(toA.Where(IsPresence), m => Assert.Equal(b.ConnectionId, m.Str("data.id")));
    }

    [Fact]
    public async Task APresence_NeverChangesTheSharedView()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);
        string viewBefore = rig.Context.View.Current.ToJson().ToJsonString();

        await a.SendAsync(Presence(map: 1, x: 5));
        await ReceivePresenceOfAsync(b, a.ConnectionId);

        Assert.Equal(viewBefore, rig.Context.View.Current.ToJson().ToJsonString());
    }

    [Fact]
    public async Task ARealTimeMove_ShowsTheLatestPositionToTheOtherClient()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);

        var xs = new List<double>();
        for (int i = 1; i <= 4; i++)
        {
            await a.SendAsync(Presence(x: i * 10, yaw: i * 20));
            JsonNode seen = await ReceivePresenceOfAsync(b, a.ConnectionId, d => d.Num("at.0") == i * 10);
            xs.Add(seen.Num("data.at.0"));
            Assert.Equal(i * 20, seen.Num("data.yaw"));
        }

        Assert.Equal(new[] { 10d, 20d, 30d, 40d }, xs);
        UiPresence stored = Assert.Single(rig.Server!.Hub.PresenceSnapshot(b.ConnectionId));
        Assert.Equal(40, stored.X);
        Assert.Equal(80, stored.Yaw);
    }

    [Fact]
    public void ThePresenceRateLimit_DefaultsTo50msOnTheSystemClock()
    {
        var options = new AtlasServerOptions();

        Assert.Equal(TimeSpan.FromMilliseconds(50), options.PresenceMinInterval);
        Assert.Same(TimeProvider.System, options.Time);
    }

    [Fact]
    public async Task PresenceMessagesCloserThanTheInterval_AreDropped_ExactlyAtTheBoundary_WhateverTheyChange()
    {
        var time = new ManualTime();
        await using HostRig rig = await StartAsync(TimeSpan.FromMilliseconds(50), time);
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);
        UiPresence Stored() => rig.Server!.Hub.PresenceSnapshot(b.ConnectionId).Single();

        await a.SendAsync(Presence(x: 1));
        await BarrierAsync(a);
        Assert.Equal(1, Stored().X);

        time.Advance(TimeSpan.FromMilliseconds(49));
        await a.SendAsync(Presence(x: 2));
        await BarrierAsync(a);
        Assert.Equal(1, Stored().X);

        // A rename gets no exemption: alternating names must not bypass the limit.
        await a.SendAsync(Presence(x: 3, name: "Ann"));
        await BarrierAsync(a);
        Assert.Equal(1, Stored().X);
        Assert.Equal(a.Hello.Str("name"), Stored().Name);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await a.SendAsync(Presence(x: 4, name: "Ann"));
        await BarrierAsync(a);
        Assert.Equal(4, Stored().X);
        Assert.Equal("Ann", Stored().Name);

        // B was told about exactly the two accepted messages, in order.
        List<JsonNode> seen = await CollectUntilAsync(b, m => IsPresence(m) && m.Num("data.at.0") == 4);
        Assert.Equal(new[] { 1d, 4d }, seen.Where(IsPresence).Select(m => m.Num("data.at.0")));
    }

    [Fact]
    public async Task AConfiguredInterval_ThinsABurstOnTheRealClock_AndKeepsTheFirstMessage()
    {
        await using HostRig rig = await StartAsync(TimeSpan.FromHours(1));
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);

        for (int i = 0; i < 60; i++)
        {
            await a.SendAsync(Presence(x: i));
        }

        await BarrierAsync(a);

        Assert.Equal(0, rig.Server!.Hub.PresenceSnapshot(b.ConnectionId).Single().X);
        JsonNode first = await ReceivePresenceOfAsync(b, a.ConnectionId);
        Assert.Equal(0, first.Num("data.at.0"));
    }

    [Fact]
    public async Task ACustomName_IsPropagated_Cleaned_AndAnExplicitlyEmptyOneResetsToTheDefault()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);
        string defaultName = a.Hello.Str("name");

        await a.SendAsync(Presence(x: 1, name: "  Ann\u0001\tBo\nb \u007f"));
        Assert.Equal("AnnBob", (await ReceivePresenceOfAsync(b, a.ConnectionId, d => d.Num("at.0") == 1)).Str("data.name"));

        await a.SendAsync(Presence(x: 2, name: new string('n', 40)));
        Assert.Equal(new string('n', 32), (await ReceivePresenceOfAsync(b, a.ConnectionId, d => d.Num("at.0") == 2)).Str("data.name"));

        await a.SendAsync(Presence(x: 3, name: string.Empty));
        Assert.Equal(defaultName, (await ReceivePresenceOfAsync(b, a.ConnectionId, d => d.Num("at.0") == 3)).Str("data.name"));

        await a.SendAsync(Presence(x: 4, name: "Cy"));
        await ReceivePresenceOfAsync(b, a.ConnectionId, d => d.Num("at.0") == 4);
        await a.SendAsync(Presence(x: 5, name: "\u0001\u0002 \t"));
        Assert.Equal(defaultName, (await ReceivePresenceOfAsync(b, a.ConnectionId, d => d.Num("at.0") == 5)).Str("data.name"));

        await a.SendAsync(Presence(x: 6, name: "x" + new string(' ', 40) + "y"));
        string spaced = (await ReceivePresenceOfAsync(b, a.ConnectionId, d => d.Num("at.0") == 6)).Str("data.name");
        Assert.Equal("x", spaced);
        Assert.Equal("session " + a.ConnectionId[..4], defaultName);
    }

    [Fact]
    public async Task APresenceWithoutAName_KeepsTheCurrentOne()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);

        await a.SendAsync(Presence(x: 1, name: "Ann"));
        await ReceivePresenceOfAsync(b, a.ConnectionId, d => d.Num("at.0") == 1);
        await a.SendAsync(Presence(x: 2));

        JsonNode seen = await ReceivePresenceOfAsync(b, a.ConnectionId, d => d.Num("at.0") == 2);
        Assert.Equal("Ann", seen.Str("data.name"));
    }

    [Fact]
    public async Task ANameLosesItsFormatCharacters_BidiOverridesAndZeroWidthMarks()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);

        await a.SendAsync(Presence(x: 1, name: "A\u202Eb\u200Bc\uFEFF\u2066d"));
        Assert.Equal("Abcd", (await ReceivePresenceOfAsync(b, a.ConnectionId, d => d.Num("at.0") == 1)).Str("data.name"));

        await a.SendAsync(Presence(x: 2, name: "\u202E\u200B"));
        Assert.Equal(a.Hello.Str("name"), (await ReceivePresenceOfAsync(b, a.ConnectionId, d => d.Num("at.0") == 2)).Str("data.name"));
    }

    [Fact]
    public async Task ANameWithALoneSurrogateEscape_IsBadRequest_AndTheConnectionStaysOpen()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);

        await a.SendTextAsync("""{"op":"presence","name":"\ud800","map":1,"at":[1,2,3],"yaw":1,"mode":"orbit"}""");
        JsonNode error = await a.ReceiveUntilAsync(m => m.Str("op") == "error", "error reply to the lone surrogate");

        Assert.Equal("bad-request", error.Str("code"));
        Assert.Equal(2, rig.Server!.Hub.ConnectionCount);
        Assert.Empty(rig.Server.Hub.PresenceSnapshot());
        await a.SendAsync(Presence(x: 9, name: "Ann"));
        Assert.Equal("Ann", (await ReceivePresenceOfAsync(b, a.ConnectionId)).Str("data.name"));
    }

    [Fact]
    public async Task AnUnnamedPresence_KeepsTheDefaultNameOfTheConnection()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);

        await a.SendAsync(Presence(name: string.Empty));
        JsonNode seen = await ReceivePresenceOfAsync(b, a.ConnectionId);

        Assert.Equal("session " + a.ConnectionId[..4], seen.Str("data.name"));
        Assert.Matches("^session [0-9a-f]{4}$", seen.Str("data.name"));
    }

    [Fact]
    public async Task AClientConnectingLater_SeesTheEarlierSessionsInHello_OldestFirst_WithoutItself()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);
        await a.SendAsync(Presence(map: 1, x: 11, name: "Ann"));
        await b.SendAsync(Presence(map: 0, x: 22, yaw: 270, mode: "top", name: "Bob"));
        await Wait.UntilAsync(() => rig.Server!.Hub.PresenceSnapshot().Count == 2, "both presences to be stored");

        await using UiClient c = await UiClient.ConnectAsync(rig);

        Assert.Equal("session " + c.ConnectionId[..4], c.Hello.Str("name"));
        JsonArray sessions = Assert.IsType<JsonArray>(c.Hello.At("sessions"));
        Assert.Equal(2, sessions.Count);
        Assert.Equal(a.ConnectionId, c.Hello.Str("sessions.0.id"));
        Assert.Equal("Ann", c.Hello.Str("sessions.0.name"));
        Assert.Equal(1, c.Hello.Num("sessions.0.map"));
        Assert.Equal(11, c.Hello.Num("sessions.0.at.0"));
        Assert.Equal(b.ConnectionId, c.Hello.Str("sessions.1.id"));
        Assert.Equal("Bob", c.Hello.Str("sessions.1.name"));
        Assert.Equal(270, c.Hello.Num("sessions.1.yaw"));
        Assert.Equal("top", c.Hello.Str("sessions.1.mode"));
        Assert.DoesNotContain(sessions, s => s.Str("id") == c.ConnectionId);
    }

    [Fact]
    public async Task AConnectionThatNeverPublished_IsNotListedInHello()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient silent = await UiClient.ConnectAsync(rig);

        await using UiClient late = await UiClient.ConnectAsync(rig);

        Assert.Empty(Assert.IsType<JsonArray>(late.Hello.At("sessions")));
    }

    [Fact]
    public async Task WhenAClientDisconnects_TheOthersGetPresenceLeft_AndLaterHellosNoLongerListIt()
    {
        await using HostRig rig = await StartAsync();
        UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);
        string aId = a.ConnectionId;
        await a.SendAsync(Presence(x: 1));
        await b.SendAsync(Presence(x: 2));
        await ReceivePresenceOfAsync(b, aId);

        await a.DisposeAsync();

        JsonNode left = await b.ReceiveEventAsync(UiHub.PresenceLeftTopic);
        Assert.Equal(aId, left.Str("data.id"));
        await Wait.UntilAsync(() => rig.Server!.Hub.ConnectionCount == 1, "A to be removed");
        await using UiClient c = await UiClient.ConnectAsync(rig);
        JsonNode only = Assert.Single(Assert.IsType<JsonArray>(c.Hello.At("sessions")))!;
        Assert.Equal(b.ConnectionId, only.Str("id"));
    }

    [Fact]
    public async Task AConnectionThatNeverPublishedPresence_LeavingSendsNoPresenceLeft()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient b = await UiClient.ConnectAsync(rig);
        UiHub hub = rig.Server!.Hub;

        // A connection added and removed by hand, so the removal has finished when Remove returns, and B's queue is in order:
        // a leave event, if one were sent, would be queued before the marker this test broadcasts afterwards.
        using WebSocket socket = WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions { IsServer = true });
        UiConnection silent = hub.Add(socket, "silent");
        Assert.Equal(2, hub.ConnectionCount);
        hub.Remove(silent);
        hub.Broadcast("test.marker", new JsonObject());
        List<JsonNode> seen = await CollectUntilAsync(b, m => m.Str("op") == "event" && m.Str("topic") == "test.marker");

        Assert.Equal(1, hub.ConnectionCount);
        Assert.DoesNotContain(seen, IsLeft);
    }

    [Fact]
    public async Task AConnectionThatPublishedPresence_LeavingSendsPresenceLeft_BeforeALaterBroadcast()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient b = await UiClient.ConnectAsync(rig);
        UiHub hub = rig.Server!.Hub;
        using WebSocket socket = WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions { IsServer = true });
        UiConnection loud = hub.Add(socket, "loud");
        loud.Presence = new UiPresence("loud", "Loud", 0, 1, 2, 3, 0, "orbit", UiPresence.KindUi);

        hub.Remove(loud);
        hub.Broadcast("test.marker", new JsonObject());
        List<JsonNode> seen = await CollectUntilAsync(b, m => m.Str("op") == "event" && m.Str("topic") == "test.marker");

        Assert.Single(seen, IsLeft);
        Assert.Equal("loud", seen.Single(IsLeft).Str("data.id"));
    }

    [Fact]
    public void CleanName_DropsUnpairedSurrogates_AndKeepsValidPairs()
    {
        (string Raw, string? Expected)[] cases =
        [
            ("Ann\ud800", "Ann"),
            ("\udc00Ann", "Ann"),
            ("A😀B", "A😀B"),
            ("\ud83dx\ude00", "x"),
            ("\ud800", null),
            ("  ‮Ann​  ", "Ann"),
        ];

        foreach ((string raw, string? expected) in cases)
        {
            Assert.Equal(expected, UiPresence.CleanName(raw));
        }
    }

    [Fact]
    public async Task InvalidPresence_AnswersBadRequest_KeepsTheConnectionOpen_AndNeverReachesTheOthers()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);
        await a.SendAsync(Presence(x: 1, name: "Ann"));
        await ReceivePresenceOfAsync(b, a.ConnectionId);
        UiPresence before = rig.Server!.Hub.PresenceSnapshot(b.ConnectionId).Single();
        string[] invalid =
        [
            """{"op":"presence","at":[1,2,3],"yaw":1,"mode":"orbit"}""",
            """{"op":"presence","map":-1,"at":[1,2,3],"yaw":1,"mode":"orbit"}""",
            """{"op":"presence","map":1.5,"at":[1,2,3],"yaw":1,"mode":"orbit"}""",
            """{"op":"presence","map":4294967296,"at":[1,2,3],"yaw":1,"mode":"orbit"}""",
            """{"op":"presence","map":"1","at":[1,2,3],"yaw":1,"mode":"orbit"}""",
            """{"op":"presence","map":1,"at":[1,2],"yaw":1,"mode":"orbit"}""",
            """{"op":"presence","map":1,"at":[1,2,3,4],"yaw":1,"mode":"orbit"}""",
            """{"op":"presence","map":1,"at":[100001,2,3],"yaw":1,"mode":"orbit"}""",
            """{"op":"presence","map":1,"at":[1,-100000.5,3],"yaw":1,"mode":"orbit"}""",
            """{"op":"presence","map":1,"at":[1,2,1e300],"yaw":1,"mode":"orbit"}""",
            """{"op":"presence","map":1,"at":[1,"2",3],"yaw":1,"mode":"orbit"}""",
            """{"op":"presence","map":1,"at":[1,null,3],"yaw":1,"mode":"orbit"}""",
            """{"op":"presence","map":1,"at":"1,2,3","yaw":1,"mode":"orbit"}""",
            """{"op":"presence","map":1,"at":[1,2,3],"mode":"orbit"}""",
            """{"op":"presence","map":1,"at":[1,2,3],"yaw":"1","mode":"orbit"}""",
            """{"op":"presence","map":1,"at":[1,2,3],"yaw":1}""",
            """{"op":"presence","map":1,"at":[1,2,3],"yaw":1,"mode":"swim"}""",
            """{"op":"presence","map":1,"at":[1,2,3],"yaw":1,"mode":"ORBIT"}""",
            """{"op":"presence","map":1,"at":[1,2,3],"yaw":1,"mode":5}""",
            """{"op":"presence","name":5,"map":1,"at":[1,2,3],"yaw":1,"mode":"orbit"}""",
            """{"op":"presence","name":["x"],"map":1,"at":[1,2,3],"yaw":1,"mode":"orbit"}""",
        ];

        foreach (string text in invalid)
        {
            await a.SendTextAsync(text);
            JsonNode error = await a.ReceiveUntilAsync(m => m.Str("op") == "error", $"error reply to {text}");
            Assert.Equal("bad-request", error.Str("code"));
        }

        Assert.Equal(before, rig.Server.Hub.PresenceSnapshot(b.ConnectionId).Single());
        Assert.Equal(2, rig.Server.Hub.ConnectionCount);

        // The connection still works, and B's queue holds nothing from the invalid messages before this valid one.
        await a.SendAsync(Presence(x: 2));
        List<JsonNode> seen = await CollectUntilAsync(b, m => IsPresence(m) && m.Num("data.at.0") == 2);
        Assert.DoesNotContain(seen, m => IsPresence(m) && m.Num("data.at.0") != 2);
        Assert.Equal("Ann", seen[^1].Str("data.name"));
    }

    [Fact]
    public async Task PresenceBoundaries_AreAccepted_AndYawIsWrappedIntoZeroTo360()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);

        await a.SendTextAsync("""{"op":"presence","map":4294967295,"at":[100000,-100000,0],"yaw":-90,"mode":"top"}""");
        JsonNode edge = await ReceivePresenceOfAsync(b, a.ConnectionId);
        Assert.Equal(4294967295d, edge.Num("data.map"));
        Assert.Equal(100000, edge.Num("data.at.0"));
        Assert.Equal(-100000, edge.Num("data.at.1"));
        Assert.Equal(270, edge.Num("data.yaw"));

        foreach ((double raw, double wrapped) in new[] { (360d, 0d), (720.5, 0.5), (-360d, 0d), (0d, 0d), (359.5, 359.5) })
        {
            await a.SendAsync(Presence(x: raw, yaw: raw));
            JsonNode seen = await ReceivePresenceOfAsync(b, a.ConnectionId, d => d.Num("at.0") == raw);
            Assert.Equal(wrapped, seen.Num("data.yaw"));
        }
    }

    [Fact]
    public async Task TheStatusCommandOverTheSocket_ListsTheSessionsThatPublished()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient quiet = await UiClient.ConnectAsync(rig);

        await a.SendAsync(Raw("""{"op":"command","id":1,"name":"status"}"""));
        JsonNode empty = await a.ReceiveUntilAsync(m => m.Str("op") == "result", "status result");
        Assert.Empty(Assert.IsType<JsonArray>(empty.At("result.data.ui.sessions")));

        await a.SendAsync(Presence(map: 1, x: 5, name: "Ann"));
        await Wait.UntilAsync(() => rig.Server!.Hub.PresenceSnapshot().Count == 1, "A's presence to be stored");
        await quiet.SendAsync(Raw("""{"op":"command","id":2,"name":"status"}"""));
        JsonNode status = await quiet.ReceiveUntilAsync(m => m.Str("op") == "result", "status result");

        Assert.Equal(2, status.Num("result.data.ui.connections"));
        JsonArray sessions = Assert.IsType<JsonArray>(status.At("result.data.ui.sessions"));
        Assert.Single(sessions);
        Assert.Equal(a.ConnectionId, status.Str("result.data.ui.sessions.0.id"));
        Assert.Equal("Ann", status.Str("result.data.ui.sessions.0.name"));
        Assert.Equal(1, status.Num("result.data.ui.sessions.0.map"));
        Assert.Equal(5, status.Num("result.data.ui.sessions.0.at.0"));
        Assert.Equal("orbit", status.Str("result.data.ui.sessions.0.mode"));
    }

    [Fact]
    public async Task BothClientsPublishing_EachSeesTheOther()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);

        await a.SendAsync(Presence(map: 0, x: 100, name: "Ann"));
        await b.SendAsync(Presence(map: 1, x: 200, mode: "fly", name: "Bob"));
        JsonNode aSeesB = await ReceivePresenceOfAsync(a, b.ConnectionId);
        JsonNode bSeesA = await ReceivePresenceOfAsync(b, a.ConnectionId);

        Assert.Equal("Bob", aSeesB.Str("data.name"));
        Assert.Equal(200, aSeesB.Num("data.at.0"));
        Assert.Equal(1, aSeesB.Num("data.map"));
        Assert.Equal("fly", aSeesB.Str("data.mode"));
        Assert.Equal("Ann", bSeesA.Str("data.name"));
        Assert.Equal(100, bSeesA.Num("data.at.0"));
        Assert.Equal(0, bSeesA.Num("data.map"));
    }

    [Fact]
    public async Task APresenceFromOneClient_ReachesEveryOtherClient()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);
        await using UiClient c = await UiClient.ConnectAsync(rig);

        await a.SendAsync(Presence(x: 42));

        Assert.Equal(42, (await ReceivePresenceOfAsync(b, a.ConnectionId)).Num("data.at.0"));
        Assert.Equal(42, (await ReceivePresenceOfAsync(c, a.ConnectionId)).Num("data.at.0"));
    }

    [Fact]
    public async Task TheViewEvent_SaysWhetherTheChangeMovedTheCamera()
    {
        await using HostRig rig = HostRig.Create();

        await rig.DataAsync("view.set", new JsonObject { ["camera"] = new JsonObject { ["yaw"] = 90 } });
        await rig.DataAsync("view.set", new JsonObject { ["hud"] = false });
        await rig.DataAsync("view.set", new JsonObject { ["camera"] = new JsonObject { ["yaw"] = 90 } });
        await rig.DataAsync("layer.set", new JsonObject { ["id"] = "terrain", ["visible"] = true });
        await rig.DataAsync("view.set", new JsonObject { ["follow"] = "AB" });
        await rig.DataAsync("view.set", new JsonObject { ["map"] = 1 });
        await rig.DataAsync("camera.goto", new JsonObject { ["at"] = new JsonArray(-9450, 20, 60) });

        IReadOnlyList<JsonNode> events = rig.EventsOf(EventHub.Topics.View);
        Assert.Equal(new[] { true, false, false, false, true, false, true }, events.Select(e => e.Flag("cameraChanged")));
        Assert.Equal(new[] { 1d, 2, 3, 4, 5, 6, 7 }, events.Select(e => e.Num("version")));
    }

    [Fact]
    public async Task TheViewEvent_NamesTheOriginThatOwnsTheFollow_UntilSomeoneElseChangesIt()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);

        // A follows through its own socket, so the follow belongs to ui:<A>.
        await a.SendAsync(Raw("""{"op":"command","id":1,"name":"view.set","args":{"follow":"AB"}}"""));
        JsonNode followed = await b.ReceiveEventAsync("view", d => d.At("view.follow") is not null);
        Assert.Equal($"ui:{a.ConnectionId}", followed.Str("data.origin"));
        Assert.Equal($"ui:{a.ConnectionId}", followed.Str("data.followOrigin"));

        // A later patch by another origin that leaves the follow alone keeps the owner (a per-frame 'live' patch looks like this).
        await rig.InvokeAsync("view.set", new JsonObject { ["camera"] = new JsonObject { ["yaw"] = 33 } }, CommandOrigin.Mcp);
        JsonNode moved = await b.ReceiveEventAsync("view", d => d.Str("origin") == "mcp");
        Assert.Equal($"ui:{a.ConnectionId}", moved.Str("data.followOrigin"));

        // Following another entity from the CLI takes the follow over.
        await rig.InvokeAsync("view.set", new JsonObject { ["follow"] = "CD" }, CommandOrigin.Cli);
        JsonNode taken = await b.ReceiveEventAsync("view", d => d.Str("origin") == "cli");
        Assert.Equal("cli", taken.Str("data.followOrigin"));

        // Stopping the follow leaves nobody as owner.
        await rig.InvokeAsync("view.set", new JsonObject { ["follow"] = null }, CommandOrigin.Mcp);
        JsonNode stopped = await b.ReceiveEventAsync("view", d => d.Str("origin") == "mcp" && d.At("view.follow") is null);
        Assert.Null(stopped.At("data.followOrigin"));
    }

    [Fact]
    public async Task AnyPatchNamingFollow_TakesTheOwnership_EvenForTheGuidAlreadyFollowed()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);

        await a.SendAsync(Raw("""{"op":"command","id":1,"name":"view.set","args":{"follow":"AB"}}"""));
        await b.ReceiveEventAsync("view", d => d.Str("followOrigin") == $"ui:{a.ConnectionId}");

        await rig.InvokeAsync("view.set", new JsonObject { ["follow"] = "AB" }, CommandOrigin.Cli);

        JsonNode taken = await b.ReceiveEventAsync("view", d => d.Str("origin") == "cli");
        Assert.Equal("cli", taken.Str("data.followOrigin"));
        Assert.Equal("cli", rig.Context.View.FollowOrigin);
    }

    [Fact]
    public async Task TheHello_CarriesTheFollowOwner_OrNullWhenNothingIsFollowed()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        Assert.Null(a.Hello.At("followOrigin"));

        await a.SendAsync(Raw("""{"op":"command","id":1,"name":"view.set","args":{"follow":"AB"}}"""));
        await Wait.UntilAsync(() => rig.Context.View.Current.FollowGuid is not null, "the follow to be stored");
        await using UiClient b = await UiClient.ConnectAsync(rig);

        Assert.Equal($"ui:{a.ConnectionId}", b.Hello.Str("followOrigin"));
    }

    [Fact]
    public async Task WhenTheFollowOwnerDisconnects_TheSharedFollowIsCleared_AndTheOthersAreTold()
    {
        await using HostRig rig = await StartAsync();
        UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);
        string owner = $"ui:{a.ConnectionId}";
        await a.SendAsync(Raw("""{"op":"command","id":1,"name":"view.set","args":{"follow":"AB"}}"""));
        await b.ReceiveEventAsync("view", d => d.Str("followOrigin") == owner);

        await a.DisposeAsync();

        JsonNode cleared = await b.ReceiveEventAsync("view", d => d.At("view.follow") is null);
        Assert.Equal(owner, cleared.Str("data.origin"));
        Assert.Null(cleared.At("data.followOrigin"));
        Assert.True(cleared.Flag("data.cameraChanged"));
        Assert.Null(rig.Context.View.Current.FollowGuid);
    }

    [Fact]
    public async Task WhenAUiThatDoesNotOwnTheFollowDisconnects_TheFollowStays()
    {
        await using HostRig rig = await StartAsync();
        UiClient a = await UiClient.ConnectAsync(rig);
        UiClient b = await UiClient.ConnectAsync(rig);
        await a.SendAsync(Raw("""{"op":"command","id":1,"name":"view.set","args":{"follow":"AB"}}"""));
        await Wait.UntilAsync(() => rig.Context.View.Current.FollowGuid is not null, "the follow to be stored");
        await rig.InvokeAsync("view.set", new JsonObject { ["follow"] = "AB" }, CommandOrigin.Cli);

        await b.DisposeAsync();
        await Wait.UntilAsync(() => rig.Server!.Hub.ConnectionCount == 1, "B to be removed");
        await a.DisposeAsync();
        await Wait.UntilAsync(() => rig.Server!.Hub.ConnectionCount == 0, "A to be removed");

        Assert.NotNull(rig.Context.View.Current.FollowGuid);
        Assert.Equal("cli", rig.Context.View.FollowOrigin);
    }

    [Fact]
    public async Task BookmarkAdd_WithMapAndCamera_SavesThose_AndLeavesTheSharedViewAlone()
    {
        await using HostRig rig = HostRig.Create();
        await rig.DataAsync("camera.goto", new JsonObject { ["at"] = new JsonArray(-9450, 20, 60), ["distance"] = 77, ["yaw"] = 10 });
        string viewBefore = rig.Context.View.Current.ToJson().ToJsonString();
        long versionBefore = rig.Context.View.Version;

        JsonNode added = await rig.DataAsync("bookmark.add", new JsonObject
        {
            ["name"] = "Elsewhere",
            ["map"] = 1,
            ["camera"] = new JsonObject
            {
                ["target"] = new JsonArray(100, 200, 300),
                ["distance"] = 55,
                ["yaw"] = 270,
                ["pitch"] = 30,
                ["mode"] = "top",
            },
        });

        Assert.Equal(1, added.Num("bookmark.map"));
        Assert.Equal(100, added.Num("bookmark.at.0"));
        Assert.Equal(300, added.Num("bookmark.at.2"));
        Assert.Equal(55, added.Num("bookmark.distance"));
        Bookmark saved = Assert.Single(new BookmarkStore(rig.State.BookmarksFile, NullLoggerFactory.Instance).All(), b => !b.BuiltIn);
        Assert.Equal((1u, 270d, 30d, CameraMode.Top), (saved.Map, saved.Yaw, saved.Pitch, saved.Mode));
        Assert.Equal(viewBefore, rig.Context.View.Current.ToJson().ToJsonString());
        Assert.Equal(versionBefore, rig.Context.View.Version);

        // Without them it still saves the shared view's camera.
        JsonNode shared = await rig.DataAsync("bookmark.add", new JsonObject { ["name"] = "Here" });
        Assert.Equal(-9450, shared.Num("bookmark.at.0"));
        Assert.Equal(77, shared.Num("bookmark.distance"));
    }

    [Fact]
    public async Task BookmarkAdd_RefusesAnInvalidCamera_WithoutSavingOrChangingTheView()
    {
        await using HostRig rig = HostRig.Create();

        CommandResult result = await rig.InvokeAsync("bookmark.add", new JsonObject { ["name"] = "Bad", ["camera"] = new JsonObject { ["mode"] = "swim" } });

        Assert.False(result.Ok);
        Assert.DoesNotContain(new BookmarkStore(rig.State.BookmarksFile, NullLoggerFactory.Instance).All(), b => !b.BuiltIn);
        Assert.Equal(0, rig.Context.View.Version);
    }

    [Fact]
    public async Task AUiThatDoesNotOwnTheFollow_CannotStopIt_ButTheOwnerCan()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);

        // A follows AB, then B takes the follow over with CD.
        await a.SendAsync(Raw("""{"op":"command","id":1,"name":"view.set","args":{"follow":"AB"}}"""));
        await b.ReceiveEventAsync("view", d => d.Str("followOrigin") == $"ui:{a.ConnectionId}");
        await b.SendAsync(Raw("""{"op":"command","id":1,"name":"view.set","args":{"follow":"CD"}}"""));
        await a.ReceiveEventAsync("view", d => d.Str("followOrigin") == $"ui:{b.ConnectionId}");

        // A drags: its UI would send follow null together with a camera. The follow is B's, so it stays.
        await a.SendAsync(Raw("""{"op":"command","id":2,"name":"view.set","args":{"follow":null,"camera":{"yaw":77}}}"""));
        JsonNode result = await a.ReceiveUntilAsync(m => m.Str("op") == "result" && m.Num("id") == 2, "the result of A's view.set");

        Assert.True(result.Flag("result.ok"));
        Assert.Equal("CD", rig.Context.View.Current.FollowGuid);
        Assert.Equal($"ui:{b.ConnectionId}", rig.Context.View.FollowOrigin);
        Assert.Equal(77, rig.Context.View.Current.Camera.Yaw);

        // B stops its own follow.
        await b.SendAsync(Raw("""{"op":"command","id":3,"name":"view.set","args":{"follow":null}}"""));
        JsonNode stopped = await a.ReceiveEventAsync("view", d => d.At("view.follow") is null);
        Assert.Equal($"ui:{b.ConnectionId}", stopped.Str("data.origin"));
        Assert.Null(rig.Context.View.Current.FollowGuid);
    }

    [Fact]
    public async Task InvalidPresenceFlood_IsAnsweredOnlyWhileTheErrorBudgetLasts_AndRefillsWithTime()
    {
        var time = new ManualTime();
        await using HostRig rig = await StartAsync(TimeSpan.FromMilliseconds(50), time);
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);
        int flood = AtlasServer.PresenceErrorBurst + 24;

        for (int i = 0; i < flood; i++)
        {
            await a.SendTextAsync("""{"op":"presence"}""");
        }

        // A valid message after the flood proves the host has read every invalid one (one connection is read in order).
        await a.SendAsync(Presence(x: 1));
        await ReceivePresenceOfAsync(b, a.ConnectionId);
        await a.SendAsync(Raw("""{"op":"command","id":9,"name":"status","args":{}}"""));
        int errors = 0;
        while (true)
        {
            JsonNode m = await a.ReceiveAsync();
            if (m.Str("op") == "error")
            {
                errors++;
            }
            else if (m.Str("op") == "result" && m.Num("id") == 9)
            {
                break;
            }
        }

        Assert.True(errors <= AtlasServer.PresenceErrorBurst, $"{errors} answers for {flood} invalid messages");

        // Time refills the budget one answer per presence interval: after two intervals, further invalid messages are answered again.
        time.Advance(TimeSpan.FromMilliseconds(100));
        while (errors < AtlasServer.PresenceErrorBurst + 1)
        {
            await a.SendTextAsync("""{"op":"presence"}""");
            await a.ReceiveUntilAsync(m => m.Str("op") == "error", "an answer after the budget refilled");
            errors++;
        }
    }
}
