using System.Globalization;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Ops.MapFeed;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The anomaly ring (docs/areas/atlas-insight.md, "Anomalies"): a bounded copy of the notices one world reported, taken on the reporting
/// thread from a ledger that is shared by every world in the process. Pure: a private <see cref="AnomalyLedger"/> and no world; the
/// positions and the pipe are tested with a real world in <see cref="MapFeedInspectTests"/>.
/// </summary>
public sealed class MapFeedAnomalyRingTests
{
    private static AnomalyNotice Notice(string subject, string detail = "detail", AnomalySeverity severity = AnomalySeverity.Warning, object? origin = null, long count = 1)
        => new("bot-test/stuck", "bot-test", "stuck", severity, subject, detail, count, 0) { Origin = origin };

    [Fact]
    public void ARingKeepsItsOwnWorldsNotices_AndRefusesAnotherWorldsOnTheSharedLedger()
    {
        var ledger = new AnomalyLedger();
        object worldA = new();
        object worldB = new();
        using var ringA = new MapFeedAnomalyRing(ledger, worldA, keepUnowned: false);
        using var ringB = new MapFeedAnomalyRing(ledger, worldB, keepUnowned: false);

        ledger.Report("bot-test", "stuck", "bot Alice (guid 1)", "a1", AnomalySeverity.Warning, worldA);
        ledger.Report("bot-test", "stuck", "bot Bob (guid 2)", "b1", AnomalySeverity.Warning, worldB);
        ledger.Report("bot-test", "stuck", "bot Carol (guid 3)", "a2", AnomalySeverity.Error, worldA);
        ledger.Report("bot-test", "stuck", "bot Nobody (guid 4)", "unowned", AnomalySeverity.Info, origin: null);

        AnomalyPage a = ringA.Query(0, 100);
        AnomalyPage b = ringB.Query(0, 100);

        Assert.Equal(["bot Alice (guid 1)", "bot Carol (guid 3)"], a.Slots.Select(s => s.Subject));
        Assert.Equal(["bot Bob (guid 2)"], b.Slots.Select(s => s.Subject));
        Assert.Equal(2, ringA.Accepted);
        Assert.Equal(2, ringA.Refused);
        Assert.Equal(1, ringB.Accepted);
        Assert.Equal(3, ringB.Refused);
    }

    [Fact]
    public void ANullOriginNotice_IsKeptOnlyWhenUnownedNoticesAreAllowed_AndAnotherOwnersNeverIs()
    {
        var ledger = new AnomalyLedger();
        object world = new();
        using var strict = new MapFeedAnomalyRing(ledger, world, keepUnowned: false);
        using var lenient = new MapFeedAnomalyRing(ledger, world, keepUnowned: true);

        ledger.Report("sys", "code", "unowned subject", "d", AnomalySeverity.Info, origin: null);
        ledger.Report("sys", "code", "foreign subject", "d", AnomalySeverity.Info, origin: new object());
        ledger.Report("sys", "code", "own subject", "d", AnomalySeverity.Info, origin: world);

        Assert.Equal(["own subject"], strict.Query(0, 10).Slots.Select(s => s.Subject));
        Assert.Equal(["unowned subject", "own subject"], lenient.Query(0, 10).Slots.Select(s => s.Subject));
    }

    [Fact]
    public void ASlotCopiesTheNoticesValues_AndTheTimeItArrived()
    {
        var clock = new ManualTimeProvider(1_700_000_000);
        object world = new();
        using var ring = new MapFeedAnomalyRing(new AnomalyLedger(), world, keepUnowned: false, clock);

        ring.Offer(Notice("creature 3100 spawn 812", "expected 1, found 2", AnomalySeverity.Error, world, count: 7));

        AnomalySlot slot = Assert.Single(ring.Query(0, 10).Slots);
        Assert.Equal(1L, slot.Seq);
        Assert.Equal("bot-test/stuck", slot.Key);
        Assert.Equal("bot-test", slot.Subsystem);
        Assert.Equal("stuck", slot.Code);
        Assert.Equal(AnomalySeverity.Error, slot.Severity);
        Assert.Equal("creature 3100 spawn 812", slot.Subject);
        Assert.Equal("expected 1, found 2", slot.Detail);
        Assert.Equal(7L, slot.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), slot.At);
    }

    [Fact]
    public void SequenceNumbers_CountFromOne_AndSinceReturnsOnlyNewerOnesOldestFirst()
    {
        object world = new();
        using var ring = new MapFeedAnomalyRing(new AnomalyLedger(), world, keepUnowned: false);
        Assert.Equal(0L, ring.Query(0, 10).Latest);
        Assert.Empty(ring.Query(0, 10).Slots);

        for (int i = 1; i <= 10; i++)
        {
            ring.Offer(Notice("s" + i.ToString(CultureInfo.InvariantCulture), origin: world));
        }

        AnomalyPage all = ring.Query(0, 100);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), all.Slots.Select(s => s.Seq));
        Assert.Equal(10L, all.Latest);
        Assert.Equal(0L, all.Lost);

        Assert.Equal(Enumerable.Range(5, 6).Select(i => (long)i), ring.Query(4, 100).Slots.Select(s => s.Seq));
        Assert.Equal([5L, 6L, 7L], ring.Query(4, 3).Slots.Select(s => s.Seq));
        Assert.Empty(ring.Query(10, 100).Slots);
        Assert.Equal(10L, ring.Query(10, 100).Latest);

        // A client that is ahead of the ring (the daemon restarted) gets nothing, and the latest number to start over from.
        AnomalyPage ahead = ring.Query(5000, 100);
        Assert.Empty(ahead.Slots);
        Assert.Equal(10L, ahead.Latest);
    }

    [Fact]
    public void AMaxBelowOne_ReturnsNoSlots_ANegativeSinceMeansFromTheStart_AndMaxIsCappedAtTheCapacity()
    {
        object world = new();
        using var ring = new MapFeedAnomalyRing(new AnomalyLedger(), world, keepUnowned: false);
        for (int i = 0; i < 5; i++)
        {
            ring.Offer(Notice("s" + i.ToString(CultureInfo.InvariantCulture), origin: world));
        }

        Assert.Empty(ring.Query(0, 0).Slots);
        Assert.Empty(ring.Query(0, -3).Slots);
        Assert.Equal(5, ring.Query(-7, 10).Slots.Length);
        Assert.Equal(5, ring.Query(0, int.MaxValue).Slots.Length);
    }

    [Fact]
    public void TheRing_HoldsTheLastCapacityNotices_AndSaysHowManyAClientMissed()
    {
        object world = new();
        using var ring = new MapFeedAnomalyRing(new AnomalyLedger(), world, keepUnowned: false);
        int total = MapFeedAnomalyRing.Capacity + 100;
        for (int i = 1; i <= total; i++)
        {
            ring.Offer(Notice("s" + i.ToString(CultureInfo.InvariantCulture), origin: world));
        }

        Assert.Equal(4096, MapFeedAnomalyRing.Capacity);
        Assert.Equal(total, ring.Accepted);

        AnomalyPage page = ring.Query(0, MapFeedAnomalyRing.Capacity);
        Assert.Equal(MapFeedAnomalyRing.Capacity, page.Slots.Length);
        Assert.Equal(101L, page.Slots[0].Seq);
        Assert.Equal("s101", page.Slots[0].Subject);
        Assert.Equal((long)total, page.Slots[^1].Seq);
        Assert.Equal((long)total, page.Latest);
        Assert.Equal(100L, page.Lost);

        // Asking from inside the lost range loses fewer; asking from inside the ring loses none.
        Assert.Equal(50L, ring.Query(50, 10).Lost);
        Assert.Equal(0L, ring.Query(100, 10).Lost);
        Assert.Equal(0L, ring.Query(4000, 10).Lost);

        // A since above anything the ring issued means the caller's numbers come from an older ring (a restart): the page says so.
        Assert.False(ring.Query(total, 10).Reset);
        Assert.True(ring.Query(total + 1, 10).Reset);
        Assert.Empty(ring.Query(total + 1, 10).Slots);
        Assert.Equal(101L, ring.Query(50, 1).Slots[0].Seq);

        // The slots are contiguous: a ring that overwrote in the wrong order would show a gap or a repeat.
        for (int i = 1; i < page.Slots.Length; i++)
        {
            Assert.Equal(page.Slots[i - 1].Seq + 1, page.Slots[i].Seq);
        }
    }

    [Fact]
    public void ARingIsAttachedToTheLedgerOnConstruction_AndDetachedOnDispose()
    {
        var ledger = new AnomalyLedger();
        object world = new();
        var ring = new MapFeedAnomalyRing(ledger, world, keepUnowned: false);
        Assert.True(ring.IsSubscribed);

        ledger.Report("sys", "code", "before", "d", AnomalySeverity.Info, world);
        Assert.Equal(1, ring.Accepted);

        ring.Dispose();
        Assert.False(ring.IsSubscribed);
        ledger.Report("sys", "code", "after", "d", AnomalySeverity.Info, world);
        Assert.Equal(1, ring.Accepted);
        Assert.Equal(0, ring.Refused);

        ring.Dispose(); // idempotent
    }

    [Fact]
    public void KeepingANotice_AllocatesNothingBeyondTheSlotCopy()
    {
        object world = new();
        using var ring = new MapFeedAnomalyRing(new AnomalyLedger(), world, keepUnowned: false);
        AnomalyNotice notice = Notice("creature 1 spawn 2", "d", origin: world);
        AnomalyNotice foreign = Notice("creature 3 spawn 4", "d", origin: new object());
        for (int i = 0; i < 5000; i++)
        {
            ring.Offer(notice); // warm up: JIT, and the ring is full so every later offer overwrites
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++)
        {
            ring.Offer(notice);
            ring.Offer(foreign);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= 512, $"40000 offers (20000 kept) allocated {allocated} bytes; the slots are preallocated and the notice's strings are shared");
    }

    [Fact]
    public async Task OffersFromManyThreads_AreAllCounted_AndAQueryNeverSeesATornSlot()
    {
        object world = new();
        using var ring = new MapFeedAnomalyRing(new AnomalyLedger(), world, keepUnowned: false);
        const int Threads = 4;
        const int PerThread = 6000;
        using var stop = new CancellationTokenSource();
        long torn = 0;
        long queries = 0;
        long lastLatest = 0;
        bool latestWentBack = false;
        var readerRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Its own thread, and the writers wait for its first query: on a busy machine the pool can run all four writers to the end before
        // it schedules a queued reader (a worker takes its own newest task first), which left nothing to check against.
        Task reader = Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                AnomalyPage page = ring.Query(0, MapFeedAnomalyRing.Capacity);
                Interlocked.Increment(ref queries);
                readerRunning.TrySetResult();
                if (page.Latest < lastLatest)
                {
                    latestWentBack = true;
                }

                lastLatest = page.Latest;
                long previous = 0;
                foreach (AnomalySlot slot in page.Slots)
                {
                    // The writer puts the same text in subject and detail: a half-written slot would show two different values.
                    if (slot.Subject != slot.Detail || slot.Seq <= previous)
                    {
                        Interlocked.Increment(ref torn);
                    }

                    previous = slot.Seq;
                }
            }
        }, TaskCreationOptions.LongRunning);
        await readerRunning.Task;

        Task[] writers = [.. Enumerable.Range(0, Threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < PerThread; i++)
            {
                string text = string.Create(CultureInfo.InvariantCulture, $"t{t}-{i}");
                ring.Offer(Notice(text, text, origin: world));
            }
        }))];
        await Task.WhenAll(writers);
        await stop.CancelAsync();
        await reader;

        Assert.Equal(Threads * PerThread, ring.Accepted);
        Assert.True(queries > 0, "the reader never ran");
        Assert.Equal(0, torn);
        Assert.False(latestWentBack, "the latest sequence number went backwards between two queries");
        AnomalyPage last = ring.Query(0, MapFeedAnomalyRing.Capacity);
        Assert.Equal(Threads * PerThread, last.Latest);
        Assert.Equal(MapFeedAnomalyRing.Capacity, last.Slots.Length);
        Assert.Equal((long)Threads * PerThread - MapFeedAnomalyRing.Capacity + 1, last.Slots[0].Seq);
    }
}
