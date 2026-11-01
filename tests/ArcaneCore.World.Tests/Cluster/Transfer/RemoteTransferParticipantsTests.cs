using System.Net;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Game;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Cluster.Transfer.Remote;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>
/// C6 of _wf/handoff-control-DESIGN.md: the real <see cref="PlayerTransferCoordinator"/> drives a per-player transfer through <see cref="RemoteTransferSourceWorker"/> and
/// <see cref="RemoteTransferTargetWorker"/> over two real links. A real <see cref="GatewaySessionLinkHost"/> per worker on loopback, real <see cref="GatewaySessionLinkClient"/>s and
/// <see cref="LinkControlChannel"/>s carry the frames, and a real <see cref="WorkerControlService"/> answers on each. Fakes sit only at the process boundary (the world participants
/// behind the services and the gateway's router) and the database boundary (the in-memory journal, or the SQLite one the coordinator and both workers share).
/// </summary>
public sealed class RemoteTransferParticipantsTests
{
    private const ulong Character = 4242UL;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);
    private static readonly WorkerNodeId SourceId = new("worker-ek");
    private static readonly WorkerNodeId TargetId = new("worker-kalimdor");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullTransfer_OverTwoLinks_RunsTheSixStagesInOrder_AndTheBundleRoundTripsThroughTheJournal(bool durable)
    {
        await using Rig rig = await Rig.StartAsync(durable);
        var stages = new List<PlayerTransferStage>();

        PlayerTransferOutcome outcome = await rig.Coordinator.ExecuteTransferAsync(
            rig.Request, (stage, _, _) =>
            {
                stages.Add(stage);
                return Task.CompletedTask;
            }).WaitAsync(Budget);

        Assert.True(outcome.Succeeded, outcome.FailureMessage);
        Assert.Equal(PlayerTransferDecisionState.Completed, outcome.DecisionState);
        Assert.Equal(
            [
                PlayerTransferStage.Reserve, PlayerTransferStage.SuspendAndCapture, PlayerTransferStage.InstallPassive,
                PlayerTransferStage.StageWorldPort, PlayerTransferStage.CommitDecision, PlayerTransferStage.ActivateAndRetire,
            ],
            stages);
        Assert.Equal(
            ["target.Reserve", "source.Suspend", "target.Install", "router.Stage", "source.Retire", "target.Install", "target.Activate", "router.CommitRoute"],
            rig.Events.Snapshot());
        Assert.False(rig.Source.IsActive);
        Assert.True(rig.Target.IsActive);

        TransferDecisionRecord record = (await rig.Store.GetDecisionAsync(rig.Request.TransferId))!;
        Assert.Equal(rig.Request.SourceOwnershipToken.Epoch + 1, record.TargetOwnershipToken!.Epoch);
        Assert.Equal(rig.Target.Incarnation, record.TargetOwnershipToken.Incarnation);
        Assert.Equal(PlayerTransferBundleJson.Serialize(rig.Source.Captured!), PlayerTransferBundleJson.Serialize(record.CapturedBundle!));
        Assert.Equal(PlayerTransferBundleJson.Serialize(rig.Source.Captured!), PlayerTransferBundleJson.Serialize(rig.Target.InstalledBundle!));
    }

    [Fact]
    public async Task ATargetThatRefusesTheReservation_AbortsTheTransfer_AndTheSourceKeepsThePlayer()
    {
        await using Rig rig = await Rig.StartAsync(false, target => target.RejectReservation = true);

        PlayerTransferOutcome outcome = await rig.Coordinator.ExecuteTransferAsync(rig.Request).WaitAsync(Budget);

        Assert.False(outcome.Succeeded);
        Assert.Equal("target-reserve-rejected", outcome.FailureCode);
        Assert.Equal(PlayerTransferDecisionState.Aborted, outcome.DecisionState);
        string[] events = rig.Events.Snapshot();
        Assert.DoesNotContain("source.Suspend", events);
        Assert.Contains("target.Discard", events);
        Assert.Contains("source.Resume", events);
        Assert.True(rig.Source.IsActive);
        Assert.False(rig.Target.IsActive);
    }

    [Fact]
    public async Task ABundleThatFailsValidation_AbortsTheTransfer_ResumesTheSource_AndDiscardsOnTheTarget()
    {
        await using Rig rig = await Rig.StartAsync(false, configureSource: source => source.CaptureInvalid = true);

        PlayerTransferOutcome outcome = await rig.Coordinator.ExecuteTransferAsync(rig.Request).WaitAsync(Budget);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PlayerTransferDecisionState.Aborted, outcome.DecisionState);
        Assert.False(string.IsNullOrEmpty(outcome.FailureCode));
        string[] events = rig.Events.Snapshot();
        Assert.Equal(["target.Reserve", "source.Suspend", "target.Discard", "router.CancelStage", "source.Resume"], events);
        Assert.True(rig.Source.IsActive);
    }

    [Fact]
    public async Task LinkLossAtInstall_FailsTheCall_AndTheAbortSendsDiscardAndResumesTheSource()
    {
        var installGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Rig rig = await Rig.StartAsync(false, target => target.InstallGate = installGate);

        Task<PlayerTransferOutcome> running = rig.Coordinator.ExecuteTransferAsync(rig.Request);
        await rig.Target.InstallStarted.Task.WaitAsync(Budget);

        // The gateway's end of the target link dies while the Install is still running on the worker.
        await rig.DropTargetLinkAsync();
        await Assert.ThrowsAsync<ControlLinkLostException>(() => running).WaitAsync(Budget);

        // The link comes back (the slot's live link is asked for on every call); the abort a host performs then reaches the target.
        await rig.ReconnectTargetAsync();
        Assert.True(await rig.Coordinator.TryAbortTransferAsync(rig.Request.TransferId, "link-lost").WaitAsync(Budget));

        installGate.SetResult();
        string[] events = rig.Events.Snapshot();
        Assert.Contains("target.Discard", events);
        Assert.Contains("source.Resume", events);
        Assert.DoesNotContain("source.Retire", events);
        Assert.Equal(PlayerTransferDecisionState.Aborted, (await rig.Store.GetDecisionAsync(rig.Request.TransferId))!.DecisionState);
        Assert.True(rig.Source.IsActive);
    }

    [Fact]
    public async Task ADiscardWhileTheTargetLinkIsDown_IsBestEffort_AndTheAbortStillResumesTheSource()
    {
        await using Rig rig = await Rig.StartAsync(false);
        await rig.DropTargetLinkAsync();

        // Reserve cannot run without the link: the transfer fails before it has a journal-visible effect on the source.
        await Assert.ThrowsAsync<ControlLinkLostException>(() => rig.Coordinator.ExecuteTransferAsync(rig.Request)).WaitAsync(Budget);
        Assert.True(await rig.Coordinator.TryAbortTransferAsync(rig.Request.TransferId, "link-lost").WaitAsync(Budget));

        string[] events = rig.Events.Snapshot();
        Assert.DoesNotContain("target.Discard", events);
        Assert.Contains("source.Resume", events);
        Assert.True(rig.Source.IsActive);
    }

    [Fact]
    public async Task ATargetIncarnationChangeBeforeTheCommit_AbortsTheTransfer()
    {
        await using Rig rig = await Rig.StartAsync(false);
        Guid transferId = rig.Request.TransferId;

        async Task Hook(PlayerTransferStage stage, TransferDecisionRecord record, CancellationToken ct)
        {
            if (stage != PlayerTransferStage.StageWorldPort)
            {
                return;
            }

            // The target process restarted: it answers TargetDescribe with a new incarnation and holds neither the reservation nor the shadow.
            rig.Target.Incarnation = Guid.NewGuid();
            if (!await rig.TargetProxy.IncarnationUnchangedSinceReserveAsync(transferId, ct))
            {
                await rig.Coordinator.AbortTransferAsync(transferId, "target-incarnation-changed", ct);
            }
        }

        PlayerTransferOutcome outcome = await rig.Coordinator.ExecuteTransferAsync(rig.Request, Hook).WaitAsync(Budget);

        Assert.False(outcome.Succeeded);
        Assert.Equal("target-incarnation-changed", outcome.FailureCode);
        Assert.Equal(PlayerTransferDecisionState.Aborted, outcome.DecisionState);
        string[] events = rig.Events.Snapshot();
        Assert.DoesNotContain("source.Retire", events);
        Assert.DoesNotContain("target.Activate", events);
        Assert.Contains("target.Discard", events);
        Assert.Contains("source.Resume", events);
        Assert.True(rig.Source.IsActive);
        Assert.False(rig.Target.IsActive);
    }

    [Fact]
    public async Task TheTargetThatAllocatesAnInstance_SurfacesItInTheReserveReply_AndTheCoordinatorCarriesItThroughEveryStage()
    {
        const uint dungeon = 33;
        const uint allocated = 117;
        await using Rig rig = await Rig.StartAsync(false, target => target.AllocatedInstance = allocated);
        PlayerTransferRequest request = rig.Request with { TargetMapId = dungeon, TargetInstanceId = 0, ExplicitTargetWorkerId = TargetId };

        // The proxy surfaces the id the target bound.
        Guid probe = await rig.AddReservationRowAsync(9001UL);
        TransferSlotReservation direct = await rig.TargetProxy.ReserveSlotAsync(probe, 9001UL, dungeon, 0).WaitAsync(Budget);
        Assert.True(direct.Reserved);
        Assert.Equal(allocated, direct.InstanceId);

        PlayerTransferOutcome outcome = await rig.Coordinator.ExecuteTransferAsync(request).WaitAsync(Budget);

        Assert.True(outcome.Succeeded, outcome.FailureMessage);
        Assert.Equal(allocated, rig.Source.SuspendedForInstance);
        Assert.Equal(allocated, rig.Router.StagedInstance);
        Assert.Equal(allocated, rig.Target.InstalledBundle!.TargetInstanceId);
        TransferDecisionRecord record = (await rig.Store.GetDecisionAsync(request.TransferId))!;
        Assert.Equal(allocated, record.CapturedBundle!.TargetInstanceId);
    }

    [Fact]
    public async Task ARequestThatNamesAnInstance_IsAnsweredWithThatInstance_WithoutAllocating()
    {
        await using Rig rig = await Rig.StartAsync(false, target => target.AllocatedInstance = 117);

        Guid probe = await rig.AddReservationRowAsync(9001UL);
        TransferSlotReservation named = await rig.TargetProxy.ReserveSlotAsync(probe, 9001UL, 33, 55).WaitAsync(Budget);

        Assert.True(named.Reserved);
        Assert.Equal(55u, named.InstanceId);
    }

    [Fact]
    public async Task TheProductionTarget_AllocatesAndBindsAnInstanceForAnInstanceZeroRequest_AndRemembersItPerTransfer()
    {
        var allocations = new List<(uint MapId, ulong Guid)>();
        var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new RecordingSaveQueue(), NullLogger<WorldRuntime>.Instance);
        world.Start();
        try
        {
            var target = new WorkerTransferTarget(
                TargetId, world, new NeverLoader(), new NeverEntry(),
                allocateInstance: (mapId, guid) =>
                {
                    allocations.Add((mapId, guid.Value));
                    return mapId == 33 ? 117u : 0u;
                });
            Guid transfer = Guid.NewGuid();

            TransferSlotReservation first = await target.ReserveSlotAsync(transfer, 7, 33, 0);
            TransferSlotReservation repeat = await target.ReserveSlotAsync(transfer, 7, 33, 0);
            TransferSlotReservation continent = await target.ReserveSlotAsync(Guid.NewGuid(), 8, 1, 0);
            TransferSlotReservation named = await target.ReserveSlotAsync(Guid.NewGuid(), 9, 33, 55);

            Assert.Equal(new TransferSlotReservation(true, 117), first);
            Assert.Equal(first, repeat);
            Assert.Equal(new TransferSlotReservation(true, 0), continent);
            Assert.Equal(new TransferSlotReservation(true, 55), named);
            Assert.Equal([(33u, 7UL), (1u, 8UL)], allocations);
            Assert.True(await target.TryReserveSlotAsync(Guid.NewGuid(), 10, 1, 0));
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public async Task AnInstanceZeroRequest_ForAnUnlistedContinent_IsRefused_WhenTheWorkerOwnsOnlyOneMapPlusInstances()
    {
        var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new RecordingSaveQueue(), NullLogger<WorldRuntime>.Instance);
        world.Ownership = new MapOwnership([new MapOwnership.Range(1, 1)], allowInstanceMaps: true);
        world.Start();
        try
        {
            var target = new WorkerTransferTarget(
                TargetId, world, new NeverLoader(), new NeverEntry(),
                allocateInstance: (mapId, _) => mapId == 33 ? 117u : 0u);

            TransferSlotReservation unlisted = await target.ReserveSlotAsync(Guid.NewGuid(), 7, 0, 0);
            TransferSlotReservation listed = await target.ReserveSlotAsync(Guid.NewGuid(), 8, 1, 0);
            TransferSlotReservation dungeon = await target.ReserveSlotAsync(Guid.NewGuid(), 9, 33, 0);

            Assert.False(unlisted.Reserved);
            Assert.Equal(new TransferSlotReservation(true, 0), listed);
            Assert.Equal(new TransferSlotReservation(true, 117), dungeon);
        }
        finally
        {
            world.Stop();
        }
    }

    // ------------------------------------------------------------------ rig

    private sealed class NeverLoader : ITransferShadowLoader
    {
        public Task<ArcaneCore.Game.Entities.Player> LoadAsync(PlayerTransferBundle bundle, CancellationToken cancellationToken) =>
            throw new NotSupportedException("a reservation loads no shadow");

        public void Release(ArcaneCore.Game.Entities.Player shadow)
        {
        }
    }

    private sealed class NeverEntry : ITransferSessionEntry
    {
        public bool TryAttach(ArcaneCore.Game.Entities.Player player) => throw new NotSupportedException("a reservation attaches nothing");

        public void ResumeSaves(ArcaneCore.Game.Entities.Player player)
        {
        }
    }

    /// <summary>The source worker and the target worker, each behind a real link host with a real control service; the gateway's two links, channels and proxies; the coordinator.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private const string Secret = "link-secret-0123456789";
        private readonly List<ServiceProvider> _providers = [];
        private readonly GatewaySessionLinkHost _sourceHost;
        private readonly GatewaySessionLinkHost _targetHost;
        private readonly GatewaySessionLinkClient _sourceClient;
        private GatewaySessionLinkClient _targetClient;
        private LinkControlChannel _targetChannel;

        private Rig(
            ITransferDecisionStore store, Events events, FakeNode source, FakeNode target, RecordingRouter router, GatewaySessionLinkHost sourceHost, GatewaySessionLinkHost targetHost,
            GatewaySessionLinkClient sourceClient, GatewaySessionLinkClient targetClient, LinkControlChannel sourceChannel, LinkControlChannel targetChannel, PlayerTransferRequest request)
        {
            Store = store;
            Events = events;
            Source = source;
            Target = target;
            Router = router;
            _sourceHost = sourceHost;
            _targetHost = targetHost;
            _sourceClient = sourceClient;
            _targetClient = targetClient;
            _targetChannel = targetChannel;
            Request = request;

            var mapTable = new ClusterMapPlacementTable();
            mapTable.RegisterWorker(SourceId, isReady: true, maxPlayerCapacity: 500, activePlayerCount: 50, incarnation: source.Incarnation);
            mapTable.RegisterWorker(TargetId, isReady: true, maxPlayerCapacity: 500, activePlayerCount: 40, incarnation: target.Incarnation);
            mapTable.AssignStaticMap(0, SourceId);
            mapTable.AssignStaticMap(1, TargetId);
            TargetProxy = new RemoteTransferTargetWorker(TargetId, () => _targetChannel);
            Coordinator = new PlayerTransferCoordinator(
                mapTable, store, router, [new RemoteTransferSourceWorker(SourceId, () => sourceChannel)], [TargetProxy]);
        }

        public ITransferDecisionStore Store { get; }

        public Events Events { get; }

        public FakeNode Source { get; }

        public FakeNode Target { get; }

        public RecordingRouter Router { get; }

        public PlayerTransferRequest Request { get; }

        public PlayerTransferCoordinator Coordinator { get; }

        public RemoteTransferTargetWorker TargetProxy { get; }

        public static async Task<Rig> StartAsync(bool durable, Action<FakeNode>? configureTarget = null, Action<FakeNode>? configureSource = null)
        {
            var events = new Events();
            var source = new FakeNode("source", SourceId, events);
            var target = new FakeNode("target", TargetId, events);
            configureTarget?.Invoke(target);
            configureSource?.Invoke(source);
            var router = new RecordingRouter(events);

            string directory = Path.Combine(Path.GetTempPath(), "arcanecore-remote-transfer-" + Guid.NewGuid().ToString("N"));
            ServiceProvider? provider = null;
            ITransferDecisionStore store;
            if (durable)
            {
                Directory.CreateDirectory(directory);
                IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["Database:ConnectionString"] = $"Data Source={Path.Combine(directory, "characters.db")}",
                }).Build();
                provider = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider();
                await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
                store = provider.GetRequiredService<EfTransferDecisionStore>();
            }
            else
            {
                store = new InMemoryTransferDecisionStore();
            }

            var sourceToken = new CharacterOwnershipToken("char:4242", 7, source.Incarnation);
            store.SeedCharacterOwnership(Character, sourceToken);
            PlayerTransferBundle seeded = PlayerTransferValidatorTests.CreateSampleValidBundle(
                transferId: Guid.NewGuid(), characterGuid: Character, sourceWorker: SourceId, targetWorker: TargetId, sourceToken: sourceToken);
            source.Seed(seeded);

            var sourceService = new WorkerControlService(
                handoffEnabled: false, handoff: null, transfersEnabled: true,
                transfers: new WorkerControlTransfer(store, source, null, () => true, () => 8),
                identity: () => new WorkerControlIdentity(SourceId, false, source.Incarnation, "node-ek", "world", [0u]));
            var targetService = new WorkerControlService(
                handoffEnabled: false, handoff: null, transfersEnabled: true,
                transfers: new WorkerControlTransfer(store, null, target, () => true, () => 8),
                identity: () => new WorkerControlIdentity(TargetId, false, target.Incarnation, "node-kalimdor", "world", [1u]));

            GatewaySessionLinkHost? sourceHost = null;
            GatewaySessionLinkHost? targetHost = null;
            GatewaySessionLinkClient? sourceClient = null;
            GatewaySessionLinkClient? targetClient = null;
            try
            {
                sourceHost = StartHost("worker-ek", sourceService);
                targetHost = StartHost("worker-kalimdor", targetService);
                sourceClient = await ConnectAsync(sourceHost);
                targetClient = await ConnectAsync(targetHost);
                var request = new PlayerTransferRequest(
                    TransferId: Guid.NewGuid(),
                    CharacterGuid: Character,
                    AccountId: seeded.AccountId,
                    SessionGeneration: seeded.SessionGeneration,
                    SourceWorkerId: SourceId,
                    SourceMapId: 0,
                    SourceInstanceId: 0,
                    TargetMapId: 1,
                    TargetInstanceId: 0,
                    TargetPosition: seeded.TargetPosition,
                    SourceOwnershipToken: sourceToken);
                var rig = new Rig(
                    store, events, source, target, router, sourceHost, targetHost, sourceClient, targetClient,
                    new LinkControlChannel(sourceClient), new LinkControlChannel(targetClient), request);
                if (provider is not null)
                {
                    rig._providers.Add(provider);
                    rig._durableDirectory = directory;
                }

                return rig;
            }
            catch
            {
                if (sourceClient is not null)
                {
                    await sourceClient.DisposeAsync();
                }

                if (targetClient is not null)
                {
                    await targetClient.DisposeAsync();
                }

                if (sourceHost is not null)
                {
                    await sourceHost.DisposeAsync();
                }

                if (targetHost is not null)
                {
                    await targetHost.DisposeAsync();
                }

                throw;
            }
        }

        private string? _durableDirectory;

        /// <summary>Journals a stage-1 reservation row for another character (the worker reads the row before it reserves), so a test can call the target proxy directly.</summary>
        public async Task<Guid> AddReservationRowAsync(ulong character)
        {
            var token = new CharacterOwnershipToken("char:" + character, 1, Source.Incarnation);
            Store.SeedCharacterOwnership(character, token);
            Guid id = Guid.NewGuid();
            await Store.CreateReservationAsync(id, character, 77, 5, SourceId, TargetId, 0, 0, 33, 0, token);
            return id;
        }

        /// <summary>The gateway's end of the target link goes away (the link host and the worker stay up).</summary>
        public async Task DropTargetLinkAsync()
        {
            await _targetClient.DisposeAsync();
            using var cts = new CancellationTokenSource(Budget);
            while (_targetChannel.IsConnected)
            {
                await Task.Delay(10, cts.Token);
            }
        }

        /// <summary>A new link to the still-running target worker; the proxy finds it on its next call.</summary>
        public async Task ReconnectTargetAsync()
        {
            _targetClient = await ConnectAsync(_targetHost);
            _targetChannel = new LinkControlChannel(_targetClient);
        }

        public async ValueTask DisposeAsync()
        {
            await _sourceClient.DisposeAsync();
            await _targetClient.DisposeAsync();
            await _sourceHost.DisposeAsync();
            await _targetHost.DisposeAsync();
            foreach (ServiceProvider provider in _providers)
            {
                await provider.DisposeAsync();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (_durableDirectory is not null && Directory.Exists(_durableDirectory))
                {
                    Directory.Delete(_durableDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A temp directory a straggling connection still holds is left for the OS to clean.
            }
        }

        private static GatewaySessionLinkHost StartHost(string node, IGatewayControlHandler handler)
        {
            var host = new GatewaySessionLinkHost(
                GatewayLinkManifest.Create(ClusterRole.Worker, node, Guid.NewGuid(), 1, "dev", 1, 1),
                new NoSessionsAcceptor(), () => 1, Secret, new IPEndPoint(IPAddress.Loopback, 0))
            {
                ControlHandler = handler,
            };
            host.Start();
            return host;
        }

        private static async Task<GatewaySessionLinkClient> ConnectAsync(GatewaySessionLinkHost host)
        {
            using var cts = new CancellationTokenSource(Budget);
            var client = new GatewaySessionLinkClient(
                GatewayLinkManifest.Create(ClusterRole.Gateway, "gateway-a", Guid.NewGuid(), 1, "dev", 1, 1), host.BoundEndPoint, Secret);
            try
            {
                await client.ConnectAsync(cts.Token);
                while (host.AdmittedLinkCount != 1)
                {
                    await Task.Delay(10, cts.Token);
                }
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }

            return client;
        }
    }

    private sealed class NoSessionsAcceptor : IGatewaySessionAcceptor
    {
        public ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GatewaySessionAcceptResult.Refused(ArcaneCore.Protocol.AuthResponseCode.Failed, "no sessions in this test"));
    }

    /// <summary>The ordered record of what the world participants and the router did.</summary>
    private sealed class Events
    {
        private readonly List<string> _list = [];

        public void Add(string what)
        {
            lock (_list)
            {
                _list.Add(what);
            }
        }

        public string[] Snapshot()
        {
            lock (_list)
            {
                return [.. _list];
            }
        }
    }

    private sealed class RecordingRouter(Events events) : ITransferGatewayRouter
    {
        private uint _stagedInstance;

        public uint StagedInstance => Volatile.Read(ref _stagedInstance);

        public Task StageWorldPortAsync(
            Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId,
            uint targetMapId, uint targetInstanceId, TransferPosition targetPosition, CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _stagedInstance, targetInstanceId);
            events.Add("router.Stage");
            return Task.CompletedTask;
        }

        public Task CancelStagedWorldPortAsync(
            Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId restoredWorkerId, string abortReason, CancellationToken cancellationToken = default)
        {
            events.Add("router.CancelStage");
            return Task.CompletedTask;
        }

        public Task CommitRouteSwitchAsync(
            Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId targetWorkerId, CharacterOwnershipToken targetOwnershipToken, CancellationToken cancellationToken = default)
        {
            events.Add("router.CommitRoute");
            return Task.CompletedTask;
        }
    }

    /// <summary>A world participant of either role: the source holds the seeded player, the target takes it. Thread-safe (the link host calls it from its own threads).</summary>
    private sealed class FakeNode(string role, WorkerNodeId workerId, Events events) : ITransferSourceWorker, ITransferTargetWorker
    {
        private readonly Lock _gate = new();
        private PlayerTransferBundle? _active;
        private PlayerTransferBundle? _suspended;
        private PlayerTransferBundle? _captured;
        private PlayerTransferBundle? _installed;
        private uint _suspendedForInstance;
        private Guid _incarnation = Guid.NewGuid();

        public WorkerNodeId WorkerId => workerId;

        public Guid Incarnation
        {
            get
            {
                lock (_gate)
                {
                    return _incarnation;
                }
            }

            set
            {
                lock (_gate)
                {
                    _incarnation = value;
                }
            }
        }

        public bool RejectReservation { get; set; }

        public bool CaptureInvalid { get; set; }

        public uint? AllocatedInstance { get; set; }

        public TaskCompletionSource? InstallGate { get; set; }

        public TaskCompletionSource InstallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsActive
        {
            get
            {
                lock (_gate)
                {
                    return _active is not null;
                }
            }
        }

        public PlayerTransferBundle? Captured
        {
            get
            {
                lock (_gate)
                {
                    return _captured;
                }
            }
        }

        public PlayerTransferBundle? InstalledBundle
        {
            get
            {
                lock (_gate)
                {
                    return _installed;
                }
            }
        }

        public uint SuspendedForInstance
        {
            get
            {
                lock (_gate)
                {
                    return _suspendedForInstance;
                }
            }
        }

        public void Seed(PlayerTransferBundle bundle)
        {
            lock (_gate)
            {
                _active = bundle;
            }
        }

        public async Task<bool> TryReserveSlotAsync(Guid transferId, ulong characterGuid, uint targetMapId, uint targetInstanceId, CancellationToken cancellationToken = default) =>
            (await ReserveSlotAsync(transferId, characterGuid, targetMapId, targetInstanceId, cancellationToken).ConfigureAwait(false)).Reserved;

        public Task<TransferSlotReservation> ReserveSlotAsync(
            Guid transferId, ulong characterGuid, uint targetMapId, uint targetInstanceId, CancellationToken cancellationToken = default)
        {
            events.Add(role + ".Reserve");
            if (RejectReservation)
            {
                return Task.FromResult(new TransferSlotReservation(false, targetInstanceId));
            }

            uint bound = targetInstanceId == 0 && AllocatedInstance is { } allocated ? allocated : targetInstanceId;
            return Task.FromResult(new TransferSlotReservation(true, bound));
        }

        public Task<PlayerTransferBundle> SuspendAndCaptureAsync(
            Guid transferId, ulong characterGuid, WorkerNodeId targetWorkerId, uint targetMapId, uint targetInstanceId, TransferPosition targetPosition,
            CancellationToken cancellationToken = default)
        {
            events.Add(role + ".Suspend");
            lock (_gate)
            {
                PlayerTransferBundle live = _active ?? throw new InvalidOperationException("the character is not active on this worker");
                PlayerTransferBundle captured = live with
                {
                    TransferId = transferId,
                    SourceWorkerId = workerId,
                    TargetWorkerId = targetWorkerId,
                    TargetMapId = targetMapId,
                    TargetInstanceId = targetInstanceId,
                    TargetPosition = targetPosition,
                };
                if (CaptureInvalid)
                {
                    captured = captured with { Health = 0, IsGhost = false, Corpse = null };
                }

                _active = null;
                _suspended = captured;
                _captured = captured;
                _suspendedForInstance = targetInstanceId;
                return Task.FromResult(captured);
            }
        }

        public Task ResumeAfterAbortAsync(Guid transferId, ulong characterGuid, CharacterOwnershipToken ownershipToken, string abortReason, CancellationToken cancellationToken = default)
        {
            events.Add(role + ".Resume");
            lock (_gate)
            {
                if (_suspended is not null)
                {
                    _active = _suspended;
                    _suspended = null;
                }
            }

            return Task.CompletedTask;
        }

        public Task RetireSourcePlayerAsync(Guid transferId, ulong characterGuid, CharacterOwnershipToken supersededToken, CancellationToken cancellationToken = default)
        {
            events.Add(role + ".Retire");
            lock (_gate)
            {
                _suspended = null;
                _active = null;
            }

            return Task.CompletedTask;
        }

        public async Task InstallPassiveShadowAsync(Guid transferId, PlayerTransferBundle bundle, CancellationToken cancellationToken = default)
        {
            events.Add(role + ".Install");
            lock (_gate)
            {
                _installed = bundle;
            }

            InstallStarted.TrySetResult();
            if (InstallGate is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }
        }

        public Task DiscardPassiveShadowAsync(Guid transferId, ulong characterGuid, string abortReason, CancellationToken cancellationToken = default)
        {
            events.Add(role + ".Discard");
            lock (_gate)
            {
                _installed = null;
            }

            return Task.CompletedTask;
        }

        public Task ActivateTargetPlayerAsync(Guid transferId, PlayerTransferBundle bundle, CharacterOwnershipToken targetOwnershipToken, CancellationToken cancellationToken = default)
        {
            events.Add(role + ".Activate");
            lock (_gate)
            {
                _active = bundle;
            }

            return Task.CompletedTask;
        }
    }
}
