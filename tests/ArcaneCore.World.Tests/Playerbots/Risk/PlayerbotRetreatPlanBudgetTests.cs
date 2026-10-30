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
}
