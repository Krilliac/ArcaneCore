using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Game.Maps;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// Phase 3a end to end over owned loopback sockets: the mock client logs on at the real realm, then talks only to an
/// in-process <see cref="GatewayServer"/>, which forwards to a real world (SQLite stores, real handlers) in worker mode.
/// </summary>
public sealed class GatewayEndToEndTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task MockClient_LogsInAndEntersTheWorld_ThroughTheGateway()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Stack stack = await Stack.StartAsync(cts.Token);
        await stack.Server.AddAccountAsync("GATEHERO", "PASSWORD", cts.Token);
        LogonResult logon = await LogonClient.AuthenticateAsync(stack.Server.RealmEndpoint, "GATEHERO", "PASSWORD", cts.Token);

        await using WorldClient client = await WorldClient.ConnectAsync(stack.Gateway.ListenEndPoint, cts.Token);
        Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync("GATEHERO", logon.SessionKey, cts.Token));

        // Exactly one worker-side session, carrying the client's own address (not the gateway's) for per-IP guards.
        // The session is tracked in Sessions when the link host starts it, which follows the accept reply the client's AuthResponse
        // is built from, so wait for it (bounded by the test deadline) before asserting.
        await WaitUntilAsync(() => Task.FromResult(stack.Endpoint.Acceptor!.Sessions.Count == 1), cts.Token);
        RemoteWorldSession remote = Assert.Single(stack.Endpoint.Acceptor!.Sessions.Values);
        Assert.Equal(client.LocalEndPoint!.ToString(), remote.Session.RemoteEndpoint);
        Assert.Equal(SessionState.CharacterSelect, remote.Session.State);
        Assert.Same(remote.Session, stack.Server.Services.GetRequiredService<SessionRegistry>().Find(remote.Session.AccountId));

        await client.SendAsync((ushort)WorldOpcode.CmsgCharCreate, CharacterCreate("Gatehero"), cts.Token);
        WorldFrame created = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharCreate, cts.Token);
        Assert.Equal(new byte[] { 0x2E }, created.Payload);

        await client.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, cts.Token);
        WorldFrame list = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, cts.Token);
        Assert.Equal(1, list.Payload[0]);
        ulong guid = BinaryPrimitives.ReadUInt64LittleEndian(list.Payload.AsSpan(1, 8));

        byte[] login = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(login, guid);
        await client.SendAsync((ushort)WorldOpcode.CmsgPlayerLogin, login, cts.Token);
        await client.ReadUntilAsync((ushort)WorldOpcode.SmsgLoginVerifyWorld, cts.Token);
        await WaitUntilAsync(async () => await stack.World.InvokeAsync(() => stack.World.OnlinePlayerCount) == 1, cts.Token);
        Assert.Equal(SessionState.InWorld, remote.Session.State);
        Assert.Equal(guid, remote.Session.Player!.Guid.Value);

        // PING is answered by the gateway and its latency still reaches the world session.
        byte[] ping = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(ping, 0x4747);
        BinaryPrimitives.WriteUInt32LittleEndian(ping.AsSpan(4), 123);
        await client.SendAsync((ushort)WorldOpcode.CmsgPing, ping, cts.Token);
        WorldFrame pong = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgPong, cts.Token, maxPackets: 1024);
        Assert.Equal(0x4747u, BinaryPrimitives.ReadUInt32LittleEndian(pong.Payload));
        await WaitUntilAsync(() => Task.FromResult(remote.Session.LatencyMs == 123), cts.Token);

        // The client goes away: the gateway closes the session on the worker and the player leaves the world.
        await client.DisposeAsync();
        await WaitUntilAsync(async () => await stack.World.InvokeAsync(() => stack.World.OnlinePlayerCount) == 0, cts.Token);
        await WaitUntilAsync(() => Task.FromResult(stack.Endpoint.Acceptor!.Sessions.IsEmpty && stack.Endpoint.Host!.SessionCount == 0), cts.Token);
        Assert.Equal(SessionState.Closed, remote.Session.State);
        Assert.Equal(0, stack.Endpoint.Host!.DroppedClientPackets);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task Login_RecordsTheLinkLatency_SessionOpens_AndForwardedPackets_PerNode()
    {
        using var cts = new CancellationTokenSource(Deadline);
        string node = "e2e-metrics-" + Guid.NewGuid().ToString("N")[..8];
        using var probe = new GatewayMeterProbe();
        await using Stack stack = await Stack.StartAsync(cts.Token, node);
        await stack.Server.AddAccountAsync("GATEMETER", "PASSWORD", cts.Token);
        LogonResult logon = await LogonClient.AuthenticateAsync(stack.Server.RealmEndpoint, "GATEMETER", "PASSWORD", cts.Token);

        Assert.Equal(1, probe.Gauge("arcanecore.gateway.link_state", node));
        await using WorldClient client = await WorldClient.ConnectAsync(stack.Gateway.ListenEndPoint, cts.Token);
        Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync("GATEMETER", logon.SessionKey, cts.Token));
        await WaitUntilAsync(() => Task.FromResult(probe.Gauge("arcanecore.gateway.sessions", node) == 1), cts.Token);
        Assert.Equal(1, probe.Gauge("arcanecore.gateway.connections", node));

        // The client sees Ok before the login continuation records its metrics, so wait for them.
        await WaitUntilAsync(() => Task.FromResult(
            probe.Sum("arcanecore.gateway.session_opens", node, result: "accepted") >= 1
            && probe.Count("arcanecore.gateway.link_latency", node) >= 1), cts.Token);

        // One accepted open, timed once, with the worker endpoint as the per-link tag.
        Assert.Equal(1, probe.Sum("arcanecore.gateway.session_opens", node, result: "accepted"));
        Assert.Equal(1, probe.Count("arcanecore.gateway.link_latency", node));
        Assert.True(probe.Sum("arcanecore.gateway.link_latency", node) >= 0);

        // A client packet crosses the link to the worker, and the worker's answer crosses back.
        await client.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, cts.Token);
        await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, cts.Token);
        Assert.True(probe.Sum("arcanecore.gateway.forwarded_packets", node, direction: "to_worker") >= 1);
        Assert.True(probe.Sum("arcanecore.gateway.forwarded_packets", node, direction: "to_client") >= 1);
        Assert.True(probe.Sum("arcanecore.gateway.forwarded_bytes", node, direction: "to_client") > 0);
    }

    [Fact]
    public async Task WrongSessionKey_IsRefusedByTheGatewayInPlainText_AndOpensNothingOnTheWorker()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Stack stack = await Stack.StartAsync(cts.Token);
        await stack.Server.AddAccountAsync("GATEBAD", "PASSWORD", cts.Token);
        LogonResult logon = await LogonClient.AuthenticateAsync(stack.Server.RealmEndpoint, "GATEBAD", "PASSWORD", cts.Token);
        byte[] key = (byte[])logon.SessionKey.Clone();
        key[0] ^= 1;

        await using WorldClient client = await WorldClient.ConnectAsync(stack.Gateway.ListenEndPoint, cts.Token);
        Assert.Equal((byte)AuthResponseCode.Failed, await client.AuthenticateAsync("GATEBAD", key, cts.Token));
        Assert.Equal(0, stack.Endpoint.Host!.SessionCount);
        Assert.Empty(stack.Endpoint.Acceptor!.Sessions);
    }

    [Fact]
    public async Task SecondLoginOfTheAccount_SupersedesTheFirstOnGatewayAndWorker()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Stack stack = await Stack.StartAsync(cts.Token);
        await stack.Server.AddAccountAsync("GATETWICE", "PASSWORD", cts.Token);
        LogonResult logon = await LogonClient.AuthenticateAsync(stack.Server.RealmEndpoint, "GATETWICE", "PASSWORD", cts.Token);

        await using WorldClient first = await WorldClient.ConnectAsync(stack.Gateway.ListenEndPoint, cts.Token);
        Assert.Equal((byte)AuthResponseCode.Ok, await first.AuthenticateAsync("GATETWICE", logon.SessionKey, cts.Token));
        await using WorldClient second = await WorldClient.ConnectAsync(stack.Gateway.ListenEndPoint, cts.Token);
        Assert.Equal((byte)AuthResponseCode.Ok, await second.AuthenticateAsync("GATETWICE", logon.SessionKey, cts.Token));

        await AssertDisconnectedAsync(first, cts.Token);
        await WaitUntilAsync(() => Task.FromResult(stack.Endpoint.Acceptor!.Sessions.Count == 1), cts.Token);
        RemoteWorldSession remaining = Assert.Single(stack.Endpoint.Acceptor!.Sessions.Values);
        Assert.Equal(second.LocalEndPoint!.ToString(), remaining.Session.RemoteEndpoint);
        Assert.Equal(1, stack.Gateway.Claims.Count);
    }

    [Fact]
    public async Task NoWorkerLink_AnswersAuthUnavailableInPlainText()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(cts.Token);
        await server.AddAccountAsync("GATEDOWN", "PASSWORD", cts.Token);
        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, "GATEDOWN", "PASSWORD", cts.Token);
        int closedPort = FreePort();
        GatewayServer gateway = CreateGateway(server, closedPort);
        await gateway.StartAsync(cts.Token);
        try
        {
            await using WorldClient client = await WorldClient.ConnectAsync(gateway.ListenEndPoint, cts.Token);
            Assert.Equal((byte)AuthResponseCode.Unavailable, await client.AuthenticateAsync("GATEDOWN", logon.SessionKey, cts.Token));
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SingleWorkerKeys_StillWork_AsOneLinkNamedWorker()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Stack stack = await Stack.StartAsync(cts.Token);

        GatewayWorkerLinkInfo only = Assert.Single(stack.Gateway.LinkStates());
        Assert.Equal(GatewayOptions.LegacyWorkerNodeId, only.NodeId);
        Assert.Equal(GatewayWorkerLinkStatus.Ready, only.Status);
        Assert.Equal(stack.Endpoint.Host!.BoundEndPoint.Port, only.EndPoint.Port);
        Assert.Same(stack.Gateway.Link, Assert.Single(stack.Gateway.Workers).Link);
        Assert.Equal(only.EndPoint, stack.Gateway.WorkerEndPoint);
    }

    [Fact]
    public async Task TwoWorkers_AreBothLinked_AndRestartingOneKeepsTheSessionsOnTheOther()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(cts.Token);
        GatewayWorkerEndpoint workerA = await StartWorkerAsync(server, 0, cts.Token);
        GatewayWorkerEndpoint workerB = await StartWorkerAsync(server, 0, cts.Token);
        int portA = workerA.Host!.BoundEndPoint.Port;
        int portB = workerB.Host!.BoundEndPoint.Port;
        GatewayServer gateway = CreateGateway(server, new GatewayOptions
        {
            Workers =
            [
                new GatewayWorkerEntry { NodeId = "w-a", Address = IPAddress.Loopback.ToString(), Port = portA, Maps = [0] },
                new GatewayWorkerEntry { NodeId = "w-b", Address = IPAddress.Loopback.ToString(), Port = portB, Maps = [1] },
            ],
        });
        await gateway.StartAsync(cts.Token);
        GatewayWorkerEndpoint? restartedA = null;
        try
        {
            await gateway.WaitForAllLinksAsync(cts.Token);
            Assert.Equal(new[] { "w-a", "w-b" }, gateway.LinkStates().Select(state => state.NodeId));
            Assert.All(gateway.LinkStates(), state => Assert.Equal(GatewayWorkerLinkStatus.Ready, state.Status));
            Assert.Equal(new[] { 0 }, gateway.LinkStates()[0].Maps);
            Assert.Equal(new[] { 1 }, gateway.LinkStates()[1].Maps);

            // Two logins spread over the two links: the second goes to the link with fewer sessions.
            await server.AddAccountAsync("MULTIONE", "PASSWORD", cts.Token);
            await server.AddAccountAsync("MULTITWO", "PASSWORD", cts.Token);
            await using WorldClient first = await LoginAsync(server, gateway, "MULTIONE", cts.Token);
            await using WorldClient second = await LoginAsync(server, gateway, "MULTITWO", cts.Token);
            await WaitUntilAsync(() => Task.FromResult(workerA.Acceptor!.Sessions.Count == 1 && workerB.Acceptor!.Sessions.Count == 1), cts.Token);

            // Which client sits on worker A?
            string onA = Assert.Single(workerA.Acceptor!.Sessions.Values).Session.RemoteEndpoint;
            WorldClient clientOnA = onA == first.LocalEndPoint!.ToString() ? first : second;
            WorldClient clientOnB = ReferenceEquals(clientOnA, first) ? second : first;

            // Worker A goes away: its client is disconnected, the other worker's client keeps working.
            await workerA.StopAsync(CancellationToken.None);
            await AssertDisconnectedAsync(clientOnA, cts.Token);
            await clientOnB.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, cts.Token);
            WorldFrame list = await clientOnB.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, cts.Token);
            Assert.Equal(0, list.Payload[0]);
            Assert.Single(workerB.Acceptor!.Sessions);
            Assert.Equal(GatewayWorkerLinkStatus.Ready, gateway.LinkStates()[1].Status);
            Assert.Equal(0, gateway.LinkStates()[1].Reconnects);

            // Worker A comes back on the same port: its link re-registers on its own, and logins are admitted to it again.
            restartedA = await StartWorkerAsync(server, portA, cts.Token);
            await WaitUntilAsync(() => Task.FromResult(gateway.LinkStates()[0] is { Status: GatewayWorkerLinkStatus.Ready, Reconnects: >= 1 }), cts.Token);
            await server.AddAccountAsync("MULTITHREE", "PASSWORD", cts.Token);
            await using WorldClient third = await LoginAsync(server, gateway, "MULTITHREE", cts.Token);
            await WaitUntilAsync(() => Task.FromResult(restartedA.Acceptor!.Sessions.Count == 1), cts.Token);
            Assert.Single(workerB.Acceptor!.Sessions);
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None);
            gateway.Dispose();
            if (restartedA is not null)
            {
                await restartedA.StopAsync(CancellationToken.None);
            }

            await workerB.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LoginWorker_TakesTheSessions_RoutesFollowItsAnnouncementAndLinkState_AndLoginFallsBackWhenItIsLost()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(cts.Token);
        GatewayWorkerEndpoint workerA = await StartWorkerAsync(server, 0, cts.Token);
        GatewayWorkerEndpoint workerB = await StartWorkerAsync(server, 0, cts.Token, ownedMaps: "1,30-40");
        GatewayServer gateway = CreateGateway(server, new GatewayOptions
        {
            Workers =
            [
                new GatewayWorkerEntry { NodeId = "w-a", Address = IPAddress.Loopback.ToString(), Port = workerA.Host!.BoundEndPoint.Port, Maps = [0] },
                new GatewayWorkerEntry { NodeId = "w-b", Address = IPAddress.Loopback.ToString(), Port = workerB.Host!.BoundEndPoint.Port, Maps = [1] },
            ],
            LoginWorker = "w-b",
        });
        await gateway.StartAsync(cts.Token);
        bool bStopped = false;
        try
        {
            await gateway.WaitForAllLinksAsync(cts.Token);

            // Registration fed the table: both ready, and w-b's rows come from its announcement (maps 30-40 are in no static list).
            Assert.All(gateway.Routes.Snapshot(), row => Assert.True(row.Ready));
            Assert.False(Assert.Single(gateway.Routes.Snapshot(), row => row.NodeId == "w-a").FromAnnouncement);
            Assert.True(Assert.Single(gateway.Routes.Snapshot(), row => row.NodeId == "w-b").FromAnnouncement);
            Assert.True(gateway.Routes.TryResolve(35, 0, out string owner));
            Assert.Equal("w-b", owner);
            Assert.True(gateway.Routes.TryResolve(0, 0, out owner));
            Assert.Equal("w-a", owner);
            Assert.True(gateway.Routes.TryGetLoginWorker(out string loginNode));
            Assert.Equal("w-b", loginNode);

            // Two sessions both open on the login worker although the other link has fewer sessions.
            await server.AddAccountAsync("LOGINONE", "PASSWORD", cts.Token);
            await server.AddAccountAsync("LOGINTWO", "PASSWORD", cts.Token);
            await server.AddAccountAsync("LOGINTHREE", "PASSWORD", cts.Token);
            await using WorldClient first = await LoginAsync(server, gateway, "LOGINONE", cts.Token);
            await using WorldClient second = await LoginAsync(server, gateway, "LOGINTWO", cts.Token);
            await WaitUntilAsync(() => Task.FromResult(workerB.Acceptor!.Sessions.Count == 2), cts.Token);
            Assert.Empty(workerA.Acceptor!.Sessions);
            Assert.Equal(2, gateway.Workers[1].Link!.SessionCount);
            Assert.Equal(0, gateway.Workers[0].Link!.SessionCount);

            // The login worker is lost: its row goes not-ready, so nothing resolves to it, and the next login lands on the other link.
            await workerB.StopAsync(CancellationToken.None);
            bStopped = true;
            await WaitUntilAsync(() => Task.FromResult(!Assert.Single(gateway.Routes.Snapshot(), row => row.NodeId == "w-b").Ready), cts.Token);
            Assert.True(Assert.Single(gateway.Routes.Snapshot(), row => row.NodeId == "w-a").Ready);
            Assert.False(gateway.Routes.TryResolve(35, 0, out _));
            Assert.False(gateway.Routes.TryGetLoginWorker(out _));

            await using WorldClient third = await LoginAsync(server, gateway, "LOGINTHREE", cts.Token);
            await WaitUntilAsync(() => Task.FromResult(workerA.Acceptor!.Sessions.Count == 1), cts.Token);
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None);
            gateway.Dispose();
            await workerA.StopAsync(CancellationToken.None);
            if (!bStopped)
            {
                await workerB.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task WorkerThatIsDown_DoesNotBlockLoginsOnTheOtherWorker()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(cts.Token);
        GatewayWorkerEndpoint live = await StartWorkerAsync(server, 0, cts.Token);
        GatewayServer gateway = CreateGateway(server, new GatewayOptions
        {
            Workers =
            [
                new GatewayWorkerEntry { NodeId = "w-down", Address = IPAddress.Loopback.ToString(), Port = FreePort() },
                new GatewayWorkerEntry { NodeId = "w-live", Address = IPAddress.Loopback.ToString(), Port = live.Host!.BoundEndPoint.Port },
            ],
        });
        await gateway.StartAsync(cts.Token);
        try
        {
            await gateway.WaitForLinkAsync(cts.Token);
            await server.AddAccountAsync("MULTIDOWN", "PASSWORD", cts.Token);
            await using WorldClient client = await LoginAsync(server, gateway, "MULTIDOWN", cts.Token);
            await WaitUntilAsync(() => Task.FromResult(live.Acceptor!.Sessions.Count == 1), cts.Token);
            await WaitUntilAsync(() => Task.FromResult(gateway.LinkStates()[0] is { Failures: >= 1, LastError: not null }), cts.Token);
            Assert.NotEqual(GatewayWorkerLinkStatus.Ready, gateway.LinkStates()[0].Status);
            Assert.Equal(GatewayWorkerLinkStatus.Ready, gateway.LinkStates()[1].Status);
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None);
            gateway.Dispose();
            await live.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void WorkerEntries_AreValidated_AndTheSingleWorkerKeysMapToOneEntry()
    {
        var legacy = new GatewayOptions { WorkerAddress = "10.0.0.5", WorkerPort = 9001 };
        GatewayWorkerEntry one = Assert.Single(legacy.EffectiveWorkers());
        Assert.Equal((GatewayOptions.LegacyWorkerNodeId, "10.0.0.5", 9001), (one.NodeId, one.Address, one.Port));
        Assert.Empty(legacy.Validate());

        var bad = new GatewayOptions
        {
            WorkerAddress = "not-an-ip", // ignored once Workers is set
            Workers =
            [
                new GatewayWorkerEntry { NodeId = "a", Address = "127.0.0.1", Port = 8095 },
                new GatewayWorkerEntry { NodeId = "a", Address = "nope", Port = 0, Maps = [-1] },
                new GatewayWorkerEntry { NodeId = " ", Address = "127.0.0.1", Port = 8096 },
            ],
        };
        IReadOnlyList<string> problems = bad.Validate();
        Assert.Contains(problems, p => p.StartsWith("Gateway:Workers:1:NodeId:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:Workers:1:Address:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:Workers:1:Port:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:Workers:1:Maps:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:Workers:2:NodeId:", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.StartsWith("Gateway:WorkerAddress:", StringComparison.Ordinal));
        Assert.Equal(5, problems.Count);
    }

    [Fact]
    public void WorkerMode_IsRegisteredOnlyWhenEnabled()
    {
        static bool Registered(Dictionary<string, string?> values)
        {
            var services = new ServiceCollection();
            services.AddGatewayWorker(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            return services.Any(d => d.ServiceType == typeof(GatewayWorkerEndpoint));
        }

        Assert.False(Registered([]));
        Assert.False(Registered(new() { ["Cluster:Worker:Enabled"] = "false" }));
        Assert.True(Registered(new() { ["Cluster:Worker:Enabled"] = "true" }));
    }

    private static byte[] CharacterCreate(string name)
    {
        byte[] text = Encoding.ASCII.GetBytes(name);
        byte[] payload = new byte[text.Length + 10];
        text.CopyTo(payload, 0);
        payload[text.Length + 1] = 1; // human
        payload[text.Length + 2] = 1; // warrior
        return payload;
    }

    private static GatewayServer CreateGateway(SyntheticArcaneServer server, int workerPort, string nodeId = "gateway") => new(
        server.Services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new GatewayOptions
        {
            BindAddress = IPAddress.Loopback.ToString(),
            Port = ArcaneCore.TestSupport.LoopbackPorts.Next(),
            WorkerAddress = IPAddress.Loopback.ToString(),
            WorkerPort = workerPort,
            LinkSecret = Stack.Secret,
            LinkRetryDelay = TimeSpan.FromMilliseconds(100),
            NodeId = nodeId,
        }),
        server.Services.GetRequiredService<ILoggerFactory>());

    private static GatewayServer CreateGateway(SyntheticArcaneServer server, GatewayOptions options)
    {
        options.BindAddress = IPAddress.Loopback.ToString();
        options.Port = ArcaneCore.TestSupport.LoopbackPorts.Next();
        options.LinkSecret = Stack.Secret;
        options.LinkRetryDelay = TimeSpan.FromMilliseconds(100);
        return new GatewayServer(
            server.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            server.Services.GetRequiredService<ILoggerFactory>());
    }

    private static async Task<GatewayWorkerEndpoint> StartWorkerAsync(SyntheticArcaneServer server, int port, CancellationToken token, string ownedMaps = "")
    {
        IServiceProvider services = server.Services;
        var endpoint = new GatewayWorkerEndpoint(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GatewayWorkerOptions { Enabled = true, Port = port, LinkSecret = Stack.Secret, OwnedMaps = ownedMaps }),
            services.GetRequiredService<IOptions<WorldSessionOptions>>(),
            services.GetRequiredService<OpcodeTable>(),
            server.World,
            services.GetRequiredService<SessionRegistry>(),
            services.GetRequiredService<ILoggerFactory>());
        await endpoint.StartAsync(token);
        return endpoint;
    }

    private static async Task<WorldClient> LoginAsync(SyntheticArcaneServer server, GatewayServer gateway, string account, CancellationToken token)
    {
        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, account, "PASSWORD", token);
        WorldClient client = await WorldClient.ConnectAsync(gateway.ListenEndPoint, token);
        Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync(account, logon.SessionKey, token));
        return client;
    }

    private static int FreePort()
    {
        using var probe = ArcaneCore.TestSupport.LoopbackPorts.CreateListener();
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition())
        {
            await Task.Delay(20, token);
        }
    }

    private static async Task AssertDisconnectedAsync(WorldClient client, CancellationToken token)
    {
        for (int index = 0; index < 256; index++)
        {
            try
            {
                await client.ReadAsync(token);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or SocketException or InvalidOperationException)
            {
                return;
            }
        }

        Assert.Fail("the superseded client was not disconnected");
    }

    /// <summary>A synthetic realm and world, the world's worker endpoint, and a gateway linked to it.</summary>
    private sealed class Stack : IAsyncDisposable
    {
        public const string Secret = "e2e-link-secret-0123456789";

        public required SyntheticArcaneServer Server { get; init; }

        public required GatewayWorkerEndpoint Endpoint { get; init; }

        public required GatewayServer Gateway { get; init; }

        public WorldRuntime World => Server.World;

        public static async Task<Stack> StartAsync(CancellationToken token, string nodeId = "gateway")
        {
            SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token);
            IServiceProvider services = server.Services;
            var endpoint = new GatewayWorkerEndpoint(
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new GatewayWorkerOptions { Enabled = true, Port = ArcaneCore.TestSupport.LoopbackPorts.Next(), LinkSecret = Secret }),
                services.GetRequiredService<IOptions<WorldSessionOptions>>(),
                services.GetRequiredService<OpcodeTable>(),
                server.World,
                services.GetRequiredService<SessionRegistry>(),
                services.GetRequiredService<ILoggerFactory>());
            await endpoint.StartAsync(token);
            GatewayServer gateway = CreateGateway(server, endpoint.Host!.BoundEndPoint.Port, nodeId);
            await gateway.StartAsync(token);
            await gateway.WaitForLinkAsync(token);
            return new Stack { Server = server, Endpoint = endpoint, Gateway = gateway };
        }

        public async ValueTask DisposeAsync()
        {
            await Gateway.StopAsync(CancellationToken.None);
            Gateway.Dispose();
            await Endpoint.StopAsync(CancellationToken.None);
            await Server.DisposeAsync();
        }
    }
}
