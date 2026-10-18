using System.Numerics;
using ArcaneCore.World.Ops.MapFeed;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The pooled frame the world thread fills and connection threads serialize (docs/areas/map-feed.md, "Capture"): bounded, reused,
/// reference counted, and cleared before it is handed out again. Pure; no world, no pipe.
/// </summary>
public sealed class FeedFrameTests
{
    [Fact]
    public void ThePool_HandsOutAtMostItsLimit_AndTakesFramesBack()
    {
        var pool = new FramePool(2);

        FeedFrame? a = pool.Rent();
        FeedFrame? b = pool.Rent();
        FeedFrame? none = pool.Rent();

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a, b);
        Assert.Null(none);
        Assert.Equal(2, pool.Created);
        Assert.Equal(0, pool.Free);

        a!.Release();
        Assert.Equal(1, pool.Free);
        FeedFrame? again = pool.Rent();
        Assert.Same(a, again); // reused, not a third frame
        Assert.Equal(2, pool.Created);
        Assert.Equal(0, pool.Free);
    }

    [Fact]
    public void AFrame_ReturnsToThePool_OnlyWhenTheLastHolderReleasesIt()
    {
        var pool = new FramePool(1);
        FeedFrame frame = pool.Rent()!; // the renter's holder
        frame.AddHolder(); // a connection queue
        frame.AddHolder(); // another

        frame.Release();
        frame.Release();
        Assert.Equal(0, pool.Free);

        frame.Release();
        Assert.Equal(1, pool.Free);
    }

    [Fact]
    public void AReturnedFrame_IsClearedBeforeItIsRentedAgain()
    {
        var pool = new FramePool(1);
        FeedFrame frame = pool.Rent()!;
        frame.MapId = 5;
        frame.InstanceId = 6;
        frame.ClockMs = 7;
        frame.View = new FeedView(5, 6, FeedKinds.All, true);
        frame.Counters.Players = 3;
        frame.AddGrid(1, 2);
        int index = frame.AddEntity();
        frame.Entities[index].Name = "Held name";
        frame.Entities[index].Guid = 99;
        Span<Vector3> points = frame.ReservePoints(2);
        points[0] = Vector3.One;
        frame.CommitPoints(2);

        frame.Release();
        FeedFrame next = pool.Rent()!;

        Assert.Same(frame, next);
        Assert.Equal(0, next.EntityCount);
        Assert.Equal(0, next.PointCount);
        Assert.Equal(0, next.GridCount);
        Assert.Equal(0u, next.MapId);
        Assert.Equal(0u, next.InstanceId);
        Assert.Equal(0, next.ClockMs);
        Assert.Equal(0, next.Counters.Players);
        Assert.Equal(default, next.View);
        // A name must not outlive its capture (the strings would be pinned by an idle pooled frame).
        Assert.Null(next.Entities[index].Name);
        Assert.Equal(0UL, next.Entities[index].Guid);
    }

    [Fact]
    public void AddingEntities_PastTheInitialRoom_GrowsTheArray_AndStartsEachFromDefault()
    {
        var pool = new FramePool(1);
        FeedFrame frame = pool.Rent()!;

        for (int i = 0; i < 700; i++)
        {
            int index = frame.AddEntity();
            Assert.Equal(i, index);
            Assert.Equal(0UL, frame.Entities[index].Guid);
            frame.Entities[index].Guid = (ulong)(i + 1);
        }

        Assert.Equal(700, frame.EntityCount);
        Assert.True(frame.Entities.Length >= 700);
        Assert.Equal(700UL, frame.Entities[699].Guid);
        Assert.Equal(1UL, frame.Entities[0].Guid);
    }

    [Fact]
    public void APointReservationThatIsNotCommitted_AddsNothing()
    {
        var pool = new FramePool(1);
        FeedFrame frame = pool.Rent()!;

        Span<Vector3> first = frame.ReservePoints(5000);
        Assert.Equal(5000, first.Length);
        Assert.Equal(0, frame.PointCount);

        first[0] = new Vector3(1, 2, 3);
        first[1] = new Vector3(4, 5, 6);
        frame.CommitPoints(2);
        Assert.Equal(2, frame.PointCount);

        Span<Vector3> second = frame.ReservePoints(10);
        second[0] = new Vector3(7, 8, 9);
        frame.CommitPoints(1);

        Assert.Equal(3, frame.PointCount);
        Assert.Equal(new Vector3(1, 2, 3), frame.Points[0]);
        Assert.Equal(new Vector3(4, 5, 6), frame.Points[1]);
        Assert.Equal(new Vector3(7, 8, 9), frame.Points[2]);
    }

    [Fact]
    public void Grids_AreKeptInOrder_AndGrowPastTheirInitialRoom()
    {
        var pool = new FramePool(1);
        FeedFrame frame = pool.Rent()!;
        for (int i = 0; i < 40; i++)
        {
            frame.AddGrid(i, 63 - i);
        }

        Assert.Equal(40, frame.GridCount);
        Assert.Equal((0, 63), frame.Grids[0]);
        Assert.Equal((39, 24), frame.Grids[39]);
    }

    [Fact]
    public void FramesOfEqualViews_AreTheSameView()
    {
        Assert.Equal(new FeedView(0, 0, FeedKinds.All, true), new FeedView(0, 0, FeedKinds.All, true));
        Assert.NotEqual(new FeedView(0, 0, FeedKinds.All, true), new FeedView(0, 0, FeedKinds.All, false));
        Assert.NotEqual(new FeedView(0, 0, FeedKinds.All, true), new FeedView(0, 1, FeedKinds.All, true));
        Assert.NotEqual(new FeedView(0, 0, FeedKinds.All, true), new FeedView(0, 0, FeedKinds.Bot, true));
        Assert.NotEqual(new FeedView(0, 0, FeedKinds.All, true), new FeedView(1, 0, FeedKinds.All, true));
    }
}
