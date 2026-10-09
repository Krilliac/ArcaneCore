using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A bot standing on steep ground (navigation-mesh polygons flagged <see cref="NavTerrain.SteepSlopes"/>, which every bot query
/// excludes) used to get no route at all, in any direction, and stood there for good: Ironwander on the mountainside north of
/// Coldridge Valley for over two hours (live 2026-10-09, <c>stalled 7960s: goal=Grind target=1196</c>). The real-terrain case is
/// <c>PlayerbotRealTerrainNavigationTests.FromIronwandersSlope_TheBotWalksDownToTheIceClawBear</c>; here the mesh is a fake one with
/// a steep strip round the bot.
/// </summary>
public sealed class PlayerbotSteepGroundTests
{
    [Fact]
    public async Task OnSteepGround_TheRouteLeadsDownToTheFirstWalkableCorner()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var origin = new Vector3(player.X, player.Y, player.Z);
            var slope = new SteepStrip(origin, climb: -6f);
            WorldCollision.Of(world.Host.World).Install(pathfinder: slope);
            var options = new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 2000 };
            var goal = origin + new Vector3(80, 0, 0);

            Assert.True(PlayerbotNavigation.TryPlan(player, goal, options, out PlayerbotRoute? route), "no way off the steep ground");
            Assert.True(route!.Navigated);
            Assert.Equal(slope.FirstWalkable, route.Points[^1]); // cut where the walkable mesh begins; the ordinary queries go on
            Assert.True(PlayerbotNavigation.TryPlanToward(player, goal, options, out PlayerbotRoute? toward), "exploration refused");
            Assert.Equal(slope.FirstWalkable, toward!.Points[^1]);
            return true;
        });
    }

    [Fact]
    public async Task OnSteepGround_AWayThatClimbs_IsRefused()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var origin = new Vector3(player.X, player.Y, player.Z);
            WorldCollision.Of(world.Host.World).Install(pathfinder: new SteepStrip(origin, climb: PlayerbotNavigation.SteepEscapeClimbYards + 3f));
            Assert.False(PlayerbotNavigation.TryPlan(player, origin + new Vector3(80, 0, 0), new PlayerbotOptions(), out _),
                "a bot walked up a slope too steep to climb");
            return true;
        });
    }

    [Fact]
    public async Task OnWalkableGround_ADestinationOffTheMesh_StillHasNoRoute()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var origin = new Vector3(player.X, player.Y, player.Z);
            // The steep strip lies 200 yards east; the bot stands on walkable ground and aims into it.
            var slope = new SteepStrip(origin + new Vector3(200, 0, 0), climb: -6f);
            WorldCollision.Of(world.Host.World).Install(pathfinder: slope);
            Assert.False(PlayerbotNavigation.TryPlan(player, origin + new Vector3(200, 0, 0), new PlayerbotOptions(), out _));
            Assert.Equal(0, slope.SteepQueries);
            return true;
        });
    }

    /// <summary>
    /// On a patch of walkable polygons among steep ones (Ironwander's second stop, -5573, 185.6, 441.4) the ordinary query is not
    /// refused but ends at the patch's edge and never closes on the goal. The way off leads past the edge, which is walkable but
    /// still holds the bot, to the first corner from which the ordinary query is a route.
    /// </summary>
    [Fact]
    public async Task OnAWalkablePatchAmongSteepSlopes_TheRouteLeadsPastItsEdgeToWhereRoutesGoOn()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var origin = new Vector3(player.X, player.Y, player.Z);
            // The bot stands half a yard inside the patch's east edge, where the ordinary routes east end.
            var patch = new PatchOnSlope(origin - new Vector3(4.5f, 0, 0));
            WorldCollision.Of(world.Host.World).Install(pathfinder: patch);
            var options = new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 2000 };

            Assert.True(PlayerbotNavigation.TryPlanToward(player, origin + new Vector3(80, 0, 0), options, out PlayerbotRoute? route),
                "no way off the walkable patch");
            Assert.Equal(patch.FirstWalkable, route!.Points[^1]);
            return true;
        });
    }

    /// <summary>
    /// The mesh: a walkable patch of <see cref="Radius"/> yards round <c>centre</c>, steep polygons 20 yards either side (east-west),
    /// walkable ground beyond. From the patch the ordinary query ends at its edge (no path when a partial one is not allowed); with
    /// steep slopes allowed the path runs east over the edge and down the slope to walkable ground.
    /// </summary>
    private sealed class PatchOnSlope(Vector3 centre) : IPathfinder
    {
        private const float Radius = 5f;

        public Vector3 Edge => centre + new Vector3(Radius, 0, 0);

        public Vector3 FirstWalkable => centre + new Vector3(22f, 0, -10f);

        public bool Enabled => true;

        private bool OnPatch(Vector3 point) => Vector2.Distance(new(point.X, point.Y), new(centre.X, centre.Y)) <= Radius;

        private bool OnSlope(Vector3 point) => !OnPatch(point) && MathF.Abs(point.X - centre.X) <= 20f && MathF.Abs(point.Y - centre.Y) <= 50f;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            options ??= PathOptions.Default;
            if ((options.ExcludeFlags & NavTerrain.SteepSlopes) == 0)
                return new PathResult(PathType.Normal, [start, Edge, centre + new Vector3(12f, 0, -6f), FirstWalkable, end]);
            if (OnSlope(start) || OnSlope(end)) return PathResult.StraightLine(start, end, PathType.NoPath);
            if (OnPatch(start) == OnPatch(end)) return new PathResult(PathType.Normal, [start, end]);
            if (!OnPatch(start) || !options.AllowPartial) return PathResult.StraightLine(start, end, PathType.NoPath);
            Vector3 toward = Vector3.Normalize(new Vector3(end.X - centre.X, end.Y - centre.Y, 0));
            return new PathResult(PathType.Incomplete, [start, centre + (toward * Radius)]);
        }
    }

    /// <summary>
    /// The mesh: steep polygons within <see cref="HalfWidth"/> yards (east-west) of <c>centre</c>, walkable ground beyond. A query that
    /// excludes steep slopes from or to the strip answers no path (Detour finds no polygon there); with steep slopes allowed the path
    /// runs east over the strip, each corner <c>climb</c> yards above the last, to walkable ground and on.
    /// </summary>
    private sealed class SteepStrip(Vector3 centre, float climb) : IPathfinder
    {
        private const float HalfWidth = 10f;

        public int SteepQueries { get; private set; }

        public Vector3 FirstWalkable => centre + new Vector3(HalfWidth + 2f, 0, climb * 2);

        public bool Enabled => true;

        private bool OnStrip(Vector3 point) => MathF.Abs(point.X - centre.X) <= HalfWidth && MathF.Abs(point.Y - centre.Y) <= 50f;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            bool excludeSteep = ((options?.ExcludeFlags ?? NavTerrain.Empty) & NavTerrain.SteepSlopes) != 0;
            if (excludeSteep)
                return OnStrip(start) || OnStrip(end) ? PathResult.StraightLine(start, end, PathType.NoPath)
                    : new PathResult(PathType.Normal, [start, end]);
            SteepQueries++;
            return new PathResult(PathType.Normal, [start, centre + new Vector3(HalfWidth / 2, 0, climb), FirstWalkable, end]);
        }
    }
}
