using System.Collections.Concurrent;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>The retained-write rules of <see cref="ExploredZonesWriteQueue"/>: nothing is dropped, the latest words are written, a lock that outlasts the inline retries is landed by the retry sweep, and shutdown says what is not durable.</summary>
public sealed class ExploredZonesWriteQueueTests
{
    private static (ExploredZonesWriteQueue Queue, InMemoryExploredZonesStore Store) Create(TimeSpan? retryInterval = null)
    {
        var store = new InMemoryExploredZonesStore();
        ServiceProvider services = new ServiceCollection().AddSingleton<IExploredZonesStore>(store).BuildServiceProvider();
        var queue = new ExploredZonesWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetryDelay = TimeSpan.Zero,
            RetainedRetryInterval = retryInterval ?? TimeSpan.FromSeconds(30),
        };
        queue.Start();
        return (queue, store);
    }

    private static uint[] Words(uint first)
    {
        var words = new uint[64];
        words[0] = first;
        return words;
    }

    private static async Task DurableAsync(ExploredZonesWriteQueue queue, int characterId)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (queue.HasRetainedFailure(characterId))
        {
            Assert.True(DateTime.UtcNow < deadline, "the retained write was never retried by the sweep");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task OnlyTheLatestWords_OfACharacterAreWritten()
    {
        (ExploredZonesWriteQueue queue, InMemoryExploredZonesStore store) = Create();
        for (uint i = 1; i <= 50; i++)
        {
            queue.Save(1, Words(i));
        }

        await queue.FlushAsync();

        Assert.Equal(50u, store.Get(1)![0]);
        Assert.Equal(0, queue.Pending);
        await queue.StopAsync();
    }

    [Fact]
    public async Task AFailedWrite_IsRetained_AndTheNextChangeCarriesIt()
    {
        (ExploredZonesWriteQueue queue, InMemoryExploredZonesStore store) = Create();
        store.FailSaves = true;
        queue.Save(1, Words(10));
        await queue.FlushAsync();
        Assert.True(queue.HasRetainedFailure(1));
        Assert.Null(store.Get(1));

        store.FailSaves = false;
        queue.Save(1, Words(11));
        await queue.FlushAsync();

        Assert.False(queue.HasRetainedFailure(1));
        Assert.Equal(11u, store.Get(1)![0]);
        await queue.StopAsync();
    }

    [Fact]
    public async Task StopRetriesTheRetainedWrite_AndNamesWhatIsStillNotDurable()
    {
        (ExploredZonesWriteQueue queue, InMemoryExploredZonesStore store) = Create();
        store.FailSaves = true;
        queue.Save(4, Words(1));
        await queue.FlushAsync();

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync());
        Assert.Contains("4", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALockThatOutlastsTheInlineRetries_IsLandedByTheRetrySweep_WithNoFurtherTrigger()
    {
        (ExploredZonesWriteQueue queue, InMemoryExploredZonesStore store) = Create(Timeout.InfiniteTimeSpan);
        store.LockedSaves = 4; // three inline attempts fail, the first sweep fails, the second lands
        queue.Save(1, Words(10));
        await queue.FlushAsync();

        Assert.True(queue.HasRetainedFailure(1));
        Assert.Null(store.Get(1));
        Assert.Equal(3, store.Attempts);

        // No timer: the sweeps are driven explicitly, so the attempt counts are exact.
        Assert.True(queue.PostRetainedSweepForTest());
        await queue.FlushAsync();
        Assert.True(queue.HasRetainedFailure(1));
        Assert.Equal(4, store.Attempts);

        Assert.True(queue.PostRetainedSweepForTest());
        await queue.FlushAsync();
        Assert.False(queue.HasRetainedFailure(1));
        Assert.Equal(10u, store.Get(1)![0]);
        Assert.Equal(5, store.Attempts);
        Assert.Equal(1, store.Saves);
        await queue.StopAsync();
    }

    [Fact]
    public async Task TheRealRetryTimer_EventuallyLandsAWriteHeldByALongLock()
    {
        (ExploredZonesWriteQueue queue, InMemoryExploredZonesStore store) = Create(TimeSpan.FromMilliseconds(50));
        store.LockedSaves = 4;
        queue.Save(1, Words(10));
        await queue.FlushAsync();

        await DurableAsync(queue, 1);
        Assert.Equal(10u, store.Get(1)![0]);
        await queue.StopAsync();
    }

    private sealed class InMemoryExploredZonesStore : IExploredZonesStore
    {
        private readonly ConcurrentDictionary<int, uint[]> _rows = new();
        private int _attempts;
        private int _saves;

        public volatile bool FailSaves;

        /// <summary>The number of upcoming saves that fail as SQLite reports a file another writer holds (a lock that clears by itself).</summary>
        public int LockedSaves;

        public int Attempts => Volatile.Read(ref _attempts);

        public int Saves => Volatile.Read(ref _saves);

        public uint[]? Get(int characterId) => _rows.GetValueOrDefault(characterId);

        public Task<uint[]?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(Get(characterId));

        public Task SaveAsync(int characterId, IReadOnlyList<uint> words, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            int left;
            do
            {
                left = Volatile.Read(ref LockedSaves);
            }
            while (left > 0 && Interlocked.CompareExchange(ref LockedSaves, left - 1, left) != left);
            if (left > 0)
            {
                throw new Microsoft.Data.Sqlite.SqliteException("SQLite Error 5: 'database is locked'.", 5);
            }

            if (FailSaves)
            {
                throw new InvalidOperationException("the characters database is unavailable");
            }

            Interlocked.Increment(ref _saves);
            _rows[characterId] = [.. words];
            return Task.CompletedTask;
        }

        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default)
        {
            _rows.TryRemove(characterId, out _);
            return Task.CompletedTask;
        }
    }
}
