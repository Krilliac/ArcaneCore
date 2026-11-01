using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Exploration;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>
/// The per-character feature queues around a transfer, run on the production <see cref="ExploredZonesPersistence"/> (a real write queue
/// behind a recording store) and the production flush barrier: the capture queues the dirty state a logout would queue before it flushes,
/// and the retire after a commit starts no feature write at all.
/// </summary>
public sealed class WorkerTransferSourceFeatureWritesTests : IDisposable
{
    private static readonly CharacterOwnershipToken Token = new("world", 3, Guid.NewGuid());
    private const int CharacterId = 7;

    private readonly ConcurrentQueue<uint[]> _exploredWrites = new();
    private readonly ServiceProvider _services;
    private readonly ServiceProvider _barrierServices;
    private readonly CharacterSaveQueue _queue;
    private readonly ExploredZonesPersistence _explored;
    private readonly WorldRuntime _world;
    private readonly WorkerTransferSource _source;
    private readonly Player _player;
    private readonly List<bool> _retireFlagSeen = [];
    private readonly ConcurrentQueue<string> _order = new();

    public WorkerTransferSourceFeatureWritesTests()
    {
        var characters = new InMemoryCharacterStore
        {
            SaveOverride = _ =>
            {
                _order.Enqueue("settlement-save");
                return Task.CompletedTask;
            },
        };
        _services = new ServiceCollection()
            .AddSingleton<ICharacterStore>(characters)
            .AddSingleton<IExploredZonesStore>(new RecordingExploredZonesStore(_exploredWrites))
            .BuildServiceProvider();
        IServiceScopeFactory scopes = _services.GetRequiredService<IServiceScopeFactory>();
        _queue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        _explored = new ExploredZonesPersistence(scopes, NullLoggerFactory.Instance);

        // The barrier resolves the queues it flushes through its provider.
        _barrierServices = new ServiceCollection()
            .AddSingleton(_queue)
            .AddSingleton(_explored)
            .BuildServiceProvider();
        var barrier = new CharacterPersistenceBarrier(_barrierServices);

        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new RecordingSaveQueue(), NullLogger<WorldRuntime>.Instance)
        {
            CharacterOwnership = Token,
        };
        _explored.Attach(_world);
        _world.PlayerLoggingOut += _ => _retireFlagSeen.Add(_world.IsRetiringTransferredPlayer);
        _world.PlayerTransferPreparing += _ => _order.Enqueue("preparing");

        _player = NewPlayer(CharacterId, "Transferee");
        _world.AddPlayer(_player, _world.GetMap(0));
        _world.Start();
        _source = new WorkerTransferSource(
            new WorkerNodeId("worker-a"),
            _world,
            new SaveQueueTransferPersistence(_queue, barrier),
            new NoBlockersProbe());
    }

    public void Dispose()
    {
        _world.Stop();
        _explored.StopAsync().GetAwaiter().GetResult();
        _queue.Dispose();
        _barrierServices.Dispose();
        _services.Dispose();
    }

    private static Player NewPlayer(int id, string name)
    {
        var session = new QuietSession(5, 9);
        var character = new CharacterRecord { Id = id, AccountId = session.AccountId, Name = name, Race = 1, Class = 1, Gender = 0, Level = 40, MapId = 0 };
        var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);
        player.Relocate(12f, 34f, 56f, 1.5f, 0);
        return player;
    }

    private Task<PlayerTransferBundle> CaptureAsync(Guid id) => _source.SuspendAndCaptureAsync(
        id, _player.Guid.Value, new WorkerNodeId("worker-b"), 1, 0, new TransferPosition(1f, 2f, 3f, 0f));

    private void SetExploredWord(uint value)
    {
        uint[] words = new uint[ExploredZones.WordCount];
        words[0] = value;
        ExploredZonesFields.Write(_player, words);
    }

    [Fact]
    public async Task Capture_QueuesTheStateAFeatureHoldsBackUntilLogout_AndItIsDurableWhenCaptureReturns()
    {
        // The words changed without the feature's sink being told (the state a debounce or logout would have written later).
        SetExploredWord(0xA5u);
        Assert.Empty(_exploredWrites);

        await CaptureAsync(Guid.NewGuid());

        Assert.Contains(_exploredWrites, words => words[0] == 0xA5u); // written by the time capture returned
        string[] order = [.. _order];
        Assert.True(Array.IndexOf(order, "preparing") >= 0);
        Assert.True(Array.IndexOf(order, "preparing") < Array.IndexOf(order, "settlement-save")); // queued before the snapshot is taken and saved
    }

    [Fact]
    public async Task Retire_StartsNoFeatureWrite_ButEveryLoggingOutSubscriberStillRuns_WithTheRetireFlagSet()
    {
        Guid id = Guid.NewGuid();
        SetExploredWord(0x11u);
        await CaptureAsync(id);
        int writesAtCommit = _exploredWrites.Count;
        Assert.Empty(_retireFlagSeen); // capture never raises the logout event

        // The detached entity changed after the capture's save (the target has loaded the row by now).
        SetExploredWord(0x22u);
        await _source.RetireSourcePlayerAsync(id, _player.Guid.Value, Token);
        await _explored.Writes.FlushAsync();

        Assert.Equal([true], _retireFlagSeen); // forgetting subscribers ran exactly once, and saw the flag
        Assert.False(_world.IsRetiringTransferredPlayer); // scoped to the retire
        Assert.Equal(writesAtCommit, _exploredWrites.Count);
        Assert.DoesNotContain(_exploredWrites, words => words[0] == 0x22u);
    }

    [Fact]
    public async Task OrdinaryLogout_StillWritesTheFeatureState_WithoutTheRetireFlag()
    {
        SetExploredWord(0x33u);
        await _world.InvokeAsync(() =>
        {
            _world.RemovePlayer(_player);
            return true;
        });
        await _explored.Writes.FlushAsync();

        Assert.Equal([false], _retireFlagSeen);
        Assert.Contains(_exploredWrites, words => words[0] == 0x33u);
    }

    private sealed class RecordingExploredZonesStore(ConcurrentQueue<uint[]> writes) : IExploredZonesStore
    {
        public Task<uint[]?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<uint[]?>(null);

        public Task SaveAsync(int characterId, IReadOnlyList<uint> words, CancellationToken cancellationToken = default)
        {
            writes.Enqueue([.. words]);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoBlockersProbe : ITransferPlayerProbe
    {
        public TransferActiveBlockers Inspect(Player player) => TransferActiveBlockers.None;

        public void CloseInterruptible(Player player) { }
    }

    private sealed class QuietSession(int accountId, long generation) : IPlayerSession
    {
        public int AccountId { get; } = accountId;
        public long SessionGeneration { get; } = generation;
        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) { }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }
}
