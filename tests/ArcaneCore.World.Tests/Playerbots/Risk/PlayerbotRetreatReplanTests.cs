using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// A retreat whose straight way out is blocked takes another way, instead of giving up cornered where it stands. Live 2026-10-16
/// (bot-risk/retreat-cornered, about 200 an hour, most "after 0 escapes"): Krezelou, map 1 at 9467.2,518.2 (Teldrassil), five plans in
/// a row toward 9476.1,489.5 refused, cornered 1.4 s later and dead 3 s after that without having moved. The retreat planned one
/// direction only, straight away from its enemies, on every replan: when that one was a cliff edge, water or a wall, all eight
/// replans failed alike within a few thinks. On the real terrain, from that very spot, 14 of 16 directions 30 yards out plan; the two
/// that do not (no ground there) are the two either side of the retreat's own direction.
/// </summary>
public sealed class PlayerbotRetreatReplanTests
{
    [Fact]
    public async Task ARetreatWhoseWayStraightAwayIsBlocked_RunsAnotherWay_AndIsNotCornered()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        // The creature stands west of the bot: straight away from it is east, and nothing east of the bot can be reached.
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(992101, health: 5000, minDamage: 1, maxDamage: 1), -3, 0));
        await world.SeeAsync(mob);
        Vector3 start = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var at = new Vector3(player.X, player.Y, player.Z);
            WorldCollision.Of(world.Host.World).Install(pathfinder: new NoWayEast(at.X + 5f));
            world.Engage(mob);
            world.Brain.Risk.Track(player);
            world.Brain.Risk.StartRetreat(player, [mob], "losing");
            return at;
        });

        bool cornered = await world.ThinkUntilAsync(2_000, () => world.Brain.Risk.Retreat.Outcome == "cornered");
        string report = await world.OnWorldAsync(() => $"{world.Brain.Risk.Retreat.Trace} | {world.Brain.RiskReport}");
        Assert.False(cornered, "the retreat gave up cornered: " + report);
        float moved = await world.OnWorldAsync(() => Vector2.Distance(new(world.Player.X, world.Player.Y), new(start.X, start.Y)));
        Assert.True(moved > 3f, $"the bot moved {moved:F1} yards: {report}");
        Assert.True(await world.OnWorldAsync(() => world.Player.X <= start.X + 5f), "the bot crossed into the blocked east");
    }

    /// <summary>
    /// Control: with every way blocked the retreat still ends cornered (it is bounded and the bot fights on), and quickly: a retreat
    /// that can go nowhere does not stand for its full minute.
    /// </summary>
    [Fact]
    public async Task ARetreatWithNoWayOut_StillEndsCornered()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(992102, health: 5000, minDamage: 1, maxDamage: 1), -3, 0));
        await world.SeeAsync(mob);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            WorldCollision.Of(world.Host.World).Install(pathfinder: new NoWayEast(float.NegativeInfinity));
            world.Engage(mob);
            world.Brain.Risk.Track(player);
            world.Brain.Risk.StartRetreat(player, [mob], "losing");
            return true;
        });

        Assert.True(await world.ThinkUntilAsync(5_000, () => world.Brain.Risk.Retreat.Outcome == "cornered"),
            await world.OnWorldAsync(() => $"{world.Brain.Risk.Retreat.Trace} | {world.Brain.RiskReport}"));
    }

    /// <summary>
    /// Issue #1492 (bot-nav/goal-unreachable, "5 plans in a row toward" a point 30 yards out): a cornered retreat replans toward the
    /// same goal, the retreat distance itself, on each of its failed thinks. Those plans now go through the route plan watch, which
    /// suppresses the pair after its own refusal limit, so a retreat that ends cornered (the expected outcome here) is no navigation
    /// anomaly.
    /// </summary>
    [Fact]
    public async Task ARetreatWithNoWayOut_IsNoGoalUnreachableAnomaly()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(992103, health: 5000, minDamage: 1, maxDamage: 1), -3, 0));
        await world.SeeAsync(mob);
        using AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.Player));
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            WorldCollision.Of(world.Host.World).Install(pathfinder: new NoWayEast(float.NegativeInfinity));
            world.Engage(mob);
            world.Brain.Risk.Track(player);
            world.Brain.Risk.StartRetreat(player, [mob], "losing");
            return true;
        });

        Assert.True(await world.ThinkUntilAsync(5_000, () => world.Brain.Risk.Retreat.Outcome == "cornered"),
            await world.OnWorldAsync(() => $"{world.Brain.Risk.Retreat.Trace} | {world.Brain.RiskReport}"));
        Assert.False(capture.Any(PlayerbotAnomalies.Key("nav", "goal-unreachable")), capture.Describe());
    }

    /// <summary>
    /// Review of #1492: the route plan watch suppresses a retreat's repeated refusals by blacklisting the goal, which marks a loop pending
    /// (PlayerbotMotion.Blacklist). The brain's loop remedy runs before the retreat update, so it must not drop the fight and send the bot
    /// exploring: the retreat still ends cornered and the bot fights its pursuer back instead of standing idle in combat.
    /// </summary>
    [Fact]
    public async Task ARetreatWithNoWayOut_WhoseReplanIsSuppressed_StillEndsCorneredAndFightsBack()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(992104, health: 5000, minDamage: 1, maxDamage: 1), -3, 0));
        await world.SeeAsync(mob);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            WorldCollision.Of(world.Host.World).Install(pathfinder: new NoWayEast(float.NegativeInfinity));
            world.Engage(mob);
            world.Brain.Risk.Track(player);
            world.Brain.Risk.StartRetreat(player, [mob], "losing");
            // What the watch's suppression does mid-retreat (PlayerbotRoutePlanWatch.Suppress): refuse the goal, mark a loop pending.
            PlayerbotMotion.Blacklist(player, [new Vector3(player.X + 30, player.Y, player.Z)], world.Host.World.NowMs);
            return true;
        });

        // The first think consumes the pending loop (PlayerbotBrain ConsumeLoop), so the remedy really ran mid-retreat.
        await world.ThinkAsync();
        Assert.False(await world.OnWorldAsync(() => PlayerbotMotion.ConsumeLoop(world.Player)), "the brain left the loop pending");
        Assert.True(await world.OnWorldAsync(() => world.Brain.Risk.Retreat.Active || world.Brain.Risk.Retreat.Outcome == "cornered"), "the loop remedy ended the retreat");

        Assert.True(await world.ThinkUntilAsync(5_000, () => world.Brain.Risk.Retreat.Outcome == "cornered"),
            await world.OnWorldAsync(() => $"goal={world.Brain.Goal} | {world.Brain.Risk.Retreat.Trace} | {world.Brain.RiskReport}"));
        Assert.True(await world.ThinkUntilAsync(3_000, () => ReferenceEquals(world.Player.Combat.Victim, mob) && world.Brain.Goal != PlayerbotGoalKind.Explore),
            await world.OnWorldAsync(() => $"goal={world.Brain.Goal} victim={world.Player.Combat.Victim?.Guid} combat={world.Player.Combat.IsInCombat} | {world.Brain.Risk.Retreat.Trace}"));
    }

    /// <summary>No path to anything east of <paramref name="limitX"/>; straight lines elsewhere (a cliff edge east of the bot).</summary>
    private sealed class NoWayEast(float limitX) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => end.X > limitX ? PathResult.None(start) : PathResult.StraightLine(start, end, PathType.Normal);
    }
}
