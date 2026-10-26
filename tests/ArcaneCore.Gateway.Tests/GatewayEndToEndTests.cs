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

    private static GatewayServer CreateGateway(SyntheticArcaneServer server, int workerPort) => new(
        server.Services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new GatewayOptions
        {
            BindAddress = IPAddress.Loopback.ToString(),
            Port = 0,
            WorkerAddress = IPAddress.Loopback.ToString(),
            WorkerPort = workerPort,
            LinkSecret = Stack.Secret,
            LinkRetryDelay = TimeSpan.FromMilliseconds(100),
        }),
        server.Services.GetRequiredService<ILoggerFactory>());

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
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

        public static async Task<Stack> StartAsync(CancellationToken token)
        {
            SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token);
            IServiceProvider services = server.Services;
            var endpoint = new GatewayWorkerEndpoint(
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new GatewayWorkerOptions { Enabled = true, Port = 0, LinkSecret = Secret }),
                services.GetRequiredService<IOptions<WorldSessionOptions>>(),
                services.GetRequiredService<OpcodeTable>(),
                server.World,
                services.GetRequiredService<SessionRegistry>(),
                services.GetRequiredService<ILoggerFactory>());
            await endpoint.StartAsync(token);
            GatewayServer gateway = CreateGateway(server, endpoint.Host!.BoundEndPoint.Port);
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
