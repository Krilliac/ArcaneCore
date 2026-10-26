using System.Diagnostics;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>
/// The shutdown stall bound on the seven per-character write queues (honor, reputation, rest, explored zones, taxi, instance, social): a healthy drain is never
/// cut, every write gets exactly one attempt at stop, and once the lane has stalled no later attempt starts but every write is still named.
/// </summary>
[Collection(PersistAnomalyCollection.Name)]
public sealed class WriteQueueStopDrainTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    public static TheoryData<string> Queues
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in QueueRig.Names)
            {
                data.Add(name);
            }

            return data;
        }
    }

    private static StopDrainBudget Lane() => new() { StallBudget = TimeSpan.FromMilliseconds(300) };

    /// <summary>Retains one write per id (the store fails), then makes the store lock up slowly: every attempt takes 200 ms and ends busy.</summary>
    private static async Task<int> RetainThenLockAsync(QueueRig rig, int characters)
    {
        rig.Store.Fail = true;
        for (int id = 1; id <= characters; id++)
        {
            rig.Write(id);
        }

        await rig.FlushAsync().WaitAsync(Wait);
        int before = rig.Store.Attempts;
        rig.Store.Fail = false;
        rig.Store.Busy = true;
        rig.Store.DelayMs = 200;
        return before;
    }

    [Theory]
    [MemberData(nameof(Queues))]
    public async Task Stop_HealthySlowDrain_IsNeverCut(string queue)
    {
        const int writes = 30;
        await using QueueRig rig = QueueRig.Create(queue, Lane());
        rig.Store.DelayMs = 40;
        var elapsed = Stopwatch.StartNew();
        for (int id = 1; id <= writes; id++)
        {
            rig.Write(id);
        }

        await rig.StopAsync().WaitAsync(Wait); // 30 landed writes cost more than the 300 ms stall budget in wall time, and none is cut
        elapsed.Stop();

        Assert.Equal(writes, rig.Store.Attempts);
        for (int id = 1; id <= writes; id++)
        {
            Assert.False(rig.HasRetained(id), $"character {id} is still retained");
        }

        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(1), $"the drain took {elapsed.Elapsed}");
    }

    [Theory]
    [MemberData(nameof(Queues))]
    public async Task Stop_WithALockedDatabase_StopsAttemptingOnceStalled_AndNamesEveryWrite(string queue)
    {
        const int writes = 10;
        await using QueueRig rig = QueueRig.Create(queue, Lane());
        int before = await RetainThenLockAsync(rig, writes);

        InvalidOperationException stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.StopAsync().WaitAsync(Wait));

        for (int id = 1; id <= writes; id++)
        {
            Assert.Contains(id.ToString(), stopped.Message, StringComparison.Ordinal); // nothing is dropped: every write is named
        }

        Assert.InRange(rig.Store.Attempts - before, 1, 3); // ten unbounded attempts of 200 ms each would be ten
        int attempted = 0;
        int refused = 0;
        for (int id = 1; id <= writes; id++)
        {
            Exception? failure = await rig.RetainedFailureAsync(id);
            Assert.NotNull(failure);
            if (failure is StopDrainExhaustedException)
            {
                refused++;
            }
            else
            {
                Assert.IsType<SqliteException>(failure);
                attempted++;
            }
        }

        Assert.InRange(attempted, 1, 3);
        Assert.True(refused >= writes - 3, $"{refused} writes were refused");
    }

    [Theory]
    [MemberData(nameof(Queues))]
    public async Task Stop_MakesOneAttemptPerRetainedWrite_NoInlineRetry(string queue)
    {
        await using QueueRig rig = QueueRig.Create(queue, Lane());
        rig.Store.Fail = true;
        rig.Write(7);
        await rig.FlushAsync().WaitAsync(Wait);
        Assert.Equal(3, rig.Store.Attempts); // the ordinary write used all its inline attempts

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.StopAsync().WaitAsync(Wait));

        Assert.Equal(4, rig.Store.Attempts); // the shutdown retry is one attempt: no inline retries, no retry delays
    }

    [Theory]
    [MemberData(nameof(Queues))]
    public async Task Stop_SharedLane_ExhaustedByOneQueue_RefusesTheNextQueueWithoutAnAttempt(string queue)
    {
        StopDrainBudget lane = Lane();
        await using QueueRig first = QueueRig.Create(nameof(HonorWriteQueue), lane);
        await using QueueRig second = QueueRig.Create(queue, lane);

        await RetainThenLockAsync(first, 1);
        first.Store.DelayMs = 400; // one attempt exhausts the lane
        second.Store.Fail = true;
        for (int id = 1; id <= 5; id++)
        {
            second.Write(id);
        }

        await second.FlushAsync().WaitAsync(Wait);
        int secondBefore = second.Store.Attempts;
        second.Store.Fail = false;
        second.Store.Busy = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => first.StopAsync().WaitAsync(Wait));
        Assert.True(lane.Exhausted);

        InvalidOperationException stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => second.StopAsync().WaitAsync(Wait));

        Assert.Equal(secondBefore, second.Store.Attempts); // not one attempt: the lane was already spent
        for (int id = 1; id <= 5; id++)
        {
            Assert.Contains(id.ToString(), stopped.Message, StringComparison.Ordinal);
            Assert.IsType<StopDrainExhaustedException>(await second.RetainedFailureAsync(id));
        }
    }

    [Theory]
    [MemberData(nameof(Queues))]
    public async Task Stop_RefusedWrites_ReportNoRetryExhaustedAnomaly_AndTheStopReportCountsThem(string queue)
    {
        const int writes = 10;
        await using QueueRig rig = QueueRig.Create(queue, Lane());
        await RetainThenLockAsync(rig, writes);
        using AnomalyCapture capture = PersistAnomalies.Capture();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.StopAsync().WaitAsync(Wait));

        AnomalyNotice dropped = Assert.Single(capture.Of("write-dropped-on-shutdown", queue));
        Assert.Contains("undrained", dropped.Detail, StringComparison.Ordinal);
        const string marker = "refusedByStopDrain=";
        int at = dropped.Detail.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, dropped.Detail);
        int end = dropped.Detail.IndexOf(' ', at);
        int refused = int.Parse(dropped.Detail.AsSpan(at + marker.Length, end - at - marker.Length));
        Assert.True(refused >= writes - 3, dropped.Detail);
        Assert.Contains("stalledMs=", dropped.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain(capture.Notices, n => n.Code == "write-retry-exhausted" && n.Detail.Contains("StopDrainExhaustedException", StringComparison.Ordinal));
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, what);
            await Task.Delay(5);
        }
    }

    [Theory]
    [MemberData(nameof(Queues))]
    public async Task Stop_ANotYetStoppedQueueHoldingASlowSuccess_DoesNotRefuseTheStoppingQueue(string queue)
    {
        StopDrainBudget lane = Lane();
        await using QueueRig slow = QueueRig.Create(nameof(HonorWriteQueue), lane); // never stopped before the asserts: its healthy write is merely slow
        await using QueueRig stopping = QueueRig.Create(queue, lane);
        stopping.Store.Fail = true;
        for (int id = 1; id <= 5; id++)
        {
            stopping.Write(id);
        }

        await stopping.FlushAsync().WaitAsync(Wait); // five retained writes, their failed attempts before the lane began
        int before = stopping.Store.Attempts;
        stopping.Store.Fail = false;

        slow.Store.DelayMs = 1500;
        slow.Write(1);
        await UntilAsync(() => slow.Store.InFlight == 1, "the slow write never started");
        lane.Begin(); // the host begins the lane before the features stop one by one
        await Task.Delay(450); // past the 300 ms budget, with the slow write still running and going to succeed

        await stopping.StopAsync().WaitAsync(Wait); // must not throw: nothing on the lane has failed

        Assert.Equal(5, stopping.Store.Attempts - before);
        Assert.Equal(0, lane.Refused);
        for (int id = 1; id <= 5; id++)
        {
            Assert.False(stopping.HasRetained(id), $"character {id} is still retained");
        }

        await slow.FlushAsync().WaitAsync(Wait);
        Assert.True(lane.Succeeded >= 6, lane.Describe());
    }

    [Theory]
    [MemberData(nameof(Queues))]
    public async Task Stop_RetryBackoffDoesNotCountAsStall(string queue)
    {
        StopDrainBudget lane = Lane();
        await using QueueRig rig = QueueRig.Create(queue, lane, retryDelay: TimeSpan.FromMilliseconds(300));
        lane.Begin();
        rig.Store.Fail = true;
        rig.Write(1);

        await rig.FlushAsync().WaitAsync(Wait); // three instantly failing attempts with at least 600 ms of backoff between them

        Assert.Equal(3, rig.Store.Attempts);
        Assert.True(lane.StalledMs < 300, lane.Describe()); // the backoff is outside the attempts
        Assert.False(lane.Exhausted);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.StopAsync().WaitAsync(Wait));

        Assert.Equal(4, rig.Store.Attempts); // the stop attempt was made, not refused
        Assert.Equal(0, lane.Refused);
    }

    [Theory]
    [MemberData(nameof(Queues))]
    public async Task Stop_AFreshWriteFailingAtStop_ReportsTheAttemptsItMade(string queue)
    {
        await using QueueRig rig = QueueRig.Create(queue, new StopDrainBudget());
        using AnomalyCapture capture = PersistAnomalies.Capture();
        rig.Store.Fail = true;
        rig.Store.DelayMs = 100;
        rig.Write(1); // consumed now: three attempts
        await UntilAsync(() => rig.Store.Attempts >= 1, "the first write never started");
        rig.Write(2); // queued behind it, so it first runs once the queue is stopping

        InvalidOperationException stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.StopAsync().WaitAsync(Wait));

        Assert.Contains("1", stopped.Message, StringComparison.Ordinal);
        Assert.Contains("2", stopped.Message, StringComparison.Ordinal);
        Assert.Equal(6, rig.Store.Attempts); // 3 + 1 for the first write, 1 + 1 for the second
        AnomalyNotice[] exhausted = capture.Of("write-retry-exhausted", queue);
        Assert.Equal(2, exhausted.Length);
        Assert.Contains("attempts=3 error=IOException", exhausted[0].Detail, StringComparison.Ordinal);
        Assert.Contains("attempts=1 error=IOException", exhausted[1].Detail, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Queues))]
    public async Task Stop_ExhaustionDuringANormalWrite_KeepsTheRealFailure(string queue)
    {
        StopDrainBudget lane = new() { StallBudget = TimeSpan.FromMilliseconds(100) };
        await using QueueRig rig = QueueRig.Create(queue, lane);
        using AnomalyCapture capture = PersistAnomalies.Capture();
        rig.Store.Fail = true;
        rig.Store.DelayMs = 150;
        rig.Write(1);
        await UntilAsync(() => rig.Store.InFlight == 1, "the write never started");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.StopAsync().WaitAsync(Wait)); // the flag flips during attempt 1

        Assert.Equal(1, rig.Store.Attempts); // attempt 1 exhausts the lane at its failure: the retry is skipped
        Assert.Equal(1, lane.Refused); // and the stop pass is refused
        AnomalyNotice exhausted = Assert.Single(capture.Of("write-retry-exhausted", queue));
        Assert.Contains("attempts=1 error=IOException", exhausted.Detail, StringComparison.Ordinal);
    }
}
