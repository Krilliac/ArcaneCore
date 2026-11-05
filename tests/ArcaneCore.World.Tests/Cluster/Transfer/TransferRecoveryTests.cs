using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Transfer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>
/// The startup journal sweep (<see cref="TransferRecoveryService"/>, S5e): a crash is injected after each of the six transfer stages, the
/// process state is thrown away (with the durable store: the store too, so only the database survives) and the sweep must leave exactly one
/// owner of the character, with the items and copper intact, the gateway routing to that owner and nothing left unresolved.
/// Runs against the in-memory store and the durable characters-database store (SQLite locally; the other providers run on hosted CI).
/// </summary>
public sealed class TransferRecoveryTests : IAsyncLifetime
{
    private const ulong Character = 4242UL;

    private readonly List<ServiceProvider> _providers = [];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-transfer-recovery-" + Guid.NewGuid().ToString("N"));
    private string? _connectionString;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (ServiceProvider provider in _providers)
            await provider.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory a straggling connection still holds is left for the OS to clean.
        }
    }

    private async Task<EfTransferDecisionStore> OpenDurableStoreAsync()
    {
        if (_connectionString is null)
        {
            Directory.CreateDirectory(_directory);
            _connectionString = $"Data Source={Path.Combine(_directory, "characters.db")}";
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = _connectionString,
        }).Build();
        ServiceProvider provider = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider();
        _providers.Add(provider);
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        return provider.GetRequiredService<EfTransferDecisionStore>();
    }

    private sealed class FakeWorker : ITransferSourceWorker, ITransferTargetWorker
    {
        public FakeWorker(string id)
        {
            WorkerId = new WorkerNodeId(id);
            Incarnation = Guid.NewGuid();
        }

        public WorkerNodeId WorkerId { get; }

        public Guid Incarnation { get; }

        public Dictionary<ulong, PlayerTransferBundle> Active { get; } = [];

        public Dictionary<ulong, PlayerTransferBundle> Suspended { get; } = [];

        public Dictionary<Guid, PlayerTransferBundle> Shadows { get; } = [];

        public HashSet<Guid> Slots { get; } = [];

        public Dictionary<ulong, CharacterOwnershipToken> Held { get; } = [];

        public Task<bool> TryReserveSlotAsync(Guid transferId, ulong characterGuid, uint targetMapId, uint targetInstanceId, CancellationToken cancellationToken = default)
        {
            Slots.Add(transferId);
            return Task.FromResult(true);
        }

        public Task<PlayerTransferBundle> SuspendAndCaptureAsync(
            Guid transferId, ulong characterGuid, WorkerNodeId targetWorkerId, uint targetMapId, uint targetInstanceId,
            TransferPosition targetPosition, CancellationToken cancellationToken = default)
        {
            if (!Active.Remove(characterGuid, out PlayerTransferBundle? live))
                throw new InvalidOperationException($"Character {characterGuid} is not active on {WorkerId}.");

            PlayerTransferBundle captured = live with
            {
                TransferId = transferId,
                SourceWorkerId = WorkerId,
                TargetWorkerId = targetWorkerId,
                TargetMapId = targetMapId,
                TargetInstanceId = targetInstanceId,
                TargetPosition = targetPosition,
            };
            Suspended[characterGuid] = captured;
            return Task.FromResult(captured);
        }

        public Task InstallPassiveShadowAsync(Guid transferId, PlayerTransferBundle bundle, CancellationToken cancellationToken = default)
        {
            Slots.Add(transferId);
            Shadows[transferId] = bundle;
            return Task.CompletedTask;
        }

        public Task DiscardPassiveShadowAsync(Guid transferId, ulong characterGuid, string abortReason, CancellationToken cancellationToken = default)
        {
            Shadows.Remove(transferId);
            Slots.Remove(transferId);
            return Task.CompletedTask;
        }

        public Task ResumeAfterAbortAsync(Guid transferId, ulong characterGuid, CharacterOwnershipToken ownershipToken, string abortReason, CancellationToken cancellationToken = default)
        {
            if (Suspended.Remove(characterGuid, out PlayerTransferBundle? suspended))
            {
                Active[characterGuid] = suspended;
                Held[characterGuid] = ownershipToken;
            }

            return Task.CompletedTask;
        }

        public Task RetireSourcePlayerAsync(Guid transferId, ulong characterGuid, CharacterOwnershipToken supersededToken, CancellationToken cancellationToken = default)
        {
            Active.Remove(characterGuid);
            Suspended.Remove(characterGuid);
            Held.Remove(characterGuid);
            return Task.CompletedTask;
        }

        /// <summary>Makes the next activations throw, after the source was already retired (a crash inside stage 6).</summary>
        public int FailActivateTimes { get; set; }

        public Task ActivateTargetPlayerAsync(Guid transferId, PlayerTransferBundle bundle, CharacterOwnershipToken targetOwnershipToken, CancellationToken cancellationToken = default)
        {
            if (FailActivateTimes > 0)
            {
                FailActivateTimes--;
                throw new InvalidOperationException("simulated-crash-in-activate");
            }

            Shadows.Remove(transferId);
            Slots.Remove(transferId);
            Active[bundle.CharacterGuid] = bundle;
            Held[bundle.CharacterGuid] = targetOwnershipToken;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGateway : ITransferGatewayRouter, IStagedRouteSource
    {
        private readonly Dictionary<Guid, StagedWorldPortRoute> _staged = [];

        public Dictionary<ulong, WorkerNodeId> Routes { get; } = [];

        public int FailCancelTimes { get; set; }

        public int Cancels { get; private set; }

        /// <summary>Makes the next route switches throw (a crash inside stage 6, after the target was activated).</summary>
        public int FailRouteSwitchTimes { get; set; }

        public bool IsStaged(Guid transferId) => _staged.ContainsKey(transferId);

        public void Stage(StagedWorldPortRoute route) => _staged[route.TransferId] = route;

        public IReadOnlyList<StagedWorldPortRoute> ListStagedRoutes() => [.. _staged.Values];

        public Task StageWorldPortAsync(
            Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId,
            uint targetMapId, uint targetInstanceId, TransferPosition targetPosition, CancellationToken cancellationToken = default)
        {
            _staged[transferId] = new StagedWorldPortRoute(transferId, characterGuid, sessionGeneration, sourceWorkerId);
            return Task.CompletedTask;
        }

        public Task CancelStagedWorldPortAsync(
            Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId restoredWorkerId, string abortReason, CancellationToken cancellationToken = default)
        {
            Cancels++;
            if (FailCancelTimes > 0)
            {
                FailCancelTimes--;
                throw new InvalidOperationException("gateway link is down");
            }

            _staged.Remove(transferId);
            Routes[characterGuid] = restoredWorkerId;
            return Task.CompletedTask;
        }

        public Task CommitRouteSwitchAsync(
            Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId targetWorkerId, CharacterOwnershipToken targetOwnershipToken,
            CancellationToken cancellationToken = default)
        {
            if (FailRouteSwitchTimes > 0)
            {
                FailRouteSwitchTimes--;
                throw new InvalidOperationException("simulated-crash-in-route-switch");
            }

            _staged.Remove(transferId);
            Routes[characterGuid] = targetWorkerId;
            return Task.CompletedTask;
        }
    }

    private sealed record Fixture(
        ClusterMapPlacementTable MapTable,
        ITransferDecisionStore Store,
        FakeGateway Gateway,
        FakeWorker Source,
        FakeWorker Target,
        PlayerTransferCoordinator Coordinator,
        PlayerTransferRequest Request,
        PlayerTransferBundle Seeded);

    private async Task<Fixture> CreateFixtureAsync(bool durable)
    {
        var mapTable = new ClusterMapPlacementTable();
        ITransferDecisionStore store = durable ? await OpenDurableStoreAsync() : new InMemoryTransferDecisionStore();
        var gateway = new FakeGateway();
        var source = new FakeWorker("worker-ek");
        var target = new FakeWorker("worker-kalimdor");

        mapTable.RegisterWorker(source.WorkerId, isReady: true, maxPlayerCapacity: 500, activePlayerCount: 50, incarnation: source.Incarnation);
        mapTable.RegisterWorker(target.WorkerId, isReady: true, maxPlayerCapacity: 500, activePlayerCount: 40, incarnation: target.Incarnation);
        mapTable.AssignStaticMap(0, source.WorkerId);
        mapTable.AssignStaticMap(1, target.WorkerId);

        var sourceToken = new CharacterOwnershipToken("char:4242", 7, source.Incarnation);
        await store.SeedCharacterOwnershipAsync(Character, sourceToken);
        gateway.Routes[Character] = source.WorkerId;

        Guid transferId = Guid.NewGuid();
        PlayerTransferBundle seeded = PlayerTransferValidatorTests.CreateSampleValidBundle(
            transferId: transferId, characterGuid: Character, sourceWorker: source.WorkerId, targetWorker: target.WorkerId, sourceToken: sourceToken);
        source.Active[Character] = seeded;
        source.Held[Character] = sourceToken;

        var coordinator = new PlayerTransferCoordinator(mapTable, store, gateway, [source], [target]);
        var request = new PlayerTransferRequest(
            TransferId: transferId,
            CharacterGuid: Character,
            AccountId: seeded.AccountId,
            SessionGeneration: seeded.SessionGeneration,
            SourceWorkerId: source.WorkerId,
            SourceMapId: 0,
            SourceInstanceId: 0,
            TargetMapId: 1,
            TargetInstanceId: 0,
            TargetPosition: seeded.TargetPosition,
            SourceOwnershipToken: sourceToken);
        return new Fixture(mapTable, store, gateway, source, target, coordinator, request, seeded);
    }

    /// <summary>A restarted process: the same fake workers and gateway, a brand-new store on the same database when durable, a new coordinator and a new service.</summary>
    private async Task<(TransferRecoveryService Service, ITransferDecisionStore Store)> RestartAsync(Fixture f, int maxAttempts = 3)
    {
        ITransferDecisionStore store = f.Store is EfTransferDecisionStore ? await OpenDurableStoreAsync() : f.Store;
        var coordinator = new PlayerTransferCoordinator(f.MapTable, store, f.Gateway, [f.Source], [f.Target]);
        var service = new TransferRecoveryService(
            coordinator, store, (ITransferJournalScan)store, f.Gateway, NullLogger.Instance, maxAttempts, retryDelay: TimeSpan.Zero);
        return (service, store);
    }

    private static async Task AssertExactlyOneOwner(Fixture f, ITransferDecisionStore store, FakeWorker expectedOwner, FakeWorker other, long expectedEpoch)
    {
        // Exactly one worker holds the player active, and not suspended anywhere.
        Assert.Equal(1, f.Source.Active.Count + f.Target.Active.Count);
        Assert.True(expectedOwner.Active.ContainsKey(Character));
        Assert.False(other.Active.ContainsKey(Character));
        Assert.Empty(f.Source.Suspended);
        Assert.Empty(f.Target.Suspended);
        Assert.Empty(f.Target.Shadows);
        Assert.Empty(f.Target.Slots);

        // The durable owner is that worker's token, at the expected epoch.
        CharacterOwnershipToken? durable = await store.GetActiveOwnershipAsync(Character);
        Assert.NotNull(durable);
        Assert.Equal(expectedEpoch, durable!.Epoch);
        Assert.Equal(expectedOwner.Incarnation, durable.Incarnation);
        Assert.Equal(durable, expectedOwner.Held[Character]);
        Assert.False(other.Held.ContainsKey(Character));

        // The gateway routes to the owner and holds no staged route.
        Assert.Equal(expectedOwner.WorkerId, f.Gateway.Routes[Character]);
        Assert.False(f.Gateway.IsStaged(f.Request.TransferId));

        // No lost items or copper.
        PlayerTransferBundle live = expectedOwner.Active[Character];
        Assert.Equal(f.Seeded.Money, live.Money);
        Assert.Equal(f.Seeded.Items.Count, live.Items.Count);
        Assert.Equal(f.Seeded.Items.Select(i => (i.ItemGuid, i.Entry, i.Count)), live.Items.Select(i => (i.ItemGuid, i.Entry, i.Count)));
    }

    public static TheoryData<PlayerTransferStage, bool> CrashCases()
    {
        var data = new TheoryData<PlayerTransferStage, bool>();
        foreach (bool durable in new[] { false, true })
        {
            data.Add(PlayerTransferStage.Reserve, durable);
            data.Add(PlayerTransferStage.SuspendAndCapture, durable);
            data.Add(PlayerTransferStage.InstallPassive, durable);
            data.Add(PlayerTransferStage.StageWorldPort, durable);
            data.Add(PlayerTransferStage.CommitDecision, durable);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CrashCases))]
    public async Task StartupSweep_AfterCrashAtEachStage_LeavesExactlyOneOwner_WithoutLosingItemsOrCopper(PlayerTransferStage crashAt, bool durable)
    {
        Fixture f = await CreateFixtureAsync(durable);

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.ExecuteTransferAsync(
            f.Request,
            afterStageHook: (stage, _, _) => stage == crashAt ? throw new InvalidOperationException("simulated-crash") : Task.CompletedTask));

        (TransferRecoveryService service, ITransferDecisionStore store) = await RestartAsync(f);
        TransferRecoverySummary summary = await service.RunStartupSweepAsync(localWorker: null, stagedRoutes: f.Gateway);

        bool committed = crashAt == PlayerTransferStage.CommitDecision;
        Assert.Equal(0, summary.Failed);
        Assert.Equal(committed ? new TransferRecoverySummary(1, 1, 0, 0, 0) : new TransferRecoverySummary(1, 0, 1, 0, 0), summary);

        if (committed)
            await AssertExactlyOneOwner(f, store, f.Target, f.Source, f.Request.SourceOwnershipToken.Epoch + 1);
        else
            await AssertExactlyOneOwner(f, store, f.Source, f.Target, f.Request.SourceOwnershipToken.Epoch);

        Assert.Equal(0, await store.CountUnresolvedAsync());
        Assert.Empty(await ((ITransferJournalScan)store).ListUnresolvedAsync());

        // A second sweep finds nothing to do and changes nothing.
        TransferRecoverySummary again = await service.RunStartupSweepAsync(localWorker: null, stagedRoutes: f.Gateway);
        Assert.Equal(TransferRecoverySummary.Empty, again);
    }

    /// <summary>
    /// Stage 6 (ActivateAndRetire) crashed halfway, with the record still Committed: the source is already retired and either the target
    /// was never activated (activate throws) or it was but the gateway route was not switched (route switch throws).
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task StartupSweep_AfterCrashInsideStage6_RollsForwardToExactlyOneOwner_WithoutLosingItemsOrCopper(bool durable, bool crashInActivate)
    {
        Fixture f = await CreateFixtureAsync(durable);
        if (crashInActivate)
            f.Target.FailActivateTimes = 1;
        else
            f.Gateway.FailRouteSwitchTimes = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.ExecuteTransferAsync(f.Request));

        // The crash left the record Committed, not Completed, with the source already retired.
        Assert.Equal(1, await f.Store.CountUnresolvedAsync());
        Assert.Empty(f.Source.Active);
        Assert.Empty(f.Source.Suspended);

        (TransferRecoveryService service, ITransferDecisionStore store) = await RestartAsync(f);
        TransferRecoverySummary summary = await service.RunStartupSweepAsync(localWorker: null, stagedRoutes: f.Gateway);

        Assert.Equal(new TransferRecoverySummary(1, 1, 0, 0, 0), summary);
        await AssertExactlyOneOwner(f, store, f.Target, f.Source, f.Request.SourceOwnershipToken.Epoch + 1);
        Assert.Equal(0, await store.CountUnresolvedAsync());
        Assert.Empty(await ((ITransferJournalScan)store).ListUnresolvedAsync());

        TransferRecoverySummary again = await service.RunStartupSweepAsync(localWorker: null, stagedRoutes: f.Gateway);
        Assert.Equal(TransferRecoverySummary.Empty, again);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupSweep_WorkerSweep_OnlyHandlesTransfersItTakesPartIn(bool durable)
    {
        Fixture f = await CreateFixtureAsync(durable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.ExecuteTransferAsync(
            f.Request,
            afterStageHook: (stage, _, _) => stage == PlayerTransferStage.InstallPassive ? throw new InvalidOperationException("simulated-crash") : Task.CompletedTask));

        (TransferRecoveryService service, ITransferDecisionStore store) = await RestartAsync(f);

        TransferRecoverySummary stranger = await service.RunStartupSweepAsync(new WorkerNodeId("worker-elsewhere"));
        Assert.Equal(TransferRecoverySummary.Empty, stranger);
        Assert.Equal(1, await store.CountUnresolvedAsync());

        TransferRecoverySummary participant = await service.RunStartupSweepAsync(f.Target.WorkerId);
        Assert.Equal(new TransferRecoverySummary(1, 0, 1, 0, 0), participant);
        await AssertExactlyOneOwner(f, store, f.Source, f.Target, f.Request.SourceOwnershipToken.Epoch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupSweep_WhenGatewayCancelFailsOnce_RetriesFromDurableStatus_AndStillRollsBack(bool durable)
    {
        Fixture f = await CreateFixtureAsync(durable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.ExecuteTransferAsync(
            f.Request,
            afterStageHook: (stage, _, _) => stage == PlayerTransferStage.StageWorldPort ? throw new InvalidOperationException("simulated-crash") : Task.CompletedTask));

        // The first attempt aborts the record durably and then fails at the gateway; the retry sees "Aborted" and repeats the cleanup.
        f.Gateway.FailCancelTimes = 1;
        (TransferRecoveryService service, ITransferDecisionStore store) = await RestartAsync(f);
        TransferRecoverySummary summary = await service.RunStartupSweepAsync();

        Assert.Equal(new TransferRecoverySummary(1, 0, 1, 0, 0), summary);
        Assert.Equal(2, f.Gateway.Cancels);
        await AssertExactlyOneOwner(f, store, f.Source, f.Target, f.Request.SourceOwnershipToken.Epoch);
    }

    [Fact]
    public async Task StartupSweep_WhenEveryAttemptFails_ReportsFailure_AndKeepsOtherTransfersGoing()
    {
        Fixture f = await CreateFixtureAsync(durable: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.ExecuteTransferAsync(
            f.Request,
            afterStageHook: (stage, _, _) => stage == PlayerTransferStage.StageWorldPort ? throw new InvalidOperationException("simulated-crash") : Task.CompletedTask));

        f.Gateway.FailCancelTimes = 10;
        (TransferRecoveryService service, _) = await RestartAsync(f, maxAttempts: 2);
        TransferRecoverySummary summary = await service.RunStartupSweepAsync();

        Assert.Equal(new TransferRecoverySummary(1, 0, 0, 1, 0), summary);
        Assert.Equal(2, f.Gateway.Cancels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagedRouteSweep_DropsOrphans_AndKeepsRoutesOfUnresolvedTransfers(bool durable)
    {
        Fixture f = await CreateFixtureAsync(durable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.ExecuteTransferAsync(
            f.Request,
            afterStageHook: (stage, _, _) => stage == PlayerTransferStage.StageWorldPort ? throw new InvalidOperationException("simulated-crash") : Task.CompletedTask));
        Assert.True(f.Gateway.IsStaged(f.Request.TransferId));

        var orphan = new StagedWorldPortRoute(Guid.NewGuid(), 777UL, 99, f.Source.WorkerId);
        f.Gateway.Stage(orphan);

        (TransferRecoveryService service, ITransferDecisionStore store) = await RestartAsync(f);
        int dropped = await service.SweepStagedRoutesAsync(f.Gateway);

        Assert.Equal(1, dropped);
        Assert.False(f.Gateway.IsStaged(orphan.TransferId));
        Assert.Equal(f.Source.WorkerId, f.Gateway.Routes[777UL]);
        Assert.True(f.Gateway.IsStaged(f.Request.TransferId)); // still unresolved: the journal recovery owns it
        Assert.Equal(1, await store.CountUnresolvedAsync());

        // After the journal recovery aborts it, its staged route is gone with it.
        await service.RecoverUnresolvedAsync();
        Assert.False(f.Gateway.IsStaged(f.Request.TransferId));
    }
}
