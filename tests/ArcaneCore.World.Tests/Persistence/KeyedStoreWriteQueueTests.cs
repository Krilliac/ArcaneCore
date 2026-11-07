using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>
/// <see cref="KeyedStoreWriteQueue{TStore}"/> directly, over a store that is not the GM audit one: the behaviour group
/// persistence and the account last-address record rely on (newest value per key wins, a failed write is retained and
/// carried, the stop drains and names what is not durable, no store means no-op). GmAuditUnitTests cover the same
/// implementation through <see cref="ArcaneCore.World.Gm.Audit.GmAuditWriteQueue"/>.
/// </summary>
public sealed class KeyedStoreWriteQueueTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>A keyed value store that can be told to fail its next writes.</summary>
    public sealed class ValueStore
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, int?> _values = [];
        private readonly List<string> _log = [];
        private int _failures;

        public int Attempts { get; private set; }

        public IReadOnlyList<string> Log
        {
            get
            {
                lock (_lock)
                {
                    return [.. _log];
                }
            }
        }

        public void FailNext(int count)
        {
            lock (_lock)
            {
                _failures = count;
            }
        }

        public int? Value(string key)
        {
            lock (_lock)
            {
                return _values.GetValueOrDefault(key);
            }
        }

        public Task SetAsync(string key, int? value)
        {
            lock (_lock)
            {
                Attempts++;
                if (_failures > 0)
                {
                    _failures--;
                    throw new InvalidOperationException("storage is down");
                }

                _values[key] = value;
                _log.Add(key + "=" + (value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "deleted"));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public ValueStore Store { get; } = new();

        public KeyedStoreWriteQueue<ValueStore> Queue { get; }

        private Rig(bool withStore)
        {
            var services = new ServiceCollection();
            if (withStore)
            {
                services.AddSingleton(Store);
            }

            Queue = new KeyedStoreWriteQueue<ValueStore>(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, "group")
            {
                RetryDelay = TimeSpan.FromMilliseconds(1),
                RetainedRetryInterval = TimeSpan.FromHours(1),
            };
            Queue.Start();
        }

        public static Rig Create(bool withStore = true) => new(withStore);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Queue.StopAsync();
            }
            catch (InvalidOperationException)
            {
                // a test that ends with a retained write
            }
        }
    }

    [Fact]
    public async Task ALockedDatabase_IsNotRetriedInline_TheWriteIsRetainedAndLandsOnTheNextRetainedRetry()
    {
        await using Rig rig = Rig.Create();
        int attempts = 0;
        bool locked = true;
        rig.Queue.Save("group:1", s =>
        {
            Interlocked.Increment(ref attempts);
            return Volatile.Read(ref locked) ? Task.FromException(new Microsoft.Data.Sqlite.SqliteException("SQLite Error 5: 'database is locked'.", 5)) : s.SetAsync("group:1", 1);
        });
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal(1, Volatile.Read(ref attempts)); // not three: the lock wait is never repeated inline
        Assert.Equal(["group:1"], rig.Queue.RetainedKeys);

        Volatile.Write(ref locked, false);
        await rig.Queue.RetryRetainedAsync().WaitAsync(Wait);
        Assert.Equal(["group:1=1"], rig.Store.Log);
        Assert.Empty(rig.Queue.RetainedKeys);
    }

    /// <summary>
    /// The flush's retry checks only what it can owe (OwedKeys leaves a lease refusal out), so it must not re-run a lease-refused key either: that would spend the handoff's single shared budget
    /// (MaxAttempts, RetryDelay each) on writes the flush never reports. The stop still retries every retained key once, so a regained lease lands it.
    /// </summary>
    [Fact]
    public async Task AFlushRetry_DoesNotReRunALeaseRefusedKey_ButStillRetriesAGenuineFailure_AndTheStopRetriesBoth()
    {
        await using Rig rig = Rig.Create();
        int refused = 0;
        rig.Queue.Save("ticket:1", _ =>
        {
            Interlocked.Increment(ref refused);
            return Task.FromException(new RealmLeaseNotHeldException("social"));
        });
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.Equal(3, Volatile.Read(ref refused)); // the first write: MaxAttempts
        Assert.Equal(["ticket:1"], rig.Queue.RetainedKeys);

        await rig.Queue.RetryRetainedAsync().WaitAsync(Wait); // completes: a lease refusal is not checked
        Assert.Equal(3, Volatile.Read(ref refused)); // and not re-run
        Assert.Equal(["ticket:1"], rig.Queue.RetainedKeys);

        rig.Store.FailNext(1000);
        rig.Queue.Save("group:2", s => s.SetAsync("group:2", 2));
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        int attemptsBefore = rig.Store.Attempts;
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.RetryRetainedAsync().WaitAsync(Wait));
        Assert.Equal("group writes are not durable: group:2", error.Message); // names the genuine key only
        Assert.True(rig.Store.Attempts > attemptsBefore, "the genuine failure was not retried");
        Assert.Equal(3, Volatile.Read(ref refused)); // still untouched

        await Assert.ThrowsAsync<InvalidOperationException>(rig.Queue.StopAsync);
        Assert.Equal(4, Volatile.Read(ref refused)); // the stop's final retry tried it once
    }

    [Fact]
    public async Task WritesOfDifferentKeys_RunInTheOrderTheyWereSaved()
    {
        await using Rig rig = Rig.Create();
        rig.Queue.Save("group:2", s => s.SetAsync("group:2", 2));
        rig.Queue.Save("group:1", s => s.SetAsync("group:1", 1));
        rig.Queue.Save("address:9", s => s.SetAsync("address:9", 9));
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal(["group:2=2", "group:1=1", "address:9=9"], rig.Store.Log);
        Assert.Equal(0, rig.Queue.Pending);
    }

    [Fact]
    public async Task TheNewestWriteOfAWaitingKey_IsTheOnlyOneThatRuns()
    {
        // A group saved, changed and then disbanded within one busy stretch reaches storage once: as deleted.
        await using Rig rig = Rig.Create();
        var gate = new TaskCompletionSource();
        rig.Queue.Save("blocker", async _ => await gate.Task);
        rig.Queue.Save("group:7", s => s.SetAsync("group:7", 2));
        rig.Queue.Save("group:7", s => s.SetAsync("group:7", 3));
        rig.Queue.Save("group:7", s => s.SetAsync("group:7", null));
        int pending = rig.Queue.Pending;
        gate.SetResult(); // before any assertion, so a failure cannot leave the consumer blocked and hang the stop
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal(2, pending); // the blocker and one entry for group:7
        Assert.Equal(["group:7=deleted"], rig.Store.Log);
        Assert.Equal(1, rig.Store.Attempts);
    }

    [Fact]
    public async Task AFailedWrite_IsRetained_ThenCarriedByTheNextWriteOfTheKey()
    {
        await using Rig rig = Rig.Create();
        rig.Store.FailNext(3);
        rig.Queue.Save("group:7", s => s.SetAsync("group:7", 2));
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.Equal(3, rig.Store.Attempts);
        Assert.Equal(["group:7"], rig.Queue.RetainedKeys);

        rig.Queue.Save("group:7", s => s.SetAsync("group:7", 4)); // the newer value replaces the retained one
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal(4, rig.Store.Value("group:7"));
        Assert.Equal(["group:7=4"], rig.Store.Log);
        Assert.Empty(rig.Queue.RetainedKeys);
    }

    [Fact]
    public async Task RetryRetained_PersistsWhatStorageNowAccepts_AndFaultsNamingTheRest()
    {
        await using Rig rig = Rig.Create();
        rig.Store.FailNext(3);
        rig.Queue.Save("group:1", s => s.SetAsync("group:1", 1));
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        await rig.Queue.RetryRetainedAsync().WaitAsync(Wait);
        Assert.Equal(1, rig.Store.Value("group:1"));

        rig.Store.FailNext(1000);
        rig.Queue.Save("group:2", s => s.SetAsync("group:2", 2));
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.RetryRetainedAsync().WaitAsync(Wait));
        Assert.Equal("group writes are not durable: group:2", error.Message);
    }

    [Fact]
    public async Task Stop_WritesEverythingSavedBeforeIt_AndRefusesButRetainsLaterWrites()
    {
        var rig = Rig.Create();
        var gate = new TaskCompletionSource();
        rig.Queue.Save("blocker", async _ => await gate.Task);
        rig.Queue.Save("group:1", s => s.SetAsync("group:1", 1));
        rig.Queue.Save("group:2", s => s.SetAsync("group:2", null));
        Task stopping = rig.Queue.StopAsync();
        gate.SetResult();
        await stopping.WaitAsync(Wait);

        Assert.Equal(["group:1=1", "group:2=deleted"], rig.Store.Log);

        rig.Queue.Save("group:3", s => s.SetAsync("group:3", 3));
        Assert.Equal(["group:3"], rig.Queue.RetainedKeys); // never silently dropped
        Assert.Null(rig.Store.Value("group:3"));
    }

    [Fact]
    public async Task Stop_RetriesARetainedWriteOnceMore_AndThrowsNamingWhatIsStillNotDurable()
    {
        var rig = Rig.Create();
        rig.Store.FailNext(3);
        rig.Queue.Save("group:1", s => s.SetAsync("group:1", 1));
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.Equal(["group:1"], rig.Queue.RetainedKeys);

        await rig.Queue.StopAsync().WaitAsync(Wait); // storage is back: the stop's retry makes it durable
        Assert.Equal(1, rig.Store.Value("group:1"));

        var failing = Rig.Create();
        failing.Store.FailNext(1000);
        failing.Queue.Save("group:9", s => s.SetAsync("group:9", 9));
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Queue.StopAsync().WaitAsync(Wait));
        Assert.Equal("group writes did not drain for group:9", error.Message);
    }

    [Fact]
    public async Task WithoutAStore_WritesAreNoOps_AndNothingIsRetained()
    {
        await using Rig rig = Rig.Create(withStore: false);
        rig.Queue.Save("group:1", s => s.SetAsync("group:1", 1));
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal(0, rig.Store.Attempts);
        Assert.Empty(rig.Queue.RetainedKeys);
        await rig.Queue.StopAsync().WaitAsync(Wait);
    }
}
