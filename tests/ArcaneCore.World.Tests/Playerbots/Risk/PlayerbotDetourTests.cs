using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// Two review findings on the detours round hazards and packs (<see cref="PlayerbotNavigation"/>, <see cref="PlayerbotRisk"/>):
/// a hazard detour joined from a navigation-mesh leg and terrain-stepped legs must keep the motion's floor and line-of-sight checks on
/// the stepped ones; and the way round the creatures on the approach must not run into another visible hostile.
/// </summary>
public sealed class PlayerbotDetourTests
{
    /// <summary>
    /// A walk 60 yards north passes a place where the bot died (a 25-yard hazard), so the route goes round it: the first leg (from the
    /// bot to the corner before the hazard) comes from the navigation mesh, the two legs past it are stepped over the terrain (the mesh
    /// has no answer there). After planning, a pit opens on the leg beside the hazard. Each leg keeps its own proof: the motion stops at
    /// the pit's edge instead of walking on through the air, as it would on a mesh route, which it does not check (the route used to
    /// be marked navigated from its first leg alone).
    /// </summary>
    [Fact]
    public async Task AHazardDetour_KeepsEachLegsProof_TheMotionChecksTheSteppedLegs()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        var ground = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var floor = new PitGround(player.Z);
            WorldCollision.Of(world.Host.World).Install(lineOfSight: floor, pathfinder: new MeshFrom(new Vector3(player.X, player.Y, player.Z)));
            return floor;
        });

        PlayerbotRoute route = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var deathSpot = new Vector3(player.X, player.Y + 30, player.Z);
            world.Brain.Risk.Hazards.Add(player.MapId, deathSpot, PlayerbotHazards.DeathYards, world.Host.World.NowMs, 300, "death");
            PlayerbotNavigation.Guard(player, world.Brain.Risk);
            Assert.True(PlayerbotNavigation.TryPlan(player, new Vector3(player.X, player.Y + 60, player.Z), world.Options, out PlayerbotRoute? planned));
            PlayerbotNavigation.Guard(player, null);
            return planned!;
        });

        // The first leg is the mesh's two corners; the rest were stepped a yard at a time.
        Assert.True(route.Points.Count > 10);
        Assert.True(route.LegsNavigated(1, 1));
        Assert.False(route.LegsNavigated(2, 2));
        Assert.False(route.LegsNavigated(1, route.Points.Count - 1));
        Assert.False(route.Navigated);

        // The pit: 8 yards round the middle of the leg beside the hazard (either side), 10 yards deep.
        Vector3 corner = route.Points[1];
        var pit = new Vector3(corner.X, corner.Y + 30, corner.Z);
        await world.OnWorldAsync(() => ground.Pit = (pit, 8f));

        float deepest = 0;
        for (int tick = 0; tick < 200; tick++)
        {
            bool going = await world.OnWorldAsync(() =>
            {
                world.Session.ManagedBudget = new ManagedActionBudget(8);
                return PlayerbotNavigation.TryAdvance(world.Session, route, world.Options, RiskTestWorld.ThinkMs, world.Host.World.NowMs);
            });
            await world.Host.World.AdvanceClockAsync(RiskTestWorld.ThinkMs);
            Vector3 at = await world.OnWorldAsync(() => new Vector3(world.Player.X, world.Player.Y, world.Player.Z));
            float into = 8f - Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(pit.X, pit.Y));
            deepest = MathF.Max(deepest, into);
            if (!going) break;
        }

        Assert.True(deepest <= 0.5f, $"the bot walked {deepest:F1} yards into the pit");
        Vector3 stopped = await world.OnWorldAsync(() => new Vector3(world.Player.X, world.Player.Y, world.Player.Z));
        Assert.True(Vector2.Distance(new Vector2(stopped.X, stopped.Y), new Vector2(corner.X, corner.Y)) > 5f, "the bot never left the first corner");
    }

    /// <summary>
    /// A pack creature on the straight way to the target makes the verdict a detour round it. A second hostile then stands on the very
    /// waypoint that detour took, clear of the straight line (so it is not one of the creatures on the approach): the next detour keeps
    /// out of its reach too, on the other side.
    /// </summary>
    [Fact]
    public async Task ADetour_KeepsOutOfEveryVisibleHostile_NotOnlyThoseOnTheStraightApproach()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync(options => options.MaxRouteYards = 300);
        // A short detection range (10 yards) leaves room for a way round within the detour's 15 to 35 yards.
        // The target alone is an easy kill; one of the strong ones as well is too much (the verdict is to walk round it).
        CreatureTemplate weak = RiskTestWorld.Template(991301, health: 40, minDamage: 1, maxDamage: 2) with { Detection = 10 };
        CreatureTemplate strong = RiskTestWorld.Template(991302, health: 300, minDamage: 12, maxDamage: 16) with { Detection = 10 };
        (Creature target, Creature onTheWay) = await world.OnWorldAsync(() => (world.Spawn(weak, 0, 60), world.Spawn(strong, 0, 30)));
        await world.SeeAsync(target, onTheWay);

        Vector3 waypoint = await world.OnWorldAsync(() =>
        {
            PlayerbotEngagement verdict = world.Brain.Risk.Assess(world.Player, target, questObjective: true, out PlayerbotRoute? detour);
            Assert.True(verdict.Decision == PlayerbotEngageDecision.Detour && detour is not null, $"{verdict}");
            Assert.Equal("path-adds-1", verdict.Reason);
            return detour!.Points[^1];
        });

        Creature aside = await world.OnWorldAsync(() =>
            world.Spawn(strong with { Entry = 991303 }, waypoint.X - world.Player.X, waypoint.Y - world.Player.Y));
        await world.SeeAsync(aside);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            PlayerbotThreat reach = PlayerbotRecovery.Threats(player).Single(t => t.Source == aside);
            Assert.False(reach.Reaches(new Vector3(player.X, player.Y + 30, player.Z), 0f)); // clear of the straight line
            PlayerbotEngagement verdict = world.Brain.Risk.Assess(player, target, questObjective: true, out PlayerbotRoute? detour);
            Assert.Equal("path-adds-1", verdict.Reason); // still only the one creature on the approach
            Assert.True(detour is not null, $"{verdict}");
            foreach (Vector3 point in Sample([.. detour!.Points, new Vector3(target.X, target.Y - 3, target.Z)]))
                Assert.False(reach.Reaches(point, 0f), $"the detour passes {point}, inside the reach of the hostile at the old waypoint");
            return true;
        });
    }

    private static IEnumerable<Vector3> Sample(IReadOnlyList<Vector3> points)
    {
        for (int i = 1; i < points.Count; i++)
            for (int step = 0; step <= 10; step++)
                yield return Vector3.Lerp(points[i - 1], points[i], step / 10f);
    }

    /// <summary>Flat ground at <c>z</c> with an optional pit (a floor 10 yards lower) that can be opened after planning.</summary>
    private sealed class PitGround(float z) : ILineOfSight
    {
        public (Vector3 At, float Radius)? Pit { get; set; }

        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z2, float maxSearchDistance)
            => Pit is { } pit && Vector2.Distance(new Vector2(x, y), new Vector2(pit.At.X, pit.At.Y)) < pit.Radius ? z - 10f : z;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z2, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    /// <summary>A navigation mesh that answers (with a straight two-corner path) only from where the bot started; elsewhere there is none.</summary>
    private sealed class MeshFrom(Vector3 origin) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => Vector2.Distance(new Vector2(start.X, start.Y), new Vector2(origin.X, origin.Y)) < 0.5f
                ? new PathResult(PathType.Normal, [start, end])
                : PathResult.StraightLine(start, end, PathType.NotUsingPath);
    }
}
