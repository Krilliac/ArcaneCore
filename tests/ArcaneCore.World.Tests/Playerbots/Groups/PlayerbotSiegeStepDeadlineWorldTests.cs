using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using GridDefines = ArcaneCore.Game.Maps.Grid.GridDefines;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Waived lag, the step deadline (held away, retreat latch, unled raids), and unreachable pull targets. Split from the former <c>PlayerbotSiegeWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotSiegeWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotSiegeStepDeadlineWorldTests : PlayerbotSiegeWorldTestBase
{
    [Fact]
    public async Task ALoneRaidThatFoughtItsWayToTheLastWaypoint_HeadsForTheObjective_AndWinsAsEver()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 1, objectiveFar: false);
        await using GroupTestWorld _ = world;
        await world.OnWorldAsync(() =>
        {
            Teleport(world, siege.Raids[0], 60f); // the last waypoint
            return true;
        });

        List<int> seen = await StepsUntil(world, siege, 3, 30_000); // the last waypoint: the step after it is the approach to the objective, which the raid fights from there
        AssertOneAtATime(seen, await world.TraceAsync());
        Assert.Equal(3, siege.Step);
        Assert.True(await world.RunUntilAsync(300_000, () => world.Coordinator.Siege is null), await world.TraceAsync());
        Assert.Equal("won", world.Coordinator.SiegeOutcome);
    }

    [Fact]
    public async Task APushingRaidWhoseLagIsWaived_WithTheFurthestWaypointTwoBeyondTheStep_MovesByOneStep_AndSaysSo()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        world.Options.Party.TeleportToLeader = false;
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        var held = raid.Group!.Members.First(m => !m.Real && m.BotId != raid.Group.LeaderBotId);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X, Origin.Y + 120f, far.Z, 0, Now(world));
        }

        await world.OnWorldAsync(() => { KeepInCombat(world, raid); Hold(); return true; });
        bool planted = false, waived = false;
        var seen = new List<int> { siege.Step };
        bool moved = await world.RunUntilAsync(PlayerbotSiegeCommander.PushThroughQuietMs + PlayerbotGroupCoordinator.LagHoldMs + 60_000, () =>
        {
            KeepInCombat(world, raid);
            Hold();
            // The leader has fought past the next waypoint too, as far as the siege knows.
            if (!planted && raid.AtWaypoint && raid.PushingThrough)
            {
                raid.FurthestWaypoint = siege.Step + 2;
                planted = true;
            }

            waived |= raid.LastFacts.LagWaived;
            if (siege.Step != seen[^1]) seen.Add(siege.Step);
            return seen.Count > 1;
        });
        Assert.True(moved && planted, $"the step never moved (planted {planted}): {raid.LastFacts}" + Environment.NewLine + await world.TraceAsync());

        // The waiver is spent by that one step: the member is still held, and nothing moves the step again.
        await world.RunUntilAsync(5_000, () =>
        {
            KeepInCombat(world, raid);
            Hold();
            if (siege.Step != seen[^1]) seen.Add(siege.Step);
            return false;
        });
        Assert.True(seen.SequenceEqual([0, 1]), $"the step went {string.Join(" -> ", seen)}" + Environment.NewLine + await world.TraceAsync());
        Assert.True(waived, "the lag was never waived: " + raid.LastFacts);
        Assert.Contains(world.Coordinator.Events, e => e.Contains("pushing on from waypoint 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWaivedLag_IsSpentByTheStepItMoves_EvenWhenTheNextWaypointLiesInTheSameDisk()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        world.Options.Party.TeleportToLeader = false;
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 24f, 28f, 32f); // 4 yards apart: the leader stands in the next disk too
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        var held = raid.Group!.Members.First(m => !m.Real && m.BotId != raid.Group.LeaderBotId);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X, Origin.Y + 120f, far.Z, 0, Now(world));
        }

        await world.OnWorldAsync(() => { KeepInCombat(world, raid); Hold(); return true; });
        var changes = new List<(int Step, uint At)>();
        int last = siege.Step;
        bool two = await world.RunUntilAsync(PlayerbotSiegeCommander.PushThroughQuietMs + (2 * PlayerbotGroupCoordinator.LagHoldMs) + 60_000, () =>
        {
            KeepInCombat(world, raid);
            Hold();
            if (siege.Step != last)
            {
                last = siege.Step;
                changes.Add((last, Now(world)));
            }

            return changes.Count >= 2;
        });

        string story = string.Join(", ", changes.Select(c => $"step {c.Step} at {c.At}")) + $": {raid.LastFacts}" + Environment.NewLine + await world.TraceAsync();
        Assert.True(two, "the step moved fewer than twice: " + story);
        // The first bump is the waived one (after LagHoldMs at the waypoint); the second costs a fresh LagHoldMs, not 400 ms and not the 5 s of a retry.
        Assert.Equal(changes[0].Step + 1, changes[1].Step);
        uint gap = unchecked(changes[1].At - changes[0].At);
        Assert.True(gap >= PlayerbotGroupCoordinator.LagHoldMs - 2_000, $"the second bump came {gap} ms after the first (a waiver is spent by the step it moves; the hold is {PlayerbotGroupCoordinator.LagHoldMs} ms): " + story);
    }

    [Fact]
    public async Task AMemberBeyondLagYardsForLagHoldMs_OutOfCombat_NoLongerHoldsTheRegroup_InTheWorld()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        world.Options.Party.TeleportToLeader = false;
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        var held = raid.Group!.Members.First(m => !m.Real && m.BotId != raid.Group.LeaderBotId);
        bool regrouped = false;
        uint since = 0;
        bool released = await world.RunUntilAsync(PlayerbotGroupCoordinator.LagHoldMs + 60_000, () =>
        {
            world.World.Host.World.FindOnlinePlayer(held.Guid)!.Relocate(Origin.X, Origin.Y + 120f, Origin.Z, 0, Now(world));
            if (raid.LastFacts.MaxLagYards > PlayerbotSiegeCommander.LagYards)
            {
                if (since == 0) since = Now(world);
                regrouped |= raid.Order == PlayerbotSiegeOrder.Regroup;
            }

            return raid.LastFacts.LagWaived && raid.Order != PlayerbotSiegeOrder.Regroup;
        });

        Assert.True(regrouped, "the raid never regrouped for the straggler");
        Assert.True(released, $"the regroup was never lifted: {raid.Order} {raid.LastFacts}" + Environment.NewLine + await world.TraceAsync());
        Assert.True(unchecked(Now(world) - since) >= PlayerbotGroupCoordinator.LagHoldMs - 2_000, "the lag was waived before LagHoldMs");
    }

    [Fact]
    public async Task APeerAwayAfterReachingTheWaypoint_WithAStragglerBeyondLagYards_StepsByCreditOnceTheLagIsWaived()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        world.Options.Party.TeleportToLeader = false;
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 2);
        PlayerbotSiegeRaid raid = siege.Raids[0], peer = siege.Raids[1];
        var held = raid.Group!.Members.First(m => !m.Real && m.BotId != raid.Group.LeaderBotId);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X, Origin.Y + 120f, far.Z, 0, Now(world));
        }

        await world.OnWorldAsync(() => { KeepInCombat(world, raid); Hold(); return true; });
        // Both raids stand at the waypoint; then the peer is kept away from it, as a defender post that keeps pulling it off would.
        Assert.True(await world.RunUntilAsync(60_000, () =>
        {
            KeepInCombat(world, raid);
            Hold();
            return peer.FurthestWaypoint >= 0 && raid.FurthestWaypoint >= 0 && siege.Step == 0 && raid.AtWaypoint && raid.PushingThrough;
        }), "the raids never stood at the first waypoint: " + await world.TraceAsync());
        int step = siege.Step;
        bool waived = false;
        await world.RunUntilAsync(PlayerbotGroupCoordinator.LagHoldMs + 40_000, () =>
        {
            KeepInCombat(world, raid);
            Hold();
            foreach (var member in peer.Group!.Members.Where(m => !m.Real && m.Joined))
                world.World.Host.World.FindOnlinePlayer(member.Guid)?.Relocate(Origin.X - 60f, Origin.Y, Origin.Z, 0, Now(world));
            waived |= raid.LastFacts.LagWaived;
            return siege.Step != step;
        });

        // The lag clock runs wherever the leader is: after LagHoldMs the lag is waived, and the credit (the peer stood at the waypoint once) moves the step by one and says the lag was waived.
        Assert.True(waived, "the lag was never waived: " + raid.LastFacts);
        Assert.Equal(step + 1, siege.Step);
        Assert.Contains(world.Coordinator.Events, e => e.Contains($"every raid has reached waypoint {step + 1}", StringComparison.Ordinal));
        Assert.Contains(world.Coordinator.Events, e => e.Contains("pushing on from waypoint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoRaidsHeldAwayFromTheWaypoint_ForStepDeadlineMs_MoveOnOneStep_AndSaySo()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        int step = siege.Step;
        Assert.True(await HoldAwayUntilDeadline(world, siege, 20_000), "the run ended before the deadline: " + await world.TraceAsync());
        Assert.Equal(step, siege.Step); // 20 s short of the deadline: the step stays put
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("not taken", StringComparison.Ordinal));

        Assert.True(await world.RunUntilAsync(40_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            return siege.Step != step;
        }), "the step never moved at the deadline: " + await world.TraceAsync());
        Assert.Equal(step + 1, siege.Step);
        Assert.Contains(world.Coordinator.Events, e => e.Contains($"waypoint {step + 1} not taken in {PlayerbotGroupCoordinator.StepDeadlineMs / 1000}s, moving on to waypoint {step + 2}", StringComparison.Ordinal));
        Assert.All(siege.Raids, r => Assert.Contains(r.Announcer.Sent, line => line.Contains("not taken", StringComparison.Ordinal)));
        await world.RunAsync(1_000);
        Assert.Contains(siege.Raids, r => r.Detail.Contains("furthest=", StringComparison.Ordinal) && r.Detail.Contains("deadline=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStepWhoseRetreatLatchWasHeldPastTheDeadline_MovesOnlyAFreshDeadlineAfterTheLatchClears()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        int step = siege.Step;
        PlayerbotSiegeRaid latched = siege.Raids[0];
        Assert.True(await world.RunUntilAsync(PlayerbotGroupCoordinator.StepDeadlineMs + 120_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            latched.RetreatLatched = true;
            latched.RetreatSinceMs = Now(world);
            KeepInCombat(world, latched);
            return unchecked(Now(world) - siege.StepSinceMs) >= PlayerbotGroupCoordinator.StepDeadlineMs + 30_000;
        }), "the run ended early: " + await world.TraceAsync());
        Assert.Equal(step, siege.Step); // the latch held the step past the deadline

        // The latch clears (the raid is out of combat): the step does not move at once.
        uint cleared = 0;
        Assert.True(await world.RunUntilAsync(60_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            if (!latched.RetreatLatched) cleared = Now(world);
            return cleared != 0;
        }), "the latch never cleared: " + latched.LastFacts);
        await world.RunUntilAsync(30_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            return siege.Step != step;
        });
        Assert.Equal(step, siege.Step);

        // And it moves after a fresh deadline.
        Assert.True(await world.RunUntilAsync(PlayerbotGroupCoordinator.StepDeadlineMs + 60_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            return siege.Step != step;
        }), "the step never moved after the latch cleared: " + await world.TraceAsync());
        Assert.True(unchecked(Now(world) - cleared) >= PlayerbotGroupCoordinator.StepDeadlineMs - 5_000, $"the step moved {unchecked(Now(world) - cleared)} ms after the latch cleared");
    }

    [Fact]
    public async Task APinnedRetreat_AdvancesRetreatSinceMs_AndReleasesAfterRetreatMaxMs_ResettingStepSinceMs()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await IntoTheFight(world);
        uint stepSinceBefore = siege.StepSinceMs;
        await KillAsync(world, raid, "mage");
        Assert.True(await world.RunUntilAsync(30_000, () => raid.RetreatLatched && raid.Order == PlayerbotSiegeOrder.Retreat), await world.TraceAsync());
        Assert.NotEqual(0u, raid.RetreatSinceMs);
        uint retreatSince = raid.RetreatSinceMs;

        // Keep the raid pinned in combat far from the retreat point (60 yards out, while the retreat point is at 20 yards).
        uint maxRetreating = 0;
        bool seenOrderRally = false;
        bool released = await world.RunUntilAsync(PlayerbotSiegeCommander.RetreatMaxMs + 20_000, () =>
        {
            TopUpGuards(world, raid);
            KeepInCombat(world, raid);
            Teleport(world, raid, 60f);
            maxRetreating = Math.Max(maxRetreating, raid.LastFacts.MsRetreating);
            seenOrderRally |= raid.Order == PlayerbotSiegeOrder.Rally;
            return !raid.RetreatLatched;
        });

        Assert.True(released, $"the pinned retreat latch never released (MsRetreating={maxRetreating}, Order={raid.Order}): " + await world.TraceAsync());
        Assert.False(seenOrderRally, "the raid reached AtRally instead of expiring via RetreatMaxMs");
        Assert.True(maxRetreating >= PlayerbotSiegeCommander.RetreatMaxMs - 1_000, $"MsRetreating only reached {maxRetreating}");
        Assert.Equal(0u, raid.RetreatSinceMs);
        Assert.True(raid.WasInCombat, "the raid left combat instead of expiring via RetreatMaxMs");
        Assert.True(siege.StepSinceMs > stepSinceBefore && unchecked(siege.StepSinceMs - retreatSince) >= PlayerbotSiegeCommander.RetreatMaxMs - 1_000,
            $"StepSinceMs ({siege.StepSinceMs}) was not reset when the retreat latch released after {retreatSince}");
    }

    [Fact]
    public async Task TheDeadline_IsPerStep_AndMovesByOne()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        int step = siege.Step;
        Assert.True(await HoldAwayUntilDeadline(world, siege, 2_000), "the run ended before the deadline: " + await world.TraceAsync());
        await world.RunUntilAsync(10_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            return siege.Step != step;
        });
        Assert.Equal(step + 1, siege.Step);

        // The clock started afresh with the new step: nothing moves it again for a whole deadline, and then it moves by one.
        Assert.True(await HoldAwayUntilDeadline(world, siege, 20_000), "the run ended before the second deadline: " + await world.TraceAsync());
        Assert.Equal(step + 1, siege.Step);
        await world.RunUntilAsync(40_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            return siege.Step != step + 1;
        });
        Assert.Equal(step + 2, siege.Step);
    }

    [Fact]
    public void ARaidFlippingBetweenUnledAndLed_OnlyPausesTheDeadline_ByTheTimeItWasUnled()
    {
        var siege = new PlayerbotSiege(1, 0, 1, [new Vector3(10f, 0f, 0f), new Vector3(20f, 0f, 0f)], default, 1_000);
        uint now = 1_000, unledTotal = 0, moved = 0;
        // Every 10 s the raid loses its leader for 1 s (a death and a prompt promotion); the ticks are 500 ms apart.
        while (moved == 0 && now < 1_000 + (2 * PlayerbotGroupCoordinator.StepDeadlineMs))
        {
            now += 500;
            bool unled = (now / 500) % 20 == 0 || (now / 500) % 20 == 1; // 1 s in every 10 s
            if (unled) unledTotal += 500;
            siege.TrackLeaders(unled, now);
            if (!unled && PlayerbotGroupCoordinator.StepDeadlineDue(siege, 0, false, now)) moved = now;
        }

        Assert.NotEqual(0u, moved);
        uint expected = 1_000 + PlayerbotGroupCoordinator.StepDeadlineMs + unledTotal;
        Assert.InRange(moved, expected - 1_000, expected + 2_000);
    }

    [Fact]
    public void TheAnnouncer_Force_SendsTheLineWithinTheGap_WhereOfferDoesNot()
    {
        var announcer = new PlayerbotSiegeAnnouncer();
        Assert.True(announcer.Offer("first", 10_000));
        Assert.False(announcer.Offer("second", 11_000)); // within the gap
        announcer.Force("forced", 11_500);
        Assert.Equal(new[] { "first", "forced" }, announcer.Sent);
    }

    [Fact]
    public void TheDeadline_WaitsForARetreatLatch_AndForAnUnledRaid()
    {
        var siege = new PlayerbotSiege(1, 0, 1, [new Vector3(10f, 0f, 0f), new Vector3(20f, 0f, 0f)], default, 1_000);
        uint due = 1_000 + PlayerbotGroupCoordinator.StepDeadlineMs;
        Assert.False(PlayerbotGroupCoordinator.StepDeadlineDue(siege, 0, false, due - 1));
        Assert.True(PlayerbotGroupCoordinator.StepDeadlineDue(siege, 0, false, due));
        Assert.False(PlayerbotGroupCoordinator.StepDeadlineDue(siege, 1, false, due)); // a raid without a leader
        Assert.False(PlayerbotGroupCoordinator.StepDeadlineDue(siege, 0, true, due)); // a retreat latch
        siege.Step = 2; // past the last waypoint: nothing to move on to
        Assert.False(PlayerbotGroupCoordinator.StepDeadlineDue(siege, 0, false, due + 1_000_000));
    }

    [Fact]
    public async Task ARaidUnledPastTheDeadline_ThenLedAgain_DoesNotMoveTheStepAtOnce_ButAfterTheRemainingLedTime()
    {
        await using GroupTestWorld world = await StartWorld(guards: false);
        (PlayerbotSiege siege, PlayerbotSiegeRaid fallen) = await LeaderlessRaid(world);
        PlayerbotSiegeRaid other = siege.Raids[0];
        uint stepSince = 0, unledSince = 0, began = Now(world);

        // Unled for longer than the deadline: the step stays.
        await world.RunUntilAsync(PlayerbotGroupCoordinator.StepDeadlineMs + 60_000, () =>
        {
            fallen.PromoteTriedMs = Math.Max(1u, Now(world)); // no promotion
            Teleport(world, other, -60f);
            if (stepSince == 0 && siege.UnledSinceMs != 0) { stepSince = siege.StepSinceMs; unledSince = siege.UnledSinceMs; }
            return unchecked(Now(world) - began) >= PlayerbotGroupCoordinator.StepDeadlineMs + 40_000;
        });
        Assert.NotEqual(0u, unledSince);
        Assert.Equal(0, siege.Step);

        // Promotion runs: the raid is led again. The step does not move at once, nor within 30 s.
        uint ledAt = 0;
        bool movedEarly = false;
        await world.RunUntilAsync(120_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) if (raid.Group is not null && (raid != fallen || siege.UnledSinceMs == 0)) Teleport(world, raid, -60f);
            if (ledAt == 0 && siege.UnledSinceMs == 0) ledAt = Now(world);
            movedEarly |= siege.Step != 0;
            return ledAt != 0 && unchecked(Now(world) - ledAt) >= 30_000;
        });
        Assert.NotEqual(0u, ledAt);
        Assert.False(movedEarly, "the step moved within 30 s of the leader's return");

        // It moves after what remained of the deadline when the raid lost its leader.
        uint movedAt = 0;
        Assert.True(await world.RunUntilAsync(PlayerbotGroupCoordinator.StepDeadlineMs + 60_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            if (siege.Step != 0 && movedAt == 0) movedAt = Now(world);
            return movedAt != 0;
        }), "the step never moved: " + await world.TraceAsync());
        uint expected = ledAt + PlayerbotGroupCoordinator.StepDeadlineMs - unchecked(unledSince - stepSince);
        Assert.InRange(movedAt, expected - 3_000, expected + 4_000);
    }

    [Fact]
    public async Task TheDeadline_DoesNotMoveTheStep_WhileARaidIsWithoutALeader()
    {
        await using GroupTestWorld world = await StartWorld(guards: false);
        (PlayerbotSiege siege, PlayerbotSiegeRaid fallen) = await LeaderlessRaid(world);
        bool moved = false;
        await world.RunUntilAsync(PlayerbotGroupCoordinator.StepDeadlineMs + 40_000, () =>
        {
            fallen.PromoteTriedMs = Math.Max(1u, Now(world));
            moved |= siege.Step != 0;
            return moved;
        });
        Assert.False(moved, "the step moved with a raid without a leader");
    }

    [Fact]
    public async Task ASiegeLeaderWithNoPathToItsPullTarget_ShunsIt_AndTheSkullMovesOn()
    {
        // An unreachable hostile within 40 yards (30) and a reachable one at 45.
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await ScanFixture(() => [Guard(GuardA, 30f, 0f, health: 600), Guard(GuardB, 45f, 0f, health: 600)]);
        await using GroupTestWorld _ = world;
        ObjectGuid unreachable = default, next = default;
        await world.OnWorldAsync(() =>
        {
            unreachable = Spawned(world, raid, GuardA).Guid;
            next = Spawned(world, raid, GuardB).Guid;
            world.Options.AllowedMaps = [1]; // walking is refused: the leader finds no way to the guard it is sent at
            return true;
        });

        bool skulledFirst = false, skullMoved = false;
        await world.RunUntilAsync(30_000, () =>
        {
            ObjectGuid skull = Skull(world, raid);
            skulledFirst |= skull == unreachable;
            skullMoved |= skulledFirst && skull == next;
            return skullMoved;
        });

        Assert.True(skulledFirst, "the nearest hostile was never skulled: " + raid.LastFacts);
        Assert.True(await world.OnWorldAsync(() => raid.Group!.IsShunned(unreachable, Now(world))), "the unreachable target was not given up");
        Assert.True(skullMoved, $"the skull never moved to the next hostile: {raid.LastFacts}" + Environment.NewLine + await world.TraceAsync());
    }

    [Fact]
    public async Task TheObjectiveWithNoPathToIt_IsNeverShunned_StaysInHostiles_AndRemainsAValidPullTarget()
    {
        // The objective 30 yards ahead, walking refused: every plan to it fails.
        GroupTestWorld world = await GroupTestWorld.StartAsync([], null, extraCreatures: [Guard(Objective, 30f, 0f, health: 1_000_000)]);
        await using GroupTestWorld _ = world;
        (Guid Id, string Name)[] bots = await Enlist(world, 100f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        ObjectGuid objective = default;
        await world.OnWorldAsync(() =>
        {
            Creature sylvanas = Spawned(world, raid, Objective);
            objective = sylvanas.Guid;
            sylvanas.Map!.FindUpdater<CreatureMapSystem>()!.SetAiImmobilized(sylvanas, true, combatOnly: false);
            world.Options.AllowedMaps = [1];
            return true;
        });

        var leaderMember = raid.Group!.Members.First(m => m.BotId == raid.Group.LeaderBotId);
        bool noPath = false, shunned = false, hostileGone = false, pulledThenDropped = false, pulled = false;
        await world.RunUntilAsync(30_000, () =>
        {
            string step = leaderMember.AI?.Step ?? string.Empty;
            noPath |= step == $"fight:{Objective}:no-path";
            if (!noPath) return false;
            shunned |= raid.Group.IsShunned(objective, Now(world));
            hostileGone |= raid.LastFacts.Hostiles < 1;
            if (raid.PullTarget == objective) pulled = true;
            else if (pulled) pulledThenDropped = true;
            return false; // the run lasts its whole time
        });

        Assert.True(noPath, "the leader never failed to plan a way to the objective: " + await world.TraceAsync());
        Assert.False(shunned, "the objective was shunned");
        Assert.False(hostileGone, "the objective dropped out of the hostiles: " + raid.LastFacts);
        Assert.False(pulledThenDropped, "the pull target left the objective");
    }
}
