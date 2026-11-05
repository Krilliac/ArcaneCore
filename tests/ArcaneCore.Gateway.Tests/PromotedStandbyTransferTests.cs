using System.Buffers.Binary;
using System.Net;
using System.Text;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Game;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Gateway.Handoff;
using ArcaneCore.Gateway.Transfers;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.World;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// Y10 end to end: a real player transfer through a standby the gateway promoted. Three real worlds on one set of databases behind one real gateway with handoffs and transfers on: slot "w-east" (map 0) is
/// served by node "w-east" and has the standby node "w-east-standby", slot "w-kalimdor" (map 1) has no standby. A whole-World handoff moves the east slot to the standby, so the slot's active process has a
/// node id that differs from the slot's, as it always does in production. Nothing here builds a request or a journal row by hand: a teleport reaches the gateway's <see cref="GatewayTransferService"/>, which runs
/// <see cref="PlayerTransferCoordinator"/> over the control channels and journals the slot names, and the promoted standby answers them only because its Promote named the slot.
/// </summary>
public sealed class PromotedStandbyTransferTests
{
    private const string Secret = "promoted-standby-transfer-secret-0123456789";
    private const string EastSlot = "w-east";
    private const string EastStandby = "w-east-standby";
    private const string KalimdorNode = "w-kalimdor";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(180);
    private static readonly TeleportDestination KalimdorLanding = new(1, -1196.22f, 29.0f, 176.949f, 3.1f);
    private static readonly TeleportDestination EasternLanding = new(0, -8949.95f, -132.493f, 83.5312f, 0f);

    /// <summary>The source half: Suspend and RetireSource run on the promoted standby, and its far-teleport router, attached at the promotion, submits the transfer.</summary>
    [Fact]
    public async Task ATeleportFromThePromotedStandby_CompletesThroughTheGateway_WithTheStandbyAsTheSource()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartPromotedAsync(cts.Token);
        await using WorldClient client = await cluster.EnterEastAsync("PROMOSOURCE", "Promosource", cts.Token);
        ObjectGuid guid = ObjectGuid.Player((uint)cluster.CharacterId);
        await Cluster.WaitUntilAsync(() => Task.FromResult(Cluster.TeleportsOf(cluster.Promoted).ClusterFarRouter is not null), cts.Token);
        GatewayClusterFarTeleportRouter router = Cluster.RouterOf(cluster.Promoted);

        Assert.True(await Cluster.TeleportAsync(cluster.Promoted, guid, KalimdorLanding));

        WorldFrame newWorld = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgNewWorld, cts.Token);
        Assert.Equal(KalimdorLanding.MapId, BinaryPrimitives.ReadUInt32LittleEndian(newWorld.Payload));
        await client.SendAsync((ushort)WorldOpcode.MsgMoveWorldportAck, ReadOnlyMemory<byte>.Empty, cts.Token);
        await Cluster.WaitUntilAsync(
            async () => await cluster.Kalimdor.World.InvokeAsync(() => cluster.Kalimdor.World.FindOnlinePlayer(guid) is { Map.MapId: 1 }, cts.Token), cts.Token);

        await cluster.AssertCompletedAsync(router.LastTransferId, EastSlot, KalimdorNode, cts.Token);
        await Cluster.WaitUntilAsync(() => Task.FromResult(router.InFlightCount == 0), cts.Token); // the outcome notice reached the source
        Assert.False(await cluster.Promoted.World.InvokeAsync(() => cluster.Promoted.World.IsOnline(guid), cts.Token));
        Assert.Equal(0, cluster.Gateway.Transfers.StagedCount);
        await Cluster.WaitUntilAsync(
            () => Task.FromResult(cluster.Gateway.Workers[1].Link!.SessionCount == 1 && cluster.Gateway.Workers[0].Link!.SessionCount == 0), cts.Token);
    }

    /// <summary>The target half: Reserve, Install and Activate run on the promoted standby, and the session the gateway stages there fires the SessionOpened hook its transfer side attached at the promotion.</summary>
    [Fact]
    public async Task ATeleportToThePromotedStandby_CompletesThroughTheGateway_WithTheStandbyAsTheTarget()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartPromotedAsync(cts.Token);
        await using WorldClient client = await cluster.EnterKalimdorAsync("PROMOTARGET", "Promotarget", cts.Token);
        ObjectGuid guid = ObjectGuid.Player((uint)cluster.CharacterId);
        await Cluster.WaitUntilAsync(() => Task.FromResult(Cluster.TeleportsOf(cluster.Kalimdor).ClusterFarRouter is not null), cts.Token);
        GatewayClusterFarTeleportRouter router = Cluster.RouterOf(cluster.Kalimdor);

        Assert.True(await Cluster.TeleportAsync(cluster.Kalimdor, guid, EasternLanding));

        WorldFrame newWorld = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgNewWorld, cts.Token);
        Assert.Equal(EasternLanding.MapId, BinaryPrimitives.ReadUInt32LittleEndian(newWorld.Payload));
        await client.SendAsync((ushort)WorldOpcode.MsgMoveWorldportAck, ReadOnlyMemory<byte>.Empty, cts.Token);
        await Cluster.WaitUntilAsync(
            async () => await cluster.Promoted.World.InvokeAsync(() => cluster.Promoted.World.FindOnlinePlayer(guid) is { Map.MapId: 0 }, cts.Token), cts.Token);

        await cluster.AssertCompletedAsync(router.LastTransferId, KalimdorNode, EastSlot, cts.Token);
        await Cluster.WaitUntilAsync(() => Task.FromResult(router.InFlightCount == 0), cts.Token);
        Assert.False(await cluster.Kalimdor.World.InvokeAsync(() => cluster.Kalimdor.World.IsOnline(guid), cts.Token));
        Assert.Equal(0, cluster.Gateway.Transfers.StagedCount);
        await Cluster.WaitUntilAsync(
            () => Task.FromResult(cluster.Gateway.Workers[0].Link!.SessionCount == 1 && cluster.Gateway.Workers[1].Link!.SessionCount == 0), cts.Token);
    }

    /// <summary>Three real worlds behind one real gateway, after the handoff of the east slot. The gateway and its journals use the Kalimdor world's container: it stays an ordinary worker throughout, while the east slot's first process is retired.</summary>
    private sealed class Cluster : IAsyncDisposable
    {
        private GatewayWorkerEndpoint _retiredEndpoint = null!;
        private GatewayWorkerEndpoint _promotedEndpoint = null!;
        private GatewayWorkerEndpoint _kalimdorEndpoint = null!;
        private GatewayHandoffService _handoffs = null!;
        private GatewayTransferService _transfers = null!;

        private Cluster()
        {
        }

        /// <summary>The east slot's first process, retired by the handoff.</summary>
        public SyntheticArcaneServer Retired { get; private set; } = null!;

        /// <summary>The east slot's standby, now its active process.</summary>
        public SyntheticArcaneServer Promoted { get; private set; } = null!;

        public SyntheticArcaneServer Kalimdor { get; private set; } = null!;

        public GatewayServer Gateway { get; private set; } = null!;

        public EfTransferDecisionStore Store { get; private set; } = null!;

        public int CharacterId { get; private set; }

        /// <summary>Starts the cluster and runs the handoff of the east slot, so the slot's active process is the promoted standby.</summary>
        public static async Task<Cluster> StartPromotedAsync(CancellationToken token)
        {
            var cluster = new Cluster();
            try
            {
                await cluster.StartAsync(token);
                await cluster.HandOffTheEastSlotAsync(token);
                return cluster;
            }
            catch
            {
                await cluster.DisposeAsync();
                throw;
            }
        }

        private async Task StartAsync(CancellationToken token)
        {
            Retired = await SyntheticArcaneServer.StartAsync(
                new SyntheticServerOptions { Configuration = Worker(EastSlot, "0", shard: EastSlot, standby: false, handoff: true) }, configureServices: null, token);
            Promoted = await SyntheticArcaneServer.StartAsync(
                new SyntheticServerOptions { SharedDatabaseDirectory = Retired.DataDirectory, Configuration = Worker(EastStandby, "0", shard: EastSlot, standby: true, handoff: true) }, configureServices: null, token);
            Kalimdor = await SyntheticArcaneServer.StartAsync(
                new SyntheticServerOptions { SharedDatabaseDirectory = Retired.DataDirectory, Configuration = Worker(KalimdorNode, "1", shard: null, standby: false, handoff: false) }, configureServices: null, token);
            Retired.World.Ownership = Owns("0");
            Promoted.World.Ownership = Owns("0");
            Kalimdor.World.Ownership = Owns("1");

            _retiredEndpoint = Retired.Services.GetRequiredService<GatewayWorkerEndpoint>();
            await _retiredEndpoint.StartAsync(token);
            _promotedEndpoint = Promoted.Services.GetRequiredService<GatewayWorkerEndpoint>();
            await _promotedEndpoint.StartAsync(token);
            _kalimdorEndpoint = Kalimdor.Services.GetRequiredService<GatewayWorkerEndpoint>();
            await _kalimdorEndpoint.StartAsync(token);

            // The container's worker endpoint verifies the gateway's Warden posture, so the gateway must run the same Warden the container configured (as the production host does).
            IServiceProvider services = Kalimdor.Services;
            ILoggerFactory loggers = services.GetRequiredService<ILoggerFactory>();
            int realmId = services.GetService<IOptions<GatewayWorkerOptions>>()?.Value.RealmId ?? new GatewayWorkerOptions().RealmId;
            var warden = new GatewayWarden(
                realmId,
                services.GetService<IOptions<ArcaneCore.World.Warden.WardenOptions>>()?.Value ?? new ArcaneCore.World.Warden.WardenOptions(),
                loggers.CreateLogger<GatewayWarden>());
            Gateway = new GatewayServer(
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new GatewayOptions
                {
                    BindAddress = IPAddress.Loopback.ToString(),
                    Port = ArcaneCore.TestSupport.LoopbackPorts.Next(),
                    LinkSecret = Secret,
                    LinkRetryDelay = TimeSpan.FromMilliseconds(100),
                    Workers =
                    [
                        new GatewayWorkerEntry
                        {
                            NodeId = EastSlot,
                            Address = IPAddress.Loopback.ToString(),
                            Port = _retiredEndpoint.Host!.BoundEndPoint.Port,
                            Maps = [0],
                            StandbyNodeId = EastStandby,
                            StandbyAddress = IPAddress.Loopback.ToString(),
                            StandbyPort = _promotedEndpoint.Host!.BoundEndPoint.Port,
                        },
                        new GatewayWorkerEntry { NodeId = KalimdorNode, Address = IPAddress.Loopback.ToString(), Port = _kalimdorEndpoint.Host!.BoundEndPoint.Port, Maps = [1] },
                    ],
                    LoginWorker = EastSlot,
                }),
                loggers,
                warden: warden,
                handoff: Options.Create(new GatewayHandoffOptions { Enabled = true }),
                control: Options.Create(new GatewayControlOptions()),
                transfers: Options.Create(new GatewayTransferOptions { Enabled = true }));
            await Gateway.StartAsync(token);
            await Gateway.WaitForAllLinksAsync(token);

            Store = services.GetRequiredService<EfTransferDecisionStore>();
            _handoffs = new GatewayHandoffService(Gateway, Store, loggers);
            await _handoffs.StartAsync(token);
            _transfers = new GatewayTransferService(Gateway, Store, Store, new GatewayTransferOptions { Enabled = true }, Store, _handoffs, loggers);
            await _transfers.StartAsync(token);
            await WaitUntilAsync(() => Task.FromResult(_handoffs.ActiveChannel(EastSlot) is not null && _handoffs.StandbyChannel(EastSlot) is not null), token);
        }

        private async Task HandOffTheEastSlotAsync(CancellationToken token)
        {
            WorldHandoffRecord done = await _handoffs.HandoffAsync(EastSlot, cancellationToken: token);
            Assert.Equal(WorldHandoffStage.Retired, done.Stage);
            Assert.Equal(EastSlot, done.SourceWorkerId.Value);
            Assert.Equal(EastStandby, done.TargetWorkerId.Value);
            Assert.False(StandbyWorkerTests.Host(Promoted).IsStandby);
            Assert.True(StandbyWorkerTests.Host(Promoted).PromotionCompleted);

            // The gateway's link of the slot now reaches the promoted process, whose own node id is not the slot's; its transfer side answers under the slot.
            Assert.Equal(_promotedEndpoint.Host!.BoundEndPoint, Gateway.WorkerEndPoint);
            WorkerControlService control = Promoted.Services.GetRequiredService<WorkerControlService>();
            Assert.Equal(EastSlot, control.Transfers!.Source!.WorkerId.Value);
            Assert.Equal(EastSlot, control.Transfers.Target!.WorkerId.Value);
        }

        /// <summary>The journal says the transfer completed, it names the two slots, and nothing of it is left for recovery (which reads those names: GatewayTransferService's journal scan keeps rows between two slots).</summary>
        public async Task AssertCompletedAsync(Guid transferId, string sourceSlot, string targetSlot, CancellationToken token)
        {
            Assert.NotEqual(Guid.Empty, transferId);
            await WaitUntilAsync(
                async () => (await Store.GetDecisionAsync(transferId, token))?.DecisionState == PlayerTransferDecisionState.Completed, token);
            TransferDecisionRecord row = Assert.IsType<TransferDecisionRecord>(await Store.GetDecisionAsync(transferId, token));
            Assert.Equal(sourceSlot, row.SourceWorkerId.Value);
            Assert.Equal(targetSlot, row.TargetWorkerId.Value);
            Assert.DoesNotContain(await Store.ListUnresolvedAsync(token), unresolved => unresolved.TransferId == transferId);
        }

        /// <summary>A client of a fresh account whose character is saved in Eastern Kingdoms and is now in the world on the promoted standby (the login worker's slot).</summary>
        public async Task<WorldClient> EnterEastAsync(string account, string character, CancellationToken token)
        {
            (WorldClient client, int id) = await ConnectAsync(account, character, mapId: 0, token);
            try
            {
                CharacterId = id;
                await WaitUntilAsync(async () => await Promoted.World.InvokeAsync(() => Promoted.World.FindOnlinePlayer(ObjectGuid.Player((uint)id)) is not null, token), token);
                await WaitUntilAsync(() => Task.FromResult(Gateway.Workers[0].Link!.SessionCount >= 1), token);
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        /// <summary>A client whose character is saved in Kalimdor; the login redirect moved it to the Kalimdor worker.</summary>
        public async Task<WorldClient> EnterKalimdorAsync(string account, string character, CancellationToken token)
        {
            (WorldClient client, int id) = await ConnectAsync(account, character, mapId: 1, token);
            try
            {
                CharacterId = id;
                await WaitUntilAsync(async () => await Kalimdor.World.InvokeAsync(() => Kalimdor.World.FindOnlinePlayer(ObjectGuid.Player((uint)id)) is not null, token), token);
                await WaitUntilAsync(() => Task.FromResult(Gateway.Workers[1].Link!.SessionCount >= 1), token);
                // The login worker's own session of this account must be gone on the worker side too.
                await WaitUntilAsync(() => Task.FromResult(_promotedEndpoint.Acceptor!.Sessions.IsEmpty), token);
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        public static TeleportService TeleportsOf(SyntheticArcaneServer server) => server.Services.GetRequiredService<TeleportFeature>().Teleports;

        /// <summary>The world's far-teleport router, built by the production feature (for the promoted standby, when its features attached at the promotion).</summary>
        public static GatewayClusterFarTeleportRouter RouterOf(SyntheticArcaneServer server) =>
            server.Services.GetRequiredService<ClusterFarTeleportFeature>().Router
            ?? throw new InvalidOperationException("the world built no far-teleport router");

        /// <summary>The teleport call a <c>.tele</c> command makes, on the world thread of <paramref name="from"/>.</summary>
        public static Task<bool> TeleportAsync(SyntheticArcaneServer from, ObjectGuid guid, TeleportDestination destination) => from.World.InvokeAsync(
            () => from.World.FindOnlinePlayer(guid) is { } player
                && TeleportsOf(from).TeleportTo(player, destination.MapId, destination.X, destination.Y, destination.Z, destination.Orientation));

        public static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken token)
        {
            while (!await condition())
            {
                await Task.Delay(20, token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Retired is not null)
            {
                ((LocalWorldHandoffSource)Retired.Services.GetRequiredService<IWorldHandoffSource>()).Dispose(); // release its parked simulation thread before its host stops
            }

            if (_transfers is not null)
            {
                await _transfers.DisposeAsync();
            }

            if (_handoffs is not null)
            {
                await _handoffs.DisposeAsync();
            }

            if (Gateway is not null)
            {
                await Gateway.StopAsync(CancellationToken.None);
                Gateway.Dispose();
            }

            foreach (GatewayWorkerEndpoint? endpoint in new[] { _kalimdorEndpoint, _promotedEndpoint, _retiredEndpoint })
            {
                if (endpoint is not null)
                {
                    await endpoint.StopAsync(CancellationToken.None);
                }
            }

            // The shutdown saves of players that arrived by transfer run here: a refusal fails the test. Every world is stopped whichever failed first.
            Exception? stopFailure = null;
            foreach (SyntheticArcaneServer? server in new[] { Kalimdor, Promoted, Retired })
            {
                try
                {
                    if (server is not null)
                    {
                        await server.DisposeAsync();
                    }
                }
                catch (Exception ex)
                {
                    stopFailure ??= ex;
                }
            }

            Assert.Null(stopFailure);
        }

        private async Task<(WorldClient Client, int CharacterId)> ConnectAsync(string account, string character, uint mapId, CancellationToken token)
        {
            await Kalimdor.AddAccountAsync(account, "PASSWORD", token);
            LogonResult logon = await LogonClient.AuthenticateAsync(Kalimdor.RealmEndpoint, account, "PASSWORD", token);
            WorldClient client = await WorldClient.ConnectAsync(Gateway.ListenEndPoint, token);
            try
            {
                Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync(account, logon.SessionKey, token));
                int id = await CreateCharacterOnMapAsync(client, character, mapId, token);
                byte[] login = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(login, (ulong)id);
                await client.SendAsync((ushort)WorldOpcode.CmsgPlayerLogin, login, token);
                await client.ReadUntilAsync((ushort)WorldOpcode.SmsgLoginVerifyWorld, token);
                return (client, id);
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        private async Task<int> CreateCharacterOnMapAsync(WorldClient client, string name, uint mapId, CancellationToken token)
        {
            byte[] text = Encoding.ASCII.GetBytes(name);
            byte[] create = new byte[text.Length + 10];
            text.CopyTo(create, 0);
            create[text.Length + 1] = 1; // human
            create[text.Length + 2] = 1; // warrior
            await client.SendAsync((ushort)WorldOpcode.CmsgCharCreate, create, token);
            WorldFrame created = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharCreate, token);
            Assert.Equal(new byte[] { 0x2E }, created.Payload);
            await client.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, token);
            WorldFrame list = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, token);
            Assert.Equal(1, list.Payload[0]);
            int id = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(list.Payload.AsSpan(1, 8)));

            await using AsyncServiceScope scope = Promoted.Services.CreateAsyncScope();
            ICharacterStore store = scope.ServiceProvider.GetRequiredService<ICharacterStore>();
            // The worlds run on Worker scope with fencing on: a tokenless write is refused while any ownership row is active, so write under the login world's own token, the one the takeover gave the promoted standby.
            await store.SaveStateAsync(new CharacterState(id, mapId, 1, 100f, 100f, 50f, 0f, 1, 0, Ownership: Promoted.World.CharacterOwnership), token);
            CharacterRecord saved = Assert.IsType<CharacterRecord>(await store.GetByIdAsync(id, token));
            Assert.Equal(mapId, saved.MapId);
            return id;
        }

        /// <summary>A Worker-scope world as start-cluster.ps1 -Standby -Transfers launches it: its own node id and owner node, the slot's shard on the slot's two processes, and the transfer key on all of them.</summary>
        private static Dictionary<string, string?> Worker(string nodeId, string ownedMaps, string? shard, bool standby, bool handoff)
        {
            var configuration = new Dictionary<string, string?>
            {
                ["Cluster:Fencing:Enabled"] = "true",
                ["Cluster:Fencing:Scope"] = "Worker",
                ["Cluster:Fencing:OwnerNode"] = nodeId,
                ["Cluster:Fencing:AllowSqliteWorkerScope"] = "true",
                ["Cluster:Worker:Enabled"] = "true",
                ["Cluster:Worker:NodeId"] = nodeId,
                ["Cluster:Worker:OwnedMaps"] = ownedMaps,
                ["Cluster:Worker:AllowInstanceMaps"] = "false",
                ["Cluster:Worker:Port"] = ArcaneCore.TestSupport.LoopbackPorts.Next().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Cluster:Worker:LinkSecret"] = Secret,
                ["Cluster:Worker:Transfers:Enabled"] = "true",
                ["Cluster:Node:Role"] = "MapWorker",
            };
            if (shard is not null)
            {
                configuration["Cluster:Fencing:Shard"] = shard;
            }

            if (standby)
            {
                configuration["Cluster:Worker:Standby"] = "true";
            }

            if (handoff)
            {
                configuration["Cluster:Worker:Handoff:Enabled"] = "true";
            }

            return configuration;
        }

        private static MapOwnership Owns(string maps)
        {
            Assert.True(MapOwnership.TryParse(maps, allowInstanceMaps: false, out MapOwnership ownership, out string? error), error);
            return ownership;
        }
    }
}
