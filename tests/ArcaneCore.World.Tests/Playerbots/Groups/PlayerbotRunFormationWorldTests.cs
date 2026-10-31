using System.Diagnostics.Metrics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Data.World.Instances;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Groups.RunFixtures;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Forming a dungeon run (band, tank and healer), the travel policy, and a stopped bot or leader. Split from the former <c>PlayerbotRunWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRunWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRunFormationWorldTests : PlayerbotRunWorldTestBase
{
    [Fact]
    public async Task FiveBotsInTheBand_WithATankAndAHealer_FormADungeonRun_TheOneOutOfTheBandIsLeftOut()
    {
        await using GroupTestWorld world = await StartAsync();
        var tank = await world.AddBotAsync("Dgtank", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Dgheal", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mages = new List<(Guid Id, string Name)>();
        foreach (string name in new[] { "Dgmagea", "Dgmageb", "Dgmagec" }) mages.Add(await world.AddBotAsync(name, Human, Mage, 12, [Fireball], EntranceArea));
        var strong = await world.AddBotAsync("Dgstrong", Human, Mage, 40, [Fireball], EntranceArea); // the wing's band is 10..15

        // The start roll decides: with a roll above the chance no run starts, however well the bots fit.
        world.Coordinator.RunRoll = () => 1d;
        await world.RunAsync(20_000);
        Assert.Empty(world.Coordinator.Groups);

        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());

        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(PlayerbotGroupGoalKind.Dungeon, group.Goal.Kind);
        Assert.Equal(DungeonEntry.Deadmines, group.Goal.MapId);
        Assert.Equal(DungeonEntry.EntranceTrigger, group.Goal.EntranceTrigger);
        Assert.Equal(5, group.Goal.Size);
        Assert.Equal(PlayerbotRunStep.Travel, group.Run!.Step);
        Assert.Equal(BossEntry, group.Run.CurrentBoss!.Entry);
        Assert.Equal(5, group.Members.Count);
        Assert.Equal(1, group.Members.Count(m => m.Role == PlayerbotGroupRole.Tank));
        Assert.Equal(1, group.Members.Count(m => m.Role == PlayerbotGroupRole.Healer));
        Assert.Equal(3, group.Members.Count(m => m.Role == PlayerbotGroupRole.Damage));
        Assert.Equal(tank.Id, group.LeaderBotId);
        Assert.Equal(PlayerbotGroupRole.Healer, group.Find(healer.Id)!.Role);
        Assert.All(mages, m => Assert.NotNull(group.Find(m.Id)));
        Assert.Null(group.Find(strong.Id));
        Assert.False(world.Coordinator.Drives(strong.Id));
        Assert.Equal((0, 0), world.Coordinator.RunTotals);
        Assert.Equal(1, world.Coordinator.Totals.Formed);
        Assert.Contains(world.Coordinator.Report(), line => line.Contains("run=The Deadmines(36):0 boss=1/1(Deadmines Boss)", StringComparison.Ordinal));
        Assert.Contains(world.Coordinator.Report(), line => line.StartsWith("runs enabled: dungeon 1/2 raid 0/1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAHealer_NoRunStarts_UntilOneIsThere()
    {
        await using GroupTestWorld world = await StartAsync();
        await world.AddBotAsync("Nhtank", Human, Warrior, 12, [Taunt], EntranceArea);
        foreach (string name in new[] { "Nhmagea", "Nhmageb", "Nhmagec", "Nhmaged" }) await world.AddBotAsync(name, Human, Mage, 12, [Fireball], EntranceArea);
        world.Coordinator.RunRoll = () => 0d;

        await world.RunAsync(30_000);
        Assert.Empty(world.Coordinator.Groups);
        Assert.Equal(0, world.Coordinator.Totals.Formed);

        await world.AddBotAsync("Nhheal", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());
        Assert.Equal(PlayerbotGroupRole.Healer, world.Coordinator.Groups.Single().Members.Single(m => m.Name == "Nhheal").Role);
    }

    [Fact]
    public async Task TheTravelPolicy_IsFightThrough_UnlessTheRunIsToldToTeleport_AndNullOutsideARun()
    {
        await using GroupTestWorld world = await StartAsync();
        var members = await AddPartyAsync(world, "Tp");
        var outsider = await world.AddBotAsync("Tpout", Human, Mage, 40, [Fireball], EntranceArea);
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());

        Assert.All(members, m => Assert.Equal(PlayerbotGroupTravelPolicy.FightThrough, world.Coordinator.TravelPolicyFor(m.Id)));
        Assert.Null(world.Coordinator.TravelPolicyFor(outsider.Id));
        Assert.Null(world.Coordinator.TravelPolicyFor(Guid.NewGuid()));

        world.Options.Groups.DungeonRuns.TeleportToEntrance = true;
        Assert.All(members, m => Assert.Equal(PlayerbotGroupTravelPolicy.Teleport, world.Coordinator.TravelPolicyFor(m.Id)));
        Assert.Null(world.Coordinator.TravelPolicyFor(outsider.Id));
    }

    [Fact]
    public async Task AStoppedBot_LeavesItsRun_AStoppedLeaderGivesTheRunUp_AndTheOthersSetTheWingAside()
    {
        await using GroupTestWorld world = await StartAsync();
        var members = await AddPartyAsync(world, "Fg");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        PlayerbotRunMachine run = group.Run!;
        (Guid Id, string Name) follower = members.First(m => m.Id != group.LeaderBotId);
        (Guid Id, string Name) leftBehind = members.First(m => m.Id != group.LeaderBotId && m.Id != follower.Id);

        await world.OnWorldAsync(() =>
        {
            // A follower that stops (ManagedPlayerbotFeature.StopAsync calls Forget) is out of the group and the group goes on.
            world.Coordinator.Forget(follower.Id);
            Assert.Equal(4, group.Members.Count);
            Assert.Null(group.Find(follower.Id));
            Assert.False(world.Coordinator.Drives(follower.Id));
            Assert.Single(world.Coordinator.Groups);
            Assert.False(run.Finished);
            Assert.Equal((0, 0), world.Coordinator.RunTotals);

            // The leader stops: the run is given up, the group ends, the members still in it are released and set the wing aside.
            world.Coordinator.Forget(group.LeaderBotId);
            Assert.Equal(PlayerbotRunStep.GiveUp, run.Step);
            Assert.Equal("leader-stopped", run.Reason);
            Assert.Empty(world.Coordinator.Groups);
            Assert.Equal((0, 1), world.Coordinator.RunTotals);
            Assert.Equal(1, world.Coordinator.Totals.Failed);
            Assert.All(members, m => Assert.False(world.Coordinator.Drives(m.Id)));
            Assert.True(world.Coordinator.IsRunSetAside(leftBehind.Id, DungeonEntry.Deadmines, 0));
            Assert.False(world.Coordinator.IsRunSetAside(follower.Id, DungeonEntry.Deadmines, 0)); // it left before the run ended
            return true;
        });

        // Nobody is matched again: three bots are set aside, two are free and a party takes five.
        await world.RunAsync(30_000);
        Assert.Empty(world.Coordinator.Groups);
    }
}
