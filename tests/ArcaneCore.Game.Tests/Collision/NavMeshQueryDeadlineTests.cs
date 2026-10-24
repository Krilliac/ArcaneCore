using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// A search past its wall-time bound (<see cref="PathOptions.MaxSearchMs"/>) stops as one that ran out of nodes does: capped, the
/// corridor to the node nearest the goal, counted in <see cref="PathSearchCounters.DeadlineHits"/>. The clock is a test seam.
/// </summary>
[Collection("NavMeshDeadlineClock")]
public sealed class NavMeshQueryDeadlineTests
{
    private const uint MapId = 0;
    private static readonly Vector3 Start = new(1, 1, 0);
    private static readonly Vector3 Goal = new(39, 39, 0);

    private static NavMeshPathfinder Open(NavMeshFixture fixture)
    {
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 20, 20)); // 40 x 40 yards of open ground, 2-yard cells
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));
        return nav;
    }

    private static string Describe(PathResult path) => $"{path.Type}: {string.Join(" ", path.Points.Select(p => $"({p.X:F2},{p.Y:F2},{p.Z:F2})"))}";

    [Fact]
    public void ASearchPastItsDeadline_IsCapped_CountedAsADeadlineHit_AndAnswersTheCorridorTowardTheGoal()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Open(fixture);
        Func<long> real = NavMesh.DeadlineClock;
        try
        {
            int calls = 0;
            // The first read sets the deadline; the second (before any node is expanded) and the third (32 expansions on) are past it only from the third.
            NavMesh.DeadlineClock = () => ++calls < 3 ? 0 : long.MaxValue / 2;
            PathSearchTotals before = PathSearchCounters.Current;

            PathResult path = nav.FindPath(MapId, Start, Goal, new PathOptions { MaxSearchNodes = 4096, MaxSearchMs = 1000, AllowPartial = true });

            PathSearchTotals spent = PathSearchCounters.Current - before;
            Assert.Equal(1, spent.Searches);
            Assert.Equal(1, spent.Capped);
            Assert.Equal(1, spent.DeadlineHits);
            Assert.Equal(32, spent.Expansions);
            Assert.Equal(PathType.Incomplete, path.Type & PathType.Incomplete);
            Assert.True(Vector3.Distance(path.End, Goal) < Vector3.Distance(Start, Goal), "the partial corridor does not close on the goal");
        }
        finally
        {
            NavMesh.DeadlineClock = real;
        }
    }

    [Fact]
    public void NoBound_OrABoundNotReached_AnswersTheSamePathAsBefore_AndCountsNoDeadlineHit()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Open(fixture);
        string expected = Describe(nav.FindPath(MapId, Start, Goal));
        PathSearchTotals before = PathSearchCounters.Current;

        Assert.Equal(expected, Describe(nav.FindPath(MapId, Start, Goal, new PathOptions { MaxSearchMs = 0 })));
        Assert.Equal(expected, Describe(nav.FindPath(MapId, Start, Goal, new PathOptions { MaxSearchMs = 60_000 })));
        Assert.Equal(0, (PathSearchCounters.Current - before).DeadlineHits);
    }

    [Fact]
    public void TheCancellationCheck_IsUnaffectedByTheDeadline()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Open(fixture);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            nav.FindPath(MapId, Start, Goal, new PathOptions { MaxSearchMs = 1000, CancellationToken = cancelled.Token }));
    }
}

/// <summary>Tests that replace <see cref="NavMesh.DeadlineClock"/> (process-wide) do not run beside each other.</summary>
[CollectionDefinition("NavMeshDeadlineClock", DisableParallelization = true)]
public sealed class NavMeshDeadlineClockCollection;
