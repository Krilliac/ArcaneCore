using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>
/// The production source worker of a transfer, run over the production persistence (<see cref="SaveQueueTransferPersistence"/> on a real
/// <see cref="CharacterSaveQueue"/>) and the production probe (<see cref="FeatureTransferPlayerProbe"/> on a real <see cref="EconomyFeature"/>
/// trade): capture freezes, flushes and detaches the player without the logout path; abort restores it whole; retire releases the claim and
/// refuses saves.
/// </summary>
public sealed class WorkerTransferSourceTests : IDisposable
{
    private static readonly CharacterOwnershipToken Token = new("world", 3, Guid.NewGuid());
    private static readonly WorkerNodeId Source = new("worker-a");
    private static readonly WorkerNodeId Target = new("worker-b");

    private readonly WorldRuntime _world;
    private readonly ServiceProvider _storeServices;
    private readonly ServiceProvider _probeServices;
    private readonly CharacterSaveQueue _queue;
    private readonly EconomyFeature _economy;
    private readonly ConcurrentQueue<CharacterState> _saved = new();
    private readonly WorkerTransferSource _source;
    private readonly Player _player;
    private readonly Player _other;
    private readonly GenerationSession _session = new(5, 9);
    private int _loggingOut;
    private bool _failFlush;

    public WorkerTransferSourceTests()
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
        _economy = new EconomyFeature(_storeServices, _storeServices.GetRequiredService<IServiceScopeFactory>(), NullLogger<EconomyFeature>.Instance);
        _probeServices = new ServiceCollection().AddSingleton(_economy).BuildServiceProvider();

        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new RecordingSaveQueue(), NullLogger<WorldRuntime>.Instance)
        {
            CharacterOwnership = Token,
        };
        _world.PlayerLoggingOut += _ => Interlocked.Increment(ref _loggingOut);
        _player = NewPlayer(7, "Transferee", _session);
        _other = NewPlayer(8, "Bystander", new GenerationSession(6, 1));
        _world.AddPlayer(_player, _world.GetMap(0));
        _world.AddPlayer(_other, _world.GetMap(0));
        _world.UseManualClock(); // the played-time test advances game time deterministically
        _world.Start();
        ITransferPersistence persistence = new SwitchableFlushPersistence(new SaveQueueTransferPersistence(_queue), () => _failFlush);
        _source = new WorkerTransferSource(Source, _world, persistence, new FeatureTransferPlayerProbe(_probeServices, _queue));
    }

    public void Dispose()
    {
        _world.Stop();
        _queue.Dispose();
        _probeServices.Dispose();
        _storeServices.Dispose();
    }

    private static Player NewPlayer(int id, string name, IPlayerSession session)
    {
        var character = new CharacterRecord { Id = id, AccountId = session.AccountId, Name = name, Race = 1, Class = 1, Gender = 0, Level = 40, MapId = 0 };
        var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);
        player.Relocate(12f, 34f, 56f, 1.5f, 0);
        return player;
    }

    private Task<PlayerTransferBundle> CaptureAsync(Guid transferId) => _source.SuspendAndCaptureAsync(
        transferId, _player.Guid.Value, Target, 1, 0, new TransferPosition(1f, 2f, 3f, 0f));

    [Fact]
    public async Task Capture_SavesHoldsAndRemovesThePlayer_WithoutTheLogoutPath_AndReturnsASlimBundle()
    {
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = await CaptureAsync(id);

        Assert.False(_world.IsOnline(_player.Guid));
        Assert.Null(_player.Map);
        Assert.Equal(new[] { 7 }, _saved.Select(state => state.Id).ToArray()); // the settlement save reached the store
        Assert.True(_queue.IsHeld(7));
        Assert.False(_queue.IsQuarantined(7));
        Assert.Equal(0, _loggingOut); // detached, not logged out
        Assert.True(_source.HoldsClaim(_player.Guid.Value));
        Assert.Equal(id, bundle.TransferId);
        Assert.Equal(_player.Guid.Value, bundle.CharacterGuid);
        Assert.Equal(5, bundle.AccountId);
        Assert.Equal(9, bundle.SessionGeneration);
        Assert.Equal(Token, bundle.SourceOwnershipToken);
        Assert.Equal(Source, bundle.SourceWorkerId);
        Assert.Equal(Target, bundle.TargetWorkerId);
        Assert.Equal(new TransferPosition(1f, 2f, 3f, 0f), bundle.TargetPosition);
        Assert.Equal(new TransferPosition(12f, 34f, 56f, 1.5f), bundle.SourcePosition);
        Assert.False(bundle.Blockers.HasAny);
        Assert.Equal(1, _session.GossipCompletes); // the interruptible windows were closed
    }

    [Fact]
    public async Task Capture_WithARealPendingTrade_IsBlocked_AndLeavesThePlayerInTheWorld()
    {
        await SeedTradeAsync();

        PlayerTransferBundle bundle = await CaptureAsync(Guid.NewGuid());

        Assert.True(bundle.Blockers.HasActiveTradeWindow);
        Assert.Equal(PlayerTransferValidator.ErrorActiveTradeWindow, PlayerTransferValidator.ValidateBundle(bundle).FailureCode);
        Assert.True(_world.IsOnline(_player.Guid));
        Assert.Empty(_saved);
        Assert.False(_queue.IsHeld(7));
        Assert.False(_source.HoldsClaim(_player.Guid.Value));
        Assert.Equal(0, _session.GossipCompletes); // a refused capture closes nothing
    }

    [Fact]
    public async Task Capture_AfterTheTradeEnds_Succeeds()
    {
        await SeedTradeAsync();
        Assert.True((await CaptureAsync(Guid.NewGuid())).Blockers.HasActiveTradeWindow);
        await _world.InvokeAsync(RemoveTrades);

        PlayerTransferBundle bundle = await CaptureAsync(Guid.NewGuid());

        Assert.False(bundle.Blockers.HasAny);
        Assert.False(_world.IsOnline(_player.Guid));
    }

    [Fact]
    public async Task Abort_RestoresThePlayerAtTheSameSpot_WithoutTheLogoutPath_AndLiftsTheHold()
    {
        Guid id = Guid.NewGuid();
        await CaptureAsync(id);

        await _source.ResumeAfterAbortAsync(id, _player.Guid.Value, Token, "test");

        Assert.True(_world.IsOnline(_player.Guid));
        Assert.NotNull(_player.Map);
        Assert.Equal((12f, 34f, 56f), (_player.X, _player.Y, _player.Z));
        Assert.False(_queue.IsHeld(7));
        Assert.False(_source.HoldsClaim(_player.Guid.Value));
        Assert.Equal(0, _loggingOut); // no feature saw the player leave, so none lost its state

        await _source.ResumeAfterAbortAsync(id, _player.Guid.Value, Token, "again"); // idempotent
    }

    /// <summary>
    /// The client's connection closed while the player was frozen (the gateway crashed mid-transfer). The close logged nobody out (the player was in no map), so before the
    /// fix the abort put a player back online with nobody behind it, and every later login of the character waited for it to go offline and failed as a duplicate.
    /// </summary>
    [Fact]
    public async Task Abort_AfterTheSessionClosedDuringTheTransfer_LogsTheRestoredPlayerOut()
    {
        Guid id = Guid.NewGuid();
        await CaptureAsync(id);
        _session.IsDisconnected = true;

        await _source.ResumeAfterAbortAsync(id, _player.Guid.Value, Token, "gateway-recovery");

        Assert.False(_world.IsOnline(_player.Guid));
        Assert.Null(_player.Map);
        Assert.Equal(1, _loggingOut); // the full logout ran once: its features saw the player leave
        Assert.False(_queue.IsHeld(7));
        Assert.False(_source.HoldsClaim(_player.Guid.Value));
        Assert.True(_world.IsOnline(_other.Guid));
    }

    [Fact]
    public async Task Abort_UnderADifferentCoordinatorToken_StillRestoresThePlayer()
    {
        Guid id = Guid.NewGuid();
        await CaptureAsync(id);

        await _source.ResumeAfterAbortAsync(id, _player.Guid.Value, Token with { Epoch = 99 }, "token differs");

        Assert.True(_world.IsOnline(_player.Guid));
        Assert.False(_queue.IsHeld(7));
        Assert.False(_source.HoldsClaim(_player.Guid.Value));
    }

    [Fact]
    public async Task Capture_AfterAFailedFlush_PutsThePlayerBack()
    {
        _failFlush = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => CaptureAsync(Guid.NewGuid()));

        Assert.True(_world.IsOnline(_player.Guid));
        Assert.False(_source.HoldsClaim(_player.Guid.Value));
        Assert.False(_queue.IsHeld(7));
        Assert.Equal(0, _loggingOut);
    }

    [Fact]
    public async Task Retire_ReleasesTheClaim_AndASaveAfterItIsRefused()
    {
        Guid id = Guid.NewGuid();
        await CaptureAsync(id);
        Assert.False(_queue.IsQuarantined(7));
        int savedAtCapture = _saved.Count;

        await _source.RetireSourcePlayerAsync(id, _player.Guid.Value, Token);

        Assert.False(_source.HoldsClaim(_player.Guid.Value));
        Assert.True(_queue.IsQuarantined(7));
        _queue.Enqueue(new CharacterState(7, 0, 0, 0f, 0f, 0f, 0f, 40, 1));
        Assert.False(_queue.IsPending(7));
        Assert.Equal(savedAtCapture, _saved.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _source.ResumeAfterAbortAsync(id, _player.Guid.Value, Token, "late"));
        await _source.RetireSourcePlayerAsync(id, _player.Guid.Value, Token); // a roll-forward repeats it
        Assert.Equal(1, _loggingOut); // the repeat did not tear the player down twice
    }

    [Fact]
    public async Task Retire_OfACharacterThisProcessNeverFroze_AndThatIsOffline_Succeeds_AndRefusesItsSaves()
    {
        // A restarted source: the suspension died with the old process, the character is not here, and the journal says the transfer is committed.
        Guid id = Guid.NewGuid();
        ObjectGuid gone = ObjectGuid.Player(9);

        await _source.RetireSourcePlayerAsync(id, gone.Value, Token);

        Assert.True(_queue.IsQuarantined(9)); // as after an ordinary retire: this world can never overwrite what the target saves
        Assert.Equal(0, _loggingOut); // there was no player to tear down
        await _source.RetireSourcePlayerAsync(id, gone.Value, Token); // a roll-forward repeats it
        await Assert.ThrowsAsync<InvalidOperationException>(() => _source.ResumeAfterAbortAsync(id, gone.Value, Token, "late"));
    }

    [Fact]
    public async Task Retire_OfACharacterThisProcessNeverFroze_ButThatIsOnline_IsRefused_AndLeavesThePlayerAlone()
    {
        Guid id = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _source.RetireSourcePlayerAsync(id, _player.Guid.Value, Token));

        Assert.False(_queue.IsQuarantined(7));
        Assert.Equal(0, _loggingOut);
        Assert.Same(_player, await _world.InvokeAsync(() => _world.FindOnlinePlayer(_player.Guid)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _source.RetireSourcePlayerAsync(id, _player.Guid.Value, Token)); // the refusal is not remembered as a retire
    }

    [Fact]
    public async Task Retire_RunsTheLogoutTeardownExactlyOnce_AndCaptureAndAbortNever()
    {
        Guid id = Guid.NewGuid();
        await CaptureAsync(id);
        Assert.Equal(0, _loggingOut);

        await _source.RetireSourcePlayerAsync(id, _player.Guid.Value, Token);

        Assert.Equal(1, _loggingOut);
        Assert.False(_world.IsOnline(_player.Guid));

        Guid abortedId = Guid.NewGuid();
        Player other = _other;
        await _source.SuspendAndCaptureAsync(abortedId, other.Guid.Value, Target, 1, 0, new TransferPosition(1f, 2f, 3f, 0f));
        await _source.ResumeAfterAbortAsync(abortedId, other.Guid.Value, Token, "abort");
        Assert.Equal(1, _loggingOut); // capture and abort of another player raised nothing
    }

    [Fact]
    public async Task Abort_KeepsThePlayedTimeBuiltUpThisSession_AndTheSameMap()
    {
        await _world.AdvanceClockAsync(120_000);
        uint before = await _world.InvokeAsync(() => _player.PlayedTimeAt(_world.NowMs));
        Assert.True(before >= 120);
        Map? map = _player.Map;

        Guid id = Guid.NewGuid();
        await CaptureAsync(id);
        await _source.ResumeAfterAbortAsync(id, _player.Guid.Value, Token, "abort");

        uint after = await _world.InvokeAsync(() => _player.PlayedTimeAt(_world.NowMs));
        uint levelAfter = await _world.InvokeAsync(() => _player.LevelPlayedTimeAt(_world.NowMs));
        Assert.Equal(before, after);
        Assert.True(levelAfter >= 120);
        Assert.Same(map, _player.Map);
    }

    [Fact]
    public async Task Retire_UnderADifferentCoordinatorToken_StillCompletes()
    {
        Guid id = Guid.NewGuid();
        await CaptureAsync(id);

        await _source.RetireSourcePlayerAsync(id, _player.Guid.Value, Token with { Epoch = 2 });

        Assert.False(_source.HoldsClaim(_player.Guid.Value));
        Assert.True(_queue.IsQuarantined(7));
    }

    private Task<bool> SeedTradeAsync() => _world.InvokeAsync(() =>
    {
        // The economy store is not wired in this host (Enabled is false, so InitiateTrade refuses): seed the open trade the way
        // InitiateTrade and BeginTrade leave it, as the fleet-reset tests do.
        System.Collections.IDictionary trades = TradesOf(_economy);
        var trade = new TradeSession(_other, _player) { Opened = true };
        trades[_other] = trade;
        trades[_player] = trade;
        Assert.NotNull(_economy.TradeOf(_player));
        return true;
    });

    private bool RemoveTrades()
    {
        TradesOf(_economy).Clear();
        return true;
    }

    private static System.Collections.IDictionary TradesOf(EconomyFeature economy)
        => (System.Collections.IDictionary)typeof(EconomyFeature)
            .GetField("_trades", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(economy)!;

    /// <summary>The production persistence, with a switch that makes the durability wait fail (the save and hold stay real).</summary>
    private sealed class SwitchableFlushPersistence(ITransferPersistence inner, Func<bool> failFlush) : ITransferPersistence
    {
        public Task SaveAndHold(CharacterState state) => inner.SaveAndHold(state);

        public Task FlushAsync(int characterId, CancellationToken cancellationToken)
            => failFlush() ? throw new InvalidOperationException("not durable") : inner.FlushAsync(characterId, cancellationToken);

        public void ReleaseHold(int characterId) => inner.ReleaseHold(characterId);

        public void Refuse(int characterId) => inner.Refuse(characterId);
    }

    private sealed class GenerationSession(int accountId, long generation) : IPlayerSession
    {
        private int _gossipCompletes;

        public int AccountId { get; } = accountId;
        public long SessionGeneration { get; } = generation;
        public int GossipCompletes => Volatile.Read(ref _gossipCompletes);
        public AccountSecurity Security => AccountSecurity.Player;
        public bool IsDisconnected { get; set; }

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            if (opcode == WorldOpcode.SmsgGossipComplete)
            {
                Interlocked.Increment(ref _gossipCompletes);
            }
        }

        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }
}
