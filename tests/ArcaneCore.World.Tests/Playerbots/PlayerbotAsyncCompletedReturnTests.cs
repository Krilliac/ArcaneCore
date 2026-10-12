using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotAsyncCompletedReturnTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedPlan_PreservesCompletedQuestReturn_IntoCachedRoute(bool completedQuestReturn)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            (bool deferred, PlayerbotAsyncNavigationPlan plan, Vector3 goal) = await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(pathfinder: new ThrowingPathfinder());
                Player player = session.Player!;
                Vector3 origin = new(player.X, player.Y, player.Z);
                Vector3 target = origin + new Vector3(10, 0, 0);
                var current = new PlayerbotAsyncNavigationPlan(player.Map!.MapId, origin);
                bool planned = PlayerbotNavigation.TryPlanTowardPrepared(player, target, new PlayerbotOptions(), current,
                    out PlayerbotRoute? route, out bool waiting, completedQuestReturn);
                Assert.False(planned);
                Assert.Null(route);
                return (waiting, current, target);
            });

            Assert.True(deferred);
            PlayerbotNavigationRequest request = Assert.IsType<PlayerbotNavigationRequest>(plan.NextRequest);
            Assert.True(plan.AddResult(request, new PlayerbotNavigationResult(
                PlayerbotNavigationResultStatus.Completed,
                PathResult.StraightLine(request.Start, request.Destination, PathType.Normal),
                request.Destination, false, false, default, TimeSpan.Zero)));

            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                bool planned = PlayerbotNavigation.TryPlanTowardPrepared(player, goal, new PlayerbotOptions(), plan,
                    out PlayerbotRoute? route, out bool waiting, completedQuestReturn);
                Assert.True(planned);
                Assert.False(waiting);
                Assert.NotNull(route);
                Assert.Equal(completedQuestReturn, route!.CompletedQuestReturn);
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    private sealed class ThrowingPathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => throw new InvalidOperationException("the cached async plan should answer without live pathfinding");
    }
}
