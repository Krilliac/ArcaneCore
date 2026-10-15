using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ValueStore = ArcaneCore.World.Tests.Persistence.KeyedStoreWriteQueueTests.ValueStore;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>The <c>persist/*</c> anomalies of <see cref="KeyedStoreWriteQueue{TStore}"/> (queue name "group" here).</summary>
[Collection(PersistAnomalyCollection.Name)]
public sealed class KeyedStoreWriteQueueAnomalyTests
{
    private const string QueueName = "group";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public Rig()
        {
            _provider = new ServiceCollection().AddSingleton(Store).BuildServiceProvider();
            Queue = new KeyedStoreWriteQueue<ValueStore>(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, QueueName)
            {
                RetryDelay = TimeSpan.FromMilliseconds(1),
                RetainedRetryInterval = TimeSpan.FromHours(1),
            };
            Queue.Start();
        }

        public ValueStore Store { get; } = new();

        public KeyedStoreWriteQueue<ValueStore> Queue { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Queue.StopAsync();
            }
            catch (InvalidOperationException)
            {
                // a scenario that ends with a retained write
            }

            await _provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task WriteDroppedOnShutdown_FiresOnce_WhenAWriteArrivesAfterTheQueueStopped()
    {
        await using var rig = new Rig();
        await rig.Queue.StopAsync().WaitAsync(Wait);
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Save("group:3", s => s.SetAsync("group:3", 3));

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal("persist/write-dropped-on-shutdown", notice.Key);
        Assert.Equal(AnomalySeverity.Error, notice.Severity);
        Assert.Equal("queue group", notice.Subject);
        Assert.Contains("key=group:3", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("reason=queue-stopped", notice.Detail, StringComparison.Ordinal);
        Assert.Equal(["group:3"], rig.Queue.RetainedKeys); // and the write is still retained, not lost
    }

    [Fact]
    public async Task WriteDroppedOnShutdown_FiresOnce_WhenTheStopCannotDrainARetainedWrite()
    {
        var rig = new Rig();
        rig.Store.FailNext(1000);
        rig.Queue.Save("group:9", s => s.SetAsync("group:9", 9));
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        using AnomalyCapture capture = PersistAnomalies.Capture();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.StopAsync().WaitAsync(Wait));

        AnomalyNotice dropped = Assert.Single(capture.Of("write-dropped-on-shutdown", QueueName));
        Assert.Equal(AnomalySeverity.Error, dropped.Severity);
        Assert.Contains("undrainedKeys=1", dropped.Detail, StringComparison.Ordinal);
        Assert.Contains("firstKey=group:9", dropped.Detail, StringComparison.Ordinal);
        await rig.DisposeAsync();
    }

    [Fact]
    public async Task WriteRetryExhausted_FiresOnce_WhenAWriteFailsAllThreeAttempts()
    {
        await using var rig = new Rig();
        rig.Store.FailNext(3);
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Save("group:7", s => s.SetAsync("group:7", 7));
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal("persist/write-retry-exhausted", notice.Key);
        Assert.Equal(AnomalySeverity.Error, notice.Severity);
        Assert.Equal("queue group", notice.Subject);
        Assert.Contains("key=group:7", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("attempts=3", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("error=InvalidOperationException", notice.Detail, StringComparison.Ordinal);
        Assert.Equal(3, rig.Store.Attempts);
    }

    [Fact]
    public async Task HealthyQueue_ReportsNothing_ThroughSaveFlushRetryAndStop()
    {
        var rig = new Rig();
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Save("group:1", s => s.SetAsync("group:1", 1));
        rig.Queue.Save("group:1", s => s.SetAsync("group:1", 2)); // coalesced
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        await rig.Queue.RetryRetainedAsync().WaitAsync(Wait);
        await rig.Queue.StopAsync().WaitAsync(Wait);

        Assert.Equal(2, rig.Store.Value("group:1"));
        Assert.Empty(capture.Notices);
        await rig.DisposeAsync();
    }

    [Fact]
    public async Task ATransientFailure_ThatTheInlineRetryAbsorbs_ReportsNothing()
    {
        await using var rig = new Rig();
        rig.Store.FailNext(2); // two failures, the third attempt lands
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Save("group:5", s => s.SetAsync("group:5", 5));
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal(5, rig.Store.Value("group:5"));
        Assert.Equal(3, rig.Store.Attempts);
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public async Task WriteRetryExhausted_IsReportedOncePerStuckKey_NotOncePerRetainedRetry()
    {
        await using var rig = new Rig();
        rig.Store.FailNext(1000);
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Save("group:8", s => s.SetAsync("group:8", 8));
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.RetryRetainedAsync().WaitAsync(Wait));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.RetryRetainedAsync().WaitAsync(Wait));

        Assert.Equal(9, rig.Store.Attempts); // three cycles of three attempts really ran
        Assert.Single(capture.Of("write-retry-exhausted", QueueName));
    }

    [Fact]
    public async Task WriteRetryExhausted_IsReportedAgain_WhenTheKeyFailsAgainAfterItFinallyWrote()
    {
        await using var rig = new Rig();
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Store.FailNext(3);
        rig.Queue.Save("group:6", s => s.SetAsync("group:6", 1));
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.Single(capture.Of("write-retry-exhausted", QueueName));

        await rig.Queue.RetryRetainedAsync().WaitAsync(Wait); // storage is back: the key finally writes
        Assert.Empty(rig.Queue.RetainedKeys);

        rig.Store.FailNext(3);
        rig.Queue.Save("group:6", s => s.SetAsync("group:6", 2));
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal(2, capture.Of("write-retry-exhausted", QueueName).Length);
    }

    [Fact]
    public async Task WriteRetryExhausted_ReportsTheTrueAttemptCount_WhenALockedDatabaseIsNotRetriedInline()
    {
        await using var rig = new Rig();
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Save("group:4", _ => Task.FromException(new Microsoft.Data.Sqlite.SqliteException("SQLite Error 5: 'database is locked'.", 5)));
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        AnomalyNotice notice = Assert.Single(capture.Of("write-retry-exhausted", QueueName));
        Assert.Contains("attempts=1", notice.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("attempts=3", notice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteDroppedOnShutdown_IsReported_AfterTheQueueGateWasReleased()
    {
        await using var rig = new Rig();
        await rig.Queue.StopAsync().WaitAsync(Wait);
        using var probe = new GateProbe(() => rig.Queue.RetainedKeys.Count > 0);

        rig.Queue.Save("group:2", s => s.SetAsync("group:2", 2));

        Assert.Equal(1, probe.Reports);
        Assert.False(probe.GateHeld, "the report was made while the queue's gate was held");
    }
}
