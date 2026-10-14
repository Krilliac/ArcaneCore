using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotAsyncNavigationPlanTests
{
    [Fact]
    public void NoPathAnswerIsRetainedAndDoesNotRequestItAgain()
    {
        var plan = new PlayerbotAsyncNavigationPlan(1, Vector3.Zero);
        var request = new PlayerbotNavigationRequest(1, Vector3.Zero, new(10, 0, 0), PathOptions.Default);
        var result = new PlayerbotNavigationResult(PlayerbotNavigationResultStatus.Completed,
            PathResult.None(Vector3.Zero), new(10, 0, 0), false, false, default, TimeSpan.Zero);

        Assert.True(plan.AddResult(request, result));
        Assert.Null(plan.NextRequest);
        Assert.True(plan.TryGet(Vector3.Zero, new(10, 0, 0), PathOptions.Default, out PlayerbotNavigationResult cached));
        Assert.Equal(PathType.NoPath, cached.Path!.Type);
    }

    [Fact]
    public void CacheMissRecordsRawFollowupAndDefers()
    {
        var plan = new PlayerbotAsyncNavigationPlan(1, Vector3.Zero);
        Assert.False(plan.TryGet(Vector3.Zero, new(20, 0, 0), PathOptions.Default, out _));
        Assert.True(plan.Deferred);
        Assert.NotNull(plan.NextRequest);
        Assert.Equal(0, plan.NextRequest!.MaxRouteYards);
        Assert.Equal(PathOptions.Default.MaxPoints, plan.NextRequest.MaxPathPoints);
    }

    [Fact]
    public async Task PlanScopeWithoutThinkProbeNeverCallsLivePathfinding()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Assert.Null(PlayerbotThinkProbe.Active);
                WorldCollision.Of(host.World).Install(pathfinder: new ThrowingPathfinder());
                var map = session.Player!.Map!;
                var plan = new PlayerbotAsyncNavigationPlan(map.MapId, Vector3.Zero);
                using (plan.Activate())
                {
                    Assert.False(PlayerbotThinkProbe.Find(map.Collision, Vector3.Zero, Vector3.UnitX, PathOptions.Default).HasPath);
                    Assert.NotNull(plan.NextRequest);
                }
                var request = plan.NextRequest!;
                Assert.True(plan.AddResult(request, new(PlayerbotNavigationResultStatus.Completed,
                    PathResult.StraightLine(Vector3.Zero, Vector3.UnitX, PathType.Normal), Vector3.UnitX,
                    false, false, default, TimeSpan.Zero)));
                plan.ResetPass();
                using (plan.Activate())
                    Assert.True(PlayerbotThinkProbe.Find(map.Collision, Vector3.Zero, Vector3.UnitX, PathOptions.Default).HasPath);
                Assert.False(plan.Deferred);
                Assert.Null(plan.NextRequest);
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public void InitialAnswerUsesTheActualDistanceScaledQueryBudget()
    {
        Vector3 target = new(500, 0, 0);
        var options = PathOptions.Default with { MaxSearchNodes = PlayerbotNavigation.MaxSearchNodes, LoadTiles = true };
        var request = new PlayerbotNavigationRequest(1, Vector3.Zero, target, options, MaxRouteYards: 2000);
        var plan = new PlayerbotAsyncNavigationPlan(1, Vector3.Zero);
        Assert.True(plan.AddResult(request, new(PlayerbotNavigationResultStatus.Completed,
            PathResult.StraightLine(Vector3.Zero, target, PathType.Normal), target, false, false, default, TimeSpan.Zero)));
        Assert.True(plan.TryGet(Vector3.Zero, target,
            options with { MaxSearchNodes = PlayerbotNavigation.SearchNodes(Vector3.Zero, target) }, out _));
        Assert.Null(plan.NextRequest);
    }

    [Fact]
    public void QueryBoundRejectsAdditionalAnswersWithoutFalseNoPathCache()
    {
        var plan = new PlayerbotAsyncNavigationPlan(1, Vector3.Zero);
        for (int i = 0; i < PlayerbotAsyncNavigationPlan.MaxQueries; i++)
        {
            Vector3 destination = new(i + 1, 0, 0);
            var request = new PlayerbotNavigationRequest(1, Vector3.Zero, destination, PathOptions.Default);
            var result = new PlayerbotNavigationResult(PlayerbotNavigationResultStatus.Completed,
                PathResult.StraightLine(Vector3.Zero, destination, PathType.Normal), destination, false, false, default, TimeSpan.Zero);
            Assert.True(plan.AddResult(request, result));
        }

        Assert.False(plan.AddResult(new PlayerbotNavigationRequest(1, Vector3.Zero, new(100, 0, 0), PathOptions.Default),
            new PlayerbotNavigationResult(PlayerbotNavigationResultStatus.Completed,
                PathResult.StraightLine(Vector3.Zero, new(100, 0, 0), PathType.Normal), new(100, 0, 0), false, false, default, TimeSpan.Zero)));
        Assert.True(plan.Exhausted);
    }

    [Fact]
    public void ReplacingAnAnswerReaccountsBytesAndEnforcesTheByteLimit()
    {
        var plan = new PlayerbotAsyncNavigationPlan(1, Vector3.Zero);
        var options = PathOptions.Default with { MaxPoints = 4096 };
        var request = new PlayerbotNavigationRequest(1, Vector3.Zero, Vector3.UnitX, options);
        PlayerbotNavigationResult Answer(int count) => new(PlayerbotNavigationResultStatus.Completed,
            new PathResult(PathType.Normal, Enumerable.Repeat(Vector3.UnitX, count).ToArray()), Vector3.UnitX,
            false, false, default, TimeSpan.Zero);

        Assert.True(plan.AddResult(request, Answer(2)));
        int initial = plan.RetainedBytes;
        Assert.True(plan.AddResult(request, Answer(4096)));
        Assert.True(plan.RetainedBytes > initial);
        Assert.Equal(1, plan.QueryCount);
        Assert.True(plan.AddResult(request, Answer(2)));
        Assert.Equal(initial, plan.RetainedBytes);

        for (int i = 2; i <= PlayerbotAsyncNavigationPlan.MaxQueries; i++)
        {
            Vector3 target = new(i, 0, 0);
            var large = new PlayerbotNavigationResult(PlayerbotNavigationResultStatus.Completed,
                new PathResult(PathType.Normal, Enumerable.Repeat(target, 4096).ToArray()), target,
                false, false, default, TimeSpan.Zero);
            bool admitted = plan.AddResult(request with { Destination = target }, large);
            Assert.InRange(plan.RetainedBytes, 0, PlayerbotAsyncNavigationPlan.MaxRetainedBytes);
            if (!admitted)
            {
                Assert.True(plan.Exhausted);
                Assert.True(plan.QueryCount < PlayerbotAsyncNavigationPlan.MaxQueries);
                return;
            }
        }
        Assert.Fail("the byte bound should reject retained paths before all query slots are used");
    }

    private sealed class ThrowingPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => throw new InvalidOperationException("owner-side A* was entered inside the asynchronous plan");
    }
}
