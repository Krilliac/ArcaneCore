using System.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>
/// <see cref="TaxiFlightWriteQueue"/>: the logout save and landing delete leave the world thread, a failing store retains
/// the write until recovery (the login barrier refuses meanwhile), and the last request for a character always wins.
/// </summary>
public sealed class TaxiFlightWriteQueueTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly TaxiFlightRoute Route = new([1, 2, 3], [10, 11], [5, 7]);

    [Fact]
    public async Task SlowStore_DoesNotBlockTheCaller_AndTheWriteLandsAfterwards()
    {
        await using var fixture = Fixture.Create();
        fixture.Store.HoldNextWrite();

        Stopwatch watch = Stopwatch.StartNew();
        fixture.Queue.Save(7, Route);
        fixture.Queue.Delete(8);
        watch.Stop();

        await fixture.Store.Entered.Task.WaitAsync(Budget);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "queuing must not wait for the store");
        Assert.Null(fixture.Store.Saved(7));
        fixture.Store.Release.TrySetResult();
        await fixture.Queue.FlushAsync().WaitAsync(Budget);

        Assert.Equal(Route, fixture.Store.Saved(7));
        Assert.Equal(0, fixture.Queue.Pending);
        Assert.Empty(fixture.Queue.RetainedCharacters);
    }

    [Fact]
    public async Task FailingStore_RetainsTheWrite_RefusesTheLoginBarrier_ThenRecovers()
    {
        await using var fixture = Fixture.Create();
        fixture.Store.FailWrites = true;
        fixture.Queue.Save(7, Route);
        await fixture.Queue.FlushAsync().WaitAsync(Budget);

        Assert.True(fixture.Queue.HasRetainedFailure(7));
        Assert.Equal([7], fixture.Queue.RetainedCharacters);
        Assert.Equal(3, fixture.Store.Attempts(7));
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Queue.FlushCharacterAsync(7));
        Assert.Contains("7", refused.Message);
        Assert.IsType<IOException>(refused.InnerException);
        await fixture.Queue.FlushCharacterAsync(8); // another character is unaffected
        Assert.Null(fixture.Store.Saved(7));

        fixture.Store.FailWrites = false;
        await fixture.Queue.FlushCharacterAsync(7);
        Assert.False(fixture.Queue.HasRetainedFailure(7));
        Assert.Equal(Route, fixture.Store.Saved(7));
    }

    [Fact]
    public async Task RequestRetry_PersistsARetainedWriteWithoutABarrier()
    {
        await using var fixture = Fixture.Create();
        fixture.Store.FailWrites = true;
        fixture.Queue.Save(7, Route);
        await fixture.Queue.FlushAsync().WaitAsync(Budget);
        fixture.Store.FailWrites = false;

        fixture.Queue.RequestRetry(7);
        await fixture.Queue.FlushAsync().WaitAsync(Budget);

        Assert.False(fixture.Queue.HasRetainedFailure(7));
        Assert.Equal(Route, fixture.Store.Saved(7));
    }

    [Fact]
    public async Task FailingSaveWithAFallback_RunsItOnce_AndDeletesTheRowInsteadOfRetainingTheSave()
    {
        await using var fixture = Fixture.Create();
        fixture.Store.FailWrites = true;
        int abandoned = 0;
        fixture.Queue.Save(7, Route, () => Interlocked.Increment(ref abandoned));
        await fixture.Queue.FlushAsync().WaitAsync(Budget);

        // The save failed every attempt; the fallback ran and the delete that replaced it is retained while the store is down.
        Assert.Equal(1, abandoned);
        Assert.Equal(6, fixture.Store.Attempts(7));
        Assert.True(fixture.Queue.HasRetainedFailure(7));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Queue.FlushCharacterAsync(7));
        Assert.Equal(1, abandoned);

        fixture.Store.FailWrites = false;
        await fixture.Queue.FlushCharacterAsync(7);
        Assert.False(fixture.Queue.HasRetainedFailure(7));
        Assert.Null(fixture.Store.Saved(7));
        Assert.Equal(["delete 7"], fixture.Store.Calls);
        Assert.Equal(1, abandoned);
    }

    [Fact]
    public async Task AbandonFallback_IsNotRunWhenTheStopDrainRefusesTheWrite()
    {
        await using var fixture = Fixture.Create(drain: new StopDrainBudget { StallBudget = TimeSpan.Zero });
        fixture.Store.HoldNextWrite();
        fixture.Queue.Save(8, Route); // keeps the consumer busy while the stop begins
        await fixture.Store.Entered.Task.WaitAsync(Budget);
        bool abandoned = false;
        fixture.Queue.Save(7, Route, () => abandoned = true);

        Task stopping = fixture.Queue.StopAsync(); // begins the lane at once: with no stall budget, nothing more may be attempted
        fixture.Store.Release.TrySetResult();
        InvalidOperationException stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => stopping.WaitAsync(Budget));

        Assert.Contains("7", stopped.Message, StringComparison.Ordinal);
        Assert.False(abandoned); // the refusal is not a verdict on the route: no departure-node snapshot is written over the logout snapshot
        Assert.Equal(0, fixture.Store.Attempts(7));
        Assert.True(fixture.Queue.HasRetainedFailure(7));
        InvalidOperationException retained = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Queue.FlushCharacterAsync(7));
        Assert.IsType<StopDrainExhaustedException>(retained.InnerException);
        Assert.Null(fixture.Store.Saved(7));
        Assert.DoesNotContain(fixture.Store.Calls, c => c.EndsWith(" 7", StringComparison.Ordinal)); // neither a save nor a delete
    }

    [Fact]
    public async Task ARealNonBusyFailureAtStop_StillAbandonsAfterItsSingleAttempt()
    {
        await using var fixture = Fixture.Create(drain: new StopDrainBudget());
        fixture.Store.HoldNextWrite();
        fixture.Queue.Save(8, Route);
        await fixture.Store.Entered.Task.WaitAsync(Budget);
        int abandoned = 0;
        fixture.Queue.Save(7, Route, () => Interlocked.Increment(ref abandoned));
        fixture.Store.FailWrites = true;

        Task stopping = fixture.Queue.StopAsync();
        fixture.Store.Release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => stopping.WaitAsync(Budget));

        Assert.Equal(1, abandoned); // an attempt the store really failed, not busy: the verdict stands
        Assert.Equal(3, fixture.Store.Attempts(7)); // the save, the delete that replaced it, and the delete once more in the final pass: one attempt each, no inline retry
    }

    [Fact]
    public async Task ALockedDatabase_KeepsTheRouteInsteadOfAbandoningIt_AndTheSaveLandsOnTheNextBarrier()
    {
        await using var fixture = Fixture.Create();
        fixture.Store.FailBusy = true;
        int abandoned = 0;
        fixture.Queue.Save(7, Route, () => Interlocked.Increment(ref abandoned));
        await fixture.Queue.FlushAsync().WaitAsync(Budget);

        // Another writer holds the file: that says nothing about the route, so the player is not teleported and the row is not deleted.
        Assert.Equal(0, abandoned);
        Assert.True(fixture.Queue.HasRetainedFailure(7));
        Assert.Empty(fixture.Store.Calls);

        fixture.Store.FailBusy = false;
        await fixture.Queue.FlushCharacterAsync(7).WaitAsync(Budget);
        Assert.False(fixture.Queue.HasRetainedFailure(7));
        Assert.NotNull(fixture.Store.Saved(7));
        Assert.Equal(0, abandoned);
    }

    [Fact]
    public async Task ALockThatOutlastsTheInlineRetries_IsLandedByTheRetrySweep_AndTheRouteIsNeverAbandoned()
    {
        await using var fixture = Fixture.Create(Timeout.InfiniteTimeSpan);
        fixture.Store.LockedWrites = 4; // three inline attempts fail, the first sweep fails, the second lands
        int abandoned = 0;
        fixture.Queue.Save(7, Route, () => Interlocked.Increment(ref abandoned));
        await fixture.Queue.FlushAsync().WaitAsync(Budget);

        Assert.True(fixture.Queue.HasRetainedFailure(7));
        Assert.Equal(3, fixture.Store.Attempts(7));

        // No timer: the sweeps are driven explicitly, so the attempt counts are exact.
        Assert.True(fixture.Queue.PostRetainedSweepForTest());
        await fixture.Queue.FlushAsync().WaitAsync(Budget);
        Assert.True(fixture.Queue.HasRetainedFailure(7));
        Assert.Equal(4, fixture.Store.Attempts(7));

        Assert.True(fixture.Queue.PostRetainedSweepForTest());
        await fixture.Queue.FlushAsync().WaitAsync(Budget);
        Assert.False(fixture.Queue.HasRetainedFailure(7));
        Assert.Equal(5, fixture.Store.Attempts(7));
        Assert.NotNull(fixture.Store.Saved(7));
        Assert.Equal(0, abandoned);
    }

    [Fact]
    public async Task ASweepThatFailsForAnotherReason_LeavesTheRouteRetained_AndNeverAbandonsIt()
    {
        await using var fixture = Fixture.Create(Timeout.InfiniteTimeSpan);
        fixture.Store.FailBusy = true; // the inline attempts fail as a lock: retained, not abandoned
        int abandoned = 0;
        fixture.Queue.Save(7, Route, () => Interlocked.Increment(ref abandoned));
        await fixture.Queue.FlushAsync().WaitAsync(Budget);
        Assert.True(fixture.Queue.HasRetainedFailure(7));

        fixture.Store.FailBusy = false;
        fixture.Store.FailWrites = true; // now a failure that is not a lock
        Assert.True(fixture.Queue.PostRetainedSweepForTest());
        await fixture.Queue.FlushAsync().WaitAsync(Budget);

        Assert.True(fixture.Queue.HasRetainedFailure(7));
        Assert.Equal(0, abandoned);
        Assert.Empty(fixture.Store.Calls);

        fixture.Store.FailWrites = false;
        Assert.True(fixture.Queue.PostRetainedSweepForTest());
        await fixture.Queue.FlushAsync().WaitAsync(Budget);
        Assert.False(fixture.Queue.HasRetainedFailure(7));
        Assert.Equal(Route, fixture.Store.Saved(7));
        Assert.Equal(0, abandoned);
    }

    [Fact]
    public async Task TheRealRetryTimer_EventuallyLandsAWriteHeldByALongLock()
    {
        await using var fixture = Fixture.Create(TimeSpan.FromMilliseconds(50));
        fixture.Store.LockedWrites = 4;
        fixture.Queue.Save(7, Route);
        await fixture.Queue.FlushAsync().WaitAsync(Budget);

        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (fixture.Queue.HasRetainedFailure(7))
        {
            Assert.True(DateTime.UtcNow < deadline, "the retained write was never retried by the timer");
            await Task.Delay(10);
        }

        Assert.NotNull(fixture.Store.Saved(7));
    }

    [Fact]
    public async Task ASupersededFailingSave_DoesNotRunItsFallback()
    {
        await using var fixture = Fixture.Create();
        var other = new TaxiFlightRoute([4, 5], [20], [9]);
        int abandoned = 0;
        fixture.Store.HoldNextWrite();
        fixture.Store.FailWrites = true;
        fixture.Queue.Save(7, Route, () => Interlocked.Increment(ref abandoned));
        await fixture.Store.Entered.Task.WaitAsync(Budget);
        fixture.Queue.Save(7, other); // the newer request is authoritative
        fixture.Store.Release.TrySetResult();
        await fixture.Queue.FlushAsync().WaitAsync(Budget);
        fixture.Store.FailWrites = false;
        await fixture.Queue.FlushCharacterAsync(7);

        Assert.Equal(0, abandoned);
        Assert.Equal(other, fixture.Store.Saved(7));
    }

    [Fact]
    public async Task SaveThenDelete_AppliesInOrder_DeleteWinsEvenWhileTheSaveIsInFlight()
    {
        await using var fixture = Fixture.Create();
        fixture.Store.HoldNextWrite();
        fixture.Queue.Save(7, Route);
        await fixture.Store.Entered.Task.WaitAsync(Budget);
        fixture.Queue.Delete(7);
        fixture.Store.Release.TrySetResult();
        await fixture.Queue.FlushAsync().WaitAsync(Budget);

        Assert.Equal(["save 7", "delete 7"], fixture.Store.Calls);
        Assert.Null(fixture.Store.Saved(7));
    }

    [Fact]
    public async Task DeleteThenSave_KeepsTheRoute_AndASupersededFailedWriteIsNotRetained()
    {
        await using var fixture = Fixture.Create();
        fixture.Store.FailWrites = true;
        fixture.Queue.Delete(7);
        await fixture.Queue.FlushAsync().WaitAsync(Budget);
        Assert.True(fixture.Queue.HasRetainedFailure(7));

        fixture.Store.FailWrites = false;
        fixture.Queue.Save(7, Route); // replaces the failed delete and is itself attempted
        await fixture.Queue.FlushAsync().WaitAsync(Budget);

        Assert.False(fixture.Queue.HasRetainedFailure(7));
        Assert.Equal(Route, fixture.Store.Saved(7));
        Assert.Equal(["save 7"], fixture.Store.Calls);
    }

    [Fact]
    public async Task CoalescedRequests_WriteOnlyTheLastOne()
    {
        await using var fixture = Fixture.Create();
        var other = new TaxiFlightRoute([4, 5], [20], [9]);
        fixture.Store.HoldNextWrite();
        fixture.Queue.Save(7, Route);
        await fixture.Store.Entered.Task.WaitAsync(Budget);
        fixture.Queue.Save(7, other);
        fixture.Queue.Delete(7);
        fixture.Queue.Save(7, other);
        fixture.Store.Release.TrySetResult();
        await fixture.Queue.FlushAsync().WaitAsync(Budget);

        Assert.Equal(["save 7", "save 7"], fixture.Store.Calls);
        Assert.Equal(other, fixture.Store.Saved(7));
    }

    [Fact]
    public async Task Stop_DrainsRetainedWrites_OrThrowsNamingTheCharacters_AndIsIdempotent()
    {
        await using var recovering = Fixture.Create();
        recovering.Store.FailWrites = true;
        recovering.Queue.Save(7, Route);
        await recovering.Queue.FlushAsync().WaitAsync(Budget);
        recovering.Store.FailWrites = false;
        await recovering.Queue.StopAsync();
        Assert.Equal(Route, recovering.Store.Saved(7));

        await using var failing = Fixture.Create();
        failing.Store.FailWrites = true;
        failing.Queue.Save(9, Route);
        await failing.Queue.FlushAsync().WaitAsync(Budget);
        InvalidOperationException first = await Assert.ThrowsAsync<InvalidOperationException>(failing.Queue.StopAsync);
        InvalidOperationException second = await Assert.ThrowsAsync<InvalidOperationException>(failing.Queue.StopAsync);
        Assert.Same(first, second);
        Assert.Contains("9", first.Message);

        failing.Queue.Delete(9); // after stop: logged and retained in memory, never thrown on the world thread
        Assert.True(failing.Queue.HasRetainedFailure(9));
        await failing.Queue.FlushAsync().WaitAsync(Budget);
    }

    [Fact]
    public async Task WithoutAStore_WritesAreANoOp()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var queue = new TaxiFlightWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        queue.Start();
        queue.Save(1, Route);
        await queue.FlushCharacterAsync(1);
        Assert.False(queue.HasRetainedFailure(1));
        await queue.StopAsync();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private Fixture(ServiceProvider services, FakeStore store, TimeSpan? retryInterval, StopDrainBudget? drain)
        {
            _services = services;
            Store = store;
            Queue = new TaxiFlightWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, drain)
            {
                RetainedRetryInterval = retryInterval ?? TimeSpan.FromSeconds(30),
            };
            Queue.Start();
        }

        public FakeStore Store { get; }
        public TaxiFlightWriteQueue Queue { get; }

        public static Fixture Create(TimeSpan? retryInterval = null, StopDrainBudget? drain = null)
        {
            var store = new FakeStore();
            return new Fixture(new ServiceCollection().AddSingleton<ICharacterTaxiFlightStore>(store).BuildServiceProvider(), store, retryInterval, drain);
        }

        public async ValueTask DisposeAsync()
        {
            Store.FailWrites = false;
            Store.Release.TrySetResult();
            try
            {
                await Queue.StopAsync();
            }
            catch (InvalidOperationException)
            {
                // a test that proves the shutdown failure leaves the failure cached
            }

            await _services.DisposeAsync();
        }
    }

    private sealed class FakeStore : ICharacterTaxiFlightStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<int, TaxiFlightRoute> _rows = [];
        private readonly Dictionary<int, int> _attempts = [];
        private readonly List<string> _calls = [];
        private int _hold;

        public volatile bool FailWrites;
        public volatile bool FailBusy;

        /// <summary>The number of upcoming writes that fail as SQLite reports a file another writer holds (a lock that clears by itself).</summary>
        public int LockedWrites;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Successful writes in order.</summary>
        public string[] Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public void HoldNextWrite() => Volatile.Write(ref _hold, 1);

        public int Attempts(int id)
        {
            lock (_gate)
            {
                return _attempts.GetValueOrDefault(id);
            }
        }

        public TaxiFlightRoute? Saved(int id)
        {
            lock (_gate)
            {
                return _rows.GetValueOrDefault(id);
            }
        }

        public Task<TaxiFlightRoute?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(Saved(characterId));

        public async Task SaveAsync(int characterId, TaxiFlightRoute route, CancellationToken cancellationToken = default)
        {
            await BeforeAsync(characterId);
            lock (_gate)
            {
                _rows[characterId] = route;
                _calls.Add($"save {characterId}");
            }
        }

        public async Task DeleteAsync(int characterId, CancellationToken cancellationToken = default)
        {
            await BeforeAsync(characterId);
            lock (_gate)
            {
                _rows.Remove(characterId);
                _calls.Add($"delete {characterId}");
            }
        }

        private async Task BeforeAsync(int id)
        {
            lock (_gate)
            {
                _attempts[id] = _attempts.GetValueOrDefault(id) + 1;
            }

            if (Interlocked.Exchange(ref _hold, 0) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }

            int left;
            do
            {
                left = Volatile.Read(ref LockedWrites);
            }
            while (left > 0 && Interlocked.CompareExchange(ref LockedWrites, left - 1, left) != left);
            if (left > 0)
            {
                throw new Microsoft.Data.Sqlite.SqliteException("SQLite Error 5: 'database is locked'.", 5);
            }

            if (FailBusy)
            {
                throw new Microsoft.Data.Sqlite.SqliteException("SQLite Error 5: 'database is locked'.", 5);
            }

            if (FailWrites)
            {
                throw new IOException("controlled taxi route storage failure");
            }
        }
    }
}
