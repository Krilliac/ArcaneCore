using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// A retreat's search for a way out is held to a time budget per think. Live 2026-10-30 (Paethzok, "slow think 916.8 ms, slowest phase
/// risk, 7 queries, 4 at the node cap"): the retreat planned its whole fan of five directions and the nearest-point fallback inside
/// one think, each a synchronous search on the world thread outside every per-think bound. Past the budget the rest of the fan waits
/// for the next think (where it resumes, so every direction is still tried), and a deferred think is no failed replan.
/// </summary>
public sealed class PlayerbotRetreatPlanBudgetTests
{
    [Fact]
    public async Task ARetreatThatFindsNoWay_PlansAFewDirectionsPerThink_ResumesThem_AndIsNotCorneredByTheDeferral()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(992103, health: 5000, minDamage: 1, maxDamage: 1), -3, 0));
        await world.SeeAsync(mob);
        var clock = new PlayerbotThinkProbeTests.ManualClock();
        var finder = new SlowNoWay(clock, SlowNoWay.QueryMs);
        List<int> callsPerUpdate = [];
        bool cornered = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            WorldCollision.Of(world.Host.World).Install(pathfinder: finder);
            world.Engage(mob);
            world.Brain.Risk.Track(player);
            world.Brain.Risk.StartRetreat(player, [mob], "losing");
            var probe = new PlayerbotThinkProbe(clock);
            probe.Begin();
            try
            {
                for (int i = 0; i < 12 && world.Brain.Risk.Retreat.Active; i++)
                {
                    int before = finder.Calls;
                    world.Brain.Risk.Retreat.Update(player, 100);
                    if (finder.Calls > before) callsPerUpdate.Add(finder.Calls - before);
                    clock.Advance(1);
                }
            }
            finally
            {
                probe.End();
            }

            return world.Brain.Risk.Retreat.Outcome == "cornered";
        });

        Assert.NotEmpty(callsPerUpdate);
        Assert.True(callsPerUpdate[0] <= 6, $"the first think ran {callsPerUpdate[0]} searches (a plan is three queries here) of {SlowNoWay.QueryMs} ms (per think: {string.Join(",", callsPerUpdate)})");
        Assert.False(cornered, "the deferred thinks counted as failed replans: " + await world.OnWorldAsync(() => world.Brain.Risk.Retreat.Trace));
        Assert.True(callsPerUpdate.Count >= 3, $"the fan was not carried on over the next thinks: {string.Join(",", callsPerUpdate)}");
    }

    /// <summary>
    /// A fan cut by the budget resumes with the points, order and goal it was cut with, though the bot moves between the thinks: each think
    /// plans one direction (every query costs more than the budget), the bot steps 3 yd each time, and the directions the pathfinder is asked
    /// for are the five of the first think, each once, in that think's order (grounded points first: the floor exists only on the +Y side).
    /// A fan rebuilt on every think would ask for points shifted by the steps and repeat or skip some of them.
    /// </summary>
    [Fact]
    public async Task AResumedFan_AsksForTheOriginalPoints_EachOnce_InTheOriginalOrder_WhileTheBotMoves()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(992104, health: 5000, minDamage: 1, maxDamage: 1), -3, 0));
        await world.SeeAsync(mob);
        var clock = new PlayerbotThinkProbeTests.ManualClock();
        var finder = new RecordingNoWay(clock);
        (Vector3[] expectedPoints, int[] expectedOrder, List<int> asked, int unmatched, int updates) = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var floor = new PositiveYFloor(player.Y + 5f, player.Z - 1f);
            WorldCollision.Of(world.Host.World).Install(lineOfSight: floor, pathfinder: finder);
            world.Engage(mob);
            world.Brain.Risk.Track(player);
            world.Brain.Risk.StartRetreat(player, [mob], "losing");
            finder.Ends.Clear(); // the plans Start made toward its first goal are not the fan's

            // The fan the first think builds: straight away from the mob (+X), grounded where the floor is.
            var here = new Vector3(player.X, player.Y, player.Z);
            var probe = new PlayerbotNavigation.FloorProbe(player.Map!, here);
            (Vector3[] points, int[] order, _) = PlayerbotRetreat.ShapeFan(probe.TryNear, here, Vector3.UnitX, 0f);

            var matched = new List<int>();
            int other = 0, thinks = 0;
            var thinkProbe = new PlayerbotThinkProbe(clock);
            thinkProbe.Begin();
            try
            {
                int seen = 0;
                for (; thinks < 12 && matched.Count < points.Length && world.Brain.Risk.Retreat.Active;)
                {
                    world.Brain.Risk.Retreat.Update(player, 100);
                    thinks++;
                    for (; seen < finder.Ends.Count; seen++)
                    {
                        Vector3 end = finder.Ends[seen];
                        int index = Array.FindIndex(points, point => Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(end.X, end.Y)) < 0.5f);
                        if (index < 0) other++;
                        else if (matched.Count == 0 || matched[^1] != index) matched.Add(index); // the plan's own repeats of one end are one ask
                    }

                    player.SetPosition(player.X + 3f, player.Y, player.Z, player.Orientation);
                    clock.Advance(1);
                }
            }
            finally
            {
                thinkProbe.End();
            }

            return (points, order, matched, other, thinks);
        });

        // The floor really was mixed: the +45 and +90 degree points are grounded and go first, then the straight and the -45 and -90 points.
        Assert.Equal([1, 3, 0, 2, 4], expectedOrder);
        Assert.Equal(5, expectedPoints.Length);
        Assert.Equal(0, unmatched);
        Assert.True(updates >= 5, $"the fan was not carried over thinks ({updates})");
        Assert.Equal(expectedOrder, asked);
    }

    /// <summary>No path to anywhere; every query takes <see cref="QueryMs"/> milliseconds of the probe's manual clock.</summary>
    private sealed class SlowNoWay(PlayerbotThinkProbeTests.ManualClock clock, double queryMs) : IPathfinder
    {
        internal const double QueryMs = 40;
        public int Calls { get; private set; }
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Calls++;
            clock.Advance(queryMs);
            return PathResult.None(start);
        }
    }

    /// <summary>No path to anywhere; every query takes 40 ms of the probe's manual clock and its end point is recorded.</summary>
    private sealed class RecordingNoWay(PlayerbotThinkProbeTests.ManualClock clock) : IPathfinder
    {
        public List<Vector3> Ends { get; } = [];
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (end != start) Ends.Add(end); // OnWalkableMesh asks Find(point, point) about the bot's own position; that is not a fan direction
            clock.Advance(40);
            return PathResult.None(start);
        }
    }

    /// <summary>A flat floor <paramref name="floorZ"/> high on the +Y side of <paramref name="minY"/> only; no ground below that line.</summary>
    private sealed class PositiveYFloor(float minY, float floorZ) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => y > minY ? floorZ : null;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }
}
