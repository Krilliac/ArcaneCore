using ArcaneCore.Game.Combat;
using System.Numerics;
using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// What the brain does with a slow cadence (a think every few seconds): an exploring bot is handed its next leg before it arrives, a
/// chase follows the target in the think that sees it move, and a wait for a navigation tile does not stand a walking bot still.
/// </summary>
public sealed class PlayerbotCadenceBrainTests
{
    private static PlayerbotRoute? RouteOf(PlayerbotBrain brain)
        => (PlayerbotRoute?)typeof(PlayerbotBrain).GetField("_route", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(brain);

    private static void Think(WorldSession session, PlayerbotBrain brain, uint ms = 500)
    {
        session.ManagedBudget = new ManagedActionBudget(4);
        brain.Update(ms);
    }

    private static void Slow(Player player)
    {
        for (int i = 0; i < 4; i++) PlayerbotMotion.NoteThinkGap(player, 5000);
    }

    [Fact]
    public async Task AnExploringBot_IsHandedItsNextLegBeforeItArrives_AndDoesNotStopBetweenLegs()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } });
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                Slow(player);
                var start = new Vector3(player.X, player.Y, player.Z);
                var explore = typeof(PlayerbotBrain).GetMethod("Explore", BindingFlags.Instance | BindingFlags.NonPublic)!;
                session.ManagedBudget = new ManagedActionBudget(4);
                explore.Invoke(brain, [player, 5000u]);
                PlayerbotRoute first = RouteOf(brain)!;
                Assert.NotNull(first);
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));

                // Five seconds on the bot has walked 35 of the leg's 48 yards: its next think would find it standing at the end.
                PlayerbotMotion.ElapseForTests(player, 5000);
                session.ManagedBudget = new ManagedActionBudget(4);
                explore.Invoke(brain, [player, 5000u]);
                PlayerbotRoute second = RouteOf(brain)!;
                Assert.NotSame(first, second);
                Assert.True(Vector2.Distance(new(second.Points[^1].X, second.Points[^1].Y), new(start.X, start.Y)) > 60f,
                    "the route was not extended past the first leg");

                // Five seconds more: the first leg's end is behind the bot, which still walks.
                PlayerbotMotion.ElapseForTests(player, 5000);
                PlayerbotMotion.Pump(session, player, host.World.NowMs);
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward), "stopped at the end of the first leg");
                Assert.True(Vector2.Distance(new(player.X, player.Y), new(start.X, start.Y)) > 55f);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    private static async Task<Creature> AttackerAsync(WorldTestHost host, WorldSession session, float east)
    {
        Creature mob = null!;
        await host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            mob = new Creature(993902, new CreatureTemplate
            {
                Entry = 993902, Name = "chased mob", CreatureType = 1, MinLevel = 20, MaxLevel = 20,
                MinLevelHealth = 2000, MaxLevelHealth = 2000,
            }, null, CreatureContent.Empty, new Random(993902));
            mob.Relocate(player.X + east, player.Y, player.Z, 0, host.World.NowMs);
            player.Map!.AddObject(mob);
            return true;
        });
        await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(mob.Guid), "mob visibility");
        await host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            Assert.True(player.Map!.Combat.Attack(mob, player));
            player.Map!.Combat.DealDamage(mob, player, 1);
            session.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
            return true;
        });
        return mob;
    }

    [Fact]
    public async Task AChase_IsPlannedAgainInTheThinkThatSeesTheTargetMove()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        Creature mob = await AttackerAsync(host, session, 30);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } });
        try
        {
            await host.OnWorldAsync(() =>
            {
                // The way ends 3 yards short of the target: the route's end is not the place it was planned to.
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new ThreeShort());
                Player player = session.Player!;
                Slow(player);
                Think(session, brain);
                PlayerbotRoute first = RouteOf(brain)!;
                Assert.NotNull(first);
                Assert.Equal(mob.X - 3, first.Points[^1].X, 1);

                // Still: the route in hand is kept.
                Think(session, brain);
                Assert.Same(first, RouteOf(brain));

                // 3 yards from where it was planned to (6 from the route's end): kept, the measure is the planned goal.
                mob.Relocate(mob.X + 3, mob.Y, mob.Z, 0, host.World.NowMs);
                Think(session, brain);
                Assert.Same(first, RouteOf(brain));

                // The target ran 20 yards: this think walks to where it is, with no think of standing between.
                mob.Relocate(mob.X + 20, mob.Y, mob.Z, 0, host.World.NowMs);
                Think(session, brain);
                PlayerbotRoute second = RouteOf(brain)!;
                Assert.NotSame(first, second);
                Assert.Equal(mob.X - 3, second.Points[^1].X, 1);
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task AChaseRouteWalkedToItsEnd_IsPlannedAgainInTheSameThink()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        Creature mob = await AttackerAsync(host, session, 30);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ChaseReplanYards = 50, Risk = { Enabled = false } });
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                Slow(player);
                Think(session, brain);
                PlayerbotRoute first = RouteOf(brain)!;
                PlayerbotMotion.ElapseForTests(player, 5000);
                PlayerbotMotion.Pump(session, player, host.World.NowMs);
                Assert.True(first.Complete);
                Assert.False(player.Movement.HasFlag(MovementFlags.Forward));

                // The target stepped back 8 yards (inside the re-plan distance of 50, so only the finished route asks for a plan) beyond the bot's reach: the bot runs again at once.
                mob.Relocate(mob.X + 8f, mob.Y, mob.Z, 0, host.World.NowMs);
                Think(session, brain);
                Assert.NotSame(first, RouteOf(brain));
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward), "stood for a think gap before the next chase");
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task APendingPlan_KeepsTheChaseRouteStillValid_InsteadOfStandingStill()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        Creature mob = await AttackerAsync(host, session, 30);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } });
        var pathfinder = new PlayerbotTileHoldTests.PendingPathfinder();
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                Player player = session.Player!;
                Slow(player);
                Think(session, brain);
                PlayerbotRoute first = RouteOf(brain)!;
                Assert.NotNull(first);
                uint renewals = PlayerbotMotion.RenewalsOf(player);

                // The target moved, the re-plan has to wait for a tile: the walk goes on along the route in hand, its lease renewed.
                pathfinder.Pending = true;
                mob.Relocate(mob.X + 20, mob.Y, mob.Z, 0, host.World.NowMs);
                Think(session, brain);
                Assert.Same(first, RouteOf(brain));
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                Assert.True(PlayerbotMotion.RenewalsOf(player) > renewals, "the lease of the valid route was not renewed");

                // The tile is in: planned.
                pathfinder.Pending = false;
                Think(session, brain);
                Assert.NotSame(first, RouteOf(brain));
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A chase route cut short of its target (the route bound) is not re-planned while the target moves, until the bot has walked most
    /// of it: planning again at every move of a far target is a path query per think that goes nowhere.
    /// </summary>
    [Fact]
    public async Task ACutShortChaseRoute_IsPlannedAgain_OnlyOnceMostOfItIsWalked()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        Creature mob = await AttackerAsync(host, session, 60);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 30, Risk = { Enabled = false } });
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new TenYardCorners());
                Player player = session.Player!;
                Slow(player);
                Think(session, brain);
                PlayerbotRoute first = RouteOf(brain)!;
                Assert.NotNull(first);
                Assert.True(first.Points[^1].X < mob.X - 20, "the route was not cut short of the target");

                // The target ran 10 yards; the bot has the whole of its 30 yards ahead: the route in hand stays.
                mob.Relocate(mob.X + 10, mob.Y, mob.Z, 0, host.World.NowMs);
                Think(session, brain);
                Assert.Same(first, RouteOf(brain));

                // 24.5 of the 30 yards walked: the rest is short, the next plan is worth its query.
                PlayerbotMotion.ElapseForTests(player, 3500);
                Think(session, brain);
                Assert.NotSame(first, RouteOf(brain));
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }


    /// <summary>A straight way with a corner every ten yards, so a route longer than the bound can be cut at a corner.</summary>
    private sealed class TenYardCorners : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            var points = new List<Vector3> { start };
            float length = Vector3.Distance(start, end);
            for (float at = 10; at < length; at += 10) points.Add(Vector3.Lerp(start, end, at / length));
            points.Add(end);
            return new PathResult(PathType.Normal, points);
        }
    }

    /// <summary>
    /// A stun, fear or confusion pauses an exploring bot: it does not move or send a start, and its brain keeps the route and refuses
    /// nothing (no abandoned route, no blocked cell); when the flag ends the bot walks the same route again.
    /// </summary>
    [Fact]
    public async Task AControlLoss_PausesTheWalk_WithoutRefusingTheRoute()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } });
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                Slow(player);
                var explore = typeof(PlayerbotBrain).GetMethod("Explore", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var blocked = (System.Collections.IDictionary)typeof(PlayerbotBrain).GetField("_exploreBlockedCells", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(brain)!;
                session.ManagedBudget = new ManagedActionBudget(4);
                explore.Invoke(brain, [player, 5000u]);
                PlayerbotRoute first = RouteOf(brain)!;
                Assert.NotNull(first);
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));

                PlayerbotMotion.ElapseForTests(player, 200);
                player.UnitFlags |= UnitFlags.Fleeing;
                PlayerbotMotion.Pump(session, player, host.World.NowMs);
                Assert.False(player.Movement.HasFlag(MovementFlags.Forward));
                uint packets = PlayerbotMotion.Of(player).Packets;
                uint renewals = PlayerbotMotion.RenewalsOf(player);

                // Thinks while feared: no start, no new route, nothing refused.
                for (int think = 0; think < 3; think++)
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    explore.Invoke(brain, [player, 5000u]);
                }

                Assert.False(player.Movement.HasFlag(MovementFlags.Forward), "restarted while feared");
                Assert.Equal(packets, PlayerbotMotion.Of(player).Packets);
                Assert.Equal(renewals, PlayerbotMotion.RenewalsOf(player));
                Assert.Same(first, RouteOf(brain));
                Assert.Empty(blocked);
                Assert.False(brain.TakeRouteAbandoned());

                // The fear ends: the same route is walked again.
                player.UnitFlags ^= UnitFlags.Fleeing;
                session.ManagedBudget = new ManagedActionBudget(4);
                explore.Invoke(brain, [player, 5000u]);
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                Assert.Same(first, RouteOf(brain));
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>A way that ends 3 yards short of where it was asked to go.</summary>
    private sealed class ThreeShort : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Vector3 direction = end - start;
            float length = direction.Length();
            return new PathResult(PathType.Normal, [start, length > 4f ? end - (direction / length * 3f) : end]);
        }
    }

}
