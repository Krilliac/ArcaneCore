using System.Numerics;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Travel;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Bot QA #1488 (stall: goal Explore map 1 near 5700,-4700, Winterspring): bots stood at exactly the same spot on a steep slope with a
/// route in hand and a walk the motion accepted, so no refusal ever fired, and the Explore give-ups were of wander legs, which have no
/// atlas candidate, so the cell blacklist blocked nothing and the next leg went the same way.
/// </summary>
public sealed class PlayerbotStallExploreInPlaceTests
{
    /// <summary>
    /// A wander leg given up for a stall blocks the cell the bot stands in, as an atlas candidate's cell is blocked, so the next explore
    /// search skips that cell and the next target lies outside it. A cell 400 yards away is not blocked.
    /// </summary>
    [Fact]
    public async Task GivenUpWanderLeg_BlocksTheCellTheBotStandsIn()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync("Stallwander");
        Player player = session.Player!;
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } });
        try
        {
            await rig.OnWorld(() =>
            {
                Assert.False(brain.IsExploreCellBlocked(player.MapId, player.X, player.Y));
                brain.HoldGoalForTests(PlayerbotGoalKind.Explore, 0, 0); // a wander leg: no atlas candidate is held
                brain.GiveUpStalledGoal(player);
                Assert.True(brain.IsExploreCellBlocked(player.MapId, player.X, player.Y),
                    $"the explore cell {PlayerbotExploreAtlas.CellOf(player.X)},{PlayerbotExploreAtlas.CellOf(player.Y)} the bot stalled in is not blocked");
                Assert.False(brain.IsExploreCellBlocked(player.MapId, player.X + 400f, player.Y));
                return 0;
            });
        }
        finally { brain.Stop(); }
    }

    private static PlayerbotBrain NewBrain(WorldSession session)
        => new(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } });

    private static async Task InstallOpenFloorAsync(MageTravelRig rig)
        => await rig.OnWorld(() =>
        {
            WorldCollision.Of(rig.Host.World).Install(lineOfSight: new MageTravelFlatFloor(), pathfinder: new MageTravelOpenPathfinder());
            return 0;
        });

    /// <summary>
    /// A bot whose walk the motion accepts but which does not displace (the thinks pass, the stored position does not change) is refused like a
    /// walk the motion rejects: the route is given up after <see cref="PlayerbotBrain.ExploreInPlaceMs"/>, and the next thinks plan another
    /// leg (the wander plans grow past their count at the give-up).
    /// </summary>
    [Fact]
    public async Task AWalkThatMovesTheBotNowhere_IsGivenUp_AndPlannedAgain()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync("Stallinplace");
        Player player = session.Player!;
        PlayerbotBrain brain = NewBrain(session);
        try
        {
            await InstallOpenFloorAsync(rig);
            Vector3 start = await rig.OnWorld(() => new Vector3(player.X, player.Y, player.Z));
            // The bot is held at its start after every think (nothing carries the walk out, whatever else the world does). The world clock
            // runs in real time here, so each think ends with the motion's own heartbeat (aged past the interval, then pumped and put back):
            // that re-anchors the motion every think, so no heartbeat falls inside a think and moves the stored position the check reads,
            // however slowly the box runs.
            int thinks = (int)(PlayerbotBrain.ExploreInPlaceMs / 500) + 6;
            int plansAtGiveUp = -1;
            for (int think = 0; think < thinks + 8; think++)
            {
                await rig.OnWorld(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(8);
                    brain.Update(500);
                    PlayerbotMotion.ElapseForTests(player, 500);
                    PlayerbotMotion.Pump(session, player, rig.Host.World.NowMs);
                    player.SetPosition(start.X, start.Y, start.Z, player.Orientation);
                    if (plansAtGiveUp < 0 && brain.ExploreInPlaceGiveUps >= 1) plansAtGiveUp = brain.ExploreWanderPlans;
                    return 0;
                });
            }

            await rig.OnWorld(() =>
            {
                Assert.Equal(PlayerbotGoalKind.Explore, brain.Goal);
                Assert.True(brain.ExploreWanderPlans >= 1, "the bot never planned a wander leg: the test proves nothing");
                Assert.True(plansAtGiveUp >= 0, $"{thinks} thinks without a step did not give the walk up (wander plans {brain.ExploreWanderPlans})");
                Assert.True(brain.ExploreWanderPlans > plansAtGiveUp,
                    $"after the give-up the bot planned no other leg (plans {plansAtGiveUp} at the give-up, {brain.ExploreWanderPlans} after eight more thinks)");
                return 0;
            });
        }
        finally { brain.Stop(); }
    }

    /// <summary>
    /// Bot QA #1488 as the motion makes it: the pump runs with time advancing, a heartbeat comes every 500 ms and each one leaves the bot at
    /// the same start point (the stored position is put back to it after every pump; the motion's anchor, and so its projected position,
    /// still creep forward). The check reads the stored position, so the walk is refused; read through the motion's projection it never was.
    /// </summary>
    [Fact]
    public async Task AWalkWhoseHeartbeatsAllValidateToTheStartPoint_WithTheMotionPumped_IsRefused()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync("Stallpumped");
        Player player = session.Player!;
        PlayerbotBrain brain = NewBrain(session);
        try
        {
            await InstallOpenFloorAsync(rig);
            Vector3 start = await rig.OnWorld(() => new Vector3(player.X, player.Y, player.Z));
            int thinks = (int)(PlayerbotBrain.ExploreInPlaceMs / 500) + 8;
            for (int think = 0; think < thinks; think++)
            {
                await rig.OnWorld(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(8);
                    brain.Update(500);
                    PlayerbotMotion.ElapseForTests(player, 500);
                    PlayerbotMotion.Pump(session, player, rig.Host.World.NowMs);
                    player.SetPosition(start.X, start.Y, start.Z, player.Orientation);
                    return 0;
                });
            }

            await rig.OnWorld(() =>
            {
                Assert.True(brain.ExploreWanderPlans >= 1, "the bot never planned a wander leg: the test proves nothing");
                Assert.True(brain.ExploreInPlaceGiveUps >= 1, $"{thinks} pumped thinks that left the bot at one point did not give the walk up");
                return 0;
            });
        }
        finally { brain.Stop(); }
    }

    /// <summary>
    /// The motion's projected position (route position at the current time, <see cref="PlayerbotMotion.CurrentPosition"/>) runs ahead of the
    /// frozen stored position by the speed times the age of the last heartbeat; the check must not read it. The think's own motion step may
    /// send a heartbeat (the age passes the heartbeat interval), which moves the stored position, so it is put back to the start after every
    /// think: the stored position is frozen at each check while the projection and the route's anchor run ahead. The motion is aged 300 ms
    /// before each think (no heartbeat is due inside it, so the projection at the check is 300 ms of walking ahead) and 200 ms more after it,
    /// where the pump sends the heartbeat that is put back.
    /// </summary>
    [Fact]
    public async Task AWalkWhoseProjectedPositionRunsAhead_ButWhoseStoredPositionIsFrozen_IsRefused()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync("Stallproject");
        Player player = session.Player!;
        PlayerbotBrain brain = NewBrain(session);
        try
        {
            await InstallOpenFloorAsync(rig);
            Vector3 start = await rig.OnWorld(() => new Vector3(player.X, player.Y, player.Z));
            int thinks = (int)(PlayerbotBrain.ExploreInPlaceMs / 500) + 8;
            for (int think = 0; think < thinks; think++)
            {
                await rig.OnWorld(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(8);
                    PlayerbotMotion.ElapseForTests(player, 300);
                    brain.Update(500);
                    PlayerbotMotion.ElapseForTests(player, 200);
                    PlayerbotMotion.Pump(session, player, rig.Host.World.NowMs);
                    player.SetPosition(start.X, start.Y, start.Z, player.Orientation);
                    return 0;
                });
            }

            await rig.OnWorld(() =>
            {
                Assert.True(brain.ExploreWanderPlans >= 1, "the bot never planned a wander leg: the test proves nothing");
                Assert.True(brain.ExploreInPlaceGiveUps >= 1, $"{thinks} thinks with a frozen stored position did not give the walk up");
                return 0;
            });
        }
        finally { brain.Stop(); }
    }

    /// <summary>The opposite: a walk the motion really carries out (the pump runs, the bot moves) is never refused.</summary>
    [Fact]
    public async Task AWalkThatReallyMovesTheBot_IsNeverRefused()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync("Stallmoving");
        Player player = session.Player!;
        PlayerbotBrain brain = NewBrain(session);
        try
        {
            await InstallOpenFloorAsync(rig);
            Vector3 start = await rig.OnWorld(() => new Vector3(player.X, player.Y, player.Z));
            int thinks = (int)(PlayerbotBrain.ExploreInPlaceMs / 500) * 3;
            float farthest = 0f;
            for (int think = 0; think < thinks; think++)
            {
                farthest = Math.Max(farthest, await rig.OnWorld(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(8);
                    brain.Update(500);
                    PlayerbotMotion.ElapseForTests(player, 500);
                    PlayerbotMotion.Pump(session, player, rig.Host.World.NowMs);
                    return Vector3.Distance(start, new Vector3(player.X, player.Y, player.Z));
                }));
            }

            await rig.OnWorld(() =>
            {
                Assert.True(brain.ExploreWanderPlans >= 1, "the bot never planned a wander leg: the test proves nothing");
                Assert.True(farthest > PlayerbotBrain.ExploreInPlaceYards * 4, $"the pumped bot never left its start (farthest {farthest:F1} yards): the test proves nothing");
                Assert.Equal(0, brain.ExploreInPlaceGiveUps);
                return 0;
            });
        }
        finally { brain.Stop(); }
    }

    /// <summary>The rule on its own: the stored position held within the bound for the whole time refuses the walk exactly then, and not before.</summary>
    [Fact]
    public async Task StandsStill_WhenTheStoredPositionHoldsForTheWholeTime_AndNotBefore()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync("Stallrule");
        Player player = session.Player!;
        PlayerbotBrain brain = NewBrain(session);
        try
        {
            await rig.OnWorld(() =>
            {
                uint steps = PlayerbotBrain.ExploreInPlaceMs / 500;
                Assert.False(brain.ExploreWalkStandsStill(player, 500)); // the first sample only sets the anchor
                for (uint step = 1; step < steps; step++)
                    Assert.False(brain.ExploreWalkStandsStill(player, 500), $"refused after {step * 500} ms");
                Assert.True(brain.ExploreWalkStandsStill(player, 500), "not refused after the whole time");
                return 0;
            });
        }
        finally { brain.Stop(); }
    }

    /// <summary>The rule on its own: a bot that steps further than the bound each think is never refused, however long it walks.</summary>
    [Fact]
    public async Task StandsStill_IsNeverTrue_ForABotThatStepsOutOfTheBoundEveryThink()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync("Stallstep");
        Player player = session.Player!;
        PlayerbotBrain brain = NewBrain(session);
        try
        {
            await rig.OnWorld(() =>
            {
                float x = player.X;
                for (int step = 0; step < 100; step++)
                {
                    player.SetPosition(x, player.Y, player.Z, player.Orientation);
                    Assert.False(brain.ExploreWalkStandsStill(player, 500), $"refused at step {step}");
                    x += PlayerbotBrain.ExploreInPlaceYards + 1f;
                }

                return 0;
            });
        }
        finally { brain.Stop(); }
    }

    /// <summary>A Vendor, Train or Mount label held past its think (the choose-target path, no errand claimed the think) becomes Explore/0; other goals are left alone.</summary>
    [Fact]
    public async Task ClearStaleErrandLabel_DropsVendorTrainMountToExplore_AndLeavesOtherGoals()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync("Stalllabel");
        PlayerbotBrain brain = NewBrain(session);
        try
        {
            await rig.OnWorld(() =>
            {
                foreach (PlayerbotGoalKind label in new[] { PlayerbotGoalKind.Vendor, PlayerbotGoalKind.Train, PlayerbotGoalKind.Mount })
                {
                    brain.HoldGoalForTests(label, 3098, 0);
                    brain.ClearStaleErrandLabel();
                    Assert.Equal(PlayerbotGoalKind.Explore, brain.Goal);
                    Assert.Equal(0u, brain.TargetEntry);
                }

                foreach (PlayerbotGoalKind kept in new[] { PlayerbotGoalKind.Quest, PlayerbotGoalKind.Grind, PlayerbotGoalKind.Combat })
                {
                    brain.HoldGoalForTests(kept, 3098, 7);
                    brain.ClearStaleErrandLabel();
                    Assert.Equal(kept, brain.Goal);
                    Assert.Equal(3098u, brain.TargetEntry);
                }

                return 0;
            });
        }
        finally { brain.Stop(); }
    }
}
