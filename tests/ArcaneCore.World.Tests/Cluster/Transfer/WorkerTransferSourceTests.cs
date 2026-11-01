using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>The production source worker of a transfer: capture freezes, flushes and removes the player; abort restores it; retire releases the claim and refuses saves.</summary>
public sealed class WorkerTransferSourceTests : IDisposable
{
    private static readonly CharacterOwnershipToken Token = new("world", 3, Guid.NewGuid());
    private static readonly WorkerNodeId Source = new("worker-a");
    private static readonly WorkerNodeId Target = new("worker-b");

    private readonly WorldRuntime _world;
    private readonly FakePersistence _persistence = new();
    private readonly FakeProbe _probe = new();
    private readonly WorkerTransferSource _source;
    private readonly Player _player;

    public WorkerTransferSourceTests()
    {
        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new RecordingSaveQueue(), NullLogger<WorldRuntime>.Instance)
        {
            CharacterOwnership = Token,
        };
        var character = new CharacterRecord { Id = 7, AccountId = 5, Name = "Transferee", Race = 1, Class = 1, Gender = 0, Level = 40, MapId = 0 };
        _player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new GenerationSession(5, 9));
        _player.Relocate(12f, 34f, 56f, 1.5f, 0);
        _world.AddPlayer(_player, _world.GetMap(0));
        _world.Start();
        _source = new WorkerTransferSource(Source, _world, _persistence, _probe);
    }

    public void Dispose() => _world.Stop();

    private Task<PlayerTransferBundle> CaptureAsync(Guid transferId) => _source.SuspendAndCaptureAsync(
        transferId, _player.Guid.Value, Target, 1, 0, new TransferPosition(1f, 2f, 3f, 0f));

    [Fact]
    public async Task Capture_FlushesAndRemovesThePlayer_AndReturnsASlimBundle()
    {
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = await CaptureAsync(id);

        Assert.False(_world.IsOnline(_player.Guid));
        Assert.Null(_player.Map);
        Assert.Equal(new[] { 7 }, _persistence.Saved);
        Assert.Equal(new[] { 7 }, _persistence.Flushed);
        Assert.Equal(new[] { 7 }, _persistence.Held);
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
    }

    [Fact]
    public async Task Capture_WithAPendingTrade_IsBlocked_AndLeavesThePlayerInTheWorld()
    {
        _probe.Blockers = new TransferActiveBlockers(HasActiveTradeWindow: true);

        PlayerTransferBundle bundle = await CaptureAsync(Guid.NewGuid());

        Assert.True(bundle.Blockers.HasActiveTradeWindow);
        Assert.Equal(PlayerTransferValidator.ErrorActiveTradeWindow, PlayerTransferValidator.ValidateBundle(bundle).FailureCode);
        Assert.True(_world.IsOnline(_player.Guid));
        Assert.Empty(_persistence.Saved);
        Assert.Empty(_persistence.Held);
        Assert.False(_source.HoldsClaim(_player.Guid.Value));
        Assert.Equal(1, _probe.Closed);
    }

    [Fact]
    public async Task Abort_RestoresThePlayerAtTheSameSpot_AndLiftsTheHold()
    {
        Guid id = Guid.NewGuid();
        await CaptureAsync(id);

        await _source.ResumeAfterAbortAsync(id, _player.Guid.Value, Token, "test");

        Assert.True(_world.IsOnline(_player.Guid));
        Assert.NotNull(_player.Map);
        Assert.Equal((12f, 34f, 56f), (_player.X, _player.Y, _player.Z));
        Assert.Equal(new[] { 7 }, _persistence.Released);
        Assert.False(_source.HoldsClaim(_player.Guid.Value));

        await _source.ResumeAfterAbortAsync(id, _player.Guid.Value, Token, "again"); // idempotent
    }

    [Fact]
    public async Task Capture_AfterAFailedFlush_PutsThePlayerBack()
    {
        _persistence.FlushFails = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => CaptureAsync(Guid.NewGuid()));

        Assert.True(_world.IsOnline(_player.Guid));
        Assert.False(_source.HoldsClaim(_player.Guid.Value));
        Assert.Equal(new[] { 7 }, _persistence.Released);
    }

    [Fact]
    public async Task Retire_ReleasesTheClaim_AndASaveAfterItIsRefused()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        using var queue = new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        _persistence.Queue = queue;
        Guid id = Guid.NewGuid();
        await CaptureAsync(id);
        Assert.False(queue.IsQuarantined(7));

        await _source.RetireSourcePlayerAsync(id, _player.Guid.Value, Token);

        Assert.False(_source.HoldsClaim(_player.Guid.Value));
        Assert.Equal(new[] { 7 }, _persistence.Refused);
        Assert.True(queue.IsQuarantined(7));
        queue.Enqueue(new CharacterState(7, 0, 0, 0f, 0f, 0f, 0f, 40, 1));
        Assert.False(queue.IsPending(7));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _source.ResumeAfterAbortAsync(id, _player.Guid.Value, Token, "late"));
        await _source.RetireSourcePlayerAsync(id, _player.Guid.Value, Token); // a roll-forward repeats it
    }

    [Fact]
    public async Task Retire_UnderAStaleToken_IsRefused_AndKeepsTheClaim()
    {
        Guid id = Guid.NewGuid();
        await CaptureAsync(id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _source.RetireSourcePlayerAsync(id, _player.Guid.Value, Token with { Epoch = 2 }));

        Assert.True(_source.HoldsClaim(_player.Guid.Value));
        Assert.Empty(_persistence.Refused);
    }

    private sealed class FakeProbe : ITransferPlayerProbe
    {
        public TransferActiveBlockers Blockers { get; set; } = TransferActiveBlockers.None;
        public int Closed;
        public TransferActiveBlockers Inspect(Player player) => Blockers;
        public void CloseInterruptible(Player player) => Closed++;
    }

    private sealed class FakePersistence : ITransferPersistence
    {
        public List<int> Saved { get; } = [];
        public List<int> Flushed { get; } = [];
        public List<int> Held { get; } = [];
        public List<int> Released { get; } = [];
        public List<int> Refused { get; } = [];
        public bool FlushFails { get; set; }
        public CharacterSaveQueue? Queue { get; set; }

        public Task SaveAndHold(CharacterState state)
        {
            Saved.Add(state.Id);
            Held.Add(state.Id);
            return Task.CompletedTask;
        }

        public Task FlushAsync(int characterId, CancellationToken cancellationToken)
        {
            if (FlushFails)
                throw new InvalidOperationException("not durable");
            Flushed.Add(characterId);
            return Task.CompletedTask;
        }

        public void ReleaseHold(int characterId) => Released.Add(characterId);

        public void Refuse(int characterId)
        {
            Refused.Add(characterId);
            Queue?.QuarantineCharacter(characterId);
        }
    }

    private sealed class GenerationSession(int accountId, long generation) : IPlayerSession
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
