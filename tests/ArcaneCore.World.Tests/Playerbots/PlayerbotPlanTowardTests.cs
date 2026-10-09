using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// <see cref="PlayerbotNavigation.TryPlanToward"/> near the edge of the loaded navigation mesh (tiles load with the map's grids round the
/// players). A point beyond a tile not loaded yet gets a straight line from the mesh (vmangos PathFinder's HaveTiles shortcut,
/// NORMAL | NOT_USING_PATH), which the terrain stepper refuses where the ground is not walkable in a straight line; the far goal is
/// still reached by a shorter step on the loaded mesh. The wave-8 quest-35 stall on the real terrain is
/// <c>PlayerbotRealTerrainNavigationTests.Quest35_FromWhereDawnroverStopped_TheBotWalksToGuardThomas_AcrossTheTileBoundary</c>.
/// </summary>
public sealed class PlayerbotPlanTowardTests
{
    [Fact]
    public async Task AFarGoal_BeyondAnUnloadedMeshTile_IsApproachedByAShorterStepOnTheLoadedMesh()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var origin = new Vector3(player.X, player.Y, player.Z);
            // The loaded mesh ends 100 yards north; a ridge 50 yards north blocks any straight line over it.
            WorldCollision.Of(world.Host.World).Install(lineOfSight: new Ridge(origin.Y + 50f, origin.Z),
                pathfinder: new LoadedUpTo(origin.Y + 100f));
            var options = new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 2000 };
            var goal = new Vector3(origin.X, origin.Y + 600f, origin.Z);

            Assert.True(PlayerbotNavigation.TryPlanToward(player, goal, options, out PlayerbotRoute? route), "no step towards the far goal");
            Assert.True(route!.Navigated);
            Assert.InRange(route.Points[^1].Y - origin.Y, 30f, 100f);
            return true;
        });
    }

    /// <summary>The mesh: a straight two-corner path to a point on a loaded tile (y below the edge), the unloaded-tile shortcut beyond.</summary>
    private sealed class LoadedUpTo(float edgeY) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => end.Y < edgeY ? new PathResult(PathType.Normal, [start, end])
                : PathResult.StraightLine(start, end, PathType.Normal | PathType.NotUsingPath);
    }

    /// <summary>Flat ground at <c>z</c>; no straight line of sight across the ridge line.</summary>
    private sealed class Ridge(float ridgeY, float z) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => (from.Y < ridgeY) == (to.Y < ridgeY);

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z2, float maxSearchDistance) => z;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z2, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }
}
