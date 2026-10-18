using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ArcaneCore.World.Ops.Control;
using ArcaneCore.World.Ops.MapFeed;
using ArcaneCore.World.Tests.Ops.Control;
using Microsoft.Extensions.Logging;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The map feed's pipe (docs/areas/map-feed.md, "Security" and "Protocol"): off by default; on, a named pipe only the daemon's account and
/// SYSTEM may open, one instance per allowed connection, never shared with a pipe some other process created first; the handshake, the
/// refusals and what ends a connection. Every test talks to the daemon's feed over a real loopback named pipe.
/// </summary>
public sealed class MapFeedPipeTests
{
    private static string UniquePipe() => "arcanecore-mapfeed-test-" + Guid.NewGuid().ToString("N");

    [SupportedOSPlatform("windows")]
    private static void AssertOnlyUserAndSystem(PipeSecurity security, SecurityIdentifier user)
    {
        Assert.True(security.AreAccessRulesProtected, "the descriptor must not inherit entries from its container");
        Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
        PipeAccessRule[] rules = [.. security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()];
        Assert.Equal(3, rules.Length);
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny && r.IdentityReference.Equals(ControlPipeSecurity.Network)
            && r.PipeAccessRights == PipeAccessRights.FullControl);
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(user)
            && r.PipeAccessRights == PipeAccessRights.FullControl);
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(ControlPipeSecurity.LocalSystem)
            && r.PipeAccessRights == (PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize));
        string sddl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        foreach (string other in new[] { "WD", "BA", "AU", "AN", "BU", "IU" })
        {
            Assert.DoesNotContain(";;;" + other + ")", sddl, StringComparison.Ordinal);
        }
    }

    private static void AssertNoErrorsLogged(FeedWorld world)
        => Assert.DoesNotContain(world.Logs.Entries, e => e.Level >= LogLevel.Error);

    /// <summary>Await a write the server may have cut short: a daemon that closed the connection leaves the client's write broken.</summary>
    private static async Task IgnoreBrokenPipeAsync(Task write)
    {
        try
        {
            await write;
        }
        catch (IOException)
        {
            // The server closed while the client was still writing: that is the point of the test.
        }
    }

    private static async Task<List<JsonElement>> SendAndReadAsync(FeedClient client, int replies, params string[] lines)
    {
        foreach (string line in lines)
        {
            await client.SendAsync(line);
        }

        var read = new List<JsonElement>();
        for (int i = 0; i < replies; i++)
        {
            read.Add(await client.ReadAsync());
        }

        return read;
    }

    [Fact]
    public async Task OffByDefault_NoPipeIsCreated_UnderTheTemplateNameOrTheProductionOne()
    {
        Assert.False(new WorldMapFeedOptions().Enabled);
        string name = UniquePipe();
        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup
        {
            Terrain = false, PipeName = name, Options = o => o.Enabled = new WorldMapFeedOptions().Enabled,
        });

        Assert.False(world.Feed.IsListening);
        Assert.Null(world.Feed.PipeName);
        Assert.Empty(world.Feed.Servers);
        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut))
        {
            Assert.Throws<TimeoutException>(() => client.Connect(200));
        }

        using (var production = new NamedPipeClientStream(".", "arcanecore-mapfeed-" + Environment.ProcessId, PipeDirection.InOut))
        {
            Assert.Throws<TimeoutException>(() => production.Connect(200));
        }

        Assert.DoesNotContain(world.FeedLog, e => e.Level >= LogLevel.Warning);
        Assert.False(world.Feed.IsAttached);
    }

    [Fact]
    public async Task AnInvalidSection_KeepsTheFeedOff_WithAWarningNamingTheKey()
    {
        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup
        {
            Terrain = false, Options = o =>
            {
                o.MaxHz = 0;
                o.MaxConnections = 99;
            },
        });

        Assert.False(world.Feed.IsListening);
        Assert.Null(world.Feed.PipeName);
        Assert.Empty(world.Feed.Servers);
        WorldControlPipeTests.LogEntry warning = Assert.Single(world.FeedLog, e => e.Level == LogLevel.Warning);
        Assert.Contains("MaxHz", warning.Message, StringComparison.Ordinal);
        Assert.Contains("MaxConnections", warning.Message, StringComparison.Ordinal);
        AssertNoErrorsLogged(world);
    }

    [WindowsFact]
    public async Task Enabled_EveryInstanceCarriesTheRestrictedDescriptor_AndRejectsRemoteClients()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string name = UniquePipe();
        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false, PipeName = name, Options = o => o.MaxConnections = 3 });

        Assert.True(world.Feed.IsListening);
        Assert.Equal(name, world.Feed.PipeName);
        Assert.Equal(3, world.Feed.Servers.Count);
        SecurityIdentifier user = ControlPipeSecurity.CurrentUser();
        foreach (NamedPipeServerStream server in world.Feed.Servers)
        {
            AssertOnlyUserAndSystem(server.GetAccessControl(), user);
            // Read back from the pipe file system, not from what was asked for.
            Assert.True(LocalPipeServer.RejectsRemoteClients(server.SafePipeHandle), "every feed pipe instance must carry PIPE_REJECT_REMOTE_CLIENTS");
        }

        WorldControlPipeTests.LogEntry on = Assert.Single(world.FeedLog, e => e.Message.StartsWith("map feed on:", StringComparison.Ordinal));
        Assert.Contains(name, on.Message, StringComparison.Ordinal);
        Assert.Contains(user.Value, on.Message, StringComparison.Ordinal);
        AssertNoErrorsLogged(world);
    }

    [WindowsFact]
    public async Task TheProductionName_IsPerProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false, DefaultPipeName = true });

        Assert.Equal("arcanecore-mapfeed-" + Environment.ProcessId, world.Feed.PipeName);
        await world.ConnectAsync();
    }

    [WindowsFact]
    public async Task ANameAnotherProcessCreatedFirst_IsRefused_NotJoined()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string name = UniquePipe();
        // A squatter that allows any number of instances: without FILE_FLAG_FIRST_PIPE_INSTANCE the feed would become its second instance.
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false, PipeName = name });

        Assert.False(world.Feed.IsListening);
        Assert.Null(world.Feed.PipeName);
        Assert.Empty(world.Feed.Servers);
        // Refusing a name is an expected outcome, never an error the QA reporter would publish.
        WorldControlPipeTests.LogEntry refusal = Assert.Single(world.FeedLog, e => e.Message.Contains("could not create", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, refusal.Level);
        AssertNoErrorsLogged(world);
    }

    [WindowsFact]
    public async Task Hello_IsAnswered_WithProtocolBuildProcessAndTheRunningMaps()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false, Content = true });
        FeedClient client = await world.ConnectAsync(hello: false);

        JsonElement empty = (await SendAndReadAsync(client, 1, """{"op":"hello","protocol":1}"""))[0];
        Assert.Equal("hello", empty.Op());
        Assert.Equal(1, empty.GetProperty("protocol").GetInt32());
        Assert.Equal(Environment.ProcessId, empty.GetProperty("pid").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(empty.GetProperty("sha").GetString()));
        Assert.DoesNotContain(empty.GetProperty("maps").EnumerateArray(), m => m.GetProperty("players").GetInt32() > 0); // nobody is in the world yet

        await world.AddPlayerAsync();
        JsonElement running = (await SendAndReadAsync(client, 1, """{"op":"hello","protocol":1}"""))[0];
        JsonElement map = Assert.Single(running.GetProperty("maps").EnumerateArray(), m => m.GetProperty("players").GetInt32() > 0);
        Assert.Equal(0, map.GetProperty("map").GetInt32());
        Assert.Equal(0, map.GetProperty("instance").GetInt32());
        Assert.Equal(1, map.GetProperty("players").GetInt32());
        Assert.True(map.GetProperty("creatures").GetInt32() >= 1, "the Young Wolf of the content is spawned on the player's map");
        AssertNoErrorsLogged(world);
    }

    [WindowsFact]
    public async Task AProtocolMismatch_IsRefused_TheConnectionClosed_AndTheNextClientServed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false });
        FeedClient client = await world.ConnectAsync(hello: false);

        await client.SendAsync("""{"op":"hello","protocol":2}""");
        JsonElement error = await client.ReadAsync();
        Assert.Equal("error", error.Op());
        Assert.Equal(MapFeedProtocol.ProtocolMismatch, error.Code());
        Assert.Contains("protocol 1", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Null(await client.TryReadAsync());

        WorldControlPipeTests.LogEntry mismatch = Assert.Single(world.FeedLog, e => e.Message.Contains("speaks protocol 2", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, mismatch.Level);
        AssertNoErrorsLogged(world);

        FeedClient next = await world.ConnectAsync();
        Assert.NotNull(next);
    }

    [WindowsFact]
    public async Task AnythingBeforeHello_IsRefused_ButTheConnectionStays()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false });
        FeedClient client = await world.ConnectAsync(hello: false);

        List<JsonElement> replies = await SendAndReadAsync(
            client, 2, FeedClient.Subscribe(), FeedClient.QueryLine(8, "height", 0, new System.Numerics.Vector3(1, 2, 3)));

        Assert.Equal("error", replies[0].Op());
        Assert.Equal(MapFeedProtocol.HelloRequired, replies[0].Code());
        // A query that arrives early is answered as its result, with its id.
        Assert.Equal("result", replies[1].Op());
        Assert.Equal(8, replies[1].GetProperty("id").GetInt64());
        Assert.False(replies[1].GetProperty("ok").GetBoolean());
        Assert.Equal(MapFeedProtocol.HelloRequired, replies[1].Code());

        Assert.Equal(0, world.Feed.SubscriptionCount); // the early subscribe ran nothing
        Assert.False(world.Feed.IsAttached);
        Assert.Equal("hello", (await client.HelloAsync()).Op());
        AssertNoErrorsLogged(world);
    }

    [WindowsFact]
    public async Task MalformedLines_AreRefusedWithTheirCodes_NeverWithAnError()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false });
        FeedClient client = await world.ConnectAsync();

        List<JsonElement> replies = await SendAndReadAsync(
            client, 4,
            "this is not json",
            """{"op":"fly-to-the-moon"}""",
            """{"op":"subscribe","map":"zero"}""",
            """{"op":"query","id":5,"kind":"los","map":0,"a":[0,0,0]}""");

        Assert.Equal(["error", "error", "error", "result"], replies.Select(r => r.Op()).ToArray());
        Assert.Equal([MapFeedProtocol.BadJson, MapFeedProtocol.UnknownOp, MapFeedProtocol.BadArgs, MapFeedProtocol.BadArgs], replies.Select(r => r.Code()).ToArray());
        Assert.Equal(5, replies[3].GetProperty("id").GetInt64());
        Assert.False(replies[3].GetProperty("ok").GetBoolean());
        Assert.All(replies, r => Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("message").GetString())));

        // The connection is still good.
        Assert.Equal("hello", (await client.HelloAsync()).Op());
        Assert.Equal(0, world.Feed.SubscriptionCount);
        Assert.Contains(world.FeedLog, e => e.Level == LogLevel.Warning && e.Message.Contains("refused a line", StringComparison.Ordinal));
        AssertNoErrorsLogged(world);
    }

    [WindowsFact]
    public async Task FiveRefusedLinesInARow_CloseTheConnection_AndALineThatIsServedStartsTheCountAgain()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false });

        FeedClient steady = await world.ConnectAsync();
        for (int round = 0; round < 3; round++)
        {
            List<JsonElement> four = await SendAndReadAsync(steady, 4, "x", "x", "x", "x");
            Assert.All(four, r => Assert.Equal(MapFeedProtocol.BadJson, r.Code()));
            Assert.Equal("hello", (await steady.HelloAsync()).Op()); // a served line: the count restarts
        }

        FeedClient closing = await world.ConnectAsync();
        List<JsonElement> five = await SendAndReadAsync(closing, 5, "x", "x", "x", "x", "x");
        Assert.All(five, r => Assert.Equal(MapFeedProtocol.BadJson, r.Code()));
        JsonElement last = await closing.ReadAsync();
        Assert.Equal(MapFeedProtocol.TooManyErrors, last.Code());
        Assert.Null(await closing.TryReadAsync());

        Assert.Contains(world.FeedLog, e => e.Level == LogLevel.Warning && e.Message.Contains("refused lines in a row", StringComparison.Ordinal));
        AssertNoErrorsLogged(world);
    }

    [WindowsFact]
    public async Task ALineOverTheLimit_RunsNothing_ClosesTheConnection_AndTheNextClientIsServed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false });
        FeedClient first = await world.ConnectAsync();

        // A well-formed subscribe padded past the limit: if any of it ran, the feed would have a subscription.
        string padded = FeedClient.Subscribe().TrimEnd('}') + ",\"pad\":\"" + new string('x', MapFeedProtocol.MaxLineBytes + 100) + "\"}";
        Task send = first.SendAsync(padded);
        JsonElement error = await first.ReadAsync();
        await IgnoreBrokenPipeAsync(send);
        Assert.Equal(MapFeedProtocol.LineTooLong, error.Code());
        Assert.Null(await first.TryReadAsync());

        Assert.Equal(0, world.Feed.SubscriptionCount);
        Assert.False(world.Feed.IsAttached);
        WorldControlPipeTests.LogEntry cut = Assert.Single(world.FeedLog, e => e.Message.Contains("longer than", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, cut.Level);

        FeedClient second = await world.ConnectAsync();
        Assert.NotNull(second);
        AssertNoErrorsLogged(world);
    }

    [WindowsFact]
    public async Task AnEndlessLineWithoutANewline_IsCutOffByTheServer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false });
        FeedClient client = await world.ConnectAsync();

        // 64 KB of one line, no newline: the server reads one buffer of it, answers and closes; the rest of the write finds a closed pipe.
        byte[] flood = Encoding.UTF8.GetBytes(new string('y', 64 * 1024));
        Task send = client.SendRawAsync(flood);
        JsonElement error = await client.ReadAsync();
        await IgnoreBrokenPipeAsync(send);

        Assert.Equal(MapFeedProtocol.LineTooLong, error.Code());
        Assert.Null(await client.TryReadAsync());
        Assert.Equal(0, world.Feed.SubscriptionCount);
        AssertNoErrorsLogged(world);
    }

    [WindowsFact]
    public async Task AnIdleClient_IsDisconnected_ButASubscribedOneIsNot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false, IdleTimeout = TimeSpan.FromMilliseconds(400) });

        FeedClient idle = await world.ConnectAsync(hello: false);
        JsonElement timeout = await idle.ReadAsync();
        Assert.Equal(MapFeedProtocol.IdleTimeout, timeout.Code());
        Assert.Null(await idle.TryReadAsync());

        await world.AddPlayerAsync();
        FeedClient watcher = await world.ConnectAsync();
        await watcher.SendAsync(FeedClient.Subscribe());
        await watcher.ReadOpAsync("frame");
        await Task.Delay(1500); // more than three idle limits
        Assert.Equal("hello", (await watcher.HelloAsync()).Op()); // still connected: a subscription is not idle

        await watcher.SendAsync("""{"op":"unsubscribe"}""");
        Assert.Equal(MapFeedProtocol.IdleTimeout, (await watcher.ReadAsync()).Code()); // unsubscribed: idle again
        AssertNoErrorsLogged(world);
    }

    [WindowsFact]
    public async Task MaxConnections_AClientBeyondTheLimit_FindsThePipeBusy_UntilASlotFrees()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false, Options = o => o.MaxConnections = 2 });
        Assert.Equal(2, world.Feed.Servers.Count);
        FeedClient first = await world.ConnectAsync();
        FeedClient second = await world.ConnectAsync();
        Assert.Equal(2, world.Feed.ActiveConnections);

        await Assert.ThrowsAsync<TimeoutException>(() => FeedClient.ConnectAsync(world.Feed.PipeName!, 300));
        Assert.Equal(2, world.Feed.ActiveConnections); // the refused client took no slot

        await first.DisposeAsync();
        await WorldTestHost.WaitForAsync(() => world.Feed.ActiveConnections == 1, "the first connection ends");

        FeedClient third = await FeedClient.ConnectAsync(world.Feed.PipeName!, 5000);
        try
        {
            Assert.Equal("hello", (await third.HelloAsync()).Op());
            Assert.Equal("hello", (await second.HelloAsync()).Op()); // the survivor was never disturbed
        }
        finally
        {
            await third.DisposeAsync();
        }

        Assert.Equal(3, world.Feed.ConnectionsServed);
        AssertNoErrorsLogged(world);
    }

    [WindowsFact]
    public async Task Stop_EndsEveryConnection_AndFreesTheName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false });
        string name = world.Feed.PipeName!;
        FeedClient client = await world.ConnectAsync();
        await world.AddPlayerAsync();
        await client.SendAsync(FeedClient.Subscribe());
        await client.ReadOpAsync("frame");

        await world.StopFeedAsync();

        Assert.Null(await client.TryReadAsync());
        Assert.False(world.Feed.IsListening);
        Assert.Equal(0, world.Feed.SubscriptionCount);
        Assert.False(world.Feed.IsAttached);
        Assert.Equal(0, world.Feed.ActiveConnections);
        using (var late = new NamedPipeClientStream(".", name, PipeDirection.InOut))
        {
            Assert.Throws<TimeoutException>(() => late.Connect(200));
        }

        // The name is free for the next owner (a restarted feed, or the control pipe's sibling).
        using NamedPipeServerStream reused = LocalPipeServer.Create(name, ControlPipeSecurity.Create(ControlPipeSecurity.CurrentUser()));
        Assert.NotNull(reused);
        AssertNoErrorsLogged(world);
    }
}
