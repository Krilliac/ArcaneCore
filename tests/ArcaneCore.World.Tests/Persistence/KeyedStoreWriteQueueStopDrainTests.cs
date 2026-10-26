using System.Diagnostics;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>The shutdown stall bound on <see cref="KeyedStoreWriteQueue{TStore}"/> (GM audit, groups, account address).</summary>
[Collection(PersistAnomalyCollection.Name)]
public sealed class KeyedStoreWriteQueueStopDrainTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private const string Name = "stopdrain";

    private sealed class Store;

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public Rig(StopDrainBudget lane)
        {
            _provider = new ServiceCollection().AddSingleton(new Store()).BuildServiceProvider();
            Queue = new KeyedStoreWriteQueue<Store>(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, Name, lane)
            {
                RetryDelay = TimeSpan.FromMilliseconds(1),
                RetainedRetryInterval = Timeout.InfiniteTimeSpan,
            };
            Queue.Start();
        }

        public KeyedStoreWriteQueue<Store> Queue { get; }

        public int Attempts;
        public volatile bool Busy;
        public volatile bool Fail;
        public volatile int DelayMs;

        public void Save(string key)
            => Queue.Save(key, _ =>
            {
                Interlocked.Increment(ref Attempts);
                if (DelayMs > 0)
                {
                    Thread.Sleep(DelayMs);
                }

                if (Busy)
                {
                    throw new SqliteException("SQLite Error 5: 'database is locked'.", 5);
                }

                if (Fail)
                {
                    throw new IOException("store down");
                }

                return Task.CompletedTask;
            });

        public async ValueTask DisposeAsync()
        {
            Busy = false;
            Fail = false;
            try
            {
                await Queue.StopAsync().WaitAsync(Wait);
            }
            catch (InvalidOperationException)
            {
                // a scenario that ends with retained writes
            }

            await _provider.DisposeAsync();
        }
    }

    private static StopDrainBudget Lane() => new() { StallBudget = TimeSpan.FromMilliseconds(300) };

    private static string Key(int id) => "key:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<int> RetainThenLockAsync(Rig rig, int keys)
    {
        rig.Fail = true;
        for (int id = 1; id <= keys; id++)
        {
            rig.Save(Key(id));
        }

        await rig.Queue.FlushAsync().WaitAsync(Wait);
        int before = Volatile.Read(ref rig.Attempts);
        rig.Fail = false;
        rig.Busy = true;
        rig.DelayMs = 200;
        return before;
    }

    [Fact]
    public async Task Stop_HealthySlowDrain_IsNeverCut()
    {
        await using Rig rig = new(Lane());
        rig.DelayMs = 40;
        var elapsed = Stopwatch.StartNew();
        for (int id = 1; id <= 30; id++)
        {
            rig.Save(Key(id));
        }

        await rig.Queue.StopAsync().WaitAsync(Wait);
        elapsed.Stop();

        Assert.Equal(30, Volatile.Read(ref rig.Attempts));
        Assert.Empty(rig.Queue.RetainedKeys);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(1), $"the drain took {elapsed.Elapsed}");
    }

    [Fact]
    public async Task Stop_WithALockedDatabase_StopsAttemptingOnceStalled_AndNamesEveryKey()
    {
        const int keys = 10;
        await using Rig rig = new(Lane());
        int before = await RetainThenLockAsync(rig, keys);

        InvalidOperationException stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.StopAsync().WaitAsync(Wait));

        for (int id = 1; id <= keys; id++)
        {
            Assert.Contains(Key(id), stopped.Message, StringComparison.Ordinal);
        }

        Assert.InRange(Volatile.Read(ref rig.Attempts) - before, 1, 3);
        Assert.Equal(keys, rig.Queue.RetainedKeys.Count);
    }

    [Fact]
    public async Task Stop_MakesOneAttemptPerRetainedWrite_NoInlineRetry()
    {
        await using Rig rig = new(Lane());
        rig.Fail = true;
        rig.Save(Key(1));
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.Equal(3, Volatile.Read(ref rig.Attempts));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.StopAsync().WaitAsync(Wait));

        Assert.Equal(4, Volatile.Read(ref rig.Attempts));
    }

    [Fact]
    public async Task Stop_RefusedWrites_ReportNoRetryExhaustedAnomaly_AndTheAttemptsInTheDetailAreTheAttemptsMade()
    {
        const int keys = 10;
        await using Rig rig = new(Lane());

        // A busy write is attempted once (the queue never waits out a lock inline): the anomaly says attempts=1, not the constant 3.
        using (AnomalyCapture busy = PersistAnomalies.Capture())
        {
            rig.Busy = true;
            rig.Save(Key(99));
            await rig.Queue.FlushAsync().WaitAsync(Wait);
            AnomalyNotice exhausted = Assert.Single(busy.Of("write-retry-exhausted", Name));
            Assert.Contains("attempts=1", exhausted.Detail, StringComparison.Ordinal);
            Assert.Contains("error=SqliteException", exhausted.Detail, StringComparison.Ordinal);
            rig.Busy = false;
            await rig.Queue.RetryRetainedAsync().WaitAsync(Wait);
        }

        await RetainThenLockAsync(rig, keys);
        using AnomalyCapture capture = PersistAnomalies.Capture();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.StopAsync().WaitAsync(Wait));

        AnomalyNotice dropped = Assert.Single(capture.Of("write-dropped-on-shutdown", Name));
        Assert.Contains("refusedByStopDrain=", dropped.Detail, StringComparison.Ordinal);
        Assert.Contains("undrainedKeys=10", dropped.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(capture.Notices, n => n.Code == "write-retry-exhausted" && n.Detail.Contains("StopDrainExhaustedException", StringComparison.Ordinal));
    }
}
