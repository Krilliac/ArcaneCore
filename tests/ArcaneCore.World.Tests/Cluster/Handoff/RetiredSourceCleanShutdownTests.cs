using System.Collections.Concurrent;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Handoff;

/// <summary>A retired handoff source drops the pending writes of the characters that moved to the new owner (the save queue, and every keyed write queue through <see cref="CharacterRetirementRegistry"/>) and stops cleanly; a character it still owns keeps its write. Offline characters with a retained (failed) snapshot are retired with them.</summary>
public sealed class RetiredSourceCleanShutdownTests
{
    private static CharacterState State(int id, uint money) => new(id, 0, 12, 0, 0, 83.5f, 0, 1, 0, Money: money);

    [Fact]
    public async Task RetiredCharacters_AreDropped_AndTheQueueStopsWithoutError_WhileAnOwnedCharacterIsStillWritten()
    {
        var store = new FencedStore(moved: 1);
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var logger = new CaptureLogger();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), logger);
        Exception? fatal = null;
        queue.FatalFailure += ex => fatal = ex;

        // Both characters have a snapshot waiting before the queue runs.
        queue.Enqueue(State(1, 11));
        queue.Enqueue(State(2, 22));

        int dropped = queue.RetireCharacters([1]);
        Assert.Equal(1, dropped);

        // A later snapshot of the retired character is skipped without any error.
        queue.Enqueue(State(1, 33));

        queue.Start();
        await queue.StopAsync(); // must not throw: no write of the retired character is attempted, so the fence never refuses

        Assert.Null(fatal);
        Assert.Null(queue.TerminalFailure);
        Assert.DoesNotContain(store.Attempts, s => s.Id == 1);
        CharacterState kept = Assert.Single(store.Saved);
        Assert.Equal(2, kept.Id);
        Assert.Equal(22u, kept.Money);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("another world", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ASaveInFlightWhenTheCharacterIsRetired_IsDroppedNotTerminal_WhenTheFenceRefusesIt()
    {
        var store = new FencedStore(moved: 1);
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var logger = new CaptureLogger();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), logger);
        Exception? fatal = null;
        queue.FatalFailure += ex => fatal = ex;
        store.OnSave = state => queue.RetireCharacters([state.Id]); // the handoff retires the character while its write is inside the store call

        queue.Enqueue(State(1, 11));
        queue.Start();
        await queue.StopAsync(); // must not throw: the refusal of a retired character is expected

        Assert.Null(fatal);
        Assert.Null(queue.TerminalFailure);
        Assert.Contains(store.Attempts, s => s.Id == 1);
        Assert.Empty(queue.RetainedCharacterIds());
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    /// <summary>An offline character whose last save is retained (the database was locked) is not frozen by the handoff, yet its retry would be refused by the fence once another world owns the shard: the retirement drops it, so a clean stop follows.</summary>
    [Fact]
    public async Task ARetainedSnapshotOfANonFrozenCharacter_IsDroppedByTheSourceRetirement_AndTheQueueStopsCleanly()
    {
        var store = new FencedStore(moved: 5) { BusyOnce = 5 }; // the first write of 5 finds the database locked (retained); every later write is refused by the fence
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var logger = new CaptureLogger();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), logger) { BusyBackoff = [TimeSpan.FromHours(1)] }; // no retry of its own before the retirement
        Exception? fatal = null;
        queue.FatalFailure += ex => fatal = ex;
        using WorldRuntime world = new(new WorldRuntimeOptions(), NullSaveQueue.Instance, NullLogger<WorldRuntime>.Instance);
        using var source = new LocalWorldHandoffSource(new WorkerNodeId("w1"), world, queue, new WorkerAdmission(), new WorldWriterGuard());

        queue.Enqueue(State(5, 55));
        queue.Start();
        await WorldTestHost.WaitForAsync(() => queue.RetainedCharacters.Contains(5), "the busy snapshot of character 5 to be retained");
        Assert.Equal(new[] { 5 }, queue.RetainedCharacterIds()); // not frozen: the source froze nobody

        await source.RetireAsync(CancellationToken.None);
        Assert.Empty(queue.RetainedCharacterIds());

        await queue.StopAsync(); // must not throw: the retained snapshot was dropped, so no refused retry reaches the fence

        Assert.Null(fatal);
        Assert.Null(queue.TerminalFailure);
        Assert.Single(store.Attempts); // only the first, busy attempt
        Assert.Empty(store.Saved);
    }

    /// <summary>A settlement failure quarantines a character without clearing its retained snapshot; retiring it afterwards must still drop that snapshot, or the stop reports it as undrained.</summary>
    [Fact]
    public async Task ARetainedSnapshotOfAnAlreadyQuarantinedCharacter_IsDroppedByTheRetirement_AndTheQueueStopsCleanly()
    {
        var store = new FencedStore(moved: 5) { BusyOnce = 5 };
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), new CaptureLogger()) { BusyBackoff = [TimeSpan.FromHours(1)] }; // no retry of its own before the retirement

        queue.Enqueue(State(5, 55));
        queue.Start();
        await WorldTestHost.WaitForAsync(() => queue.RetainedCharacters.Contains(5), "the busy snapshot of character 5 to be retained");

        queue.QuarantineCharacter(5);
        Assert.Equal(new[] { 5 }, queue.RetainedCharacterIds()); // quarantine keeps the retained snapshot
        Assert.Equal(1, queue.RetireCharacters([5]));
        Assert.Empty(queue.RetainedCharacterIds());

        await queue.StopAsync(); // must not throw: nothing retained is left to report as undrained

        Assert.Null(queue.TerminalFailure);
        Assert.Empty(store.Saved);
    }

    /// <summary>The write is in flight when the handoff retires the character and fails as a locked database: the failure path re-retains it, and it must not survive to the stop.</summary>
    [Fact]
    public async Task ABusyFailureOfAWriteInFlightAtRetirement_DoesNotLeaveARetainedSnapshot_AndTheQueueStopsCleanly()
    {
        var store = new FencedStore(moved: 5) { BusyOnce = 5 };
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), new CaptureLogger());
        store.OnSave = state => queue.RetireCharacters([state.Id]); // retired while inside the store call, which then reports the database locked

        queue.Enqueue(State(5, 55));
        queue.Start();
        await WorldTestHost.WaitForAsync(() => !store.Attempts.IsEmpty, "the write of character 5 to be attempted");

        await queue.StopAsync(); // must not throw: the busy failure's retained snapshot of a retired character is discarded

        Assert.Null(queue.TerminalFailure);
        Assert.Empty(queue.RetainedCharacterIds());
        Assert.Empty(store.Saved);
    }

    /// <summary>
    /// The coordinator's real order against the real source and queue. A snapshot of an offline character turns up after the flush (the flush itself would have written it) and is retained
    /// as locked. The store keeps answering "locked" until the target's promotion starts, where the takeover has long fenced A out; from there the fence refuses it. So the retry cannot race the
    /// commit, and the promotion holds the window open until the consumer, past its busy pause, has either written the retry into the fence or looked and found nothing it may retry. With the
    /// step run anywhere after the promotion the retry is refused and the queue goes terminal.
    /// </summary>
    [Fact]
    public async Task ARetainedRetryInsideTheRollForwardWindow_IsNotAttempted_BecauseTheWritesRetireRightAfterTheCommit()
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();

        async Task AfterStage(WorldHandoffStage stage, CancellationToken cancellationToken)
        {
            if (stage == WorldHandoffStage.Flushed)
            {
                rig.Queue.Enqueue(State(5, 55));
                await WorldTestHost.WaitForAsync(() => rig.Queue.RetainedCharacters.Contains(5), "the busy snapshot of character 5 to be retained after the flush");
            }
        }

        WorldHandoffRecord done = await new WorldHandoffCoordinator(rig.Journal, source, rig.HoldingTarget, new NoRouter()).RunAsync(Guid.NewGuid(), AfterStage);

        Assert.True(rig.HoldingTarget.Promoted);
        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        await rig.AssertCleanStopAsync();
    }

    /// <summary>The same window on a recovery: the handoff is journalled as committed and a coordinator of this process finishes it; the retained snapshot was never flushed by this run.</summary>
    [Fact]
    public async Task ARetainedRetryInsideTheWindow_IsNotAttempted_OnARecoveredRollForward()
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.UnstartedWorld();
        using var source = rig.Source(world);
        rig.Queue.Enqueue(State(5, 55));
        rig.Queue.Start();
        await WorldTestHost.WaitForAsync(() => rig.Queue.RetainedCharacters.Contains(5), "the busy snapshot of character 5 to be retained");
        Guid id = Guid.NewGuid();
        rig.Journal.SeedCommitted(id);

        WorldHandoffRecord done = await new WorldHandoffCoordinator(rig.Journal, source, rig.HoldingTarget, new NoRouter()).RecoverAsync(id);

        Assert.True(rig.HoldingTarget.Promoted);
        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        await rig.AssertCleanStopAsync();
    }

    /// <summary>A failure that is not a locked database is retained and never retried by the idle consumer: only the flush can write it, and it does, while A still owns the character.</summary>
    [Fact]
    public async Task TheFlush_WritesARetainedSnapshotOfAnOfflineCharacter_BeforeTheCommit()
    {
        await using var rig = new HandoffRig();
        rig.Write = _ => new InvalidOperationException("the disk said no");
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Enqueue(State(5, 55));
        rig.Queue.Start();
        await WorldTestHost.WaitForAsync(() => rig.Store.Attempts.Count >= 3 && rig.Queue.Pending == 0, "the inline attempts of character 5 to end");
        rig.Write = _ => null;
        bool durableAtFlushed = false;

        Task AfterStage(WorldHandoffStage stage, CancellationToken cancellationToken)
        {
            if (stage == WorldHandoffStage.Flushed)
            {
                durableAtFlushed = rig.Store.Saved.Any(s => s.Id == 5);
            }

            return Task.CompletedTask;
        }

        await new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(Guid.NewGuid(), AfterStage);

        Assert.True(durableAtFlushed);
        Assert.Empty(rig.Queue.RetainedCharacterIds());
        await rig.Queue.StopAsync();
        Assert.Null(rig.Queue.TerminalFailure);
    }

    /// <summary>A save the flush cannot write fails the handoff before the commit: A resumes, still owns the character, keeps the snapshot and writes it once the store recovers.</summary>
    [Fact]
    public async Task ARetainedSnapshotTheFlushCannotWrite_AbortsTheHandoff_AndStaysAWriteOfTheSource()
    {
        await using var rig = new HandoffRig();
        rig.Write = _ => new InvalidOperationException("the disk said no");
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Enqueue(State(5, 55));
        rig.Queue.Start();
        await WorldTestHost.WaitForAsync(() => rig.Store.Attempts.Count >= 3 && rig.Queue.Pending == 0, "the inline attempts of character 5 to end");

        WorldHandoffAbortedException aborted = await Assert.ThrowsAsync<WorldHandoffAbortedException>(
            () => new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(Guid.NewGuid()));

        Assert.Equal(PlayerTransferDecisionState.Aborted, aborted.Record.DecisionState);
        Assert.Null(aborted.Record.TargetOwnershipToken);
        Assert.Equal(new[] { 5 }, rig.Queue.RetainedCharacterIds());
        Assert.False(rig.Queue.IsQuarantined(5)); // the retirement never ran
        rig.Write = _ => null;
        await rig.Queue.StopAsync();
        Assert.Contains(rig.Store.Saved, s => s.Id == 5 && s.Money == 55);
    }

    [Fact]
    public async Task WithoutRetirement_TheFenceRefusalOfAMovedCharacter_IsTerminalForTheQueue()
    {
        var store = new FencedStore(moved: 1);
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);

        queue.Enqueue(State(1, 11));
        queue.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync());
        Assert.Contains(store.Attempts, s => s.Id == 1);
    }

    [Fact]
    public async Task RetireCharacters_IsIdempotent_AndLogsNothingForAnEmptyOrRepeatedSet()
    {
        var logger = new CaptureLogger();
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(new FencedStore(moved: 1)).BuildServiceProvider();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), logger);

        Assert.Equal(0, queue.RetireCharacters([]));
        queue.Enqueue(State(1, 11));
        Assert.Equal(1, queue.RetireCharacters([1]));
        Assert.Equal(0, queue.RetireCharacters([1]));
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
    }

    private static CharacterRestState Rest(float bonus) => new(bonus, 1000, false);

    private static (RestWriteQueue Queue, CharacterRetirementRegistry Registry, FencedRestStore Store, CaptureLogger Logger, ServiceProvider Services) RestRig(int moved, bool withRegistry = true)
    {
        var store = new FencedRestStore(moved);
        ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterRestStore>(store).BuildServiceProvider();
        var registry = new CharacterRetirementRegistry();
        store.Registry = registry;
        var logger = new CaptureLogger();
        var queue = new RestWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), logger)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
            Retirement = withRegistry ? registry : null,
        };
        return (queue, registry, store, logger, services);
    }

    [Fact]
    public async Task KeyedQueue_DropsTheWritesOfARetiredCharacter_StopsCleanly_AndStillWritesAnOwnedCharacter()
    {
        (RestWriteQueue queue, CharacterRetirementRegistry registry, FencedRestStore store, CaptureLogger logger, ServiceProvider services) = RestRig(moved: 1);
        await using ServiceProvider owned = services;
        Exception? fatal = null;
        queue.FatalFailure += ex => fatal = ex;

        // Both characters have a write waiting before the queue runs; then character 1 moves to the new owner; a later write of it is dropped on arrival.
        queue.Save(1, Rest(1f));
        queue.Save(2, Rest(2f));
        registry.Retire([1]);
        queue.Save(1, Rest(3f));

        queue.Start();
        await queue.StopAsync(); // must not throw: nothing is refused by the fence and nothing is left undrained, so no announcement either

        Assert.Null(fatal);
        Assert.Null(queue.TerminalFailure);
        Assert.Equal(0, queue.Pending);
        Assert.Empty(queue.RetainedCharacters);
        Assert.DoesNotContain(store.Attempts, a => a.Id == 1);
        (int id, CharacterRestState kept) = Assert.Single(store.Saved);
        Assert.Equal(2, id);
        Assert.Equal(2f, kept.RestBonus);
        Assert.Equal(2, registry.DroppedCount("RestWriteQueue"));
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("RestWriteQueue dropped 2 write(s)", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task KeyedQueue_CountsAFenceRefusalOfACharacterRetiredMidWrite_WithoutAnErrorOrTerminalFailure()
    {
        (RestWriteQueue queue, CharacterRetirementRegistry registry, FencedRestStore store, CaptureLogger logger, ServiceProvider services) = RestRig(moved: 1);
        await using ServiceProvider owned = services;
        store.RetireWhenWriting = true; // the character moves while its write is in the store call: the fence refuses it

        queue.Save(1, Rest(1f));
        queue.Start();
        await queue.StopAsync();

        Assert.Null(queue.TerminalFailure);
        Assert.Empty(queue.RetainedCharacters);
        Assert.Contains(store.Attempts, a => a.Id == 1);
        Assert.Equal(1, registry.DroppedCount("RestWriteQueue"));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task KeyedQueue_AFenceRefusalOfACharacterThatIsStillOwned_IsStillTerminal()
    {
        // The registry exists but names nobody: a refusal of character 1 is a real loss of ownership, handled exactly as before.
        (RestWriteQueue queue, CharacterRetirementRegistry registry, FencedRestStore store, _, ServiceProvider services) = RestRig(moved: 1);
        await using ServiceProvider owned = services;

        queue.Save(1, Rest(1f));
        queue.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync());

        Assert.NotNull(queue.TerminalFailure);
        Assert.Contains(store.Attempts, a => a.Id == 1);
        Assert.Equal(0, registry.DroppedCount("RestWriteQueue"));
    }

    [Fact]
    public async Task KeyedQueue_WithoutARegistry_BehavesAsBefore()
    {
        (RestWriteQueue queue, CharacterRetirementRegistry registry, FencedRestStore store, _, ServiceProvider services) = RestRig(moved: 1, withRegistry: false);
        await using ServiceProvider owned = services;
        registry.Retire([1]); // not wired into the queue

        queue.Save(1, Rest(1f));
        queue.Save(2, Rest(2f));
        queue.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync());

        Assert.Contains(store.Attempts, a => a.Id == 1);
        Assert.Equal(0, registry.DroppedCount("RestWriteQueue"));
    }

    [Fact]
    public void Registry_CountsPerQueue_AndOnlyForRetiredCharacters()
    {
        var registry = new CharacterRetirementRegistry();
        Assert.False(registry.TryDrop("A", 1));
        Assert.Equal(2, registry.Retire([1, 2, 2]));
        Assert.Equal(0, registry.Retire([1]));
        Assert.True(registry.IsRetired(1));
        Assert.False(registry.IsRetired(3));
        Assert.True(registry.TryDrop("A", 1));
        Assert.True(registry.TryDrop("A", 2));
        Assert.True(registry.TryDrop("B", 1));
        Assert.False(registry.TryDrop("A", 3));
        Assert.Equal(2, registry.DroppedCount("A"));
        Assert.Equal(1, registry.DroppedCount("B"));
        Assert.Equal(0, registry.DroppedCount("C"));
        Assert.False(CharacterRetirementRegistry.Drops(null, "A", 1));
        Assert.False(CharacterRetirementRegistry.Retired(null, 1));
    }

    /// <summary>Refuses every write of the moved character the way the characters fence refuses a stale owner.</summary>
    private sealed class FencedRestStore(int moved) : ICharacterRestStore
    {
        public ConcurrentQueue<(int Id, CharacterRestState State)> Attempts { get; } = new();
        public ConcurrentQueue<(int Id, CharacterRestState State)> Saved { get; } = new();
        public CharacterRetirementRegistry? Registry { get; set; }
        public bool RetireWhenWriting { get; set; }

        public Task<CharacterRestState?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<CharacterRestState?>(null);

        public Task SaveAsync(int characterId, CharacterRestState state, CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue((characterId, state));
            if (characterId == moved)
            {
                if (RetireWhenWriting)
                    Registry?.Retire([characterId]);
                return Task.FromException(new CharacterOwnershipRejectedException("world", 7));
            }

            Saved.Enqueue((characterId, state));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// A real queue and a real source around a store whose answers the test scripts. By default character 5's writes find the database locked until the holding target's promotion starts,
    /// and are refused by the fence from then on.
    /// </summary>
    private sealed class HandoffRig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly TaskCompletionSource _nothingToRetry = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _fenceMoved;
        private int _promoting;
        private int _refusals;
        private Exception? _fatal;

        public HandoffRig()
        {
            Store = new FencedStore(moved: 0) { Answer = state => Write(state) };
            Write = state => state.Id != 5 ? null : LockedOrRefused();
            _services = new ServiceCollection().AddSingleton<ICharacterStore>(Store).BuildServiceProvider();
            Queue = new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(), new CaptureLogger()) { BusyBackoff = [TimeSpan.FromMilliseconds(10)] };
            Queue.FatalFailure += ex => _fatal = ex;
            Queue.AfterIdleRetainedScanForTesting = picked =>
            {
                if (picked is null && Volatile.Read(ref _promoting) == 1)
                {
                    _nothingToRetry.TrySetResult();
                }
            };
            HoldingTarget = new HoldingTarget(HoldTheWindowOpenAsync);
        }

        public FencedStore Store { get; }

        public Func<CharacterState, Exception?> Write { get; set; }

        public CharacterSaveQueue Queue { get; }

        public HandoffJournal Journal { get; } = new();

        public HoldingTarget HoldingTarget { get; }

        public WorldRuntime UnstartedWorld() => new(new WorldRuntimeOptions(), NullSaveQueue.Instance, NullLogger<WorldRuntime>.Instance)
        {
            CharacterOwnership = new CharacterOwnershipToken("world", 3, Guid.NewGuid()),
        };

        public WorldRuntime StartWorld()
        {
            WorldRuntime world = UnstartedWorld();
            world.Start();
            return world;
        }

        public LocalWorldHandoffSource Source(WorldRuntime world) => new(new WorkerNodeId("w1"), world, Queue, new WorkerAdmission(), new WorldWriterGuard());

        public async Task AssertCleanStopAsync()
        {
            Assert.Null(_fatal);
            Assert.Null(Queue.TerminalFailure);
            Assert.Equal(0, Volatile.Read(ref _refusals));
            Assert.Empty(Queue.RetainedCharacterIds());
            await Queue.StopAsync(); // must not throw
            Assert.Null(Queue.TerminalFailure);
            Assert.DoesNotContain(Store.Saved, s => s.Id == 5);
        }

        public async ValueTask DisposeAsync() => await _services.DisposeAsync();

        private Exception LockedOrRefused()
        {
            if (Volatile.Read(ref _fenceMoved) == 0)
            {
                return new SqliteException("SQLite Error 5: 'database is locked'.", 5);
            }

            Interlocked.Increment(ref _refusals);
            return new CharacterOwnershipRejectedException("world", 7);
        }

        private async Task HoldTheWindowOpenAsync()
        {
            Volatile.Write(ref _fenceMoved, 1);
            Volatile.Write(ref _promoting, 1);
            await WorldTestHost.WaitForAsync(() => _nothingToRetry.Task.IsCompleted || Queue.TerminalFailure is not null, "the consumer to retry character 5 or to find nothing to retry");
        }
    }

    private sealed class HoldingTarget(Func<Task> whilePromoting) : PlainTarget
    {
        private int _promoted;

        public bool Promoted => Volatile.Read(ref _promoted) == 1;

        public override async Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken)
        {
            await whilePromoting().ConfigureAwait(false);
            Volatile.Write(ref _promoted, 1);
        }
    }

    private class PlainTarget : IWorldHandoffTarget
    {
        public WorkerNodeId WorkerId { get; } = new("w2");

        public string OwnerNode => "w2";

        public Guid Incarnation { get; } = Guid.NewGuid();

        public Task WarmAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public virtual Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoRouter : IWorldHandoffRouter
    {
        public Task RerouteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class HandoffJournal : IWorldHandoffStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<Guid, WorldHandoffRecord> _records = [];

        public void SeedCommitted(Guid id)
        {
            var source = new CharacterOwnershipToken("world", 3, Guid.NewGuid());
            Put(new WorldHandoffRecord(id, new WorkerNodeId("w1"), new WorkerNodeId("w2"), WorldHandoffStage.EpochTakeover, PlayerTransferDecisionState.Committed, source,
                source with { Epoch = 4 }, "w2", null, 0, 0));
        }

        private WorldHandoffRecord Current(Guid id)
        {
            lock (_gate)
            {
                return _records[id];
            }
        }

        private WorldHandoffRecord Put(WorldHandoffRecord record)
        {
            lock (_gate)
            {
                _records[record.HandoffId] = record;
            }

            return record;
        }

        public Task<WorldHandoffRecord> BeginAsync(Guid handoffId, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId, CharacterOwnershipToken sourceOwnershipToken, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(new WorldHandoffRecord(handoffId, sourceWorkerId, targetWorkerId, WorldHandoffStage.Begun, PlayerTransferDecisionState.PreCommit, sourceOwnershipToken, null, null, null, 0, 0)));

        public Task<WorldHandoffRecord> AdvancePreCommitAsync(Guid handoffId, WorldHandoffStage nextStage, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(Current(handoffId) with { Stage = nextStage }));

        public Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(Guid handoffId, string targetOwnerNode, Guid targetIncarnation, CancellationToken cancellationToken = default)
        {
            WorldHandoffRecord before = Current(handoffId);
            var taken = new CharacterOwnershipToken(before.SourceOwnershipToken.Scope, before.SourceOwnershipToken.Epoch + 1, targetIncarnation);
            return Task.FromResult<WorldHandoffRecord?>(Put(before with
            {
                Stage = WorldHandoffStage.EpochTakeover,
                DecisionState = PlayerTransferDecisionState.Committed,
                TargetOwnershipToken = taken,
                TargetOwnerNode = targetOwnerNode,
            }));
        }

        public Task<WorldHandoffRecord> MarkReroutedAsync(Guid handoffId, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(Current(handoffId) with { Stage = WorldHandoffStage.Rerouted }));

        public Task<WorldHandoffRecord> MarkRetiredAsync(Guid handoffId, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(Current(handoffId) with { Stage = WorldHandoffStage.Retired, DecisionState = PlayerTransferDecisionState.Completed }));

        public Task<WorldHandoffRecord> AbortAsync(Guid handoffId, string reason, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(Current(handoffId) with { Stage = WorldHandoffStage.Aborted, DecisionState = PlayerTransferDecisionState.Aborted, AbortReason = reason }));

        public Task<WorldHandoffRecord?> GetAsync(Guid handoffId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_records.GetValueOrDefault(handoffId));
            }
        }

        public Task<IReadOnlyList<WorldHandoffRecord>> GetUnresolvedAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<WorldHandoffRecord>>([.. _records.Values.Where(r => !r.IsResolved)]);
            }
        }

        public Task<IReadOnlyList<Guid>> GetTransferIdsAsync(Guid handoffId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public ITransferDecisionStore ForHandoff(Guid handoffId) => throw new NotSupportedException();
    }

    /// <summary>Refuses every write of the moved character the way the characters fence refuses a stale owner.</summary>
    private sealed class FencedStore(int moved) : ICharacterStore
    {
        private readonly InMemoryCharacterStore _inner = new();
        public ConcurrentQueue<CharacterState> Attempts { get; } = new();
        public ConcurrentQueue<CharacterState> Saved { get; } = new();

        /// <summary>Runs inside the store call, before the fence answers: the point where a handoff can retire a character whose write is already in flight.</summary>
        public Action<CharacterState>? OnSave { get; set; }

        /// <summary>The character whose first write fails as a locked database (retained by the queue) before the fence logic applies; null for none.</summary>
        public int? BusyOnce { get; set; }

        /// <summary>The failure of this write, scripted by the test; null lets the rules below decide.</summary>
        public Func<CharacterState, Exception?>? Answer { get; init; }

        private int _busyDone;

        public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue(state);
            OnSave?.Invoke(state);
            if (Answer?.Invoke(state) is { } scripted)
                return Task.FromException(scripted);
            if (BusyOnce == state.Id && Interlocked.Exchange(ref _busyDone, 1) == 0)
                return Task.FromException(new SqliteException("SQLite Error 5: 'database is locked'.", 5));
            if (state.Id == moved)
                return Task.FromException(new CharacterOwnershipRejectedException("world", 7));
            Saved.Enqueue(state);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default) => _inner.GetByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => _inner.GetByIdAsync(id, cancellationToken);
        public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default) => _inner.IsNameTakenAsync(name, cancellationToken);
        public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default) => _inner.CountByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default) => _inner.CreateAsync(character, cancellationToken);
        public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default) => _inner.DeleteAsync(id, accountId, cancellationToken);
        public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default) => _inner.GetActionButtonsAsync(characterId, cancellationToken);
        public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default) => _inner.GetAllIdentitiesAsync(cancellationToken);
    }

    private sealed class CaptureLogger : ILogger<CharacterSaveQueue>
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_gate)
                    return [.. _entries];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
