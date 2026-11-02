using System.Buffers.Binary;
using System.Net;
using System.Text;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Game;
using ArcaneCore.Game.Maps;
using ArcaneCore.Gateway.Transfers;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.World;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// Commit C9 of _wf/handoff-control-DESIGN.md (section 4.4), end to end: the coordinator installs the target's passive shadow at stage 3, before the gateway's stage 4 opens the client's session on the
/// target, and the production shadow loader needs that session. Two real worlds on one set of databases (node "w-east" owns map 0, node "w-kalimdor" owns map 1) behind one real gateway with transfers on,
/// a real client in the world on the Kalimdor worker, and the real <see cref="GatewayTransferService"/> (coordinator, six stages, <see cref="GatewayTransferRouter"/> stage 4 over the real link, the
/// characters database journal). Both worker ends are built by the production hosting: the worlds run in worker mode with <c>Cluster:Worker:Transfers:Enabled</c>, the endpoint is the container's
/// <see cref="GatewayWorkerEndpoint"/> and its link handler is the <see cref="WorkerControlService"/> that <see cref="WorkerTransferHosting"/> builds, so the readiness probe and the
/// <see cref="WorldGatewaySessionAcceptor.SessionOpened"/> subscription of the target are the production ones: removing or breaking that wiring fails the first test.
/// </summary>
public sealed class GatewayDeferredShadowLoadTests
{
    private const string Secret = "deferred-shadow-link-secret-0123456789";
    private const string EastNode = "w-east";
    private const string KalimdorNode = "w-kalimdor";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(150);
    private static readonly TransferPosition Landing = new(-8949.95f, -132.493f, 83.5312f, 0f);

    [Fact]
    public async Task WithTheDeferral_TheSixStageTransfer_CompletesThroughTheProductionHosting_AndThePlayerArrivesOnTheTarget()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using TransferCluster cluster = await TransferCluster.StartAsync(preC9Target: false, cts.Token);
        await using WorldClient client = await cluster.EnterKalimdorWorldAsync("DEFERGREEN", "Defergreen", cts.Token);
        PlayerTransferRequest request = await cluster.RequestAsync(cts.Token);

        GatewayTransferSubmission submission = await cluster.Service.SubmitAsync(request, cts.Token);
        Assert.True(submission.Accepted, submission.Reason);
        PlayerTransferOutcome outcome = await submission.Completion!.WaitAsync(cts.Token);

        Assert.True(outcome.Succeeded, outcome.FailureMessage);
        Assert.Equal(PlayerTransferDecisionState.Completed, outcome.DecisionState);
        Assert.Equal(PlayerTransferDecisionState.Completed, (await cluster.Store.GetDecisionAsync(request.TransferId, cts.Token))!.DecisionState);
        ObjectGuid guid = new(request.CharacterGuid);
        await TransferCluster.WaitUntilAsync(async () => await cluster.Eastern.World.InvokeAsync(() => cluster.Eastern.World.FindOnlinePlayer(guid) is not null), cts.Token);
        await TransferCluster.WaitUntilAsync(async () => await cluster.Kalimdor.World.InvokeAsync(() => cluster.Kalimdor.World.FindOnlinePlayer(guid) is null), cts.Token);
        Assert.Contains(cluster.EastWorker.Acceptor!.Sessions.Values, remote => remote.Session.AccountId == request.AccountId);
        Assert.Equal(0, cluster.Gateway.Transfers.StagedCount);

        // Exactly one writer after the commit: the target's save of the arrived player lands under the target world's own token, and a save the source still
        // holds for the character is refused by the database (the claim moved) without halting the source's queue. The logout and shutdown saves of the
        // arrived player run at disposal, where any refusal fails the test.
        int id = checked((int)guid.Low);
        CharacterState arrived = await cluster.Eastern.World.InvokeAsync(
            () => cluster.Eastern.World.FindOnlinePlayer(guid)!.CreateSnapshot(cluster.Eastern.World.NowMs), cts.Token);
        Assert.Equal(cluster.Eastern.World.CharacterOwnership, arrived.Ownership);
        CharacterSaveQueue eastSaves = cluster.Eastern.Services.GetRequiredService<CharacterSaveQueue>();
        eastSaves.Enqueue(arrived with { X = Landing.X + 3f });
        await eastSaves.FlushCharacterAsync(id, cts.Token);
        Assert.Null(eastSaves.TerminalFailure);
        Assert.Empty(eastSaves.RetainedCharacters);
        CharacterRecord afterTarget = (await cluster.StoredCharacterAsync(id, cts.Token))!;
        Assert.Equal(0u, afterTarget.MapId);
        Assert.Equal(Landing.X + 3f, afterTarget.X);

        CharacterSaveQueue kalimdorSaves = cluster.Kalimdor.Services.GetRequiredService<CharacterSaveQueue>();
        kalimdorSaves.Enqueue(new CharacterState(id, 1, 1, 100f, 100f, 50f, 0f, 1, 0, Ownership: cluster.Kalimdor.World.CharacterOwnership));
        await kalimdorSaves.FlushCharacterAsync(id, cts.Token);
        Assert.Null(kalimdorSaves.TerminalFailure);
        Assert.Empty(kalimdorSaves.RetainedCharacters);
        CharacterRecord afterSource = (await cluster.StoredCharacterAsync(id, cts.Token))!;
        Assert.Equal(0u, afterSource.MapId);
        Assert.Equal(Landing.X + 3f, afterSource.X);
    }

    [Fact]
    public async Task WithoutTheDeferral_TheSameTransfer_AbortsAtStageThree_BecauseTheClientSessionIsNotOpenOnTheTargetYet()
    {
        using var cts = new CancellationTokenSource(Deadline);
        // The east worker's link handler is the pre-C9 one: the same participants over the same loader, but a target with no readiness probe, so it loads at the stage 3 install.
        await using TransferCluster cluster = await TransferCluster.StartAsync(preC9Target: true, cts.Token);
        await using WorldClient client = await cluster.EnterKalimdorWorldAsync("DEFERRED", "Deferred", cts.Token);
        PlayerTransferRequest request = await cluster.RequestAsync(cts.Token);

        GatewayTransferSubmission submission = await cluster.Service.SubmitAsync(request, cts.Token);
        Assert.True(submission.Accepted, submission.Reason);
        PlayerTransferOutcome outcome = await submission.Completion!.WaitAsync(cts.Token);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PlayerTransferDecisionState.Aborted, outcome.DecisionState);
        Assert.True(
            outcome.FinalStage is PlayerTransferStage.Aborted || outcome.FinalStage < PlayerTransferStage.StageWorldPort,
            $"the transfer must end before stage 4, it ended at {outcome.FinalStage}");
        Assert.Null((await cluster.Store.GetDecisionAsync(request.TransferId, cts.Token))!.TargetOwnershipToken);
        // Stage 4 never ran: the target opened no session, and the source resumed the player.
        Assert.Empty(cluster.EastWorker.Acceptor!.Sessions);
        ObjectGuid guid = new(request.CharacterGuid);
        await TransferCluster.WaitUntilAsync(async () => await cluster.Kalimdor.World.InvokeAsync(() => cluster.Kalimdor.World.FindOnlinePlayer(guid) is not null), cts.Token);
        Assert.False(await cluster.Eastern.World.InvokeAsync(() => cluster.Eastern.World.FindOnlinePlayer(guid) is not null));
    }

    /// <summary>
    /// Two real worlds on one set of databases behind one real gateway with transfers on, and the gateway's real transfer service. With <c>preC9Target</c> the east worker's link handler is replaced by
    /// the control service as it was built before C9 (a target without the session probe); otherwise every handler is the one <see cref="WorkerTransferHosting"/> registered.
    /// </summary>
    private sealed class TransferCluster : IAsyncDisposable
    {
        public required SyntheticArcaneServer Eastern { get; init; }

        public required SyntheticArcaneServer Kalimdor { get; init; }

        public required GatewayWorkerEndpoint EastWorker { get; init; }

        public required GatewayWorkerEndpoint KalimdorWorker { get; init; }

        public required GatewayServer Gateway { get; init; }

        public required EfTransferDecisionStore Store { get; init; }

        public required GatewayTransferService Service { get; init; }

        private int _characterId;

        public static async Task<TransferCluster> StartAsync(bool preC9Target, CancellationToken token)
        {
            SyntheticArcaneServer eastern = await SyntheticArcaneServer.StartAsync(
                new SyntheticServerOptions { Configuration = WorkerScopeConfiguration(EastNode, "0") }, configureServices: null, token);
            SyntheticArcaneServer? kalimdor = null;
            GatewayWorkerEndpoint? eastWorker = null;
            GatewayWorkerEndpoint? kalimdorWorker = null;
            GatewayServer? gateway = null;
            GatewayTransferService? service = null;
            try
            {
                kalimdor = await SyntheticArcaneServer.StartAsync(
                    new SyntheticServerOptions { SharedDatabaseDirectory = eastern.DataDirectory, Configuration = WorkerScopeConfiguration(KalimdorNode, "1") },
                    configureServices: null,
                    token);
                eastern.World.Ownership = Owns("0");
                kalimdor.World.Ownership = Owns("1");
                eastWorker = await StartWorkerAsync(eastern, token);
                kalimdorWorker = await StartWorkerAsync(kalimdor, token);
                if (preC9Target)
                {
                    eastWorker.Host!.ControlHandler = PreC9ControlService(eastern, eastWorker);
                }

                // The container's worker endpoint verifies the gateway's Warden posture, so the gateway must run the same Warden the container configured (as the production host does).
                int realmId = eastern.Services.GetService<IOptions<GatewayWorkerOptions>>()?.Value.RealmId ?? new GatewayWorkerOptions().RealmId;
                var warden = new GatewayWarden(
                    realmId,
                    eastern.Services.GetService<IOptions<ArcaneCore.World.Warden.WardenOptions>>()?.Value ?? new ArcaneCore.World.Warden.WardenOptions(),
                    eastern.Services.GetRequiredService<ILoggerFactory>().CreateLogger<GatewayWarden>());
                gateway = new GatewayServer(
                    eastern.Services.GetRequiredService<IServiceScopeFactory>(),
                    Options.Create(new GatewayOptions
                    {
                        BindAddress = IPAddress.Loopback.ToString(),
                        Port = ArcaneCore.TestSupport.LoopbackPorts.Next(),
                        LinkSecret = Secret,
                        LinkRetryDelay = TimeSpan.FromMilliseconds(100),
                        Workers =
                        [
                            new GatewayWorkerEntry { NodeId = EastNode, Address = IPAddress.Loopback.ToString(), Port = eastWorker.Host!.BoundEndPoint.Port, Maps = [0] },
                            new GatewayWorkerEntry { NodeId = KalimdorNode, Address = IPAddress.Loopback.ToString(), Port = kalimdorWorker.Host!.BoundEndPoint.Port, Maps = [1] },
                        ],
                        LoginWorker = EastNode,
                    }),
                    eastern.Services.GetRequiredService<ILoggerFactory>(),
                    warden: warden,
                    transfers: Options.Create(new GatewayTransferOptions { Enabled = true }));
                await gateway.StartAsync(token);
                await gateway.WaitForAllLinksAsync(token);
                EfTransferDecisionStore store = eastern.Services.GetRequiredService<EfTransferDecisionStore>();
                service = new GatewayTransferService(
                    gateway, store, store, new GatewayTransferOptions { Enabled = true }, loggers: eastern.Services.GetRequiredService<ILoggerFactory>());
                return new TransferCluster
                {
                    Eastern = eastern, Kalimdor = kalimdor, EastWorker = eastWorker, KalimdorWorker = kalimdorWorker, Gateway = gateway, Store = store, Service = service,
                };
            }
            catch
            {
                if (service is not null)
                {
                    await service.DisposeAsync();
                }

                if (gateway is not null)
                {
                    await gateway.StopAsync(CancellationToken.None);
                    gateway.Dispose();
                }

                if (kalimdorWorker is not null)
                {
                    await kalimdorWorker.StopAsync(CancellationToken.None);
                }

                if (eastWorker is not null)
                {
                    await eastWorker.StopAsync(CancellationToken.None);
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
            LogonResult logon = await LogonClient.AuthenticateAsync(Eastern.RealmEndpoint, account, "PASSWORD", token);
            WorldClient client = await WorldClient.ConnectAsync(Gateway.ListenEndPoint, token);
            try
            {
                Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync(account, logon.SessionKey, token));
                _characterId = await CreateCharacterOnMapAsync(client, character, mapId: 1, token);
                byte[] login = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(login, (ulong)_characterId);
                await client.SendAsync((ushort)WorldOpcode.CmsgPlayerLogin, login, token);
                await client.ReadUntilAsync((ushort)WorldOpcode.SmsgLoginVerifyWorld, token);
                await WaitUntilAsync(async () => await Kalimdor.World.InvokeAsync(() => Kalimdor.World.OnlinePlayerCount) == 1, token);
                await WaitUntilAsync(() => Task.FromResult(Gateway.Workers[1].Link!.SessionCount == 1 && Gateway.Workers[0].Link!.SessionCount == 0), token);
                // The login worker's own session of this account must be gone on the worker side too, or it would answer the target's session probe at stage 3.
                await WaitUntilAsync(() => Task.FromResult(EastWorker.Acceptor!.Sessions.IsEmpty), token);
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        /// <summary>The transfer request of the client's character from the Kalimdor worker to Eastern Kingdoms (map 0, worker w-east), under the ownership token the source holds for it.</summary>
        public async Task<PlayerTransferRequest> RequestAsync(CancellationToken token)
        {
            ulong guid = ObjectGuid.Player((uint)_characterId).Value;
            KeyValuePair<long, RemoteWorldSession> open = Assert.Single(KalimdorWorker.Acceptor!.Sessions);
            CharacterOwnershipToken? ownership = await Kalimdor.World.InvokeAsync(
                () => Kalimdor.World.FindOnlinePlayer(new ObjectGuid(guid))?.CharacterOwnership ?? Kalimdor.World.CharacterOwnership, token);
            Assert.NotNull(ownership);
            return new PlayerTransferRequest(
                Guid.NewGuid(), guid, open.Value.Session.AccountId, open.Key, new WorkerNodeId(KalimdorNode), SourceMapId: 1, SourceInstanceId: 0, TargetMapId: 0, TargetInstanceId: 0, Landing, ownership);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Gateway.StopAsync(CancellationToken.None);
            Gateway.Dispose();
            await KalimdorWorker.StopAsync(CancellationToken.None);
            await EastWorker.StopAsync(CancellationToken.None);
            await Kalimdor.DisposeAsync();
            await Eastern.DisposeAsync(); // the arrived player's shutdown save runs here: a refusal fails the test
        }

        public async Task<CharacterRecord?> StoredCharacterAsync(int id, CancellationToken token)
        {
            await using AsyncServiceScope scope = Eastern.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICharacterStore>().GetByIdAsync(id, token);
        }

        public static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken token)
        {
            while (!await condition())
            {
                await Task.Delay(20, token);
            }
        }

        /// <summary>
        /// The control service of a transfer-enabled worker as it was built before C9: production source and target participants over the production loader and the same acceptor, and no
        /// <c>sessionReady</c> probe, so the target loads the shadow when stage 3 installs it.
        /// </summary>
        private static WorkerControlService PreC9ControlService(SyntheticArcaneServer server, GatewayWorkerEndpoint endpoint)
        {
            IServiceProvider services = server.Services;
            WorldHost host = services.GetServices<IHostedService>().OfType<WorldHost>().Single();
            WorldRuntime world = server.World;
            WorkerAdmission admission = services.GetRequiredService<WorkerAdmission>();
            var nodeId = new WorkerNodeId(EastNode);
            WorldGatewaySessionAcceptor acceptor = endpoint.Acceptor!;
            WorldSession? Adopted(PlayerTransferBundle bundle)
            {
                foreach (KeyValuePair<long, RemoteWorldSession> open in acceptor.Sessions)
                {
                    if (open.Value.Session.AccountId == bundle.AccountId)
                    {
                        return open.Value.Session;
                    }
                }

                return null;
            }

            var target = new WorkerTransferTarget(
                nodeId, world, new CharacterHooksShadowLoader(Adopted), new WorldSessionTransferEntry(), admission, WorkerTransferHosting.MaxTargetReservations, host.Incarnation);
            var transfer = new WorkerControlTransfer(
                services.GetRequiredService<ITransferDecisionStore>(), null, target, () => admission.IsOpen, () => WorkerTransferHosting.MaxTargetReservations - target.ReservedCount);
            return new WorkerControlService(
                handoffEnabled: false, handoff: null, transfersEnabled: true, transfers: transfer,
                identity: () => new WorkerControlIdentity(
                    nodeId, host.IsStandby, host.Incarnation, host.OwnerNode, world.CharacterOwnership?.Scope ?? string.Empty,
                    host.WorkerOwnedMaps is { } maps ? maps.Order().ToArray() : Array.Empty<uint>()));
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

            await using AsyncServiceScope scope = Eastern.Services.CreateAsyncScope();
            ICharacterStore store = scope.ServiceProvider.GetRequiredService<ICharacterStore>();
            // The worlds run on Worker scope with fencing on: a tokenless write is refused while any ownership row is active, so write under the login world's own token.
            CharacterOwnershipToken? owner = Eastern.Services.GetRequiredService<ICharacterOwnershipContext>().Current;
            await store.SaveStateAsync(new CharacterState(id, mapId, 1, 100f, 100f, 50f, 0f, 1, 0, Ownership: owner), token);
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
            ["Cluster:Worker:AllowInstanceMaps"] = "false",
            ["Cluster:Worker:Port"] = ArcaneCore.TestSupport.LoopbackPorts.Next().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cluster:Worker:LinkSecret"] = Secret,
            ["Cluster:Worker:Transfers:Enabled"] = "true",
            ["Cluster:Node:Role"] = "MapWorker",
        };

        private static MapOwnership Owns(string maps)
        {
            Assert.True(MapOwnership.TryParse(maps, allowInstanceMaps: false, out MapOwnership ownership, out string? error), error);
            return ownership;
        }

        /// <summary>The container's own endpoint (the production registration), started the way the host would start it.</summary>
        private static async Task<GatewayWorkerEndpoint> StartWorkerAsync(SyntheticArcaneServer server, CancellationToken token)
        {
            GatewayWorkerEndpoint endpoint = server.Services.GetRequiredService<GatewayWorkerEndpoint>();
            await endpoint.StartAsync(token);
            return endpoint;
        }
    }
}
