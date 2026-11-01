using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>
/// The production target worker of a transfer over a real <see cref="WorldRuntime"/> and a real <see cref="CharacterSaveQueue"/>, with the
/// shadow loader and the session entry replaced by recording doubles (the production ones need a gateway-backed <c>WorldSession</c>).
/// </summary>
public sealed class WorkerTransferTargetTests : IDisposable
{
    private static readonly CharacterOwnershipToken SourceToken = new("world", 3, Guid.NewGuid());
    private static readonly CharacterOwnershipToken TargetToken = new("world", 4, Guid.NewGuid());
    private static readonly WorkerNodeId Source = new("worker-a");
    private static readonly WorkerNodeId Target = new("worker-b");

    private readonly WorldRuntime _world;
    private readonly ServiceProvider _storeServices;
    private readonly CharacterSaveQueue _queue;
    private readonly ConcurrentQueue<CharacterState> _saved = new();
    private readonly RecordingSession _session = new(5, 9);
    private readonly FakeLoader _loader;
    private readonly FakeEntry _entry = new();

    public WorkerTransferTargetTests()
    {
        var store = new InMemoryCharacterStore
        {
            SaveOverride = state =>
            {
                _saved.Enqueue(state);
                return Task.CompletedTask;
            },
        };
        _storeServices = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        _queue = new CharacterSaveQueue(_storeServices.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new RecordingSaveQueue(), NullLogger<WorldRuntime>.Instance)
        {
            CharacterOwnership = TargetToken,
        };
        _world.Start();
        _loader = new FakeLoader(_session);
    }

    public void Dispose()
    {
        _world.Stop();
        _queue.Dispose();
        _storeServices.Dispose();
    }

    private WorkerTransferTarget NewTarget(WorkerAdmission? admission = null, int maxReservations = 64)
        => new(Target, _world, _loader, _entry, admission, maxReservations);

    private static PlayerTransferBundle NewBundle(Guid id, ulong characterGuid = 7) => new()
    {
        TransferId = id,
        CharacterGuid = characterGuid,
        CharacterId = (int)characterGuid,
        AccountId = 5,
        SessionGeneration = 9,
        SourceWorkerId = Source,
        TargetWorkerId = Target,
        SourceOwnershipToken = SourceToken,
        TargetMapId = 1,
        TargetInstanceId = 0,
        TargetPosition = new TransferPosition(10f, 20f, 30f, 1f),
        Health = 100,
        Powers = [0, 0, 0, 0, 0],
        Level = 40,
    };

    [Fact]
    public async Task Reserve_IsIdempotentPerTransfer_AndRefusesASecondTransferOfTheSameCharacter()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();

        Assert.True(await target.TryReserveSlotAsync(id, 7, 1, 0));
        Assert.True(await target.TryReserveSlotAsync(id, 7, 1, 0));
        Assert.False(await target.TryReserveSlotAsync(Guid.NewGuid(), 7, 1, 0));
        Assert.Equal(1, target.ReservedCount);
    }

    [Fact]
    public async Task Reserve_IsRefused_WhenAdmissionIsClosed_OrTheMapIsNotOwned_OrTheCharacterIsOnline()
    {
        var admission = new WorkerAdmission(WorkerAdmission.StandbyReason);
        WorkerTransferTarget target = NewTarget(admission);
        Assert.False(await target.TryReserveSlotAsync(Guid.NewGuid(), 7, 1, 0));
        admission.Open();
        Assert.True(await target.TryReserveSlotAsync(Guid.NewGuid(), 7, 1, 0));

        _world.Ownership = new MapOwnership([new MapOwnership.Range(0, 0)], allowInstanceMaps: false);
        Assert.False(await target.TryReserveSlotAsync(Guid.NewGuid(), 8, 1, 0));
        Assert.True(await target.TryReserveSlotAsync(Guid.NewGuid(), 8, 0, 0));

        Player online = _loader.NewPlayer(9, _session);
        await _world.InvokeAsync(() =>
        {
            _world.AddPlayer(online, _world.GetMap(0));
            return true;
        });
        Assert.False(await target.TryReserveSlotAsync(Guid.NewGuid(), 9, 0, 0));
    }

    [Fact]
    public async Task ShadowLoad_WritesNothing_AndLeavesThePlayerOutOfTheWorld()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.TryReserveSlotAsync(id, 7, 1, 0);

        await target.InstallPassiveShadowAsync(id, NewBundle(id));
        await target.InstallPassiveShadowAsync(id, NewBundle(id)); // repeated: the same load

        Assert.True(target.HasShadow(id));
        Assert.Equal(1, _loader.Loads);
        Assert.Empty(_saved);
        Assert.False(_queue.IsHeld(7));
        Assert.False(_world.IsOnline(new ObjectGuid(7)));
        Assert.Equal(0, _world.OnlinePlayerCount);
        Assert.Equal(0, _entry.Attached);
        Assert.Empty(_session.Opcodes);
    }

    [Fact]
    public async Task Activate_AfterCommit_PutsThePlayerInTheMap_AtTheTargetPosition_AndSendsNewWorld()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.TryReserveSlotAsync(id, 7, 1, 0);
        await target.InstallPassiveShadowAsync(id, NewBundle(id));

        await target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken);

        Player? player = _world.FindOnlinePlayer(new ObjectGuid(7));
        Assert.NotNull(player);
        Assert.NotNull(player.Map);
        Assert.Equal(1u, player.Map.MapId);
        Assert.Equal((10f, 20f, 30f), (player.X, player.Y, player.Z));
        Assert.Equal(TargetToken, player.CharacterOwnership);
        Assert.Contains(WorldOpcode.SmsgNewWorld, _session.Opcodes);
        Assert.Equal(new[] { "attach", "before", "after" }, _entry.Calls);
        Assert.True(target.WasActivated(id));
        Assert.Equal(0, target.ReservedCount);
    }

    [Fact]
    public async Task Activate_WithoutAnEarlierInstall_LoadsTheShadowFirst()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid(); // no reserve either: the roll-forward after a target restart

        await target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken);

        Assert.True(_world.IsOnline(new ObjectGuid(7)));
        Assert.Equal(1, _loader.Loads);
    }

    [Fact]
    public async Task DuplicateActivate_IsANoOp()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.TryReserveSlotAsync(id, 7, 1, 0);
        await target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken);
        int newWorlds = _session.Opcodes.Count(op => op == WorldOpcode.SmsgNewWorld);

        await target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken);
        await target.InstallPassiveShadowAsync(id, NewBundle(id));

        Assert.Equal(1, _loader.Loads);
        Assert.Equal(1, _entry.Attached);
        Assert.Equal(newWorlds, _session.Opcodes.Count(op => op == WorldOpcode.SmsgNewWorld));
        Assert.Equal(1, _world.OnlinePlayerCount);
    }

    [Fact]
    public async Task Discard_FreesTheSlot_DropsTheShadow_AndBlocksALaterActivate()
    {
        WorkerTransferTarget target = NewTarget(maxReservations: 1);
        Guid id = Guid.NewGuid();
        Assert.True(await target.TryReserveSlotAsync(id, 7, 1, 0));
        await target.InstallPassiveShadowAsync(id, NewBundle(id));
        Assert.False(await target.TryReserveSlotAsync(Guid.NewGuid(), 8, 1, 0)); // full

        await target.DiscardPassiveShadowAsync(id, 7, "test");
        await target.DiscardPassiveShadowAsync(id, 7, "again"); // idempotent

        Assert.Equal(0, target.ReservedCount);
        Assert.False(target.HasShadow(id));
        Assert.True(await target.TryReserveSlotAsync(Guid.NewGuid(), 8, 1, 0)); // the slot is free again
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken));
        Assert.False(_world.IsOnline(new ObjectGuid(7)));
    }

    [Fact]
    public async Task Discard_AfterActivation_DoesNotRemoveThePlayer()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken);

        await target.DiscardPassiveShadowAsync(id, 7, "late");

        Assert.True(_world.IsOnline(new ObjectGuid(7)));
    }

    [Fact]
    public async Task FailedLoad_IsRetried_AndTheReservationSurvives()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.TryReserveSlotAsync(id, 7, 1, 0);
        _loader.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => target.InstallPassiveShadowAsync(id, NewBundle(id)));
        Assert.Equal(1, target.ReservedCount);
        await target.InstallPassiveShadowAsync(id, NewBundle(id));

        Assert.True(target.HasShadow(id));
        Assert.Equal(2, _loader.Loads);
    }

    [Fact]
    public async Task Install_ForABundleOfAnotherTransferOrWorker_Throws()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => target.InstallPassiveShadowAsync(id, NewBundle(Guid.NewGuid())));
        await Assert.ThrowsAsync<ArgumentException>(() => target.InstallPassiveShadowAsync(id, NewBundle(id) with { TargetWorkerId = new WorkerNodeId("other") }));
    }

    private sealed class FakeLoader(RecordingSession session) : ITransferShadowLoader
    {
        private int _loads;
        private volatile bool _failNext;

        public int Loads => Volatile.Read(ref _loads);

        public bool FailNext
        {
            get => _failNext;
            set => _failNext = value;
        }

        public Player NewPlayer(int id, IPlayerSession playerSession)
        {
            var character = new CharacterRecord { Id = id, AccountId = playerSession.AccountId, Name = "Shadow" + id, Race = 1, Class = 1, Gender = 0, Level = 40, MapId = 0 };
            return new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), playerSession);
        }

        public Task<Player> LoadAsync(PlayerTransferBundle bundle, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loads);
            if (_failNext)
            {
                _failNext = false;
                return Task.FromException<Player>(new InvalidOperationException("load failed"));
            }

            return Task.FromResult(NewPlayer(checked((int)bundle.CharacterGuid), session));
        }
    }

    private sealed class FakeEntry : ITransferSessionEntry
    {
        private int _attached;

        public List<string> Calls { get; } = [];

        public int Attached => Volatile.Read(ref _attached);

        public bool TryAttach(Player player)
        {
            Interlocked.Increment(ref _attached);
            Calls.Add("attach");
            return true;
        }

        public void SendInitialPacketsBeforeAddToMap(Player player) => Calls.Add("before");

        public void SendInitialPacketsAfterAddToMap(Player player) => Calls.Add("after");
    }

    private sealed class RecordingSession(int accountId, long generation) : IPlayerSession
    {
        private readonly ConcurrentQueue<WorldOpcode> _opcodes = new();

        public int AccountId { get; } = accountId;

        public long SessionGeneration { get; } = generation;

        public AccountSecurity Security => AccountSecurity.Player;

        public IReadOnlyCollection<WorldOpcode> Opcodes => _opcodes;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) => _opcodes.Enqueue(opcode);

        public void ProcessWorldPackets(Player player) { }

        public void Kick() { }

        public void OnLoggedOut() { }
    }
}
