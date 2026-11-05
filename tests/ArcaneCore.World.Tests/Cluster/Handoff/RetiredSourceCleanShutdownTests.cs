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
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), logger);
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

        private int _busyDone;

        public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue(state);
            OnSave?.Invoke(state);
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
