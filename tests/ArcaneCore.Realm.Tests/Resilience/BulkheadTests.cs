using ArcaneCore.Kernel.Resilience;
using Xunit;

namespace ArcaneCore.Realm.Tests.Resilience;

public sealed class BulkheadTests
{
    [Fact]
    public async Task BeyondSlotsAndQueue_RejectsAtOnce_WithoutWaiting()
    {
        using var bulkhead = new Bulkhead(new BulkheadOptions { Name = "db", MaxConcurrency = 2, MaxQueue = 1 });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        static ValueTask<int> Hold(TaskCompletionSource g, CancellationToken _) => new(g.Task.ContinueWith(_ => 1, TaskScheduler.Default));

        ValueTask<int> first = bulkhead.ExecuteAsync(Hold, gate);
        ValueTask<int> second = bulkhead.ExecuteAsync(Hold, gate);
        Assert.Equal(2, bulkhead.Running);
        Assert.Equal(0, bulkhead.Queued);

        ValueTask<int> queued = bulkhead.ExecuteAsync(Hold, gate);
        Assert.Equal(1, bulkhead.Queued);

        ValueTask<int> rejected = bulkhead.ExecuteAsync(Hold, gate);
        Assert.True(rejected.IsFaulted); // no wait at all
        BulkheadRejectedException ex = await Assert.ThrowsAsync<BulkheadRejectedException>(async () => await rejected);
        Assert.Equal("db", ex.BulkheadName);
        Assert.Equal(1, bulkhead.Rejected);

        gate.SetResult();
        Assert.Equal(3, await first + await second + await queued);
        Assert.Equal(0, bulkhead.Running);
        Assert.Equal(0, bulkhead.Queued);
    }

    [Fact]
    public async Task QueuedCaller_RunsWhenASlotFrees()
    {
        using var bulkhead = new Bulkhead(new BulkheadOptions { MaxConcurrency = 1, MaxQueue = 4 });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask<int> holder = bulkhead.ExecuteAsync(static (g, _) => new ValueTask<int>(g.Task.ContinueWith(_ => 1, TaskScheduler.Default)), gate);
        ValueTask<int> waiter = bulkhead.ExecuteAsync(static (x, _) => new ValueTask<int>(x), 2);
        Assert.False(waiter.IsCompleted);
        gate.SetResult();
        Assert.Equal(1, await holder);
        Assert.Equal(2, await waiter.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task QueuedCaller_CanBeCancelled_AndLeavesTheQueue()
    {
        using var bulkhead = new Bulkhead(new BulkheadOptions { MaxConcurrency = 1, MaxQueue = 4 });
        Assert.True(bulkhead.TryEnter());
        using var cts = new CancellationTokenSource();
        ValueTask<int> waiter = bulkhead.ExecuteAsync(static (x, _) => new ValueTask<int>(x), 2, cts.Token);
        Assert.Equal(1, bulkhead.Queued);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter);
        Assert.Equal(0, bulkhead.Queued);
        bulkhead.Exit();
        Assert.Equal(0, bulkhead.Running);
    }

    [Fact]
    public async Task FailingOperation_ReleasesItsSlot()
    {
        using var bulkhead = new Bulkhead(new BulkheadOptions { MaxConcurrency = 1 });
        await Assert.ThrowsAsync<IOException>(async () => await bulkhead.ExecuteAsync<int, int>(static (_, _) => throw new IOException(), 0));
        await Assert.ThrowsAsync<IOException>(async () => await bulkhead.ExecuteAsync<int, int>(static (_, _) => ValueTask.FromException<int>(new IOException()), 0));
        Assert.Equal(0, bulkhead.Running);
    }

    [Fact]
    public async Task ConcurrentCallers_NeverExceedTheCap()
    {
        using var bulkhead = new Bulkhead(new BulkheadOptions { MaxConcurrency = 4, MaxQueue = 1000 });
        int inside = 0;
        int peak = 0;
        Task[] tasks = [.. Enumerable.Range(0, 200).Select(_ => bulkhead.ExecuteAsync(
            async (_, _) =>
            {
                int now = Interlocked.Increment(ref inside);
                int seen;
                do
                {
                    seen = Volatile.Read(ref peak);
                }
                while (now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen);
                await Task.Yield();
                Interlocked.Decrement(ref inside);
                return 0;
            },
            0).AsTask())];
        await Task.WhenAll(tasks);
        Assert.InRange(peak, 1, 4);
        Assert.Equal(0, bulkhead.Running);
    }

    [Fact]
    public void FreeSlotFastPath_AllocatesNothing()
    {
        using var bulkhead = new Bulkhead(new BulkheadOptions { MaxConcurrency = 4 });
        static ValueTask<int> Op(int x, CancellationToken _) => new(x);
        Func<int, CancellationToken, ValueTask<int>> op = Op;
        for (int i = 0; i < 2000; i++)
        {
            _ = bulkhead.ExecuteAsync(op, i);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            Assert.True(bulkhead.ExecuteAsync(op, i).IsCompletedSuccessfully);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
