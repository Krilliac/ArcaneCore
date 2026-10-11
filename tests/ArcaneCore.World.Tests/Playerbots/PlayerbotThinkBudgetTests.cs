using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The bounds on one bot's think and on one tick of thinks (200-bot terrain benchmark, 2026-10-11: thinks up to 557 ms, 41 searches of
/// up to 32 768 nodes each, every one by a Coldridge Valley bot planning to a quest destination in Stormwind 1 800 yards away; live-w24-r1:
/// a 2 139 ms ManagedPlayerbotFeature tick).
/// </summary>
public sealed class PlayerbotThinkBudgetTests
{
    /// <summary>
    /// Within a think, the fallback queries (<see cref="PlayerbotThinkProbe.Fallback"/>) share
    /// <see cref="PlayerbotOptions.MaxThinkSearchNodes"/>: past it a fallback runs only with what is left, and with less than
    /// <see cref="PlayerbotThinkProbe.MinQueryNodes"/> left it is answered no path without a search. A plan's own query is neither held
    /// to it nor counted against it (an expensive plan does not starve the fallbacks after it), and no query off a think is held.
    /// </summary>
    [Fact]
    public async Task ThinkNodeBudget_CutsTheLastFallbackShort_RefusesTheRest_AndNeverTouchesAPlansOwnQuery()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var origin = new Vector3(player.X, player.Y, player.Z);
            // Each search uses all the nodes it is given.
            var search = new PlayerbotCappedSearchTests.CappedEverywhere { Capped = false, NodesPerQuery = int.MaxValue };
            WorldCollision.Of(world.Host.World).Install(pathfinder: search);
            MapCollision collision = player.Map!.Collision;
            var asked = new PathOptions { MaxSearchNodes = 32_768, AllowPartial = true };
            Vector3 far = origin + new Vector3(500, 0, 0);

            var probe = new PlayerbotThinkProbe { NodeBudget = 40_000 };
            probe.Begin();
            PathResult own = PlayerbotThinkProbe.Find(collision, origin, far, asked); // a plan's own: in full, not counted
            PathResult first, second, third;
            using (PlayerbotThinkProbe.Fallback())
            {
                first = PlayerbotThinkProbe.Find(collision, origin, far, asked);  // 40 000 left: in full
                second = PlayerbotThinkProbe.Find(collision, origin, far, asked); // 7 232 left: cut short
            }

            PathResult ownAgain = PlayerbotThinkProbe.Find(collision, origin, far, asked); // a plan's own query again: in full
            using (PlayerbotThinkProbe.Fallback())
                third = PlayerbotThinkProbe.Find(collision, origin, far, asked); // none left: refused
            probe.End();

            Assert.Equal([32_768, 32_768, 40_000 - 32_768, 32_768], search.Budgets);
            Assert.True(own.HasPath && first.HasPath && second.HasPath && ownAgain.HasPath); // a short search still answers
            Assert.False(third.HasPath);
            Assert.Equal(1, probe.Clamped);
            Assert.Equal(1, probe.Refused);
            Assert.Equal((3 * 32_768) + (40_000 - 32_768), probe.Search.Nodes);
            Assert.Contains("node budget 40000: 1 fallback queries cut short, 1 refused", probe.Describe());

            // Off a think: no budget, even inside a fallback scope.
            search.Budgets.Clear();
            using (PlayerbotThinkProbe.Fallback())
                PlayerbotThinkProbe.Find(collision, origin, far, asked);
            Assert.Equal([32_768], search.Budgets);
            return true;
        });
    }

    /// <summary>
    /// The per-tick bound: once <see cref="PlayerbotThinkRotation.TickBudgetMs"/> went by, a tick thinks for no further bot, and the
    /// next tick begins with the first bot this one did not reach; over the ticks every bot thinks. A fake clock (3 ms a think) keeps
    /// it deterministic on a loaded machine.
    /// </summary>
    [Fact]
    public void TickWithin_StopsThinkingOnceTheBudgetIsSpent_AndCarriesTheRestOver()
    {
        var rotation = new PlayerbotThinkRotation();
        const int bots = 10;
        const double thinkMs = 3;
        var thought = new List<int>();
        var perTick = new List<int>();
        for (int tick = 0; tick < 4; tick++)
        {
            double clock = 0;
            perTick.Add(rotation.TickWithin(bots, PlayerbotThinkRotation.TickBudgetMs, index =>
            {
                clock += thinkMs;
                thought.Add(index);
                return true;
            }, () => clock));
        }

        // 3 ms thinks against the 8 ms budget: three a tick (0, 3 and 6 ms in; at 9 ms it stops), never all ten.
        Assert.Equal([3, 3, 3, 3], perTick);
        // Fair: each tick begins where the last one stopped, so the thinks run in order round the bots.
        Assert.Equal(Enumerable.Range(0, 12).Select(i => i % bots), thought);
    }

    /// <summary>The real clock: a tick of 3 ms thinks stops after the budget, not after every bot (a generous bound for a loaded box).</summary>
    [Fact]
    public void TickWithin_OnTheRealClock_LeavesBotsForTheNextTick()
    {
        var rotation = new PlayerbotThinkRotation();
        int visited = rotation.TickWithin(50, budgetMs: 8, _ =>
        {
            Spin(3);
            return true;
        });

        Assert.InRange(visited, 1, 3);
        Assert.Equal(visited % 50, rotation.Cursor);
    }

    /// <summary>One think longer than the whole budget still lets the rotation move on (the first bot of a tick always thinks).</summary>
    [Fact]
    public void TickWithin_AThinkLongerThanTheBudget_StillAdvancesTheRotation()
    {
        var rotation = new PlayerbotThinkRotation();
        var thought = new List<int>();
        for (int tick = 0; tick < 3; tick++)
        {
            double clock = 0;
            rotation.TickWithin(5, budgetMs: 1, index =>
            {
                clock += 3;
                thought.Add(index);
                return true;
            }, () => clock);
        }

        Assert.Equal([0, 1, 2], thought);
    }

    private static void Spin(double ms)
    {
        long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
    }
}
