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
/// Lag holds and step credit: a held member, raids at different waypoints, and a further waypoint reached beyond the lag yards. Split from the former <c>PlayerbotSiegeWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotSiegeWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotSiegeLagCreditWorldTests : PlayerbotSiegeWorldTestBase
{
    [Fact]
    public async Task AMemberHeldFarAway_HoldsAPushingRaidForNinetySeconds_ThenTheStepMovesWithoutIt()
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

        // The member is held for the whole run: the step moves once the leader has waited LagHoldMs at the waypoint, not before.
        bool waived = false, noted = false;
        uint arrivedAt = 0, movedAt = 0, lagBegan = 0;
        float lagWhenMoved = -1f;
        bool moved = await world.RunUntilAsync(PlayerbotSiegeCommander.PushThroughQuietMs + PlayerbotGroupCoordinator.LagHoldMs + 60_000, () =>
        {
            KeepInCombat(world, raid);
            Hold();
            if (arrivedAt == 0 && raid.AtWaypoint && raid.PushingThrough) arrivedAt = Now(world);
            if (lagBegan == 0 && raid.LastFacts.MaxLagYards > PlayerbotSiegeCommander.LagYards) lagBegan = Now(world);
            waived |= raid.LastFacts.LagWaived;
            noted |= world.Coordinator.Events.Any(e => e.Contains("pushing on from waypoint 1"));
            if (siege.Step >= 1 && movedAt == 0)
            {
                movedAt = Now(world);
                lagWhenMoved = MaxLag(world, raid);
            }

            return siege.Step >= 1;
        });

        Assert.True(moved, $"the step never moved: {raid.LastFacts}" + Environment.NewLine + await world.TraceAsync());
        Assert.True(arrivedAt != 0, "the pushing leader never waited at the waypoint");
        Assert.True(lagWhenMoved > PlayerbotSiegeCommander.LagYards, $"the member was within reach when the step moved: {lagWhenMoved:F0} yd");
        // The lag clock runs from the first read of the straggler (the leader need not have arrived), so the hold is counted from there.
        Assert.True(lagBegan != 0 && unchecked(movedAt - lagBegan) >= PlayerbotGroupCoordinator.LagHoldMs - 1_000, $"the step moved {unchecked(movedAt - lagBegan)} ms after the lag began (the hold is {PlayerbotGroupCoordinator.LagHoldMs} ms)");
        Assert.True(waived, "the lag was never waived: " + raid.LastFacts);
        Assert.True(noted, "no event note says the raid pushed on:" + Environment.NewLine + await world.TraceAsync());
        Assert.Contains(PlayerbotGroupCoordinator.PushOnLine, raid.Announcer.Sent);
    }

    [Fact]
    public async Task TwoRaidsWhoseLeadersFoughtOnPastAWaypoint_NeverStandingAtIt_GoOnOneStepAtATime_UpToTheFurthestWaypointBothReached()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        int before = siege.Step;
        await world.OnWorldAsync(() =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, 45f); // the third waypoint, the step is at the first or second
            return true;
        });

        List<int> seen = await StepsUntil(world, siege, 3, 20_000);
        string story = await world.TraceAsync();
        AssertOneAtATime(seen, story);
        Assert.True(seen.Count >= 2 && seen[1] == before + 1, $"the step went {string.Join(" -> ", seen)}");
        Assert.Equal(3, siege.Step); // every leader stood at the third waypoint: the step after it is the fourth
        Assert.Contains(world.Coordinator.Events, e => e.Contains("every raid has reached waypoint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoRaidsReachingTheWaypointAtDifferentTimes_NeverTogether_StillMoveTheStepByOne()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        int step = siege.Step;
        float x = siege.Waypoints[step].X - Origin.X;
        bool together = false;
        await world.OnWorldAsync(() =>
        {
            Teleport(world, siege.Raids[1], -80f);
            Teleport(world, siege.Raids[0], x);
            return true;
        });

        // The first raid stands at the waypoint (once is enough), then is dragged away; the second arrives after, with the first 60 yards off.
        Assert.True(await world.RunUntilAsync(5_000, () => siege.Raids[0].FurthestWaypoint >= step), "the first raid never stood at the waypoint");
        Assert.Equal(step, siege.Step);
        await world.OnWorldAsync(() =>
        {
            Teleport(world, siege.Raids[0], -60f);
            return true;
        });
        await world.RunAsync(500);
        Assert.Equal(step, siege.Step);
        await world.OnWorldAsync(() =>
        {
            Teleport(world, siege.Raids[1], x);
            return true;
        });

        List<int> seen = [siege.Step];
        await world.RunUntilAsync(3_000, () =>
        {
            together |= siege.Raids.All(r => r.AtWaypointReal);
            if (siege.Step != seen[^1]) seen.Add(siege.Step);
            return false;
        });

        Assert.False(together, "both leaders stood at the waypoint together: the lockstep moved the step, not the credit");
        Assert.True(seen.SequenceEqual([step, step + 1]), $"the step went {string.Join(" -> ", seen)}" + Environment.NewLine + await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, e => e.Contains($"every raid has reached waypoint {step + 1}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneRaidAtAWaypointAndTheOtherBeyondIt_TheStepGoesOnOneAtATime_UpToTheLowerRaidsWaypoint()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        int before = siege.Step;
        await world.OnWorldAsync(() =>
        {
            Teleport(world, siege.Raids[0], 30f); // the second waypoint
            Teleport(world, siege.Raids[1], 45f); // the third
            return true;
        });

        List<int> seen = await StepsUntil(world, siege, 2, 20_000);
        AssertOneAtATime(seen, await world.TraceAsync());
        Assert.True(seen.Count >= 2 && seen[1] == before + 1, $"the step went {string.Join(" -> ", seen)}");
        Assert.Equal(2, siege.Step); // the credit stops there: the lower raid has not stood at the third waypoint (it walks there, and the lockstep goes on)
    }

    [Fact]
    public async Task OneRaidFarAhead_WhileTheOtherNeverReachedAnything_MovesNoStep()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        int before = siege.Step;
        await world.OnWorldAsync(() =>
        {
            Teleport(world, siege.Raids[0], 45f);
            Teleport(world, siege.Raids[1], -40f); // behind the start
            return true;
        });

        Assert.Equal(-1, await NextStep(world, siege, 2_000));
        Assert.Equal(before, siege.Step);
        Assert.Equal(2, siege.Raids[0].FurthestWaypoint);
        Assert.Equal(-1, siege.Raids[1].FurthestWaypoint);
    }

    [Fact]
    public async Task AFurtherWaypointReached_WithAMemberBeyondLagYards_GivesNoCredit_UntilTheLagClears()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        int before = siege.Step;
        PlayerbotSiegeRaid lagging = siege.Raids[0];
        var held = lagging.Group!.Members.First(m => !m.Real && m.BotId != lagging.Group.LeaderBotId);
        await world.OnWorldAsync(() =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, 45f);
            return true;
        });

        // One member of the first raid stays 130 yards behind its leader: the leaders have both been at the third waypoint, and the step must not move.
        bool moved = false;
        await world.RunUntilAsync(4_000, () =>
        {
            world.World.Host.World.FindOnlinePlayer(held.Guid)!.Relocate(Origin.X - 80f, Origin.Y, Origin.Z, 0, Now(world));
            moved |= siege.Step != before;
            return moved;
        });
        Assert.False(moved, $"the step moved to {siege.Step} with a member {lagging.LastFacts.MaxLagYards:F0} yd behind: {lagging.LastFacts}");
        Assert.True(lagging.LastFacts.MaxLagYards > PlayerbotSiegeCommander.LagYards, "the member is far: " + lagging.LastFacts);
        Assert.True(siege.Raids.All(r => r.FurthestWaypoint >= 2), "both leaders have stood at the third waypoint");

        // The member catches up: the credit applies, by one step.
        await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, lagging);
            world.World.Host.World.FindOnlinePlayer(held.Guid)!.Relocate(leader.X, leader.Y, leader.Z, 0, Now(world));
            return true;
        });
        Assert.Equal(before + 1, await NextStep(world, siege, 20_000));
    }
}
