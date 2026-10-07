using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.World.Playerbots;
using ArcaneCore.Game;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotNavigationTests
{
    [Fact]
    public void DenseAndSparseWaypointsUseTheSameDistanceBudgetWithoutMutatingCursor()
    {
        var dense = new PlayerbotRoute(Enumerable.Range(0, 11).Select(i => new Vector3(i, 0, 5)).ToArray(), 10);
        var sparse = new PlayerbotRoute([new(0, 0, 5), new(10, 0, 5)], 10);
        Assert.True(PlayerbotNavigation.TryStep(dense, new(0, 0, 5), 3.5f,
            static point => point, out Vector3 denseStep, out int denseCursor));
        Assert.True(PlayerbotNavigation.TryStep(sparse, new(0, 0, 5), 3.5f,
            static point => point, out Vector3 sparseStep, out int sparseCursor));
        Assert.Equal(sparseStep, denseStep);
        Assert.Equal(new Vector3(3.5f, 0, 5), denseStep);
        Assert.Equal(4, denseCursor);
        Assert.Equal(1, sparseCursor);
        Assert.Equal(1, dense.NextPoint);
        Assert.Equal(1, sparse.NextPoint);
    }

    [Fact]
    public void ABlockedChordStopsAtLastSafeWaypointInsteadOfCuttingTheCorner()
    {
        var route = new PlayerbotRoute([new(0, 0, 5), new(1, 0, 5), new(1, 1, 5), new(2, 1, 5)], 3);
        Assert.True(PlayerbotNavigation.TryStep(route, new(0, 0, 5), 3,
            static point => point.X > 0 && point.Y > 0 ? null : point,
            out Vector3 next, out int cursor));
        Assert.Equal(new Vector3(1, 0, 5), next);
        Assert.Equal(2, cursor);
        Assert.Equal(1, route.NextPoint);
    }

    [Fact]
    public void ACloseWaypointDoesNotDiscardTheRemainingMovementBudget()
    {
        var route = new PlayerbotRoute([new(0, 0, 5), new(0.03f, 0, 5), new(1, 0, 5), new(2, 0, 5)], 2);
        Assert.True(PlayerbotNavigation.TryStep(route, new(0, 0, 5), 1.5f,
            static point => point, out Vector3 next, out int cursor));
        Assert.Equal(new Vector3(1.5f, 0, 5), next);
        Assert.Equal(3, cursor);
        Assert.Equal(1, route.NextPoint);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidDistanceBudgetDoesNotAdvanceTheRoute(float budget)
    {
        var route = new PlayerbotRoute([Vector3.Zero, Vector3.UnitX], 1);
        Assert.False(PlayerbotNavigation.TryStep(route, Vector3.Zero, budget,
            static point => point, out _, out _));
        Assert.Equal(1, route.NextPoint);
    }

    [Theory]
    [InlineData(1f, 7f, 500u, 3.5f)]
    [InlineData(0.5f, 7f, 500u, 1.75f)]
    [InlineData(1f, 7f, 8000u, 12f)] // arrives: the STOP lands on the 12-yard route end
    [InlineData(1f, 2f, 500u, 1.25f)] // a cap below the run speed walks (walk speed 2.5)
    public async Task TerrainMovementHonorsSpeedAndServerTimeUsingOneNormalHeartbeat(
        float rate, float configuredSpeed, uint elapsed, float expected)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                float floor = player.Z;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(floor));
                UnitSpeed.SetRate(player, MoveType.Run, rate);
                session.ManagedBudget = new ManagedActionBudget(16);
                for (int i = 0; i < 8 && player.Locomotion.Pending.HasPending; i++)
                    PlayerbotMovementControl.Update(session, player);
                Assert.False(player.Locomotion.Pending.HasPending);
                var options = new PlayerbotOptions { Enabled = true, MoveSpeed = configuredSpeed };
                Assert.True(PlayerbotNavigation.TryPlan(player, new(player.X + 12, player.Y, player.Z), options,
                    out PlayerbotRoute? route));
                Vector3 before = new(player.X, player.Y, player.Z);
                session.ManagedBudget = new ManagedActionBudget(0);
                Assert.False(PlayerbotNavigation.TryAdvance(session, route!, options, elapsed, host.World.NowMs));
                Assert.Equal(before, new Vector3(player.X, player.Y, player.Z));
                Assert.Equal(1, route!.NextPoint);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(PlayerbotNavigation.TryAdvance(session, route, options, elapsed, host.World.NowMs));
                Assert.Equal(0, session.ManagedBudget.Remaining);
                Assert.Equal(before, new Vector3(player.X, player.Y, player.Z));
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                Assert.Equal(1, route.NextPoint);
                // START establishes prediction at the current position. Only the
                // following elapsed interval advances through an ordinary heartbeat.
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.Equal(configuredSpeed < 7f, player.Movement.HasFlag(MovementFlags.WalkMode));
                Assert.True(PlayerbotNavigation.TryAdvance(session, route, options, elapsed,
                    unchecked(host.World.NowMs + elapsed)));
                Assert.Equal(1, session.ManagedBudget.Remaining); // reporting its own motion costs no action budget
                Assert.InRange(player.X - before.X, expected - 0.02f, expected + 0.02f);
                Assert.InRange(Vector3.Distance(before, new(player.X, player.Y, player.Z)), expected - 0.02f, expected + 0.02f);
                // A one-yard budget can stop just short of the first waypoint because
                // the validated route also includes its small ground-clearance lift.
                Assert.InRange(route.NextPoint, 1, route.Points.Count);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private sealed class FlatFloor(float height) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => height;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    [Fact]
    public void PathResultRejectsNoPathAndAcceptsFiniteBoundedPath()
    {
        Assert.False(PlayerbotNavigation.IsUsablePath(PathResult.None(new Vector3(1, 2, 3)), 8, 100));
        Assert.True(PlayerbotNavigation.IsUsablePath(
            new PathResult(PathType.Normal, [new Vector3(0, 0, 1), new Vector3(3, 4, 1)]), 8, 100));
    }

    [Fact]
    public void PathResultRejectsTooManyPointsAndDistance()
    {
        PathResult path = new(PathType.Normal,
            [new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(2, 0, 1)]);
        Assert.False(PlayerbotNavigation.IsUsablePath(path, 2, 100));
        Assert.False(PlayerbotNavigation.IsUsablePath(
            new PathResult(PathType.Normal, [new Vector3(0, 0, 1), new Vector3(10, 0, 1)]), 8, 5));
    }

    [Fact]
    public void PathResultRejectsNonFinitePoints()
    {
        Assert.False(PlayerbotNavigation.IsUsablePath(
            new PathResult(PathType.Normal, [new Vector3(0, 0, 1), new Vector3(float.NaN, 0, 1)]), 8, 100));
    }

    [Theory]
    [InlineData(PathType.NotUsingPath)]
    [InlineData(PathType.DestForced)]
    [InlineData(PathType.FlyPath)]
    public void PathResultRejectsUnprovenOrForcedRoutes(PathType type)
    {
        Assert.False(PlayerbotNavigation.IsUsablePath(
            new PathResult(PathType.Normal | type, [new Vector3(0, 0, 1), new Vector3(1, 0, 1)]), 8, 100));
    }

    [Fact]
    public void TerrainRouteRejectsMissingHeight()
    {
        PlayerbotOptions options = Options();
        Assert.False(PlayerbotNavigation.TryTerrainRoute(
            new(0, 0, 1), new(3, 0, 1), options,
            static (_, _, _) => TerrainTile.InvalidHeightValue,
            static (_, _) => true, out _));
    }

    [Fact]
    public void TerrainRouteRejectsSteepCliff()
    {
        PlayerbotOptions options = Options();
        Assert.False(PlayerbotNavigation.TryTerrainRoute(
            new(0, 0, 1), new(3, 0, 1), options,
            static (x, _, _) => x >= 1 ? 3f : 1f,
            static (_, _) => true, out _));
    }

    [Fact]
    public void TerrainRouteRejectsBlockedSegmentAndNonFiniteInput()
    {
        PlayerbotOptions options = Options();
        Assert.False(PlayerbotNavigation.TryTerrainRoute(
            new(0, 0, 1), new(3, 0, 1), options,
            static (_, _, _) => 1f,
            static (_, _) => false, out _));
        Assert.False(PlayerbotNavigation.TryTerrainRoute(
            new(float.NaN, 0, 1), new(3, 0, 1), options,
            static (_, _, _) => 1f,
            static (_, _) => true, out _));
    }

    [Fact]
    public void TerrainRouteRejectsPointAndDistanceBudgets()
    {
        PlayerbotOptions options = Options();
        options.MaxPathPoints = 3;
        Assert.False(PlayerbotNavigation.TryTerrainRoute(
            new(0, 0, 1), new(4, 0, 1), options,
            static (_, _, _) => 1f,
            static (_, _) => true, out _));

        options = Options();
        options.MaxRouteYards = 2f;
        Assert.False(PlayerbotNavigation.TryTerrainRoute(
            new(0, 0, 1), new(3, 0, 1), options,
            static (_, _, _) => 1f,
            static (_, _) => true, out _));
    }

    [Fact]
    public void TerrainRouteUsesGroundHeightAndOneYardProbes()
    {
        PlayerbotOptions options = Options();
        Assert.True(PlayerbotNavigation.TryTerrainRoute(
            new(0, 0, 5), new(3, 0, 5), options,
            static (_, _, _) => 5f,
            static (_, _) => true, out PlayerbotRoute? route));
        Assert.NotNull(route);
        Assert.Equal(4, route!.Points.Count);
        Assert.Equal(5.05f, route.Points[^1].Z, 3);
        Assert.InRange(route.Distance, 3f, 3.01f); // Ground clearance adds a small initial vertical segment.
    }

    private static PlayerbotOptions Options() => new()
    {
        Enabled = true, MaxPathPoints = 16, MaxRouteYards = 100, MoveSpeed = 7,
        AllowedMaps = [0], ThinkIntervalMs = 100, MaxActionsPerTick = 1,
    };
}
