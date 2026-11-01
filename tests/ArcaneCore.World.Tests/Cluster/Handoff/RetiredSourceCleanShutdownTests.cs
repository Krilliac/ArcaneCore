using System.Collections.Concurrent;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Handoff;

/// <summary>A retired handoff source drops the pending saves of the characters that moved to the new owner and stops cleanly; a character it still owns keeps its write.</summary>
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

    /// <summary>Refuses every write of the moved character the way the characters fence refuses a stale owner.</summary>
    private sealed class FencedStore(int moved) : ICharacterStore
    {
        private readonly InMemoryCharacterStore _inner = new();
        public ConcurrentQueue<CharacterState> Attempts { get; } = new();
        public ConcurrentQueue<CharacterState> Saved { get; } = new();

        public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue(state);
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
