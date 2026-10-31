using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// A lagging member caught up or waived after 30 s, and a gathering ending after 60 s. Split from the former <c>PlayerbotGroupBoundedWaitTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotGroupBoundedWaitTestBase"/>.
/// </summary>
public sealed class PlayerbotGroupBoundedWaitLaggardTests : PlayerbotGroupBoundedWaitTestBase
{
    // --- B1: the lag bound --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALeaderWaitingOnALaggard_NamesTheWait_AndTheLaggardIsCaughtUpAfterThirtySeconds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
        });
        (PlayerbotGroupCoordinator.BotGroup group, Guid leaderId, Guid laggardId) = await TravellingWithALaggardAsync(world, 60f);
        PlayerbotGroupAI leaderAI = world.Coordinator.FindAI(leaderId)!;

        // The first twenty seconds: the leader holds, and says which wait it is.
        var steps = new HashSet<string>();
        await world.RunUntilAsync(20_000, () =>
        {
            steps.Add(leaderAI.Step);
            return false;
        });
        Assert.Contains("lead:hold-members", steps);
        Assert.DoesNotContain(steps, step => step.StartsWith("lead:walk", StringComparison.Ordinal));
        Assert.True(await world.OnWorldAsync(() => Flat(world.Player(leaderId), world.Player(laggardId)) > PlayerbotGroupAI.WaitForMembersYards), "the laggard was caught up before the bound");

        // Past the thirty seconds the laggard is brought to the leader (the follow catch-up teleport) and the wait ends.
        Assert.True(await world.RunUntilAsync(20_000, () => Flat(world.Player(leaderId), world.Player(laggardId)) <= PlayerbotGroupAI.WaitForMembersYards), await world.TraceAsync());
        Assert.Equal(PlayerbotGroupState.Travelling, group.State);
    }

    [Fact]
    public async Task ALeaderWaitingOnALaggardThatCannotBeTeleported_WaivesTheWaitAfterThirtySeconds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
            options.Party.TeleportToLeader = false;
        });
        (_, Guid leaderId, Guid laggardId) = await TravellingWithALaggardAsync(world, 60f);
        PlayerbotGroupAI leaderAI = world.Coordinator.FindAI(leaderId)!;

        await world.RunAsync(20_000);
        Assert.Equal("lead:hold-members", leaderAI.Step);

        // The laggard stays where it is; the leader goes on without waiting for it.
        Assert.True(await world.RunUntilAsync(20_000, () => leaderAI.Step != "lead:hold-members"), await world.TraceAsync());
        Assert.True(await world.OnWorldAsync(() => Flat(world.Player(leaderId), world.Player(laggardId)) > PlayerbotGroupAI.WaitForMembersYards));
    }

    [Fact]
    public async Task AGatheringThatWaitsOnAMissingMember_CatchesItUpAfterSixtySeconds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
        });
        (PlayerbotGroupCoordinator.BotGroup group, Guid leaderId, Guid farId, uint start) = await GatheringWithAMissingMemberAsync(world);

        await world.RunAsync(50_000);
        Assert.Equal(PlayerbotGroupState.Gathering, group.State);

        Assert.True(await world.RunUntilAsync(25_000, () => group.State != PlayerbotGroupState.Gathering), await world.TraceAsync());
        Assert.InRange(unchecked(await world.OnWorldAsync(() => NowMs(world)) - start), 55_000u, 75_000u);
        Assert.Equal(PlayerbotGroupState.Travelling, group.State);
        await world.OnWorldAsync(() =>
        {
            Assert.True(Flat(world.Player(leaderId), world.Player(farId)) <= PlayerbotGroupCoordinator.GatherYards, "the missing member was brought to the leader");
            return true;
        });
    }

    [Fact]
    public async Task AGatheringThatWaitsOnAMemberItCannotCatchUp_DropsItAfterSixtySeconds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
            options.Party.TeleportToLeader = false;
        });
        (PlayerbotGroupCoordinator.BotGroup group, Guid leaderId, Guid farId, uint start) = await GatheringWithAMissingMemberAsync(world);

        await world.RunAsync(50_000);
        Assert.Equal(PlayerbotGroupState.Gathering, group.State);

        Assert.True(await world.RunUntilAsync(25_000, () => group.State != PlayerbotGroupState.Gathering), await world.TraceAsync());
        Assert.InRange(unchecked(await world.OnWorldAsync(() => NowMs(world)) - start), 55_000u, 75_000u);
        Assert.True(await world.OnWorldAsync(() => Flat(world.Player(leaderId), world.Player(farId)) > PlayerbotGroupCoordinator.GatherYards), "the member is still where it was: the gathering went on without it");
    }
}
