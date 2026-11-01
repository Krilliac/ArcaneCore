using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.World;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// Cross-process handoff control channel, commit C1: the gateway's handoff hold, release and seamless retarget can be keyed by worker node id, so the handoff of one
/// worker leaves every other worker's sessions alone (the unkeyed forms keep acting on the first configured worker). Two real worlds on one set of databases behind
/// one real gateway (the LoginRedirectTests layout: node "w-east" owns map 0 and is the login worker, node "w-kalimdor" owns map 1), with real clients: one in the
/// world on the Kalimdor worker, one at character select on the Eastern Kingdoms worker.
/// </summary>
public sealed class GatewayHandoffNodeKeyedTests
{
    private const string Secret = "node-keyed-handoff-secret-0123456789";
    private const string EastNode = "w-east";
    private const string KalimdorNode = "w-kalimdor";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task HoldAndRelease_OfOneWorker_LeaveTheOtherWorkersSessionsAlone()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token);
        await using WorldClient kalimdorClient = await cluster.EnterKalimdorWorldAsync("KALHOLD", "Kalhold", cts.Token);
        await using WorldClient eastClient = await cluster.ConnectAtCharacterSelectAsync("EASTHOLD", cts.Token);
        GatewayServer gateway = cluster.Gateway;

        // Holding the Kalimdor worker holds exactly its one session, once.
        Assert.Equal(1, gateway.HoldSessionsForHandoff(KalimdorNode));
        Assert.Equal(0, gateway.HoldSessionsForHandoff(KalimdorNode));

        // The Eastern Kingdoms session keeps flowing: the character list is answered while the other worker is held.
        await eastClient.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, cts.Token);
        await eastClient.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, cts.Token);

        // Input sent to the held world waits at the gateway; releasing that worker replays it in order, and the reply arrives.
        await kalimdorClient.SendAsync((ushort)WorldOpcode.CmsgQueryTime, ReadOnlyMemory<byte>.Empty, cts.Token);
        gateway.ReleaseHandoffHold(KalimdorNode);
        await kalimdorClient.ReadUntilAsync((ushort)WorldOpcode.SmsgQueryTimeResponse, cts.Token);

        // Both held at once, then only the Kalimdor hold released: the Eastern hold is still in place (a second hold of it finds nothing to hold) and the Kalimdor one is gone.
        Assert.Equal(1, gateway.HoldSessionsForHandoff(EastNode));
        Assert.Equal(1, gateway.HoldSessionsForHandoff(KalimdorNode));
        gateway.ReleaseHandoffHold(KalimdorNode);
        Assert.Equal(0, gateway.HoldSessionsForHandoff(EastNode));
        Assert.Equal(1, gateway.HoldSessionsForHandoff(KalimdorNode));

        // Releasing the Eastern worker replays what its client sent while held.
        await eastClient.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, cts.Token);
        gateway.ReleaseHandoffHold(EastNode);
        await eastClient.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, cts.Token);
        gateway.ReleaseHandoffHold(KalimdorNode);
        Assert.Equal(1, gateway.HoldSessionsForHandoff(EastNode));
        gateway.ReleaseHandoffHold(EastNode);
    }

    [Fact]
    public async Task TheUnkeyedHold_StillActsOnTheFirstConfiguredWorkerOnly()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token);
        await using WorldClient kalimdorClient = await cluster.EnterKalimdorWorldAsync("KALUNKEY", "Kalunkey", cts.Token);
        await using WorldClient eastClient = await cluster.ConnectAtCharacterSelectAsync("EASTUNKEY", cts.Token);
        GatewayServer gateway = cluster.Gateway;

        Assert.Equal(1, gateway.HoldSessionsForHandoff());
        Assert.Equal(0, gateway.HoldSessionsForHandoff(EastNode));
        Assert.Equal(1, gateway.HoldSessionsForHandoff(KalimdorNode));
        // The unkeyed hold acts on the first worker only, but the unkeyed release frees every hold (both filters null), so the Kalimdor hold is gone too.
        gateway.ReleaseHandoffHold();
        Assert.Equal(1, gateway.HoldSessionsForHandoff(EastNode));
        Assert.Equal(1, gateway.HoldSessionsForHandoff(KalimdorNode));
        gateway.ReleaseHandoffHold(KalimdorNode);
        gateway.ReleaseHandoffHold(EastNode);
    }

    [Fact]
    public async Task TheRouter_BuiltWithASourceNode_HoldsAndReleasesThatWorkerOnly()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token);
        await using WorldClient kalimdorClient = await cluster.EnterKalimdorWorldAsync("KALROUTER", "Kalrouter", cts.Token);
        await using WorldClient eastClient = await cluster.ConnectAtCharacterSelectAsync("EASTROUTER", cts.Token);
        GatewayServer gateway = cluster.Gateway;
        var target = new IPEndPoint(IPAddress.Loopback, 1);

        var router = new GatewayWorldHandoffRouter(gateway, KalimdorNode, target);
        await router.HoldAsync(cts.Token);
        Assert.Equal(0, gateway.HoldSessionsForHandoff(KalimdorNode)); // the router already holds it
        Assert.Equal(1, gateway.HoldSessionsForHandoff(EastNode)); // and left the other worker free to hold
        gateway.ReleaseHandoffHold(EastNode);

        await router.ReleaseAsync(cts.Token);
        Assert.Equal(1, gateway.HoldSessionsForHandoff(KalimdorNode)); // released: it can be held again
        gateway.ReleaseHandoffHold(KalimdorNode);
    }

    [Fact]
    public async Task AHeldConnection_ThatIsNoLongerForwardingOnTheSourceLink_IsStillReleased()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token);
        await using WorldClient kalimdorClient = await cluster.EnterKalimdorWorldAsync("KALREPOINT", "Kalrepoint", cts.Token);
        GatewayServer gateway = cluster.Gateway;
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        var connections = (System.Collections.Concurrent.ConcurrentDictionary<GatewayClientConnection, Task>)typeof(GatewayServer).GetField("_connections", Private)!.GetValue(gateway)!;
        GatewayClientConnection connection = Assert.Single(connections.Keys, c => c.IsForwardingOn(gateway.Workers[1].Link!));
        System.Reflection.FieldInfo linkField = typeof(GatewayClientConnection).GetField("_link", Private)!;
        System.Reflection.FieldInfo holding = typeof(GatewayClientConnection).GetField("_handoffHolding", Private)!;

        Assert.Equal(1, gateway.HoldSessionsForHandoff(KalimdorNode));
        Assert.True((bool)holding.GetValue(connection)!);

        // A per-player transfer re-points the held connection to the other worker's link while the source is frozen (the hold stays).
        linkField.SetValue(connection, gateway.Workers[0].Link);
        Assert.False(connection.IsForwardingOn(gateway.Workers[1].Link!));

        // Another node's release leaves it held; the source node's release (keyed on where the hold began) frees it.
        gateway.ReleaseHandoffHold(EastNode);
        Assert.True((bool)holding.GetValue(connection)!);
        gateway.ReleaseHandoffHold(KalimdorNode);
        Assert.False((bool)holding.GetValue(connection)!);

        // Put the link back so the client disposes normally.
        linkField.SetValue(connection, gateway.Workers[1].Link);
    }

    [Fact]
    public void TheNodeKeyedForms_RefuseAnUnknownNode_AndTheRouterRefusesBadArguments()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        using var server = new GatewayServer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GatewayOptions { LinkSecret = Secret }),
            NullLoggerFactory.Instance);
        var target = new IPEndPoint(IPAddress.Loopback, 1);

        Assert.Throws<ArgumentNullException>("gateway", () => new GatewayWorldHandoffRouter(null!, EastNode, target));
        Assert.Throws<ArgumentNullException>("target", () => new GatewayWorldHandoffRouter(server, EastNode, null!));
        Assert.Throws<ArgumentNullException>("sourceNode", () => new GatewayWorldHandoffRouter(server, null!, target));
        Assert.Throws<ArgumentException>("sourceNode", () => new GatewayWorldHandoffRouter(server, " ", target));

        // No worker is configured on a server that never started, so every node id is unknown.
        Assert.Throws<ArgumentException>("nodeId", () => server.HoldSessionsForHandoff(EastNode));
        Assert.Throws<ArgumentException>("nodeId", () => server.ReleaseHandoffHold(EastNode));
        Assert.Throws<ArgumentNullException>("worker", () => server.RetargetWorkerSeamlessAsync(EastNode, null!, CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task TheSeamlessRetarget_OfOneWorker_DoesNotTouchTheOtherWorkersLinkOrSessions()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token);
        await using WorldClient eastClient = await cluster.ConnectAtCharacterSelectAsync("EASTMOVE", cts.Token);
        GatewayServer gateway = cluster.Gateway;
        IPEndPoint eastBefore = gateway.Workers[0].EndPoint;
        IPEndPoint eastConnected = Assert.IsType<IPEndPoint>(gateway.Workers[0].ConnectedEndPoint);

        // A retarget of the Kalimdor worker to an address nobody listens on: its own link is the only one that moves (the move never completes, so the call is cancelled).
        IPEndPoint nobody = FreeEndPoint();
        using var shortToken = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        shortToken.CancelAfter(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.RetargetWorkerSeamlessAsync(KalimdorNode, nobody, shortToken.Token));

        Assert.Equal(nobody, gateway.Workers[1].EndPoint);
        Assert.Equal(eastBefore, gateway.Workers[0].EndPoint);
        Assert.Equal(eastBefore, gateway.WorkerEndPoint);
        Assert.Equal(eastConnected, gateway.Workers[0].ConnectedEndPoint);
        Assert.NotNull(gateway.Workers[0].Link);
        Assert.Equal(1, gateway.Workers[0].Link!.SessionCount);

        // The Eastern Kingdoms session still works through the untouched link.
        await eastClient.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, cts.Token);
        await eastClient.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, cts.Token);

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.RetargetWorkerSeamlessAsync("w-unknown", nobody, cts.Token));
        Assert.Throws<ArgumentException>(() => gateway.HoldSessionsForHandoff("w-unknown"));
        Assert.Throws<ArgumentException>(() => gateway.ReleaseHandoffHold("w-unknown"));
    }

    private static IPEndPoint FreeEndPoint()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static byte[] LoginPayload(int character)
    {
        byte[] login = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(login, (ulong)character);
        return login;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition())
        {
            await Task.Delay(20, token);
        }
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

    /// <summary>Two real worlds on one set of databases behind one real gateway; node "w-east" owns map 0 and is the login worker, node "w-kalimdor" owns map 1.</summary>
    private sealed class Cluster : IAsyncDisposable
    {
        public required SyntheticArcaneServer Eastern { get; init; }

        public required SyntheticArcaneServer Kalimdor { get; init; }

        public required GatewayWorkerEndpoint EasternWorker { get; init; }

        public required GatewayWorkerEndpoint KalimdorWorker { get; init; }

        public required GatewayServer Gateway { get; init; }

        public static async Task<Cluster> StartAsync(CancellationToken token)
        {
            SyntheticArcaneServer eastern = await SyntheticArcaneServer.StartAsync(
                new SyntheticServerOptions { Configuration = WorkerScopeConfiguration(EastNode, "0") }, configureServices: null, token);
            SyntheticArcaneServer? kalimdor = null;
            GatewayWorkerEndpoint? easternWorker = null;
            GatewayWorkerEndpoint? kalimdorWorker = null;
            GatewayServer? gateway = null;
            try
            {
                kalimdor = await SyntheticArcaneServer.StartAsync(
                    new SyntheticServerOptions { SharedDatabaseDirectory = eastern.DataDirectory, Configuration = WorkerScopeConfiguration(KalimdorNode, "1") },
                    configureServices: null,
                    token);
                eastern.World.Ownership = Owns("0");
                kalimdor.World.Ownership = Owns("1");
                easternWorker = await StartWorkerAsync(eastern, "0", token);
                kalimdorWorker = await StartWorkerAsync(kalimdor, "1", token);
                gateway = new GatewayServer(
                    eastern.Services.GetRequiredService<IServiceScopeFactory>(),
                    Options.Create(new GatewayOptions
                    {
                        BindAddress = IPAddress.Loopback.ToString(),
                        Port = 0,
                        LinkSecret = Secret,
                        LinkRetryDelay = TimeSpan.FromMilliseconds(100),
                        Workers =
                        [
                            new GatewayWorkerEntry { NodeId = EastNode, Address = IPAddress.Loopback.ToString(), Port = easternWorker.Host!.BoundEndPoint.Port, Maps = [0] },
                            new GatewayWorkerEntry { NodeId = KalimdorNode, Address = IPAddress.Loopback.ToString(), Port = kalimdorWorker.Host!.BoundEndPoint.Port, Maps = [1] },
                        ],
                        LoginWorker = EastNode,
                    }),
                    eastern.Services.GetRequiredService<ILoggerFactory>());
                await gateway.StartAsync(token);
                await gateway.WaitForAllLinksAsync(token);
                return new Cluster
                {
                    Eastern = eastern, Kalimdor = kalimdor, EasternWorker = easternWorker, KalimdorWorker = kalimdorWorker, Gateway = gateway,
                };
            }
            catch
            {
                if (gateway is not null)
                {
                    await gateway.StopAsync(CancellationToken.None);
                    gateway.Dispose();
                }

                if (kalimdorWorker is not null)
                {
                    await kalimdorWorker.StopAsync(CancellationToken.None);
                }

                if (easternWorker is not null)
                {
                    await easternWorker.StopAsync(CancellationToken.None);
                }

                if (kalimdor is not null)
                {
                    await kalimdor.DisposeAsync();
                }

                await eastern.DisposeAsync();
                throw;
            }
        }

        /// <summary>A client of a fresh account whose character is saved in Kalimdor and is now in the world on the Kalimdor worker (the login redirect moved it there).</summary>
        public async Task<WorldClient> EnterKalimdorWorldAsync(string account, string character, CancellationToken token)
        {
            await Eastern.AddAccountAsync(account, "PASSWORD", token);
            WorldClient client = await ConnectAsync(account, token);
            try
            {
                int id = await CreateCharacterOnMapAsync(client, character, mapId: 1, token);
                await client.SendAsync((ushort)WorldOpcode.CmsgPlayerLogin, LoginPayload(id), token);
                await client.ReadUntilAsync((ushort)WorldOpcode.SmsgLoginVerifyWorld, token);
                await WaitUntilAsync(async () => await Kalimdor.World.InvokeAsync(() => Kalimdor.World.OnlinePlayerCount) == 1, token);
                await WaitUntilAsync(() => Task.FromResult(Gateway.Workers[1].Link!.SessionCount == 1 && Gateway.Workers[0].Link!.SessionCount == 0), token);
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        /// <summary>A client of a fresh account that has seen its character list: its session sits on the login (Eastern Kingdoms) worker.</summary>
        public async Task<WorldClient> ConnectAtCharacterSelectAsync(string account, CancellationToken token)
        {
            await Eastern.AddAccountAsync(account, "PASSWORD", token);
            WorldClient client = await ConnectAsync(account, token);
            try
            {
                await client.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, token);
                await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, token);
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Gateway.StopAsync(CancellationToken.None);
            Gateway.Dispose();
            await KalimdorWorker.StopAsync(CancellationToken.None);
            await EasternWorker.StopAsync(CancellationToken.None);
            await Kalimdor.DisposeAsync();
            await Eastern.DisposeAsync();
        }

        private async Task<WorldClient> ConnectAsync(string account, CancellationToken token)
        {
            LogonResult logon = await LogonClient.AuthenticateAsync(Eastern.RealmEndpoint, account, "PASSWORD", token);
            WorldClient client = await WorldClient.ConnectAsync(Gateway.ListenEndPoint, token);
            Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync(account, logon.SessionKey, token));
            return client;
        }

        private async Task<int> CreateCharacterOnMapAsync(WorldClient client, string name, uint mapId, CancellationToken token)
        {
            await client.SendAsync((ushort)WorldOpcode.CmsgCharCreate, CharacterCreate(name), token);
            WorldFrame created = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharCreate, token);
            Assert.Equal(new byte[] { 0x2E }, created.Payload);
            await client.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, token);
            WorldFrame list = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, token);
            Assert.Equal(1, list.Payload[0]);
            int id = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(list.Payload.AsSpan(1, 8)));

            await using AsyncServiceScope scope = Eastern.Services.CreateAsyncScope();
            ICharacterStore store = scope.ServiceProvider.GetRequiredService<ICharacterStore>();
            await store.SaveStateAsync(new CharacterState(id, mapId, 1, 100f, 100f, 50f, 0f, 1, 0), token);
            CharacterRecord saved = Assert.IsType<CharacterRecord>(await store.GetByIdAsync(id, token));
            Assert.Equal(mapId, saved.MapId);
            return id;
        }

        private static Dictionary<string, string?> WorkerScopeConfiguration(string nodeId, string ownedMaps) => new()
        {
            ["Cluster:Fencing:Enabled"] = "true",
            ["Cluster:Fencing:Scope"] = "Worker",
            ["Cluster:Fencing:OwnerNode"] = nodeId,
            ["Cluster:Fencing:AllowSqliteWorkerScope"] = "true",
            ["Cluster:Worker:Enabled"] = "true",
            ["Cluster:Worker:NodeId"] = nodeId,
            ["Cluster:Worker:OwnedMaps"] = ownedMaps,
            ["Cluster:Worker:LinkSecret"] = Secret,
            ["Cluster:Node:Role"] = "MapWorker",
        };

        private static MapOwnership Owns(string maps)
        {
            Assert.True(MapOwnership.TryParse(maps, allowInstanceMaps: false, out MapOwnership ownership, out string? error), error);
            return ownership;
        }

        private static async Task<GatewayWorkerEndpoint> StartWorkerAsync(SyntheticArcaneServer server, string ownedMaps, CancellationToken token)
        {
            IServiceProvider services = server.Services;
            var endpoint = new GatewayWorkerEndpoint(
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new GatewayWorkerOptions { Enabled = true, Port = 0, LinkSecret = Secret, OwnedMaps = ownedMaps, AllowInstanceMaps = false }),
                services.GetRequiredService<IOptions<WorldSessionOptions>>(),
                services.GetRequiredService<OpcodeTable>(),
                server.World,
                services.GetRequiredService<SessionRegistry>(),
                services.GetRequiredService<ILoggerFactory>());
            await endpoint.StartAsync(token);
            return endpoint;
        }
    }
}
