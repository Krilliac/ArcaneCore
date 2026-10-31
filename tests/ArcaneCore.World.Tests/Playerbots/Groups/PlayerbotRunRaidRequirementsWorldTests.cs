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
/// Dungeon bots never waived anything, and a raid needing every bot to meet the entrance requirements. Split from the former <c>PlayerbotRunWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRunWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRunRaidRequirementsWorldTests : PlayerbotRunWorldTestBase
{
    [Fact]
    public async Task ADungeonRunsBots_AreNeverWaivedAnything_WhateverSkipPrerequisitesSays()
    {
        await using GroupTestWorld world = await StartAsync(options => options.Groups.DungeonRuns.Raids.SkipPrerequisites = true);
        var members = await AddPartyAsync(world, "Dw");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());

        await world.OnWorldAsync(() =>
        {
            Player bot = world.Player(members[0].Id);
            Assert.Equal(AreaTriggerWaivers.None, world.Coordinator.Waives(bot, DeadminesTeleport));
            Assert.Equal(AreaTriggerWaivers.None, world.Coordinator.Waives(bot, OnyxiaTeleport)); // not a raid run
            return true;
        });
    }

    // --- the raids ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARaid_NeedsEveryBotToMeetTheEntrancesRequirements_PerKind_AndFormsOnceTheyDo()
    {
        await using GroupTestWorld world = await StartAsync();
        var raid = await AddRaidAsync(world, "Pr", warriors: 1, priests: 3, mages: 6);
        world.Coordinator.RunRoll = () => 0d;

        // No bot has the amulet, the attunement quest or the condition: each raid is left out for the first requirement that refuses.
        await world.RunAsync(20_000);
        Assert.Empty(world.Coordinator.Groups);
        await AssertNoted(world, "run Onyxia's Lair: 10 bots in the level band lack the item requirement");
        await AssertNoted(world, "run Molten Core: 10 bots in the level band lack the quest requirement");
        await AssertNoted(world, "run Naxxramas: 10 bots in the level band lack the condition requirement");

        // Nine of ten with the amulet is not enough: the whole raid must meet the requirement.
        await GiveAmuletAsync(world, raid.Take(9));
        await world.RunAsync(20_000);
        Assert.Empty(world.Coordinator.Groups);
        await AssertNoted(world, "run Onyxia's Lair: 1 bots in the level band lack the item requirement");

        await GiveAmuletAsync(world, raid.Skip(9));
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());

        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.True(group.Run!.Plan.Raid);
        Assert.Equal(OnyxiasLair, group.Run.Plan.MapId);
        Assert.Equal(PlayerbotGroupGoalKind.Raid, group.Goal.Kind);
        Assert.Equal(10, group.Goal.Size);
        Assert.Equal(10, group.Members.Count);
        Assert.Equal(raid.Select(r => r.Id).Order(), group.Members.Select(m => m.BotId).Order());
    }
}
