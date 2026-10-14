using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

public sealed class PlayerbotAsyncHazardDetourTests
{
    [Fact]
    public async Task DeferredHazardDetour_DoesNotReportAnUnfinishedRouteAsRefused()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.Host.World).Install(pathfinder: new ThrowingPathfinder());
            Creature strong = world.Spawn(RiskTestWorld.Template(991701, level: 60, health: 500), 0, 30);
            world.Brain.Risk.Hazards.SetStrong(world.Player.MapId, new Vector3(strong.X, strong.Y, strong.Z), 20,
                world.Host.World.NowMs, 60_000, strong.Guid);
            PlayerbotNavigation.Guard(world.Player, world.Brain.Risk);
            Assert.Equal(0u, world.Brain.Risk.BoxedSinceMs);

            Vector3 origin = new(world.Player.X, world.Player.Y, world.Player.Z);
            var plan = new PlayerbotAsyncNavigationPlan(world.Player.MapId, origin);
            Vector3 goal = origin + new Vector3(0, 60, 0);
            bool planned = PlayerbotNavigation.TryPlanTowardPrepared(world.Player, goal, world.Options, plan,
                out _, out bool deferred);
            Assert.False(planned);
            Assert.True(deferred);

            PlayerbotNavigationRequest request = Assert.IsType<PlayerbotNavigationRequest>(plan.NextRequest);
            Assert.True(plan.AddResult(request, new PlayerbotNavigationResult(
                PlayerbotNavigationResultStatus.Completed,
                PathResult.StraightLine(request.Start, request.Destination, PathType.Normal),
                request.Destination, false, false, default, TimeSpan.Zero)));

            bool second = PlayerbotNavigation.TryPlanTowardPrepared(world.Player, goal, world.Options, plan,
                out _, out bool stillDeferred);
            Assert.False(second);
            Assert.True(stillDeferred);
            Assert.NotNull(plan.NextRequest);
            Assert.Equal(0u, world.Brain.Risk.BoxedSinceMs);
            Assert.Equal(0, world.Brain.Risk.Escapes);
            return true;
        });
    }

    private sealed class ThrowingPathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => throw new InvalidOperationException("the async hazard-detour test requires cached direct work");
    }
}
