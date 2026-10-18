using System.Diagnostics;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

public sealed class LiveStoreTests
{
    private static LiveEntity Unit(ulong id, double x, double y, double z, LiveKind kind = LiveKind.Creature, uint map = 0, uint instance = 0, double o = 0)
        => new(id, kind, 1, "unit" + id, map, instance, new WorldPoint(x, y, z), o, 100, "idle", null, null, null, null);

    private static LiveFrame Frame(long seq, uint map, uint instance, params LiveEntity[] entities)
        => new(seq, seq * 100, map, instance, [], null, entities, Stopwatch.GetTimestamp());

    [Fact]
    public void AFrameReplacesItsMapWholesale_AnAbsentEntityIsGone()
    {
        var store = new LiveStore();
        store.Apply(Frame(1, 0, 0, Unit(1, 0, 0, 0), Unit(2, 1, 1, 1), Unit(3, 2, 2, 2)));

        store.Apply(Frame(2, 0, 0, Unit(2, 5, 5, 5), Unit(4, 9, 9, 9)));

        Assert.Equal(new ulong[] { 2, 4 }, store.Snapshot().Select(s => s.Entity.Id));
        Assert.False(store.TryGetEntity(1, out _));
        Assert.True(store.TryGetEntity(2, out LiveEntity? moved));
        Assert.Equal(new WorldPoint(5, 5, 5), moved.Position);
        Assert.Equal(2, store.EntityCount);
    }

    [Fact]
    public void TheLatestFrameIsKeptPerMapAndInstance()
    {
        var store = new LiveStore();
        store.Apply(Frame(1, 0, 0, Unit(1, 0, 0, 0)));
        store.Apply(Frame(1, 1, 0, Unit(2, 0, 0, 0, map: 1)));
        store.Apply(Frame(1, 1, 7, Unit(3, 0, 0, 0, map: 1, instance: 7)));
        store.Apply(Frame(2, 0, 0, Unit(4, 0, 0, 0)));

        Assert.Equal(2, store.Latest(0, 0)!.Seq);
        Assert.Equal(1, store.Latest(1, 0)!.Seq);
        Assert.Equal(1, store.Latest(1, 7)!.Seq);
        Assert.Null(store.Latest(2, 0));
        Assert.Equal(new ulong[] { 2, 3, 4 }, store.Snapshot().Select(s => s.Entity.Id));
        Assert.Equal(
            new[] { (0u, 0u), (1u, 0u), (1u, 7u) },
            store.Summaries().Select(s => (s.Map, s.Instance)));
    }

    [Fact]
    public void ATrailKeepsTheLast64Positions_OldestFirst()
    {
        var store = new LiveStore();
        for (int i = 0; i < 100; i++)
        {
            store.Apply(Frame(i + 1, 0, 0, Unit(1, i, -i, 10)));
        }

        IReadOnlyList<WorldPoint> trail = store.Trail(1);

        Assert.Equal(64, trail.Count);
        Assert.Equal(new WorldPoint(36, -36, 10), trail[0]);
        Assert.Equal(new WorldPoint(99, -99, 10), trail[^1]);
        for (int i = 1; i < trail.Count; i++)
        {
            Assert.Equal(trail[i - 1].X + 1, trail[i].X);
        }
    }

    [Fact]
    public void AShortTrailHoldsOnlyWhatHappened_AndAStationaryEntityAddsNothing()
    {
        var store = new LiveStore();
        store.Apply(Frame(1, 0, 0, Unit(1, 1, 1, 1)));
        store.Apply(Frame(2, 0, 0, Unit(1, 1, 1, 1)));
        store.Apply(Frame(3, 0, 0, Unit(1, 2, 2, 2)));
        store.Apply(Frame(4, 0, 0, Unit(1, 2, 2, 2)));

        Assert.Equal(new[] { new WorldPoint(1, 1, 1), new WorldPoint(2, 2, 2) }, store.Trail(1));
        Assert.Empty(store.Trail(999));
    }

    [Fact]
    public void AnEntityThatLeavesAndComesBackStartsANewTrail()
    {
        var store = new LiveStore();
        store.Apply(Frame(1, 0, 0, Unit(1, 1, 1, 1)));
        store.Apply(Frame(2, 0, 0, Unit(1, 2, 2, 2)));
        store.Apply(Frame(3, 0, 0));

        store.Apply(Frame(4, 0, 0, Unit(1, 50, 50, 50)));

        Assert.Equal(new[] { new WorldPoint(50, 50, 50) }, store.Trail(1));
    }

    [Fact]
    public void AnEntityThatChangesMap_IsReportedOnTheNewMapAndItsTrailDoesNotCarryOver()
    {
        var store = new LiveStore();
        store.Apply(Frame(1, 0, 0, Unit(1, 100, 100, 10, kind: LiveKind.Player)));
        store.Apply(Frame(2, 0, 0, Unit(1, 101, 101, 10, kind: LiveKind.Player)));

        // The player reaches map 1 before map 0's next frame drops it: the newer frame's copy wins.
        store.Apply(Frame(1, 1, 0, Unit(1, -5, -5, 3, kind: LiveKind.Player, map: 1)));

        Assert.True(store.TryGetEntity(1, out LiveEntity? now));
        Assert.Equal(1u, now.Map);
        Assert.Equal(new[] { new WorldPoint(-5, -5, 3) }, store.Trail(1));

        store.Apply(Frame(3, 0, 0));
        Assert.True(store.TryGetEntity(1, out LiveEntity? stillThere));
        Assert.Equal(1u, stillThere.Map);
    }

    [Fact]
    public void MissedSequenceNumbers_AreCountedPerMap_AndResetByASubscribe()
    {
        var store = new LiveStore();
        Assert.Equal(0, store.Apply(Frame(5, 0, 0)));
        Assert.Equal(0, store.Apply(Frame(6, 0, 0)));
        Assert.Equal(3, store.Apply(Frame(10, 0, 0)));
        Assert.Equal(0, store.Apply(Frame(1, 1, 0, Unit(9, 0, 0, 0, map: 1))));

        store.ResetSequence(0, 0);
        Assert.Equal(0, store.Apply(Frame(1, 0, 0)));
        Assert.Equal(1, store.Apply(Frame(3, 0, 0)));

        MapSummary map0 = store.Summaries().Single(s => s.Map == 0);
        Assert.Equal(4, map0.MissedFrames);
    }

    [Fact]
    public void Clear_DropsEverythingSoNoStalePositionOutlivesItsConnection()
    {
        var store = new LiveStore();
        store.Apply(Frame(1, 0, 0, Unit(1, 1, 1, 1)));

        store.Clear();

        Assert.Equal(0, store.EntityCount);
        Assert.Empty(store.Snapshot());
        Assert.Empty(store.Summaries());
        Assert.Null(store.Latest(0, 0));
        Assert.False(store.TryGetEntity(1, out _));
    }

    [Fact]
    public void Snapshot_FiltersAndOrdersByGuid_AndAddsTrailsOnlyWhenAsked()
    {
        var store = new LiveStore();
        store.Apply(Frame(1, 0, 0, Unit(30, 0, 0, 0, LiveKind.Bot), Unit(10, 0, 0, 0), Unit(20, 0, 0, 0, LiveKind.Bot)));
        store.Apply(Frame(2, 0, 0, Unit(30, 1, 0, 0, LiveKind.Bot), Unit(10, 1, 0, 0), Unit(20, 1, 0, 0, LiveKind.Bot)));

        IReadOnlyList<StoredEntity> bots = store.Snapshot(e => e.Kind == LiveKind.Bot, withTrails: true);
        IReadOnlyList<StoredEntity> plain = store.Snapshot();

        Assert.Equal(new ulong[] { 20, 30 }, bots.Select(b => b.Entity.Id));
        Assert.All(bots, b => Assert.Equal(2, b.Trail!.Count));
        Assert.All(plain, p => Assert.Null(p.Trail));
        Assert.Equal(3, plain.Count);
    }

    [Fact]
    public void AMapHoldsAtMost16384Entities_TheRestAreCounted()
    {
        var store = new LiveStore();
        LiveEntity[] many = [.. Enumerable.Range(1, LiveStore.MaxEntitiesPerMap + 10).Select(i => Unit((ulong)i, i, 0, 0))];

        store.Apply(Frame(1, 0, 0, many));

        Assert.Equal(LiveStore.MaxEntitiesPerMap, store.EntityCount);
        Assert.Equal(10, store.DroppedForCapacity);
    }

    [Fact]
    public void AtMost64MapsAreKept_TheLeastRecentlyUpdatedGoesFirst()
    {
        var store = new LiveStore();
        for (uint map = 0; map < LiveStore.MaxMaps; map++)
        {
            store.Apply(Frame(1, map, 0, Unit(map + 1, 0, 0, 0, map: map)));
        }

        store.Apply(Frame(2, 0, 0, Unit(1, 1, 1, 1)));
        store.Apply(Frame(1, 500, 0, Unit(900, 0, 0, 0, map: 500)));

        Assert.Equal(LiveStore.MaxMaps, store.Summaries().Count);
        Assert.NotNull(store.Latest(0, 0));
        Assert.NotNull(store.Latest(500, 0));
        Assert.Null(store.Latest(1, 0));
    }

    [Fact]
    public async Task ReadersAlwaysSeeOneWholeFrame_NeverAMixOfTwo()
    {
        // The writer applies frames whose 50 entities all carry the frame number in their orientation; a reader holding a snapshot must
        // never see two numbers, a short list, or an entity from a frame older than the one before it.
        var store = new LiveStore();
        const int Frames = 400;
        using var stop = new CancellationTokenSource();
        long torn = 0;

        Task writer = Task.Run(() =>
        {
            for (int f = 1; f <= Frames; f++)
            {
                store.Apply(Frame(f, 0, 0, [.. Enumerable.Range(1, 50).Select(i => Unit((ulong)i, f, i, 0, o: f))]));
            }
        });
        Task[] readers = [.. Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            double last = 0;
            while (!stop.IsCancellationRequested)
            {
                IReadOnlyList<StoredEntity> snapshot = store.Snapshot();
                if (snapshot.Count == 0)
                {
                    continue;
                }

                double first = snapshot[0].Entity.Orientation;
                if (snapshot.Count != 50 || snapshot.Any(s => s.Entity.Orientation != first) || first < last)
                {
                    Interlocked.Increment(ref torn);
                }

                last = first;
            }
        }))];

        await writer;
        await stop.CancelAsync();
        await Task.WhenAll(readers);

        Assert.Equal(0, Interlocked.Read(ref torn));
        Assert.Equal(Frames, store.Latest(0, 0)!.Seq);
    }

    [Fact]
    public void Summaries_CountKindsAndReportTheAgeOfTheFrame()
    {
        var store = new LiveStore();
        store.Apply(Frame(
            4, 0, 0, Unit(1, 0, 0, 0, LiveKind.Player), Unit(2, 0, 0, 0, LiveKind.Bot), Unit(3, 0, 0, 0), Unit(4, 0, 0, 0), Unit(5, 0, 0, 0, LiveKind.GameObject)));

        MapSummary summary = Assert.Single(store.Summaries());

        Assert.Equal(new[] { 1, 1, 2, 1 }, summary.ByKind);
        Assert.Equal(5, summary.Entities);
        Assert.Equal(400, summary.ClockMs);
        Assert.True(summary.AgeMs >= 0);
    }
}
