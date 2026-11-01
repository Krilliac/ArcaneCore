using System.Collections.Concurrent;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Transfer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>
/// Target-side instance allocation of a transfer (orchestrator decision Q8, hardening C6b) over a real <see cref="WorldRuntime"/>, a real <see cref="InstanceManager"/> and the production
/// <see cref="InstanceManagerTransferAllocator"/> (the allocator <c>TransferHosting</c> wires): the reservation is undone when the post-allocation re-check refuses, when the slot race is lost and
/// on discard; the bind is deferred to activation (while the source owns the character the worker write fence refuses the target's bind write); a repeated reserve answers the allocated id; a
/// continent request never visits the world thread. The shadow loader and session entry are recording doubles, as in <see cref="WorkerTransferTargetTests"/>.
/// </summary>
public sealed class TargetInstanceAllocationTests : IDisposable
{
    private const uint Dungeon = 33;
    private const ulong Character = 7;

    private static readonly CharacterOwnershipToken SourceToken = new("world", 3, Guid.NewGuid());
    private static readonly CharacterOwnershipToken TargetToken = new("world", 4, Guid.NewGuid());
    private static readonly WorkerNodeId Source = new("worker-a");
    private static readonly WorkerNodeId Target = new("worker-b");

    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
            new MapTemplate(Dungeon, 0, MapType.Instance, 0, 5, 0, 0, 0, 0, "Test Dungeon", ""),
        ],
        [], [], [], []);

    private readonly WorldRuntime _world;
    private readonly RecordingSaveQueue _saveQueue = new();
    private readonly TeleportService _teleports;
    private readonly ConcurrentQueue<string> _events = new();
    private readonly RecordingSession _session = new(5, 9);
    private readonly InstanceBinds _persistence = new();
    private readonly InstanceManager _instances;
    private readonly FakeLoader _loader;
    private readonly FakeEntry _entry;

    public TargetInstanceAllocationTests()
    {
        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, _saveQueue, NullLogger<WorldRuntime>.Instance)
        {
            CharacterOwnership = TargetToken,
        };
        WorldMaps.Of(_world).Load(Content);
        _teleports = new TeleportService(_world, _ => _events.Enqueue("before"), _ => _events.Enqueue("after"));
        _world.PlayerLoggingOut += _teleports.Forget;
        _instances = new InstanceManager(_world, new InstanceOptions(), _persistence);
        _instances.Install();
        _instances.Load(InstanceStoreSnapshot.Empty);
        _world.Start();
        _loader = new FakeLoader(_session);
        _entry = new FakeEntry(_events);
    }

    public void Dispose() => _world.Stop();

    private SpyAllocator NewSpy() => new(new InstanceManagerTransferAllocator(_instances));

    private WorkerTransferTarget NewTarget(ITransferInstanceAllocator allocator)
        => new(Target, _world, _loader, _entry, instances: allocator);

    private static PlayerTransferBundle NewBundle(Guid id, uint instanceId) => new()
    {
        TransferId = id,
        CharacterGuid = Character,
        CharacterId = (int)Character,
        AccountId = 5,
        SessionGeneration = 9,
        SourceWorkerId = Source,
        TargetWorkerId = Target,
        SourceOwnershipToken = SourceToken,
        TargetMapId = Dungeon,
        TargetInstanceId = instanceId,
        TargetPosition = new TransferPosition(10f, 20f, 30f, 1f),
        Health = 100,
        Powers = [0, 0, 0, 0, 0],
        Level = 40,
    };

    private async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await _world.InvokeAsync(condition))
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    private Task WaitUntilGoneAsync(uint instanceId)
        => WaitForAsync(() => _instances.FindSave(instanceId) is null && _world.FindMap(Dungeon, instanceId) is null, $"instance {instanceId} to be dropped");

    // ---- (5) the real allocator path, and (1) the bind is deferred

    [Fact]
    public async Task Reserve_ThroughTheRealInstanceManager_AllocatesAHeldInstanceAndWritesNoBind()
    {
        WorkerTransferTarget target = NewTarget(NewSpy());

        TransferSlotReservation reservation = await target.ReserveSlotAsync(Guid.NewGuid(), Character, Dungeon, 0);

        Assert.True(reservation.Reserved);
        Assert.True(reservation.InstanceId >= InstanceManager.FirstInstanceId);
        (bool saved, bool bound, bool map, uint timer) = await _world.InvokeAsync(() =>
        {
            Map? created = _world.FindMap(Dungeon, reservation.InstanceId);
            return (
                _instances.FindSave(reservation.InstanceId) is not null,
                _instances.GetPlayerBind(new ObjectGuid(Character), Dungeon) is not null,
                created is not null,
                created is null ? uint.MaxValue : _instances.StateOf(created)!.UnloadTimerMs);
        });
        Assert.True(saved);
        Assert.True(map);
        Assert.False(bound); // while the source owns the character, the worker write fence refuses the target's bind write: the bind waits for activation
        Assert.Equal(0u, timer); // held: it does not unload on its timer before the transfer commits or aborts
        Assert.Empty(_persistence.Binds);
    }

    [Fact]
    public async Task Activate_BindsTheCharacterToTheReservedInstance_AfterTheCommit()
    {
        WorkerTransferTarget target = NewTarget(NewSpy());
        Guid id = Guid.NewGuid();
        TransferSlotReservation reservation = await target.ReserveSlotAsync(id, Character, Dungeon, 0);
        await target.InstallPassiveShadowAsync(id, NewBundle(id, reservation.InstanceId));
        Assert.Empty(_persistence.Binds);

        await target.ActivateTargetPlayerAsync(id, NewBundle(id, reservation.InstanceId), TargetToken);

        InstanceBind? bind = await _world.InvokeAsync(() => _instances.GetPlayerBind(new ObjectGuid(Character), Dungeon));
        Assert.NotNull(bind);
        Assert.Equal(reservation.InstanceId, bind.Value.Save.InstanceId);
        Assert.False(bind.Value.Permanent);
        Assert.Contains(((uint)Character, reservation.InstanceId, false), _persistence.Binds);
        Assert.True(target.WasActivated(id));
    }

    // ---- (2) a repeated reserve answers the allocated id

    [Fact]
    public async Task RepeatedReserve_AnswersTheAllocatedInstance_BeforeAndAfterActivation()
    {
        SpyAllocator spy = NewSpy();
        WorkerTransferTarget target = NewTarget(spy);
        Guid id = Guid.NewGuid();

        TransferSlotReservation first = await target.ReserveSlotAsync(id, Character, Dungeon, 0);
        TransferSlotReservation replay = await target.ReserveSlotAsync(id, Character, Dungeon, 0);
        Assert.Equal(first, replay);
        Assert.Equal(1, spy.Reserves);

        await target.ActivateTargetPlayerAsync(id, NewBundle(id, first.InstanceId), TargetToken);
        TransferSlotReservation afterActivation = await target.ReserveSlotAsync(id, Character, Dungeon, 0);

        Assert.True(afterActivation.Reserved);
        Assert.Equal(first.InstanceId, afterActivation.InstanceId); // not the requested 0
        Assert.Equal(1, spy.Reserves);
    }

    // ---- (4) continents skip the world thread

    [Fact]
    public async Task ContinentRequest_IsAnsweredWithoutVisitingTheWorldThreadAllocator()
    {
        SpyAllocator spy = NewSpy();
        WorkerTransferTarget target = NewTarget(spy);

        TransferSlotReservation reservation = await target.ReserveSlotAsync(Guid.NewGuid(), Character, 1, 0);

        Assert.Equal(new TransferSlotReservation(true, 0), reservation);
        Assert.Equal(1, spy.IsInstancedCalls);
        Assert.Equal(0, spy.Reserves);
        Assert.Equal(0, spy.Releases);
    }

    [Fact]
    public async Task DungeonRequest_ReservesOnTheWorldThreadOnce()
    {
        SpyAllocator spy = NewSpy();
        WorkerTransferTarget target = NewTarget(spy);

        TransferSlotReservation reservation = await target.ReserveSlotAsync(Guid.NewGuid(), Character, Dungeon, 0);

        Assert.True(reservation.Reserved);
        Assert.Equal(1, spy.Reserves);
        Assert.Equal(1, spy.IsInstancedCalls);
    }

    // ---- (1) the reservation is undone

    [Fact]
    public async Task PostAllocationRecheckRefusal_UndoesTheReservedInstance()
    {
        SpyAllocator spy = NewSpy();
        // The worker stops owning instance copies of the dungeon between the allocation and the re-check.
        spy.AfterReserve = () => _world.Ownership = new MapOwnership([new MapOwnership.Range(0, 0)], allowInstanceMaps: false);
        WorkerTransferTarget target = NewTarget(spy);

        TransferSlotReservation reservation = await target.ReserveSlotAsync(Guid.NewGuid(), Character, Dungeon, 0);

        Assert.False(reservation.Reserved);
        Assert.Equal(1, spy.Releases);
        Assert.Equal(0, target.ReservedCount);
        await WaitUntilGoneAsync(spy.LastReserved);
    }

    [Fact]
    public async Task LostSlotRace_UndoesTheLosingAllocation()
    {
        SpyAllocator spy = NewSpy();
        WorkerTransferTarget target = NewTarget(spy);
        // While the allocation runs on the world thread, another transfer of the same character takes the slot (a continent request needs no world thread, so it completes on the pool).
        spy.AfterReserve = () => Assert.True(Task.Run(() => target.TryReserveSlotAsync(Guid.NewGuid(), Character, 1, 0)).GetAwaiter().GetResult());

        TransferSlotReservation reservation = await target.ReserveSlotAsync(Guid.NewGuid(), Character, Dungeon, 0);

        Assert.False(reservation.Reserved);
        Assert.Equal(1, spy.Releases);
        Assert.Equal(1, target.ReservedCount); // only the winner holds a slot
        await WaitUntilGoneAsync(spy.LastReserved);
    }

    [Fact]
    public async Task Discard_UndoesTheReservedInstance_AndRearmsItsUnloadTimer()
    {
        SpyAllocator spy = NewSpy();
        WorkerTransferTarget target = NewTarget(spy);
        Guid id = Guid.NewGuid();
        TransferSlotReservation reservation = await target.ReserveSlotAsync(id, Character, Dungeon, 0);

        await target.DiscardPassiveShadowAsync(id, Character, "test abort");
        await target.DiscardPassiveShadowAsync(id, Character, "again"); // idempotent

        Assert.Equal(0, target.ReservedCount);
        Assert.Equal(1, spy.Releases);
        await WaitUntilGoneAsync(reservation.InstanceId);
        Assert.Empty(_persistence.Binds);
    }

    [Fact]
    public async Task Activate_WhenTheClientLeftBeforeActivation_ReleasesTheReservedInstance()
    {
        SpyAllocator spy = NewSpy();
        WorkerTransferTarget target = NewTarget(spy);
        Guid id = Guid.NewGuid();
        TransferSlotReservation reservation = await target.ReserveSlotAsync(id, Character, Dungeon, 0);
        await target.InstallPassiveShadowAsync(id, NewBundle(id, reservation.InstanceId));
        _entry.AttachResult = false;

        await target.ActivateTargetPlayerAsync(id, NewBundle(id, reservation.InstanceId), TargetToken);

        Assert.Equal(1, spy.Releases);
        await WaitUntilGoneAsync(reservation.InstanceId); // otherwise the held map (unload timer 0) would stay loaded until restart
        Assert.Empty(_persistence.Binds);
    }

    [Fact]
    public async Task Discard_LeavesAnInstanceTheCharacterIsBoundTo()
    {
        WorkerTransferTarget target = NewTarget(NewSpy());
        Guid id = Guid.NewGuid();
        TransferSlotReservation reservation = await target.ReserveSlotAsync(id, Character, Dungeon, 0);
        await _world.InvokeAsync(() =>
        {
            _instances.BindForTest(new ObjectGuid(Character), _instances.FindSave(reservation.InstanceId)!);
            return true;
        });

        await target.DiscardPassiveShadowAsync(id, Character, "test abort");

        (bool saved, uint timer) = await _world.InvokeAsync(() => (
            _instances.FindSave(reservation.InstanceId) is not null,
            _instances.StateOf(_world.FindMap(Dungeon, reservation.InstanceId)!)!.UnloadTimerMs));
        Assert.True(saved);
        Assert.Equal(0u, timer); // no unload was requested for the bound instance
    }

    [Fact]
    public async Task ReleaseEntryInstance_IsIdempotent_AndIgnoresUnknownInstances()
    {
        (uint released, bool second, bool unknownBind) = await _world.InvokeAsync(() =>
        {
            uint id = _instances.ReserveEntryInstance(new ObjectGuid(Character), Dungeon);
            _instances.ReleaseEntryInstance(Dungeon, id);
            _instances.ReleaseEntryInstance(Dungeon, id);
            _instances.ReleaseEntryInstance(Dungeon, 987654);
            return (id, _instances.FindSave(id) is not null, _instances.BindReservedEntry(new ObjectGuid(Character), Dungeon, 987654));
        });

        Assert.True(released >= InstanceManager.FirstInstanceId);
        Assert.True(second); // the map unloads after the current map pass, then the unbound save is deleted
        Assert.False(unknownBind);
        await WaitUntilGoneAsync(released);
    }

    [Fact]
    public async Task ReserveEntryInstance_ForAContinent_IsNotInstanced()
    {
        (bool continent, bool dungeon, uint id) = await _world.InvokeAsync(() => (
            _instances.IsInstancedMap(1),
            _instances.IsInstancedMap(Dungeon),
            _instances.ReserveEntryInstance(new ObjectGuid(Character), 1)));

        Assert.False(continent);
        Assert.True(dungeon);
        Assert.Equal(0u, id);
    }

    /// <summary>The production allocator with call counts and a hook that runs on the world thread right after a reservation.</summary>
    private sealed class SpyAllocator(ITransferInstanceAllocator inner) : ITransferInstanceAllocator
    {
        private int _reserves;
        private int _releases;
        private int _isInstanced;
        private uint _lastReserved;

        public int Reserves => Volatile.Read(ref _reserves);

        public int Releases => Volatile.Read(ref _releases);

        public int IsInstancedCalls => Volatile.Read(ref _isInstanced);

        public uint LastReserved => Volatile.Read(ref _lastReserved);

        public Action? AfterReserve { get; set; }

        public bool IsInstanced(uint mapId)
        {
            Interlocked.Increment(ref _isInstanced);
            return inner.IsInstanced(mapId);
        }

        public uint Reserve(ObjectGuid player, uint mapId)
        {
            Interlocked.Increment(ref _reserves);
            uint id = inner.Reserve(player, mapId);
            Volatile.Write(ref _lastReserved, id);
            Action? hook = AfterReserve;
            AfterReserve = null;
            hook?.Invoke();
            return id;
        }

        public void Release(uint mapId, uint instanceId)
        {
            Interlocked.Increment(ref _releases);
            inner.Release(mapId, instanceId);
        }

        public bool Bind(ObjectGuid player, uint mapId, uint instanceId) => inner.Bind(player, mapId, instanceId);
    }

    /// <summary>Records the player binds the instance manager asks to persist.</summary>
    private sealed class InstanceBinds : IInstancePersistence
    {
        public HashSet<(uint Character, uint Instance, bool Permanent)> Binds { get; } = [];

        public void InstanceSaved(InstanceSave save)
        {
        }

        public void InstanceDeleted(uint instanceId) => Binds.RemoveWhere(b => b.Instance == instanceId);

        public void PlayerBound(uint characterId, uint instanceId, bool permanent) => Binds.Add((characterId, instanceId, permanent));

        public void PlayerUnbound(uint characterId, uint instanceId) => Binds.RemoveWhere(b => b.Character == characterId && b.Instance == instanceId);

        public void RaidResetTimeChanged(uint mapId, long resetTime)
        {
        }

        public void PlayerEnteredInstance(uint characterId, uint mapId, uint instanceId)
        {
        }

        public void InstanceDataSaved(InstanceSave save)
        {
        }
    }

    private sealed class FakeLoader(RecordingSession session) : ITransferShadowLoader
    {
        public Task<Player> LoadAsync(PlayerTransferBundle bundle, CancellationToken cancellationToken)
        {
            var character = new CharacterRecord { Id = checked((int)bundle.CharacterGuid), AccountId = session.AccountId, Name = "Shadow" + bundle.CharacterGuid, Race = 1, Class = 1, Gender = 0, Level = 40, MapId = 0 };
            return Task.FromResult(new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session));
        }

        public void Release(Player shadow)
        {
        }
    }

    private sealed class FakeEntry(ConcurrentQueue<string> events) : ITransferSessionEntry
    {
        /// <summary>False simulates a client that left before the activation.</summary>
        public bool AttachResult { get; set; } = true;

        public bool TryAttach(Player player)
        {
            events.Enqueue("attach");
            return AttachResult;
        }

        public void ResumeSaves(Player player) => events.Enqueue("resume");
    }

    private sealed class RecordingSession(int accountId, long generation) : IPlayerSession
    {
        public int AccountId { get; } = accountId;

        public long SessionGeneration { get; } = generation;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player) { }

        public void Kick()
        {
        }

        public void OnLoggedOut() { }
    }
}

/// <summary>
/// The journal half of the instance allocation: the target's allocated id is recorded on the durable decision row (both stores), so the row, recovery and the captured bundle agree.
/// </summary>
public sealed class TargetInstanceJournalTests : IAsyncLifetime
{
    private static readonly WorkerNodeId SourceWorker = new("worker-ek");
    private static readonly WorkerNodeId TargetWorker = new("worker-dungeons");

    private readonly List<ServiceProvider> _providers = [];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-instance-journal-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (ServiceProvider provider in _providers)
        {
            await provider.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory a straggling connection still holds is left for the OS to clean.
        }
    }

    private async Task<ITransferDecisionStore> OpenStoreAsync(bool durable)
    {
        if (!durable)
        {
            return new InMemoryTransferDecisionStore();
        }

        Directory.CreateDirectory(_directory);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = $"Data Source={Path.Combine(_directory, "characters.db")}",
        }).Build();
        ServiceProvider provider = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider();
        _providers.Add(provider);
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        return provider.GetRequiredService<EfTransferDecisionStore>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RebindTargetInstance_RecordsTheAllocatedId_IsIdempotent_AndRefusesALaterStage(bool durable)
    {
        ITransferDecisionStore store = await OpenStoreAsync(durable);
        var token = new CharacterOwnershipToken("char:4242", 7, Guid.NewGuid());
        store.SeedCharacterOwnership(4242UL, token);
        Guid id = Guid.NewGuid();
        await store.CreateReservationAsync(id, 4242UL, 5, 9, SourceWorker, TargetWorker, 0, 0, 33, 0, token);

        TransferDecisionRecord rebound = await store.RebindTargetInstanceAsync(id, 117);
        TransferDecisionRecord again = await store.RebindTargetInstanceAsync(id, 117);

        Assert.Equal(117u, rebound.TargetInstanceId);
        Assert.Equal(117u, again.TargetInstanceId);
        Assert.Equal(117u, (await store.GetDecisionAsync(id))!.TargetInstanceId);

        PlayerTransferBundle bundle = PlayerTransferValidatorTests.CreateSampleValidBundle(id, 4242UL, SourceWorker, TargetWorker, token) with { TargetMapId = 33, TargetInstanceId = 117 };
        await store.AdvancePreCommitStageAsync(id, PlayerTransferStage.SuspendAndCapture, bundle);

        Assert.Equal(117u, (await store.RebindTargetInstanceAsync(id, 117)).TargetInstanceId); // the same id still answers
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RebindTargetInstanceAsync(id, 118));
        Assert.Equal(117u, (await store.GetDecisionAsync(id))!.TargetInstanceId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.RebindTargetInstanceAsync(Guid.NewGuid(), 5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coordinator_JournalsTheInstanceTheTargetAllocated_BeforeTheCapture(bool durable)
    {
        ITransferDecisionStore store = await OpenStoreAsync(durable);
        var table = new ClusterMapPlacementTable();
        var source = new SourceThatStopsAtCapture(SourceWorker);
        var target = new AllocatingTarget(TargetWorker, 117);
        table.RegisterWorker(SourceWorker, isReady: true, maxPlayerCapacity: 500, activePlayerCount: 50, incarnation: Guid.NewGuid());
        table.RegisterWorker(TargetWorker, isReady: true, maxPlayerCapacity: 500, activePlayerCount: 40, incarnation: target.Incarnation);
        table.AssignStaticMap(0, SourceWorker);
        table.AssignStaticMap(33, TargetWorker);
        var token = new CharacterOwnershipToken("char:4242", 7, Guid.NewGuid());
        store.SeedCharacterOwnership(4242UL, token);
        var coordinator = new PlayerTransferCoordinator(table, store, new UnusedGateway(), [source], [target]);
        Guid id = Guid.NewGuid();
        var request = new PlayerTransferRequest(
            id, 4242UL, 5, 9, SourceWorker, 0, 0, 33, 0, new TransferPosition(1f, 2f, 3f, 0f), token);

        await Assert.ThrowsAsync<StopAtCaptureException>(() => coordinator.PrepareTransferAsync(request));

        Assert.Equal(117u, source.RequestedInstance); // the capture (and so the bundle) names the allocated instance
        TransferDecisionRecord record = (await store.GetDecisionAsync(id))!;
        Assert.Equal(117u, record.TargetInstanceId); // and so does the durable row
        Assert.Equal(PlayerTransferStage.Reserve, record.CurrentStage);
    }

    private sealed class StopAtCaptureException : Exception;

    private sealed class SourceThatStopsAtCapture(WorkerNodeId workerId) : ITransferSourceWorker
    {
        public WorkerNodeId WorkerId { get; } = workerId;

        public uint RequestedInstance { get; private set; }

        public Task<PlayerTransferBundle> SuspendAndCaptureAsync(
            Guid transferId, ulong characterGuid, WorkerNodeId targetWorkerId, uint targetMapId, uint targetInstanceId, TransferPosition targetPosition, CancellationToken cancellationToken = default)
        {
            RequestedInstance = targetInstanceId;
            throw new StopAtCaptureException();
        }

        public Task ResumeAfterAbortAsync(Guid transferId, ulong characterGuid, CharacterOwnershipToken ownershipToken, string abortReason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RetireSourcePlayerAsync(Guid transferId, ulong characterGuid, CharacterOwnershipToken supersededToken, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class AllocatingTarget(WorkerNodeId workerId, uint allocated) : ITransferTargetWorker
    {
        public WorkerNodeId WorkerId { get; } = workerId;

        public Guid Incarnation { get; } = Guid.NewGuid();

        public Task<bool> TryReserveSlotAsync(Guid transferId, ulong characterGuid, uint targetMapId, uint targetInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<TransferSlotReservation> ReserveSlotAsync(Guid transferId, ulong characterGuid, uint targetMapId, uint targetInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(new TransferSlotReservation(true, allocated));

        public Task InstallPassiveShadowAsync(Guid transferId, PlayerTransferBundle bundle, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DiscardPassiveShadowAsync(Guid transferId, ulong characterGuid, string abortReason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ActivateTargetPlayerAsync(Guid transferId, PlayerTransferBundle bundle, CharacterOwnershipToken targetOwnershipToken, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class UnusedGateway : ITransferGatewayRouter
    {
        public Task StageWorldPortAsync(
            Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId, uint targetMapId, uint targetInstanceId, TransferPosition targetPosition, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelStagedWorldPortAsync(Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId restoredWorkerId, string abortReason, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CommitRouteSwitchAsync(Guid transferId, ulong characterGuid, long sessionGeneration, WorkerNodeId targetWorkerId, CharacterOwnershipToken targetOwnershipToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
