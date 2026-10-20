using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using ArcaneCore.Game;
using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The map feed's packet tools (docs/areas/map-feed.md, "Packet tools"): off by default and refused outside Development; console lines
/// through the console executor (logged and audited); injections through the session's own dispatcher, reaching the real handler and its
/// real reply; sends through the session's own send path; taps that see both directions, mark tool packets, end on count, time or
/// untap, and drop rather than block. A real world on the manual clock, a real named pipe, a real client over TCP.
/// </summary>
public sealed class PacketToolsTests
{
    private static FeedWorldSetup ToolsOn(bool bots = false, string environment = "Development", bool allowOutside = false) => new()
    {
        Bots = bots,
        Terrain = !bots,
        PacketTools = o =>
        {
            o.Enabled = true;
            o.AllowOutsideDevelopment = allowOutside;
        },
        Environment = environment,
    };

    private static string Hex(params byte[] bytes) => Convert.ToHexString(bytes);

    private static async Task<JsonElement> RequestAsync(FeedClient client, string line)
    {
        await client.SendAsync(line);
        return await client.ReadOpAsync("result");
    }

    /// <summary>Send a request and read its result; a tap's lines that arrive first are put in <paramref name="others"/>.</summary>
    private static async Task<JsonElement> RequestAsync(FeedClient client, string line, List<JsonElement> others)
    {
        await client.SendAsync(line);
        while (true)
        {
            JsonElement next = await client.ReadAsync();
            if (next.Op() == "result")
            {
                return next;
            }

            others.Add(next);
        }
    }

    private static void AssertRefused(JsonElement result, long id, string kind, string code)
    {
        Assert.Equal(id, result.GetProperty("id").GetInt64());
        Assert.False(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.Equal(kind, result.GetProperty("kind").GetString());
        Assert.Equal(code, result.Code());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("message").GetString()));
    }

    [WindowsFact]
    public async Task ByDefault_TheToolsAreOff_TheHelloSaysNothing_AndEveryToolOpIsRefused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup());
        Assert.Null(world.Feed.Tools);
        FeedClient client = await world.ConnectAsync(hello: false);
        JsonElement hello = await client.HelloAsync();
        Assert.False(hello.TryGetProperty("tools", out _), hello.ToString());

        AssertRefused(await RequestAsync(client, """{"op":"console","id":1,"line":"reload status"}"""), 1, "console", MapFeedProtocol.ToolsOff);
        AssertRefused(await RequestAsync(client, """{"op":"inject","id":2,"target":"x","opcode":"CMSG_QUERY_TIME"}"""), 2, "inject", MapFeedProtocol.ToolsOff);
        AssertRefused(await RequestAsync(client, """{"op":"send","id":3,"target":"x","opcode":"SMSG_NOTIFICATION"}"""), 3, "send", MapFeedProtocol.ToolsOff);
        AssertRefused(await RequestAsync(client, """{"op":"tap","id":4,"target":"x"}"""), 4, "tap", MapFeedProtocol.ToolsOff);
    }

    [WindowsFact]
    public async Task Enabled_OutsideDevelopment_IsRefusedWithAWarning_UnlessTheSecondOptInIsSet()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using (FeedWorld refused = await FeedWorld.StartAsync(ToolsOn(environment: "Production")))
        {
            Assert.Null(refused.Feed.Tools);
            Assert.Contains(refused.FeedLog, e => e.Level == LogLevel.Warning && e.Message.Contains("packet tools off: the environment is 'Production'", StringComparison.Ordinal));
            FeedClient client = await refused.ConnectAsync(hello: false);
            Assert.False((await client.HelloAsync()).TryGetProperty("tools", out _));
            AssertRefused(await RequestAsync(client, """{"op":"console","id":1,"line":"reload status"}"""), 1, "console", MapFeedProtocol.ToolsOff);
        }

        await using FeedWorld allowed = await FeedWorld.StartAsync(ToolsOn(environment: "Production", allowOutside: true));
        Assert.NotNull(allowed.Feed.Tools);
        Assert.Contains(allowed.FeedLog, e => e.Level == LogLevel.Warning && e.Message.Contains("packet tools on OUTSIDE Development (environment 'Production'", StringComparison.Ordinal));
        FeedClient on = await allowed.ConnectAsync(hello: false);
        Assert.True((await on.HelloAsync()).GetProperty("tools").GetBoolean());
    }

    [WindowsFact]
    public async Task InDevelopment_TheToolsStart_WithAWarning_AndTheHelloSaysSo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn());
        Assert.NotNull(world.Feed.Tools);
        Assert.Contains(world.FeedLog, e => e.Level == LogLevel.Warning && e.Message.StartsWith("packet tools on: ", StringComparison.Ordinal));
        FeedClient client = await world.ConnectAsync(hello: false);
        Assert.True((await client.HelloAsync()).GetProperty("tools").GetBoolean());
    }

    [WindowsFact]
    public async Task Console_RunsTheLineAsTheConsole_ReturnsItsReply_LogsItRedacted_AndIsAudited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn());
        FeedClient client = await world.ConnectAsync();

        JsonElement result = await RequestAsync(client, """{"op":"console","id":7,"line":".reload status"}""");
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.Equal("console", result.GetProperty("kind").GetString());
        Assert.Equal(7, result.GetProperty("id").GetInt64());
        Assert.True(result.GetProperty("settled").GetBoolean());
        Assert.Contains("config: not reloaded since start", result.GetProperty("lines").Strings());
        Assert.All(result.GetProperty("lines").Strings(), l => Assert.False(l.StartsWith("[console]", StringComparison.Ordinal), l));

        JsonElement secret = await RequestAsync(client, """{"op":"console","id":8,"line":"account set-password nosuchaccount hunter2secret hunter2secret"}""");
        Assert.True(secret.GetProperty("ok").GetBoolean(), secret.ToString());

        Assert.Contains(world.FeedLog, e => e.Level == LogLevel.Information && e.Message.StartsWith("Map feed console command: .reload status (client", StringComparison.Ordinal));
        Assert.DoesNotContain(world.Logs.Entries, e => e.Message.Contains("hunter2secret", StringComparison.Ordinal));
        GmAuditEntry audited = Assert.Single(world.Host.WorldServices.GetRequiredService<GmAuditFeature>().Tail(50), e => e.Command.StartsWith("reload status", StringComparison.Ordinal));
        Assert.Equal("Console", audited.Player);
    }

    [WindowsFact]
    public async Task Inject_ASessionOpcode_RunsTheRealHandler_AndTheClientGetsTheRealReply()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn());
        await using WorldTestClient player = await world.AddPlayerAsync("Feedalice");
        FeedClient client = await world.ConnectAsync();
        using var counted = new ToolsCounter();

        JsonElement result = await RequestAsync(client, """{"op":"inject","id":1,"target":"feedalice","opcode":"CMSG_QUERY_TIME"}""");

        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.Equal("inject", result.GetProperty("kind").GetString());
        Assert.Equal("session", result.GetProperty("handler").GetString());
        Assert.Equal("handled", result.GetProperty("verdict").GetString());
        Assert.Equal((int)WorldOpcode.CmsgQueryTime, result.GetProperty("opcode").GetInt32());
        Assert.Equal("CMSG_QUERY_TIME", result.GetProperty("name").GetString());
        Assert.Equal("Feedalice", result.GetProperty("target").GetProperty("name").GetString());
        Assert.False(result.GetProperty("target").GetProperty("bot").GetBoolean());

        // SMSG_QUERY_TIME_RESPONSE: u32 unix time (vmangos QueryHandler.cpp HandleQueryTimeOpcode).
        byte[] reply = await player.ReadUntilAsync(WorldOpcode.SmsgQueryTimeResponse);
        Assert.Equal(4, reply.Length);

        Assert.Contains(world.FeedLog, e => e.Level == LogLevel.Information && e.Message.StartsWith("Map feed inject: CMSG_QUERY_TIME (0 bytes) as 'Feedalice'", StringComparison.Ordinal));
        Assert.Equal(1, counted.Count("inject", "accepted"));
    }

    /// <summary>Replace the session handler of CMSG_QUERY_TIME on the host's table (the sessions share it).</summary>
    private static void ReplaceQueryTimeHandler(FeedWorld world, SessionHandler handler)
    {
        var replacement = new OpcodeTable();
        replacement.OnSession(WorldOpcode.CmsgQueryTime, SessionStates.Authenticated, handler);
        OpcodeTable swapped = world.Host.Opcodes.TrySwap([WorldOpcode.CmsgQueryTime], replacement, out string? error)!;
        Assert.Null(error);
        world.Host.Opcodes.Replace(swapped);
    }

    [WindowsFact]
    public async Task Inject_ASessionHandlerThatThrows_EndsTheSession_LikeTheReader_AndTheVerdictSaysSo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn());
        await using WorldTestClient player = await world.AddPlayerAsync("Feedalice");
        WorldSession session = world.Host.Registry.Sessions.Single(s => s.Player?.Name == "Feedalice");
        FeedClient client = await world.ConnectAsync();
        ReplaceQueryTimeHandler(world, (_, _) => throw new InvalidOperationException("injected handler fault"));

        JsonElement result = await RequestAsync(client, """{"op":"inject","id":1,"target":"Feedalice","opcode":"CMSG_QUERY_TIME"}""");

        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.Equal("disconnected", result.GetProperty("verdict").GetString());
        await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the faulted session is closed");
    }

    [Fact]
    public void ConsoleSink_ACommandOfAnEndedRequest_NeverFillsALaterRequest()
    {
        var late = new List<string>();
        var sink = new ToolConsoleSink(late.Add);

        long abandoned = sink.Begin();
        sink.End();
        Assert.False(sink.Enter(abandoned)); // gave up before the world thread got to it: the command must not run

        long running = sink.Begin();
        Assert.True(sink.Enter(running));
        sink.WriteLine("[console] mine");
        IReadOnlyList<string> mine = sink.End(); // the request ended while its command was still printing
        long next = sink.Begin();
        sink.WriteLine("[console] straggler");
        sink.Exit();
        sink.WriteLine("[console] next request");
        IReadOnlyList<string> nextLines = sink.End();

        Assert.Equal(["mine"], mine);
        Assert.Equal(["straggler"], late);
        Assert.Equal(["next request"], nextLines);
        Assert.True(next > running);
    }

    [WindowsFact]
    public async Task Inject_WhileTheReaderOrAnotherInjectionHoldsTheDispatcher_IsBusy_AndRunsOnceItIsFree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn());
        await using WorldTestClient player = await world.AddPlayerAsync("Feedalice");
        WorldSession session = world.Host.Registry.Sessions.Single(s => s.Player?.Name == "Feedalice");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ReplaceQueryTimeHandler(world, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        });

        Task<InjectVerdict> first = session.InjectAsync(WorldOpcode.CmsgQueryTime, [], TimeSpan.FromSeconds(5), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(InjectVerdict.Busy, await session.InjectAsync(WorldOpcode.CmsgQueryTime, [], TimeSpan.FromMilliseconds(100), CancellationToken.None));

        release.SetResult();
        Assert.Equal(InjectVerdict.Handled, await first);
        Assert.Equal(InjectVerdict.Handled, await session.InjectAsync(WorldOpcode.CmsgQueryTime, [], TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [WindowsFact]
    public async Task Inject_AWorldOpcode_IsQueued_AndHandledOnTheWorldThreadInTheNextUpdate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn());
        await using WorldTestClient player = await world.AddPlayerAsync("Feedalice");
        FeedClient client = await world.ConnectAsync();
        ulong guid = await world.Host.OnWorldAsync(() => world.Host.World.FindOnlinePlayer("Feedalice")!.Guid.Value);

        JsonElement result = await RequestAsync(client, $$"""{"op":"inject","id":2,"guid":"{{FeedWorld.Hex(guid)}}","opcode":{{(int)WorldOpcode.CmsgPlayedTime}}}""");
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.Equal("world", result.GetProperty("handler").GetString());
        Assert.Equal("queued", result.GetProperty("verdict").GetString());

        await world.StepAsync(2);

        // SMSG_PLAYED_TIME: u32 total, u32 level (vmangos MiscHandler.cpp HandlePlayedTime).
        byte[] reply = await player.ReadUntilAsync(WorldOpcode.SmsgPlayedTime);
        Assert.Equal(8, reply.Length);
    }

    [WindowsFact]
    public async Task Inject_Refusals_HaveTheirOwnCodes_AndAreLoggedAtWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn());
        await using WorldTestClient player = await world.AddPlayerAsync("Feedalice");
        FeedClient client = await world.ConnectAsync();
        using var counted = new ToolsCounter();

        OpcodeTable table = world.Host.Opcodes;
        WorldOpcode noHandler = Enum.GetValues<WorldOpcode>().First(o =>
            WorldOpcodeNames.GetName(o).StartsWith("CMSG_", StringComparison.Ordinal) && !table.TryGet(o, out _) && !PacketToolOpcodes.RefusedInject.Contains(o));
        string oversize = new('0', (PacketToolOpcodes.MaxInjectPayload + 1) * 2);

        AssertRefused(await RequestAsync(client, """{"op":"inject","id":1,"target":"Feedalice","opcode":32767}"""), 1, "inject", MapFeedProtocol.UnknownOpcode);
        AssertRefused(await RequestAsync(client, """{"op":"inject","id":2,"target":"Feedalice","opcode":"CMSG_NOT_A_THING"}"""), 2, "inject", MapFeedProtocol.UnknownOpcode);
        AssertRefused(await RequestAsync(client, """{"op":"inject","id":3,"target":"Feedalice","opcode":"SMSG_NOTIFICATION"}"""), 3, "inject", MapFeedProtocol.WrongDirection);
        foreach (WorldOpcode lifecycle in PacketToolOpcodes.RefusedInject)
        {
            AssertRefused(await RequestAsync(client, $$"""{"op":"inject","id":4,"target":"Feedalice","opcode":{{(int)lifecycle}}}"""), 4, "inject", MapFeedProtocol.LifecycleOpcode);
        }

        AssertRefused(await RequestAsync(client, $$"""{"op":"inject","id":5,"target":"Feedalice","opcode":"CMSG_QUERY_TIME","payload":"{{oversize}}"}"""), 5, "inject", MapFeedProtocol.Oversize);
        AssertRefused(await RequestAsync(client, """{"op":"inject","id":6,"target":"Nobody","opcode":"CMSG_QUERY_TIME"}"""), 6, "inject", MapFeedProtocol.Offline);
        AssertRefused(await RequestAsync(client, """{"op":"inject","id":7,"guid":"00000000DEADBEEF","opcode":"CMSG_QUERY_TIME"}"""), 7, "inject", MapFeedProtocol.Offline);
        AssertRefused(await RequestAsync(client, $$"""{"op":"inject","id":8,"target":"Feedalice","opcode":{{(int)noHandler}}}"""), 8, "inject", MapFeedProtocol.NoHandler);
        AssertRefused(await RequestAsync(client, """{"op":"inject","id":10,"target":"Feedalice","opcode":"CMSG_QUERY_TIME","payload":"0G"}"""), 10, "inject", MapFeedProtocol.BadArgs);

        // Refusals are answers, not strikes: the connection still serves.
        JsonElement fine = await RequestAsync(client, """{"op":"inject","id":11,"target":"Feedalice","opcode":"CMSG_QUERY_TIME"}""");
        Assert.True(fine.GetProperty("ok").GetBoolean(), fine.ToString());

        Assert.DoesNotContain(world.FeedLog, e => e.Level >= LogLevel.Error);
        Assert.Contains(world.FeedLog, e => e.Level == LogLevel.Warning && e.Message.StartsWith("Map feed inject refused: CMSG_QUERY_TIME (10237 bytes)", StringComparison.Ordinal));
        Assert.True(counted.Count("inject", "refused") >= 8);
    }

    [WindowsFact]
    public async Task Inject_IntoASessionNotInTheWorld_IsWrongState_WhereTheDispatcherWouldDropItSilently()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Every opcode a character in the world cannot send is a lifecycle opcode the tools refuse first, so the feed (which finds
        // only characters) never reaches this verdict today; the session's own check is tested here, at the character screen.
        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn());
        byte[] key = await world.Host.AddAccountAsync("FEEDSCREEN");
        await using WorldTestClient screen = await world.Host.ConnectAsync();
        await screen.AuthenticateAsync("FEEDSCREEN", key);
        await WorldTestHost.WaitForAsync(() => world.Host.Registry.Sessions.Any(s => s.State == SessionState.CharacterSelect), "the session is at the character screen");
        WorldSession session = world.Host.Registry.Sessions.Single(s => s.State == SessionState.CharacterSelect);

        OpcodeTable table = world.Host.Opcodes;
        WorldOpcode inWorldOnly = table.Opcodes.First(o =>
            table.TryGet(o, out OpcodeHandler h) && h.World is null && h.AllowsState(SessionState.InWorld) && !h.AllowsState(SessionState.CharacterSelect)
            && WorldOpcodeNames.GetName(o).StartsWith("CMSG_", StringComparison.Ordinal));
        Assert.Equal(InjectVerdict.WrongState, await session.InjectAsync(inWorldOnly, [], TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(SessionState.CharacterSelect, session.State);
    }

    [WindowsFact]
    public async Task Inject_ToABot_GoesThroughItsWorldQueue_AndASessionTaskOpcodeIsNotInjectable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn(bots: true));
        await world.AddBotAsync("Feedbot");
        FeedClient client = await world.ConnectAsync();

        // CMSG_STANDSTATECHANGE: u32 state, 1 = sit (vmangos CharacterHandler.cpp HandleStandStateChangeOpcode; src/ArcaneCore.World/Handlers/PlayerHandlers.cs).
        JsonElement sit = await RequestAsync(client, $$"""{"op":"inject","id":1,"target":"Feedbot","opcode":"CMSG_STANDSTATECHANGE","payload":"{{Hex(1, 0, 0, 0)}}"}""");
        Assert.True(sit.GetProperty("ok").GetBoolean(), sit.ToString());
        Assert.True(sit.GetProperty("target").GetProperty("bot").GetBoolean());
        Assert.Equal("queued", sit.GetProperty("verdict").GetString());
        await world.StepAsync(2);
        Assert.Equal(StandState.Sit, await world.Host.OnWorldAsync(() => world.Host.World.FindOnlinePlayer("Feedbot")!.StandState));

        AssertRefused(await RequestAsync(client, """{"op":"inject","id":2,"target":"Feedbot","opcode":"CMSG_QUERY_TIME"}"""), 2, "inject", MapFeedProtocol.NotInjectable);
    }

    [WindowsFact]
    public async Task Send_QueuesTheServerOpcodeToTheClient_AndRefusesClientAndLifecycleOpcodes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn());
        await using WorldTestClient player = await world.AddPlayerAsync("Feedalice");
        FeedClient client = await world.ConnectAsync();
        using var counted = new ToolsCounter();

        // SMSG_NOTIFICATION: cstring text (vmangos WorldSession::SendNotification).
        JsonElement sent = await RequestAsync(client, $$"""{"op":"send","id":1,"target":"Feedalice","opcode":"SMSG_NOTIFICATION","payload":"{{Hex([.. Encoding.UTF8.GetBytes("hello"), 0])}}"}""");
        Assert.True(sent.GetProperty("ok").GetBoolean(), sent.ToString());
        Assert.Equal("send", sent.GetProperty("kind").GetString());
        Assert.Equal(6, sent.GetProperty("length").GetInt32());
        byte[] notification = await player.ReadUntilAsync(WorldOpcode.SmsgNotification);
        Assert.Equal("hello\0", Encoding.UTF8.GetString(notification));

        AssertRefused(await RequestAsync(client, """{"op":"send","id":2,"target":"Feedalice","opcode":"CMSG_QUERY_TIME"}"""), 2, "send", MapFeedProtocol.WrongDirection);
        foreach (WorldOpcode lifecycle in PacketToolOpcodes.RefusedSend)
        {
            AssertRefused(await RequestAsync(client, $$"""{"op":"send","id":3,"target":"Feedalice","opcode":{{(int)lifecycle}}}"""), 3, "send", MapFeedProtocol.LifecycleOpcode);
        }

        string oversize = new('0', (PacketToolOpcodes.MaxSendPayload + 1) * 2);
        AssertRefused(await RequestAsync(client, $$"""{"op":"send","id":4,"target":"Feedalice","opcode":"SMSG_NOTIFICATION","payload":"{{oversize}}"}"""), 4, "send", MapFeedProtocol.Oversize);
        AssertRefused(await RequestAsync(client, """{"op":"send","id":5,"target":"Nobody","opcode":"SMSG_NOTIFICATION"}"""), 5, "send", MapFeedProtocol.Offline);
        AssertRefused(await RequestAsync(client, """{"op":"send","id":6,"target":"Feedalice","opcode":65000}"""), 6, "send", MapFeedProtocol.UnknownOpcode);

        Assert.Contains(world.FeedLog, e => e.Level == LogLevel.Information && e.Message.StartsWith("Map feed send: SMSG_NOTIFICATION (6 bytes) to 'Feedalice'", StringComparison.Ordinal));
        Assert.DoesNotContain(world.FeedLog, e => e.Message.Contains("68656C6C6F", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("hello\0", StringComparison.Ordinal));
        Assert.Equal(1, counted.Count("send", "accepted"));
        Assert.True(counted.Count("send", "refused") >= 4);
    }

    [WindowsFact]
    public async Task Tap_SeesBothDirections_MarksInjectedPackets_AndEndsOnItsCount()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn());
        await using WorldTestClient player = await world.AddPlayerAsync("Feedalice");
        FeedClient client = await world.ConnectAsync();

        JsonElement started = await RequestAsync(client, """{"op":"tap","id":40,"target":"Feedalice","opcodes":["CMSG_QUERY_TIME","SMSG_QUERY_TIME_RESPONSE"],"maxPackets":4,"bytes":2}""");
        Assert.True(started.GetProperty("ok").GetBoolean(), started.ToString());
        Assert.Equal(40, started.GetProperty("tap").GetInt64());

        // The client's own request and its reply, then an injected one and its reply.
        await player.SendAsync(WorldOpcode.CmsgQueryTime, []);
        await player.ReadUntilAsync(WorldOpcode.SmsgQueryTimeResponse);
        var early = new List<JsonElement>();
        JsonElement injected = await RequestAsync(client, """{"op":"inject","id":41,"target":"Feedalice","opcode":"CMSG_QUERY_TIME"}""", early);

        var packets = early.Where(l => l.Op() == "packet").ToList();
        JsonElement end = early.FirstOrDefault(l => l.Op() == "tapEnd");
        while (end.ValueKind == JsonValueKind.Undefined)
        {
            JsonElement line = await client.ReadAsync();
            if (line.Op() == "packet")
            {
                packets.Add(line);
            }
            else if (line.Op() == "tapEnd")
            {
                end = line;
            }
        }

        Assert.True(injected.GetProperty("ok").GetBoolean(), injected.ToString());
        Assert.Equal(4, packets.Count);
        Assert.Equal(["in", "out", "in", "out"], packets.Select(p => p.GetProperty("dir").GetString()));
        Assert.Equal(["CMSG_QUERY_TIME", "SMSG_QUERY_TIME_RESPONSE", "CMSG_QUERY_TIME", "SMSG_QUERY_TIME_RESPONSE"], packets.Select(p => p.GetProperty("name").GetString()));
        Assert.False(packets[0].TryGetProperty("inj", out _));
        Assert.True(packets[2].GetProperty("inj").GetBoolean());
        JsonElement response = packets[1];
        Assert.Equal(4, response.GetProperty("len").GetInt32());
        Assert.Equal(4, response.GetProperty("hex").GetString()!.Length); // 2 bytes of 4, cut
        Assert.True(response.GetProperty("cut").GetBoolean());
        Assert.True(response.GetProperty("t").GetInt64() > 0);
        Assert.Equal(40, end.GetProperty("tap").GetInt64());
        Assert.Equal("count", end.GetProperty("reason").GetString());
        Assert.Equal(4, end.GetProperty("packets").GetInt64());
        Assert.Equal(0, end.GetProperty("dropped").GetInt64());
    }

    [WindowsFact]
    public async Task Untap_AnswersThenEndsTheTap_ASecondTapIsRefusedMeanwhile_AndAnUntapWithoutATapIsRefused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn());
        await using WorldTestClient player = await world.AddPlayerAsync("Feedalice");
        FeedClient client = await world.ConnectAsync();

        AssertRefused(await RequestAsync(client, """{"op":"untap","id":1}"""), 1, "untap", MapFeedProtocol.NoTap);
        AssertRefused(await RequestAsync(client, """{"op":"tap","id":2,"target":"Nobody"}"""), 2, "tap", MapFeedProtocol.Offline);
        var tapped = new List<JsonElement>();
        Assert.True((await RequestAsync(client, """{"op":"tap","id":3,"target":"Feedalice","dir":"in"}""", tapped)).GetProperty("ok").GetBoolean());
        AssertRefused(await RequestAsync(client, """{"op":"tap","id":4,"target":"Feedalice"}""", tapped), 4, "tap", MapFeedProtocol.TapBusy);

        JsonElement untapped = await RequestAsync(client, """{"op":"untap","id":5}""", tapped);
        Assert.True(untapped.GetProperty("ok").GetBoolean(), untapped.ToString());
        Assert.Equal(3, untapped.GetProperty("tap").GetInt64());
        JsonElement end = await ReadUntilOpAsync(client, "tapEnd");
        Assert.Equal("stopped", end.GetProperty("reason").GetString());
        await WorldTestHost.WaitForAsync(() => !HasTaps(world, "Feedalice"), "the tap is detached");

        // A new tap may start once the old one ended.
        Assert.True((await RequestAsync(client, """{"op":"tap","id":6,"target":"Feedalice","seconds":1}""", tapped)).GetProperty("ok").GetBoolean());
        JsonElement timed = await ReadUntilOpAsync(client, "tapEnd");
        Assert.Equal("time", timed.GetProperty("reason").GetString());
    }

    [WindowsFact]
    public async Task Tap_EndsWhenTheSessionCloses_AndWhenTheFeedConnectionEnds_DetachingItself()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn());
        WorldTestClient leaving = await world.AddPlayerAsync("Feedbob");
        await using WorldTestClient staying = await world.AddPlayerAsync("Feedalice");
        FeedClient client = await world.ConnectAsync();
        Assert.True((await RequestAsync(client, """{"op":"tap","id":1,"target":"Feedbob"}""")).GetProperty("ok").GetBoolean());

        await leaving.DisposeAsync();
        JsonElement end = await ReadUntilOpAsync(client, "tapEnd");
        Assert.Equal("closed", end.GetProperty("reason").GetString());

        FeedClient second = await world.ConnectAsync();
        Assert.True((await RequestAsync(second, """{"op":"tap","id":2,"target":"Feedalice"}""")).GetProperty("ok").GetBoolean());
        Assert.True(HasTaps(world, "Feedalice"));
        await second.DisposeAsync();
        await WorldTestHost.WaitForAsync(() => !HasTaps(world, "Feedalice"), "the closed connection's tap is detached");
    }

    [WindowsFact]
    public async Task ATap_ThatNobodyReads_DropsAndCounts_AndNeverHoldsTheWorldThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn());
        await using WorldTestClient player = await world.AddPlayerAsync("Feedalice");
        WorldSession session = world.Host.Registry.Sessions.Single(s => s.Player?.Name == "Feedalice");
        ToolTarget target = new(session, "Feedalice", 1, false);
        // Only the notifications, so a packet the session sends on its own (a time sync) does not shift the count.
        var tap = new FeedPacketTap(1, target, TapDirections.Both, [(ushort)WorldOpcode.SmsgNotification], 100_000, 64, capacity: 4);
        Assert.True(session.AttachTap(tap));

        // 2000 packets sent on the world thread into a queue of 4 that nobody drains: the world thread finishes, the tap counts.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await world.Host.OnWorldAsync(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                session.Send(WorldOpcode.SmsgNotification, "x\0"u8);
            }
        });
        elapsed.Stop();

        Assert.Equal(1996, tap.Dropped);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"the world thread took {elapsed.Elapsed}");
        int queued = 0;
        while (tap.Reader.TryRead(out TapPacket packet))
        {
            Assert.False(packet.Inbound);
            Assert.Equal((ushort)WorldOpcode.SmsgNotification, packet.Opcode);
            queued++;
        }

        Assert.Equal(4, queued);
        session.DetachTap(tap);
    }

    [WindowsFact]
    public async Task ATapOverTheFeed_ThatTheClientDoesNotRead_ReportsItsDrops_WhileTheWorldKeepsTicking()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A bot: no socket, so nothing but the tap is slow.
        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn(bots: true));
        await world.AddBotAsync("Feedbot");
        FeedClient client = await world.ConnectAsync();
        Assert.True((await RequestAsync(client, """{"op":"tap","id":1,"target":"Feedbot","dir":"out","opcodes":["SMSG_NOTIFICATION"],"maxPackets":5000,"bytes":4096}""")).GetProperty("ok").GetBoolean());
        WorldSession session = world.Host.Registry.Sessions.Single(s => s.Player?.Name == "Feedbot");

        // The feed client reads nothing while 5000 packets go out: the pipe and the queues fill, the tap drops, the world ticks on.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        byte[] text = [.. Enumerable.Repeat((byte)'a', 2000), 0];
        await world.Host.OnWorldAsync(() =>
        {
            for (int i = 0; i < 5000; i++)
            {
                session.Send(WorldOpcode.SmsgNotification, text);
            }
        });
        await world.StepAsync(5);
        elapsed.Stop();
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"the world thread took {elapsed.Elapsed}");

        long packets = 0;
        JsonElement end;
        while (true)
        {
            JsonElement line = await client.ReadAsync();
            if (line.Op() == "packet")
            {
                packets++;
                continue;
            }

            if (line.Op() == "tapEnd")
            {
                end = line;
                break;
            }
        }

        Assert.Equal("count", end.GetProperty("reason").GetString());
        Assert.Equal(packets, end.GetProperty("packets").GetInt64());
        Assert.True(end.GetProperty("dropped").GetInt64() > 0, end.ToString());
        Assert.Equal(5000, packets + end.GetProperty("dropped").GetInt64());
    }

    [WindowsFact]
    public async Task WithTheToolsOn_ALineMayCarryTheLargestClientPacket_AndAParseRefusalWithAnIdIsAResult()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(ToolsOn());
        await using WorldTestClient player = await world.AddPlayerAsync("Feedalice");
        FeedClient client = await world.ConnectAsync();

        string payload = new('0', PacketToolOpcodes.MaxInjectPayload * 2);
        JsonElement big = await RequestAsync(client, $$"""{"op":"send","id":1,"target":"Feedalice","opcode":"SMSG_NOTIFICATION","payload":"{{payload}}"}""");
        Assert.True(big.GetProperty("ok").GetBoolean(), big.ToString());
        Assert.Equal(PacketToolOpcodes.MaxInjectPayload, big.GetProperty("length").GetInt32());

        AssertRefused(await RequestAsync(client, """{"op":"tap","id":2,"target":"Feedalice","dir":"sideways"}"""), 2, "tap", MapFeedProtocol.BadArgs);
        AssertRefused(await RequestAsync(client, """{"op":"inject","id":3,"target":"a","guid":"01","opcode":1}"""), 3, "inject", MapFeedProtocol.BadArgs);
    }

    private static bool HasTaps(FeedWorld world, string name)
    {
        WorldSession session = world.Host.Registry.Sessions.Single(s => s.Player?.Name == name);
        var field = typeof(WorldSession).GetField("_taps", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WorldSession._taps is gone: the detach check cannot run");
        return ((Array)field.GetValue(session)!).Length != 0;
    }

    private static async Task<JsonElement> ReadUntilOpAsync(FeedClient client, string op)
    {
        while (true)
        {
            JsonElement line = await client.ReadAsync();
            if (line.Op() == op)
            {
                return line;
            }
        }
    }

    /// <summary>Listens to <c>arcanecore.tools.packets</c> for the length of a test (other tests' counts are told apart by nothing, so assert lower bounds or run alone).</summary>
    private sealed class ToolsCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _counts = new();

        public ToolsCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "ArcaneCore.Tools" && instrument.Name == "arcanecore.tools.packets")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                string? op = null;
                string? result = null;
                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    if (tag.Key == "op")
                    {
                        op = tag.Value as string;
                    }
                    else if (tag.Key == "result")
                    {
                        result = tag.Value as string;
                    }
                }

                _counts.AddOrUpdate(op + "/" + result, value, (_, old) => old + value);
            });
            _listener.Start();
        }

        public long Count(string op, string result) => _counts.TryGetValue(op + "/" + result, out long value) ? value : 0;

        public void Dispose() => _listener.Dispose();
    }
}
