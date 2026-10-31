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
/// Quest groups and dungeon groups that can plan no way, and a group that cannot get out of an instance. Split from the former <c>PlayerbotGroupBoundedWaitTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotGroupBoundedWaitTestBase"/>.
/// </summary>
public sealed class PlayerbotGroupBoundedWaitNoPathTests : PlayerbotGroupBoundedWaitTestBase
{
    [Fact]
    public async Task AQuestGroupThatCanPlanNoWayToItsObjective_GivesUpAfterThirtySeconds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
        });
        await world.AddBotAsync("Noobwar", Human, Warrior, 1, []);
        await world.AddBotAsync("Noobmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Engaging)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        await world.OnWorldAsync(() =>
        {
            Block(world);
            return true;
        });

        Assert.True(await world.RunUntilAsync(120_000, () => group.Reason is not null), await world.TraceAsync());
        Assert.Equal("no-path-to-objective", group.Reason);
        Assert.False(group.Succeeded);
    }

    // --- B5: the way out of an instance --------------------------------------------------------------------------------

    [Fact]
    public async Task AGroupThatCannotGetEveryoneOutOfTheInstance_IsLetGoAfterTwoBringOuts()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options => options.StallSeconds = 600, dungeon: true);
        var tank = await world.AddBotAsync("Capwar", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Capprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Capmage", Human, Mage, 12, [Fireball], EntranceArea);
        (Guid Id, string Name)[] bots = [tank, healer, mage];
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == 36)), await world.TraceAsync());

        // The run is over and one member lies dead inside: the others are brought out, the body is not, and the group waits for it.
        await world.OnWorldAsync(() =>
        {
            Block(world);
            Player dead = world.Player(mage.Id);
            dead.Health = 0;
            dead.Combat.DeathState = ArcaneCore.Game.Combat.DeathState.Corpse;
            group.Find(mage.Id)!.AI = null;
            group.Succeeded = true;
            group.Reason = null;
            group.State = PlayerbotGroupState.Leaving;
            group.StateSinceMs = NowMs(world);
            return true;
        });

        // Two bring-outs (a step limit apart) and then the group is let go, not brought out again for good.
        Assert.True(await world.RunUntilAsync(4 * PlayerbotGroupCoordinator.StepTimeoutMs, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Equal(2, world.Coordinator.Events.Count(line => line.Contains("could not walk out: brought to the entrance", StringComparison.Ordinal)));
        Assert.True(group.Succeeded);
        Assert.Equal("completed", group.Reason);
        Assert.Equal(0, world.Coordinator.Totals.Failed);
    }

    [Fact]
    public async Task ADungeonLeaderThatCanPlanNoWayToTheApproachPoint_ButStandsNearIt_StartsEntering()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options => options.StallSeconds = 600, dungeon: true);
        var tank = await world.AddBotAsync("Nearwar", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Nearprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Nearmage", Human, Mage, 12, [Fireball], EntranceArea);
        PlayerbotGroupCoordinator.BotGroup group = await TravellingNearAnUnreachableApproachAsync(world, [tank, healer, mage], 10f, 0f);
        await world.OnWorldAsync(() =>
        {
            Player leader = world.Player(group.LeaderBotId);
            float ring = PlayerbotGroupCoordinator.ApproachYards + PlayerbotGroupCoordinator.ApproachSlackYards;
            Assert.True(MathF.Sqrt(((leader.X - group.Goal.Meeting.X) * (leader.X - group.Goal.Meeting.X)) + ((leader.Y - group.Goal.Meeting.Y) * (leader.Y - group.Goal.Meeting.Y))) > ring, "outside the ring");
            Assert.True(Vector3.Distance(new Vector3(leader.X, leader.Y, leader.Z), group.Approach) > 5f, "outside the arrival radius");
            return true;
        });

        // Well inside the 30 s no-path bound: the group goes on as soon as the refusal is seen, it does not wait for the bound or the travel timeout.
        Assert.True(await world.RunUntilAsync(10_000, () => group.State is not (PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling)), await world.TraceAsync());
        Assert.Equal(PlayerbotGroupState.Entering, group.State);
        Assert.Null(group.Reason);
    }

    [Fact]
    public async Task ADungeonGroupLetOnFromNearAnUnreachableApproachPoint_ThatCanPlanNoWayToTheEntrance_EndsAtTheNoPathBound()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options => options.StallSeconds = 600, dungeon: true);
        var tank = await world.AddBotAsync("Gatewar", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Gateprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Gatemage", Human, Mage, 12, [Fireball], EntranceArea);
        PlayerbotGroupCoordinator.BotGroup group = await TravellingNearAnUnreachableApproachAsync(world, [tank, healer, mage], 10f, 0f);
        Assert.True(await world.RunUntilAsync(10_000, () => group.State == PlayerbotGroupState.Entering), await world.TraceAsync());

        // The way to the entrance stays unplanned: the group ends at the 30 s no-path bound, not at the 300 s enter step timeout.
        Assert.True(await world.RunUntilAsync((uint)PlayerbotGroupAI.RunStuckMs + 15_000, () => group.Reason is not null), await world.TraceAsync());
        Assert.Equal("no-path-to-entrance", group.Reason);
        Assert.False(group.Succeeded);
    }

    [Fact]
    public async Task ADungeonLeaderThatCanPlanNoWayToTheApproachPoint_AndStandsOnAnotherFloorOfIt_DoesNotArrive()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options => options.StallSeconds = 600, dungeon: true);
        var tank = await world.AddBotAsync("Floorwar", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Floorprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Floormage", Human, Mage, 12, [Fireball], EntranceArea);
        PlayerbotGroupCoordinator.BotGroup group = await TravellingNearAnUnreachableApproachAsync(world, [tank, healer, mage], 10f, PlayerbotGroupCoordinator.ApproachHeightYards + 5f);

        await world.RunAsync(10_000);
        Assert.Equal(PlayerbotGroupState.Travelling, group.State);
    }

    [Fact]
    public async Task ADungeonLeaderThatCanPlanNoWayToTheApproachPoint_AndStandsFarFromIt_DoesNotArrive()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options => options.StallSeconds = 600, dungeon: true);
        var tank = await world.AddBotAsync("Distwar", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Distprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Distmage", Human, Mage, 12, [Fireball], EntranceArea);
        PlayerbotGroupCoordinator.BotGroup group = await TravellingNearAnUnreachableApproachAsync(world, [tank, healer, mage], PlayerbotGroupCoordinator.ApproachYards + 15f, 0f);

        await world.RunAsync(10_000);
        Assert.Equal(PlayerbotGroupState.Travelling, group.State);
    }
}
