using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host;
using ArcaneCore.Atlas.Host.Cli;
using ArcaneCore.Atlas.Tests.Host.Public;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>
/// Agent sessions: <c>session.join</c>, <c>session.move</c>, <c>session.leave</c> and <c>sessions.list</c>, held by the hub
/// beside the UI connections and announced to the UIs like a UI's own presence. Time is a <see cref="ManualTime"/>, so nothing
/// here sleeps.
/// </summary>
public sealed class HostSessionTests
{
    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    private static Task<HostRig> StartAsync(TimeProvider? clock = null, TimeSpan? interval = null, int? maxAgents = null, TimeSpan? timeout = null)
        => HostRig.StartAsync(new AtlasServerOptions
        {
            PresenceMinInterval = interval ?? TimeSpan.Zero,
            Time = clock ?? new ManualTime(),
            MaxAgentSessions = maxAgents ?? 16,
            AgentSessionTimeout = timeout ?? TimeSpan.FromMinutes(10),
        });

    private static async Task<string> JoinAsync(HostRig rig, string name = "Grok Bot", JsonObject? more = null)
    {
        var args = new JsonObject { ["name"] = name };
        foreach ((string key, JsonNode? value) in more ?? [])
        {
            args[key] = value?.DeepClone();
        }

        return (await rig.DataAsync("session.join", args)).Str("id");
    }

    private static async Task<CommandResult> FailedAsync(HostRig rig, string name, string json)
    {
        CommandResult result = await rig.InvokeAsync(name, Args(json));
        Assert.False(result.Ok, $"{name} {json} should have failed");
        return result;
    }

    private static Task<JsonNode> ReceivePresenceOfAsync(UiClient ui, string id, Func<JsonNode, bool>? where = null)
        => ui.ReceiveEventAsync(UiHub.PresenceTopic, d => d.Str("id") == id && (where?.Invoke(d) ?? true));

    private static Task<JsonNode> ReceiveLeftOfAsync(UiClient ui, string id)
        => ui.ReceiveEventAsync(UiHub.PresenceLeftTopic, d => d.Str("id") == id);

    [Fact]
    public async Task JoinMoveAndLeave_AreSeenByAConnectedUi_AsPresenceEvents()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient ui = await UiClient.ConnectAsync(rig);

        JsonNode joined = await rig.DataAsync("session.join", Args("""{"name":"Grok Bot","map":1,"at":[-9000,400,90],"yaw":90,"mode":"fly"}"""));
        string id = joined.Str("id");

        Assert.Matches("^[0-9a-f]{12}$", id);
        Assert.Equal("Grok Bot", joined.Str("name"));
        JsonNode first = await ReceivePresenceOfAsync(ui, id);
        Assert.Equal("Grok Bot", first.Str("data.name"));
        Assert.Equal("agent", first.Str("data.kind"));
        Assert.Equal(1, first.Num("data.map"));
        Assert.Equal(-9000, first.Num("data.at.0"));
        Assert.Equal(400, first.Num("data.at.1"));
        Assert.Equal(90, first.Num("data.at.2"));
        Assert.Equal(90, first.Num("data.yaw"));
        Assert.Equal("fly", first.Str("data.mode"));

        JsonNode moved = await rig.DataAsync("session.move", Args($$"""{"id":"{{id}}","at":[1,2,3],"yaw":-90}"""));
        Assert.Equal(id, moved.Str("id"));
        Assert.Equal("Grok Bot", moved.Str("name"));
        Assert.Equal(1, moved.Num("map"));
        Assert.Equal("fly", moved.Str("mode"));
        Assert.Equal(270, moved.Num("yaw"));
        Assert.Equal("agent", moved.Str("kind"));
        JsonNode second = await ReceivePresenceOfAsync(ui, id, d => d.Num("at.0") == 1);
        Assert.Equal(270, second.Num("data.yaw"));
        Assert.Equal("fly", second.Str("data.mode"));

        JsonNode left = await rig.DataAsync("session.leave", Args($$"""{"id":"{{id}}"}"""));
        Assert.Equal(id, left.Str("id"));
        Assert.True(left.Flag("left"));
        await ReceiveLeftOfAsync(ui, id);
        Assert.Empty(rig.Server!.Hub.PresenceSnapshot());
        Assert.Equal(CommandException.BadArguments, (await FailedAsync(rig, "session.move", $$"""{"id":"{{id}}","yaw":5}""")).ErrorCode);
        Assert.Equal(CommandException.BadArguments, (await FailedAsync(rig, "session.leave", $$"""{"id":"{{id}}"}""")).ErrorCode);
    }

    [Fact]
    public async Task AnAgentSession_ChangesNeitherTheSharedViewNorTheUiCount_AndNothingReturnedHoldsTheToken()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        string viewBefore = rig.Context.View.Current.ToJson().ToJsonString();
        long versionBefore = rig.Context.View.Version;

        JsonNode joined = await rig.DataAsync("session.join", Args("""{"name":"Grok Bot","map":1,"at":[5,6,7]}"""));
        JsonNode moved = await rig.DataAsync("session.move", Args($$"""{"id":"{{joined.Str("id")}}","yaw":10}"""));
        JsonNode listed = await rig.DataAsync("sessions.list");
        JsonNode status = await rig.DataAsync("status");

        Assert.Equal(viewBefore, rig.Context.View.Current.ToJson().ToJsonString());
        Assert.Equal(versionBefore, rig.Context.View.Version);
        Assert.Equal(1, rig.Server!.Hub.ConnectionCount);
        Assert.Equal(1, status.Num("ui.connections"));
        string everything = joined.ToJsonString() + moved.ToJsonString() + listed.ToJsonString();
        Assert.DoesNotContain(rig.Server.Token, everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAgentSession_IsListedBesideAUiSession_InHello_SessionsList_AndStatus_WithTheRightKind()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await a.SendAsync(Args("""{"op":"presence","name":"Ann","map":0,"at":[1,2,3],"yaw":45,"mode":"orbit"}"""));
        await Wait.UntilAsync(() => rig.Server!.Hub.PresenceSnapshot().Count == 1, "A's presence to be stored");
        string agentId = await JoinAsync(rig, "Grok Bot", Args("""{"map":1,"at":[9,8,7]}"""));

        await using UiClient late = await UiClient.ConnectAsync(rig);
        JsonNode listed = await rig.DataAsync("sessions.list");
        JsonNode status = await rig.DataAsync("status");

        JsonArray hello = Assert.IsType<JsonArray>(late.Hello.At("sessions"));
        Assert.Equal(new[] { a.ConnectionId, agentId }, hello.Select(s => s.Str("id")));
        Assert.Equal(new[] { "ui", "agent" }, hello.Select(s => s.Str("kind")));
        JsonArray sessions = Assert.IsType<JsonArray>(listed.At("sessions"));
        Assert.Equal(new[] { a.ConnectionId, agentId }, sessions.Select(s => s.Str("id")));
        Assert.Equal(new[] { "Ann", "Grok Bot" }, sessions.Select(s => s.Str("name")));
        Assert.Equal(new[] { "ui", "agent" }, sessions.Select(s => s.Str("kind")));
        Assert.Equal(1, sessions[1]!.Num("map"));
        Assert.Equal(9, sessions[1]!.Num("at.0"));
        Assert.Equal(new[] { a.ConnectionId, agentId }, Assert.IsType<JsonArray>(status.At("ui.sessions")).Select(s => s.Str("id")));
        Assert.Equal(new[] { "ui", "agent" }, Assert.IsType<JsonArray>(status.At("ui.sessions")).Select(s => s.Str("kind")));
    }

    [Fact]
    public async Task APresenceFromAUi_CarriesKindUi_ToTheOtherUis()
    {
        await using HostRig rig = await StartAsync();
        await using UiClient a = await UiClient.ConnectAsync(rig);
        await using UiClient b = await UiClient.ConnectAsync(rig);

        await a.SendAsync(Args("""{"op":"presence","map":0,"at":[1,2,3],"yaw":45,"mode":"orbit"}"""));

        Assert.Equal("ui", (await ReceivePresenceOfAsync(b, a.ConnectionId)).Str("data.kind"));
    }

    [Fact]
    public async Task AnAgentSession_ExpiresWithoutAJoinOrMove_AndTheUisAreToldItLeft()
    {
        var clock = new ManualTime();
        await using HostRig rig = await StartAsync(clock);
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        string id = await JoinAsync(rig);
        await ReceivePresenceOfAsync(ui, id);

        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.Single(Assert.IsType<JsonArray>((await rig.DataAsync("sessions.list")).At("sessions")));

        clock.Advance(TimeSpan.FromSeconds(1));
        JsonNode listed = await rig.DataAsync("sessions.list");

        Assert.Empty(Assert.IsType<JsonArray>(listed.At("sessions")));
        await ReceiveLeftOfAsync(ui, id);
        CommandResult moved = await FailedAsync(rig, "session.move", $$"""{"id":"{{id}}","yaw":5}""");
        Assert.Equal(CommandException.BadArguments, moved.ErrorCode);
        Assert.Contains("expired", moved.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMove_RefreshesTheExpiry_AndTheSweepAlsoRunsOnAnySessionCommandAndOnSnapshotReads()
    {
        var clock = new ManualTime();
        await using HostRig rig = await StartAsync(clock);
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        string kept = await JoinAsync(rig, "Kept");
        string lost = await JoinAsync(rig, "Lost");
        clock.Advance(TimeSpan.FromMinutes(9));
        await rig.DataAsync("session.move", Args($$"""{"id":"{{kept}}","yaw":1}"""));

        clock.Advance(TimeSpan.FromMinutes(2));

        // A snapshot read (what hello and status use) sweeps, without any session command.
        UiPresence only = Assert.Single(rig.Server!.Hub.PresenceSnapshot());
        Assert.Equal(kept, only.Id);
        await ReceiveLeftOfAsync(ui, lost);
        clock.Advance(TimeSpan.FromMinutes(9));
        string another = await JoinAsync(rig, "Another");
        await ReceiveLeftOfAsync(ui, kept);
        Assert.Equal(another, Assert.Single(rig.Server.Hub.PresenceSnapshot()).Id);
    }

    [Fact]
    public async Task TheTimeoutIsAnOption()
    {
        var clock = new ManualTime();
        await using HostRig rig = await StartAsync(clock, timeout: TimeSpan.FromSeconds(30));
        await JoinAsync(rig);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Single(rig.Server!.Hub.PresenceSnapshot());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(rig.Server.Hub.PresenceSnapshot());
        Assert.Equal(TimeSpan.FromMinutes(10), new AtlasServerOptions().AgentSessionTimeout);
        Assert.Equal(16, new AtlasServerOptions().MaxAgentSessions);
    }

    [Fact]
    public async Task JoiningPastTheCap_Fails_UntilASessionLeavesOrExpires()
    {
        var clock = new ManualTime();
        await using HostRig rig = await StartAsync(clock, maxAgents: 2);
        string first = await JoinAsync(rig, "One");
        await JoinAsync(rig, "Two");

        CommandResult refused = await FailedAsync(rig, "session.join", """{"name":"Three"}""");

        Assert.Equal(CommandException.Failed, refused.ErrorCode);
        Assert.Contains("2 agent sessions", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(2, rig.Server!.Hub.PresenceSnapshot().Count);

        await rig.DataAsync("session.leave", Args($$"""{"id":"{{first}}"}"""));
        await JoinAsync(rig, "Three");
        Assert.Equal(2, rig.Server.Hub.PresenceSnapshot().Count);

        clock.Advance(TimeSpan.FromMinutes(10));
        await JoinAsync(rig, "Four");
        await JoinAsync(rig, "Five");
        Assert.Equal(new[] { "Four", "Five" }, rig.Server.Hub.PresenceSnapshot().Select(p => p.Name));
    }

    [Fact]
    public async Task AMoveThatComesTooSoon_FailsWithARetryMessage_InsteadOfBeingDropped()
    {
        var clock = new ManualTime();
        await using HostRig rig = await StartAsync(clock, interval: TimeSpan.FromMilliseconds(50));
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        string id = await JoinAsync(rig);

        // The first move after a join is not limited: a script may join and move at once.
        await rig.DataAsync("session.move", Args($$"""{"id":"{{id}}","at":[1,1,1]}"""));
        clock.Advance(TimeSpan.FromMilliseconds(49));
        CommandResult tooSoon = await FailedAsync(rig, "session.move", $$"""{"id":"{{id}}","at":[2,2,2]}""");

        Assert.Equal(CommandException.Unavailable, tooSoon.ErrorCode);
        Assert.Contains("retry in 1 ms", tooSoon.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, rig.Server!.Hub.PresenceSnapshot().Single().X);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        JsonNode accepted = await rig.DataAsync("session.move", Args($$"""{"id":"{{id}}","at":[3,3,3]}"""));
        Assert.Equal(3, accepted.Num("at.0"));
        List<double> seen = [];
        while (seen.Count < 2)
        {
            seen.Add((await ReceivePresenceOfAsync(ui, id, d => d.Num("at.0") != 0)).Num("data.at.0"));
        }

        Assert.Equal(new[] { 1d, 3d }, seen);
    }

    [Fact]
    public async Task AnInvalidJoinOrMove_IsRefused_AndChangesNothing()
    {
        await using HostRig rig = await StartAsync();
        string id = await JoinAsync(rig, "Grok Bot", Args("""{"map":2,"at":[10,20,30],"yaw":15,"mode":"top"}"""));
        UiPresence before = rig.Server!.Hub.PresenceSnapshot().Single();
        string[] badMoves =
        [
            $$"""{"id":"{{id}}","at":[1,2]}""",
            $$"""{"id":"{{id}}","at":[1,2,3,4]}""",
            $$"""{"id":"{{id}}","at":[100001,0,0]}""",
            $$"""{"id":"{{id}}","at":"1,2"}""",
            $$"""{"id":"{{id}}","at":[1,"x",3]}""",
            $$"""{"id":"{{id}}","map":-1}""",
            $$"""{"id":"{{id}}","map":4294967296}""",
            $$"""{"id":"{{id}}","map":1.5}""",
            $$"""{"id":"{{id}}","mode":"swim"}""",
            $$"""{"id":"{{id}}","yaw":"north"}""",
            """{"id":"0123456789ab","yaw":1}""",
            """{"yaw":1}""",
        ];

        foreach (string json in badMoves)
        {
            CommandResult result = await FailedAsync(rig, "session.move", json);
            Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        }

        string[] badJoins = ["""{"at":[1,2,3]}""", """{"name":"x","at":[1,2]}""", """{"name":"x","map":-3}""", """{"name":"x","mode":"swim"}""", """{"name":"x","at":[0,0,200000]}"""];
        foreach (string json in badJoins)
        {
            Assert.Equal(CommandException.BadArguments, (await FailedAsync(rig, "session.join", json)).ErrorCode);
        }

        Assert.Equal(before, rig.Server.Hub.PresenceSnapshot().Single());
    }

    [Fact]
    public async Task JoinDefaultsAndNames_FollowTheSamePathAsAUiPresence()
    {
        await using HostRig rig = await StartAsync();

        JsonNode plain = await rig.DataAsync("session.join", Args("""{"name":"  Grok\u0001 \u202eBot  "}"""));
        JsonNode blank = await rig.DataAsync("session.join", Args("""{"name":"\u200b  "}"""));
        JsonNode empty = await rig.DataAsync("session.join", Args("""{"name":""}"""));
        JsonNode emoji = await rig.DataAsync("session.join", Args("""{"name":"Ann\ud83d\ude00x"}"""));
        JsonNode csv = await rig.DataAsync("session.join", Args("""{"name":"Csv","at":"-9000,400,90","map":"1"}"""));
        JsonNode cut = await rig.DataAsync("session.join", Args($$"""{"name":"{{new string('n', 40)}}"}"""));

        Assert.Equal("Grok Bot", plain.Str("name"));
        Assert.Equal("session " + blank.Str("id")[..4], blank.Str("name"));
        Assert.Equal("session " + empty.Str("id")[..4], empty.Str("name"));
        Assert.Equal("Ann" + char.ConvertFromUtf32(0x1F600) + "x", emoji.Str("name"));
        Assert.Equal(new string('n', 32), cut.Str("name"));
        UiPresence stored = rig.Server!.Hub.PresenceSnapshot().Single(p => p.Id == csv.Str("id"));
        Assert.Equal((1u, -9000d, 400d, 90d, 0d, "orbit", "agent"), (stored.Map, stored.X, stored.Y, stored.Z, stored.Yaw, stored.Mode, stored.Kind));
        UiPresence defaults = rig.Server.Hub.PresenceSnapshot().Single(p => p.Id == plain.Str("id"));
        Assert.Equal((0u, 0d, 0d, 0d, 0d, "orbit"), (defaults.Map, defaults.X, defaults.Y, defaults.Z, defaults.Yaw, defaults.Mode));
        Assert.Equal(CommandException.BadArguments, (await FailedAsync(rig, "session.join", "{}")).ErrorCode);
    }

    [Fact]
    public async Task TheSessionCommands_NeedAServer_Headless_AndOnAContextNobodyServes()
    {
        await using HostRig headless = HostRig.Create(headless: true);
        await using HostRig unserved = HostRig.Create();

        foreach ((string name, string json) in new[] { ("session.join", """{"name":"Grok Bot"}"""), ("session.move", """{"id":"0123456789ab"}"""), ("session.leave", """{"id":"0123456789ab"}"""), ("sessions.list", "{}") })
        {
            CommandResult a = await headless.InvokeAsync(name, Args(json));
            CommandResult b = await unserved.InvokeAsync(name, Args(json));

            Assert.False(a.Ok);
            Assert.Equal(CommandException.Unavailable, a.ErrorCode);
            Assert.Contains("arcane-atlas serve", a.ErrorMessage, StringComparison.Ordinal);
            Assert.False(b.Ok);
            Assert.Equal(CommandException.Unavailable, b.ErrorCode);
            Assert.Contains("arcane-atlas serve", b.ErrorMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AgentSessionsAreForgotten_WhenTheServerStops()
    {
        HostRig rig = await StartAsync();
        await JoinAsync(rig);
        UiHub hub = rig.Server!.Hub;

        await rig.DisposeAsync();

        Assert.False(hub.Attached);
        Assert.Empty(hub.PresenceSnapshot());
    }

    [Fact]
    public async Task JoiningAfterTheServerStopped_Fails_AndCreatesNothing()
    {
        HostRig rig = await StartAsync();
        UiHub hub = rig.Server!.Hub;
        await rig.DisposeAsync();

        CommandException join = Assert.Throws<CommandException>(() => hub.JoinAgent((id, name) => new UiPresence(id, name, 0, 0, 0, 0, 0, "orbit", UiPresence.KindAgent)));
        CommandException move = Assert.Throws<CommandException>(() => hub.MoveAgent("0123456789ab", (current, _) => current));
        CommandException leave = Assert.Throws<CommandException>(() => hub.LeaveAgent("0123456789ab"));

        Assert.Equal(CommandException.Unavailable, join.Code);
        Assert.Equal(CommandException.Unavailable, move.Code);
        Assert.Equal(CommandException.Unavailable, leave.Code);
        Assert.Empty(hub.PresenceSnapshot());
    }

    [Fact]
    public async Task SessionMove_PublishesNoCommandEvent_ButJoinAndLeaveDo()
    {
        await using HostRig rig = await StartAsync();
        string id = await JoinAsync(rig);

        await rig.DataAsync("session.move", Args($$"""{"id":"{{id}}","at":[1,2,3]}"""));
        await rig.DataAsync("session.move", Args($$"""{"id":"{{id}}","yaw":10}"""));
        await rig.DataAsync("session.leave", Args($$"""{"id":"{{id}}"}"""));

        Assert.Equal(new[] { "session.join", "session.leave" }, rig.EventsOf(EventHub.Topics.Command).Select(e => e.Str("command")));
    }

    [Fact]
    public async Task TheSessionCommands_AreInTheCatalog_WithAPointArgument()
    {
        await using HostRig rig = HostRig.Create();

        JsonNode all = await rig.DataAsync("help");
        string[] names = [.. all.At("commands")!.AsArray().Select(c => c.Str("name"))];
        foreach (string name in new[] { "session.join", "session.move", "session.leave", "sessions.list" })
        {
            Assert.Contains(name, names);
        }

        JsonNode join = await rig.DataAsync("help", Args("""{"command":"session.join"}"""));
        Assert.Equal(new[] { "name" }, join.At("schema.required")!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal("array", join.Str("schema.properties.at.type"));
    }

    [Fact]
    public async Task TheSweepTimer_IsCreatedOnTheOptionsClock_AndDropsExpiredSessionsWhenItFires()
    {
        var clock = new TimerSpyTime();
        await using HostRig rig = await StartAsync(clock, timeout: TimeSpan.FromSeconds(30));
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        string id = await JoinAsync(rig);

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(clock.Timers).Period);

        clock.Advance(TimeSpan.FromSeconds(31));
        clock.Timers[0].Fire();

        await ReceiveLeftOfAsync(ui, id);
    }

    /// <summary>A manual clock whose timers never run by themselves: a test fires them.</summary>
    private sealed class TimerSpyTime : TimeProvider
    {
        private long _ticks;

        public List<SpyTimer> Timers { get; } = [];

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan span) => Interlocked.Add(ref _ticks, span.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new SpyTimer(callback, state, period);
            lock (Timers)
            {
                Timers.Add(timer);
            }

            return timer;
        }
    }

    private sealed class SpyTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        public TimeSpan Period { get; } = period;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>The <c>url</c> verb and the session commands through <c>ctl</c>, against a real in-process server found through its discovery file.</summary>
public sealed class CliSessionTests
{
    [Fact]
    public async Task Url_PrintsOneJsonLine_WithThePidAndTheBrowserAddressOfTheRunningInstance()
    {
        await using HostRig server = await HostRig.StartAsync();
        using var cli = new CliRig(server.State);

        CliResult result = await cli.RunAsync("url");

        Assert.Equal(0, result.Exit);
        Assert.Single(result.Lines);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(Environment.ProcessId, (int)result.Json().Num("pid"));
        Assert.Equal($"http://127.0.0.1:{server.Server!.Port}/#t={server.Server.Token}", result.Json().Str("url"));
        Assert.Equal(server.Server.BrowserAddress.AbsoluteUri, result.Json().Str("url"));
    }

    [Fact]
    public async Task Url_WithAName_AddsItUrlEncoded_AndPidChoosesAnInstance()
    {
        await using HostRig server = await HostRig.StartAsync();
        using var cli = new CliRig(server.State);
        Discovery.Write(server.State, new DiscoveryRecord(4_000_010, 9, "other", DateTimeOffset.UtcNow.AddHours(-1), "x", null, null));
        cli.IsAlive = _ => true;
        string pid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        CliResult named = await cli.RunAsync("url", "--pid", pid, "--name", "Grok Bot");
        CliResult odd = await cli.RunAsync("url", "--pid", pid, "--name=A&B=c #1é");
        CliResult ambiguous = await cli.RunAsync("url");
        CliResult other = await cli.RunAsync("url", "--pid", "4000010");

        Assert.Equal($"http://127.0.0.1:{server.Server!.Port}/#t={server.Server.Token}&name=Grok%20Bot", named.Json().Str("url"));
        Assert.Equal($"http://127.0.0.1:{server.Server.Port}/#t={server.Server.Token}&name={Uri.EscapeDataString("A&B=c #1é")}", odd.Json().Str("url"));
        Assert.DoesNotContain("&B", odd.Json().Str("url").Split("&name=")[1], StringComparison.Ordinal);
        Assert.Equal(2, ambiguous.Exit);
        Assert.Equal(0, other.Exit);
        Assert.Equal("http://127.0.0.1:9/#t=other", other.Json().Str("url"));
    }

    [Fact]
    public async Task Url_MakesNoNetworkCall_SoItWorksForAnInstanceNobodyListensOn()
    {
        using var cli = new CliRig();
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        int port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        Discovery.Write(cli.State, new DiscoveryRecord(4_000_011, port, "tok-en_1", DateTimeOffset.UtcNow, "x", null, null));
        cli.IsAlive = _ => true;

        CliResult result = await cli.RunAsync("url", "--name", "Bot");

        Assert.Equal(0, result.Exit);
        Assert.Equal($"http://127.0.0.1:{port}/#t=tok-en_1&name=Bot", result.Json().Str("url"));
    }

    [Fact]
    public async Task Url_WithoutAnInstance_ExitsOne_AndBadUsageExitsTwo()
    {
        using var cli = new CliRig();

        CliResult none = await cli.RunAsync("url");
        CliResult extra = await cli.RunAsync("url", "status");
        CliResult unknown = await cli.RunAsync("url", "--bogus");
        CliResult emptyName = await cli.RunAsync("url", "--name", "  ");
        CliResult badPid = await cli.RunAsync("url", "--pid", "abc");

        Assert.Equal(1, none.Exit);
        Assert.Contains("no running 'arcane-atlas serve' instance", none.Error, StringComparison.Ordinal);
        Assert.Empty(none.Lines);
        foreach (CliResult usage in new[] { extra, unknown, emptyName, badPid })
        {
            Assert.Equal(2, usage.Exit);
            Assert.Empty(usage.Lines);
        }
    }

    [Fact]
    public async Task TheUsage_ListsUrl_AndSaysItsOutputIsASecret()
    {
        using var cli = new CliRig();

        CliResult help = await cli.RunAsync("help");

        Assert.Equal(0, help.Exit);
        Assert.Contains("url     [--pid N] [--name NAME]", AtlasCli.Usage, StringComparison.Ordinal);
        Assert.Contains("secret", AtlasCli.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ctl_JoinMoveListAndLeave_WorkWithTheDocumentedArgumentSyntax()
    {
        await using HostRig server = await HostRig.StartAsync(new AtlasServerOptions { PresenceMinInterval = TimeSpan.Zero });
        using var cli = new CliRig(server.State);

        CliResult joined = await cli.RunAsync("ctl", "--compact", "session.join", "name=Grok Bot", "map=0", "at=[-9000,400,90]");
        string id = joined.Json().Str("data.id");
        CliResult moved = await cli.RunAsync("ctl", "--compact", "session.move", $"id={id}", "at=[1,2,3]", "yaw=90", "mode=fly");
        CliResult movedPlain = await cli.RunAsync("ctl", "--compact", "session.move", $"id={id}", "at=4,5,6");
        CliResult listed = await cli.RunAsync("ctl", "--compact", "sessions.list");
        CliResult left = await cli.RunAsync("ctl", "--compact", "session.leave", $"id={id}");

        Assert.Equal(0, joined.Exit);
        Assert.Equal("Grok Bot", joined.Json().Str("data.name"));
        Assert.Equal(0, moved.Exit);
        Assert.Equal(90, moved.Json().Num("data.yaw"));
        Assert.Equal("fly", moved.Json().Str("data.mode"));
        Assert.Equal(1, moved.Json().Num("data.at.0"));
        Assert.Equal(0, movedPlain.Exit);
        Assert.Equal(6, movedPlain.Json().Num("data.at.2"));
        Assert.Equal(id, listed.Json().Str("data.sessions.0.id"));
        Assert.Equal("agent", listed.Json().Str("data.sessions.0.kind"));
        Assert.Equal(4, listed.Json().Num("data.sessions.0.at.0"));
        Assert.Equal(0, left.Exit);
        Assert.True(left.Json().Flag("data.left"));
        Assert.Empty(server.Server!.Hub.PresenceSnapshot());
    }

    [Fact]
    public async Task Run_Headless_SessionCommandsExitOne_WithTheAttachHint()
    {
        using var cli = new CliRig();

        CliResult result = await cli.RunAsync("run", "--compact", "session.join", "name=Grok Bot");

        Assert.Equal(1, result.Exit);
        Assert.Equal(CommandException.Unavailable, result.Json().Str("error.code"));
        Assert.Contains("arcane-atlas serve", result.Json().Str("error.message"), StringComparison.Ordinal);
    }
}
