using System.Numerics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotLoopDetectorTests
{
    private static readonly Vector3 A = new(0, 0, 5), B = new(30, 0, 5), C = new(60, 0, 5);

    [Fact]
    public void BackAndForthBetweenTwoDestinations_IsALoop_GivingUpBoth()
    {
        var detector = new PlayerbotLoopDetector();
        Assert.Null(detector.OnRoute(A, 0));
        Assert.Null(detector.OnRoute(B, 1000));
        Assert.Null(detector.OnRoute(A, 2000)); // first return
        Assert.Null(detector.OnRoute(B, 3000)); // second
        IReadOnlyList<Vector3>? places = detector.OnRoute(A, 4000); // third
        Assert.NotNull(places);
        Assert.Equal(2, places!.Count);
        Assert.Contains(A, places);
        Assert.Contains(B, places);
    }

    [Fact]
    public void ReplanningTheSameGoal_OrAMovingTarget_IsNotALoop()
    {
        var detector = new PlayerbotLoopDetector();
        for (uint i = 0; i < 40; i++)
            Assert.Null(detector.OnRoute(A + new Vector3(i * 0.5f, 0, 0), i * 500)); // drifts 0.5 yd per re-plan
    }

    [Fact]
    public void ReturnsSpreadOverMoreThanTheWindow_AreNotALoop()
    {
        var detector = new PlayerbotLoopDetector();
        uint step = PlayerbotLoopDetector.DestinationWindowMs / 2 + 1; // a quest giver and its mobs, a round trip each
        Vector3[] trip = [A, B, A, B, A, B, A];
        for (int i = 0; i < trip.Length; i++)
            Assert.Null(detector.OnRoute(trip[i], (uint)i * step));
    }

    [Fact]
    public void TravellingOnward_IsNotALoop()
    {
        var detector = new PlayerbotLoopDetector();
        Assert.Null(detector.OnRoute(A, 0));
        Assert.Null(detector.OnRoute(B, 5000));
        Assert.Null(detector.OnRoute(C, 10_000));
        for (uint t = 500; t <= 30_000; t += 500)
            Assert.Null(detector.OnPosition(new Vector3(t / 1000f * 7f, 0, 5), 3.5f, t));
    }

    [Fact]
    public void RunningInASmallCircle_IsALoop_GivingUpTheCurrentDestination()
    {
        var detector = new PlayerbotLoopDetector();
        Assert.Null(detector.OnRoute(C, 0));
        IReadOnlyList<Vector3>? places = null;
        // Radius 4 yd at 7 yd/s, a heartbeat every 500 ms.
        for (uint t = 500; t <= 20_000 && places is null; t += 500)
        {
            float angle = t / 1000f * 7f / 4f;
            places = detector.OnPosition(new Vector3(MathF.Cos(angle) * 4f, MathF.Sin(angle) * 4f, 5), 3.5f, t);
        }

        Assert.NotNull(places);
        Assert.Equal([C], places);
    }

    [Fact]
    public void Walk_ReportsTheTurnAndTheHeadingOfTheSegmentItIsOn()
    {
        var route = new PlayerbotRoute([new(0, 0, 5), new(10, 0, 5), new(10, 10, 5)], 20);
        Vector3 before = PlayerbotMotion.Walk(route, new(0, 0, 5), 1, 9f, 0f, out int next, out float heading, out bool arrived, out bool turned);
        Assert.Equal(new Vector3(9, 0, 5), before);
        Assert.Equal((1, 0f, false, false), (next, heading, arrived, turned));

        Vector3 after = PlayerbotMotion.Walk(route, new(0, 0, 5), 1, 12f, 0f, out next, out heading, out arrived, out turned);
        Assert.Equal(10f, after.X, 3);
        Assert.Equal(2f, after.Y, 3);
        Assert.Equal(2, next);
        Assert.Equal(MathF.PI / 2, heading, 3);
        Assert.True(turned);
        Assert.False(arrived);

        Vector3 end = PlayerbotMotion.Walk(route, new(0, 0, 5), 1, 50f, 0f, out next, out _, out arrived, out _);
        Assert.Equal(new Vector3(10, 10, 5), end);
        Assert.Equal(3, next);
        Assert.True(arrived);
    }
}
