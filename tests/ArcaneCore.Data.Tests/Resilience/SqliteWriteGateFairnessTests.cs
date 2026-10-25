using System.Diagnostics;
using ArcaneCore.Data.Resilience;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.Data.Tests.Resilience;

/// <summary>
/// Arrival-order fairness of <see cref="SqliteWriteGate"/> for the async and the blocking path alike, and the guarantee that a throwing stuck-release handler neither
/// fails an innocent waiter nor leaks the gate. No SQLite file is created: the gate only needs a normalized path.
/// </summary>
public sealed class SqliteWriteGateFairnessTests
{
    private static SqliteWriteGate NewGate()
        => SqliteWriteGate.For(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetTempPath(), "gate-fair-" + Guid.NewGuid().ToString("N") + ".db"),
        }.ToString())!;

    private static void WaitUntilWaiting(SqliteWriteGate gate, int count)
    {
        var deadline = Stopwatch.StartNew();
        while (gate.Waiting != count)
        {
            Assert.True(deadline.ElapsedMilliseconds < 5000, $"Waiting stayed at {gate.Waiting}, expected {count}");
            Thread.Sleep(2);
        }
    }

    private static Task StartAsyncWaiter(SqliteWriteGate gate, int id, List<int> order, int timeoutMs = 10_000, int maxHoldMs = 0)
        => Task.Run(async () =>
        {
            SqliteWriteGate.Hold? hold = await gate.EnterAsync(timeoutMs, "w" + id, maxHoldMs, CancellationToken.None);
            Assert.NotNull(hold);
            lock (order) order.Add(id);
            hold.Release();
        });

    private static Task StartSyncWaiter(SqliteWriteGate gate, int id, List<int> order, int timeoutMs = 10_000, int maxHoldMs = 0)
        => Task.Factory.StartNew(() =>
        {
            SqliteWriteGate.Hold? hold = gate.Enter(timeoutMs, "w" + id, maxHoldMs);
            Assert.NotNull(hold);
            lock (order) order.Add(id);
            hold.Release();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    [Fact]
    public async Task AsyncWaiters_AreServedOldestFirst_WhenTheQueueIsOlderThanASecond()
    {
        SqliteWriteGate gate = NewGate();
        SqliteWriteGate.Hold h = gate.Enter(0, "H", 0)!;
        var order = new List<int>();
        var clock = Stopwatch.StartNew();
        var tasks = new List<Task> { StartAsyncWaiter(gate, 0, order) };
        WaitUntilWaiting(gate, 1);
        await Task.Delay(500);
        for (int i = 1; i <= 3; i++)
        {
            tasks.Add(StartAsyncWaiter(gate, i, order));
            WaitUntilWaiting(gate, i + 1);
        }

        int left = 1300 - (int)clock.ElapsedMilliseconds;
        if (left > 0) await Task.Delay(left);
        h.Release();
        await Task.WhenAll(tasks);
        Assert.Equal(new[] { 0, 1, 2, 3 }, order);
    }

    [Fact]
    public async Task AnAsyncWaiter_IsNotOvertakenByASyncWaiterQueuedAfterIt()
    {
        SqliteWriteGate gate = NewGate();
        SqliteWriteGate.Hold h = gate.Enter(0, "H", 0)!;
        var order = new List<int>();
        Task w0 = StartAsyncWaiter(gate, 0, order);
        WaitUntilWaiting(gate, 1);
        Task w1 = StartSyncWaiter(gate, 1, order);
        WaitUntilWaiting(gate, 2);
        h.Release();
        await Task.WhenAll(w0, w1);
        Assert.Equal(new[] { 0, 1 }, order);
    }

    [Fact]
    public async Task SyncWaiters_AreServedOldestFirst_WhenTheQueueIsOlderThanASecond()
    {
        SqliteWriteGate gate = NewGate();
        SqliteWriteGate.Hold h = gate.Enter(0, "H", 0)!;
        var order = new List<int>();
        var clock = Stopwatch.StartNew();
        var tasks = new List<Task> { StartSyncWaiter(gate, 0, order) };
        WaitUntilWaiting(gate, 1);
        await Task.Delay(500);
        for (int i = 1; i <= 3; i++)
        {
            tasks.Add(StartSyncWaiter(gate, i, order));
            WaitUntilWaiting(gate, i + 1);
        }

        int left = 1300 - (int)clock.ElapsedMilliseconds;
        if (left > 0) await Task.Delay(left);
        h.Release();
        await Task.WhenAll(tasks);
        Assert.Equal(new[] { 0, 1, 2, 3 }, order);
    }

    [Fact]
    public async Task AStuckReleaseWhoseHandlerThrows_StillGrantsTheWaiter_AndLeaksNothing()
    {
        SqliteWriteGate gate = NewGate();
        SqliteWriteGate.Hold leaked = gate.Enter(100, "leaked", 200)!;
        int ran = 0;
        void Throwing(SqliteWriteGate.Hold _)
        {
            Interlocked.Increment(ref ran);
            throw new InvalidOperationException("handler fault");
        }

        gate.StuckReleased += Throwing;
        try
        {
            var order = new List<int>();
            Task a = StartAsyncWaiter(gate, 0, order, 3000, 200);
            Task b = StartAsyncWaiter(gate, 1, order, 3000, 200);
            WaitUntilWaiting(gate, 2);
            await Task.WhenAll(a, b);
            Assert.Equal(2, order.Count);
        }
        finally
        {
            gate.StuckReleased -= Throwing;
        }

        Assert.True(leaked.Forced);
        Assert.Equal(1, Volatile.Read(ref ran));
        Assert.Equal(1, gate.HandlerFaultsTotal);
        Assert.Contains("InvalidOperationException: handler fault", gate.FirstHandlerFault);
        Assert.Null(gate.Current);
        Assert.Equal(0, gate.Waiting);
        SqliteWriteGate.Hold? fresh = gate.Enter(0, "fresh", 0);
        Assert.NotNull(fresh);
        fresh.Release();
    }

    [Fact]
    public async Task AStuckReleaseWhoseHandlerThrows_StillGrantsTheSyncWaiter_AndLeaksNothing()
    {
        SqliteWriteGate gate = NewGate();
        SqliteWriteGate.Hold leaked = gate.Enter(100, "leaked", 200)!;
        int ran = 0;
        void Throwing(SqliteWriteGate.Hold _)
        {
            Interlocked.Increment(ref ran);
            throw new InvalidOperationException("handler fault");
        }

        gate.StuckReleased += Throwing;
        try
        {
            var order = new List<int>();
            Task a = StartSyncWaiter(gate, 0, order, 3000, 200);
            WaitUntilWaiting(gate, 1);
            Task b = StartSyncWaiter(gate, 1, order, 3000, 200);
            WaitUntilWaiting(gate, 2);
            await Task.WhenAll(a, b);
            Assert.Equal(2, order.Count);
        }
        finally
        {
            gate.StuckReleased -= Throwing;
        }

        Assert.True(leaked.Forced);
        Assert.Equal(1, Volatile.Read(ref ran));
        Assert.Equal(1, gate.HandlerFaultsTotal);
        Assert.Contains("InvalidOperationException: handler fault", gate.FirstHandlerFault);
        Assert.Null(gate.Current);
        Assert.Equal(0, gate.Waiting);
        SqliteWriteGate.Hold? fresh = gate.Enter(0, "fresh", 0);
        Assert.NotNull(fresh);
        fresh.Release();
    }

    [Fact]
    public async Task AThrowingSubscriber_DoesNotSuppressTheSubscribersAfterIt()
    {
        SqliteWriteGate gate = NewGate();
        SqliteWriteGate.Hold leaked = gate.Enter(100, "leaked", 200)!;
        int firstRan = 0;
        int secondRan = 0;
        void First(SqliteWriteGate.Hold _)
        {
            Interlocked.Increment(ref firstRan);
            throw new InvalidOperationException("first subscriber fault");
        }

        void Second(SqliteWriteGate.Hold _) => Interlocked.Increment(ref secondRan);
        gate.StuckReleased += First;
        gate.StuckReleased += Second;
        try
        {
            SqliteWriteGate.Hold? hold = await gate.EnterAsync(3000, "waiter", 200, CancellationToken.None);
            Assert.NotNull(hold);
            hold.Release();
        }
        finally
        {
            gate.StuckReleased -= First;
            gate.StuckReleased -= Second;
        }

        Assert.True(leaked.Forced);
        Assert.Equal(1, Volatile.Read(ref firstRan));
        Assert.Equal(1, Volatile.Read(ref secondRan));
        Assert.Equal(1, gate.HandlerFaultsTotal);
        Assert.Contains("first subscriber fault", gate.FirstHandlerFault);
        Assert.Equal(1, gate.TakeStats().HandlerFaultsTotal);
    }

    [Fact]
    public async Task ManyMixedWaiters_WithCancelAndTimeout_LeaveAnIdleTakeableGate_WithAtMostOneHolder()
    {
        SqliteWriteGate gate = NewGate();
        var random = new Random(12345);
        var tasks = new List<Task>();
        var tokens = new List<CancellationTokenSource>();
        int holders = 0, maxHolders = 0, granted = 0, timedOut = 0, canceled = 0;
        SqliteWriteGate.Hold blocker = gate.Enter(0, "blocker", 0)!;
        try
        {
            for (int i = 0; i < 120; i++)
            {
                int id = i;
                bool sync = id % 2 == 0;
                bool cancels = !sync && id % 3 == 0;
                int timeoutMs = id % 5 == 0 ? random.Next(1, 40) : 20_000;
                int holdMs = random.Next(0, 3);
                CancellationTokenSource? cts = null;
                if (cancels)
                {
                    cts = new CancellationTokenSource();
                    cts.CancelAfter(random.Next(1, 30));
                    tokens.Add(cts);
                }

                CancellationToken token = cts?.Token ?? CancellationToken.None;
                tasks.Add(sync
                    ? Task.Factory.StartNew(() => Use(gate.Enter(timeoutMs, "s" + id, 0), holdMs), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                    : Task.Run(async () =>
                    {
                        try
                        {
                            Use(await gate.EnterAsync(timeoutMs, "a" + id, 0, token), holdMs);
                        }
                        catch (OperationCanceledException)
                        {
                            Interlocked.Increment(ref canceled);
                        }
                    }));
            }

            // Everyone queues behind the blocker, so the short timeouts and the cancels fire while it is held; then the survivors are served one at a time.
            await Task.Delay(150);
        }
        finally
        {
            blocker.Release();
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
        foreach (CancellationTokenSource source in tokens) source.Dispose();
        Assert.Equal(0, gate.Waiting);
        Assert.Null(gate.Current);
        Assert.Equal(1, Volatile.Read(ref maxHolders));
        Assert.True(Volatile.Read(ref granted) > 0, "no waiter was granted");
        Assert.True(Volatile.Read(ref timedOut) > 0, "no waiter timed out");
        Assert.True(Volatile.Read(ref canceled) > 0, "no waiter was canceled");
        Assert.Equal(120, granted + timedOut + canceled);
        SqliteWriteGate.Hold? fresh = gate.Enter(0, "fresh", 0);
        Assert.NotNull(fresh);
        fresh.Release();

        void Use(SqliteWriteGate.Hold? hold, int holdMs)
        {
            if (hold is null)
            {
                Interlocked.Increment(ref timedOut);
                return;
            }

            int now = Interlocked.Increment(ref holders);
            int seen;
            while ((seen = Volatile.Read(ref maxHolders)) < now && Interlocked.CompareExchange(ref maxHolders, now, seen) != seen)
            {
            }

            Interlocked.Increment(ref granted);
            if (holdMs > 0) Thread.Sleep(holdMs);
            Interlocked.Decrement(ref holders);
            hold.Release();
        }
    }

    [Fact]
    public async Task ACanceledWaiter_LeavesTheQueue_AndTheNextIsServed()
    {
        SqliteWriteGate gate = NewGate();
        SqliteWriteGate.Hold h = gate.Enter(0, "H", 0)!;
        using var cts = new CancellationTokenSource();
        Task<SqliteWriteGate.Hold?> w0 = Task.Run(async () => await gate.EnterAsync(10_000, "w0", 0, cts.Token));
        WaitUntilWaiting(gate, 1);
        var order = new List<int>();
        Task w1 = StartAsyncWaiter(gate, 1, order);
        WaitUntilWaiting(gate, 2);
        cts.Cancel();
        WaitUntilWaiting(gate, 1);
        h.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w0);
        await w1;
        Assert.Equal(new[] { 1 }, order);
        Assert.Equal(0, gate.Waiting);
        Assert.Null(gate.Current);
    }

    [Fact]
    public async Task ATimedOutWaiter_ReturnsNull_WithinItsBound_AndLeavesTheQueue()
    {
        SqliteWriteGate gate = NewGate();
        SqliteWriteGate.Hold h = gate.Enter(0, "H", 0)!;
        long timeouts = gate.TimeoutsTotal;
        // Each wait is timed inside its own task, so pool scheduling delay is excluded. A 300 ms bound must be honoured by the 1 s wake slice
        // (Math.Min(remaining, 1000)): a fixed 1 s slice would take about 1000 ms, an instant null would take about 0.
        (SqliteWriteGate.Hold? Hold, double Ms) asyncResult = await Task.Run(async () =>
        {
            long start = Stopwatch.GetTimestamp();
            SqliteWriteGate.Hold? hold = await gate.EnterAsync(300, "async", 0, CancellationToken.None);
            return (hold, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        });
        (SqliteWriteGate.Hold? Hold, double Ms) syncResult = await Task.Run(() =>
        {
            long start = Stopwatch.GetTimestamp();
            SqliteWriteGate.Hold? hold = gate.Enter(300, "sync", 0);
            return (hold, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        });
        Assert.Null(asyncResult.Hold);
        Assert.Null(syncResult.Hold);
        Assert.InRange(asyncResult.Ms, 250, 800);
        Assert.InRange(syncResult.Ms, 250, 800);
        Assert.Equal(0, gate.Waiting);
        Assert.Equal(timeouts + 2, gate.TimeoutsTotal);
        h.Release();
        SqliteWriteGate.Hold? fresh = gate.Enter(0, "fresh", 0);
        Assert.NotNull(fresh);
        fresh.Release();
    }
}
