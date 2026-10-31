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
/// Permanent raid binds, and SkipPrerequisites matching a raid without the requirements. Split from the former <c>PlayerbotRunWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRunWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRunRaidBindsAndWaiverWorldTests : PlayerbotRunWorldTestBase
{
    [Fact]
    public async Task ABotWithAPermanentBindToTheRaid_IsLeftOut_ATemporaryBindIsNot()
    {
        await using GroupTestWorld world = await StartAsync();
        var raid = await AddRaidAsync(world, "Pb", warriors: 1, priests: 3, mages: 6);
        await GiveAmuletAsync(world, raid);
        world.Coordinator.RunRoll = () => 0d;
        (Guid Id, string Name) saved = raid[^1];

        await Bind(world, saved.Id, instance: 5000, permanent: true);
        await world.RunAsync(20_000);
        Assert.Empty(world.Coordinator.Groups);
        await AssertNoted(world, "run Onyxia's Lair: 1 bots are locked to the raid");

        // Unbound and bound temporarily (a raid never saves a temporary bind that way; it is the case of a group bind): not locked.
        Assert.True(await world.OnWorldAsync(() => world.Instances().UnbindPlayer(world.Player(saved.Id).Guid, OnyxiasLair)));
        await Bind(world, saved.Id, instance: 5001, permanent: false);
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());
        Assert.NotNull(world.Coordinator.Groups.Single().Find(saved.Id));
    }

    [Fact]
    public async Task WithSkipPrerequisites_TheRaidIsMatchedWithoutThem_AndWaivesOnlyForItsOwnBotsAtItsOwnEntrance()
    {
        await using GroupTestWorld world = await StartAsync(options =>
        {
            options.MaxBots = 12;
            options.Groups.DungeonRuns.Raids.SkipPrerequisites = true;
        });
        var raid = await AddRaidAsync(world, "Sk", warriors: 1, priests: 3, mages: 7);
        world.Coordinator.RunRoll = () => 0d;

        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(OnyxiasLair, group.Run!.Plan.MapId); // the lowest map of the three raids all ten bots fit
        Assert.Equal(10, group.Members.Count);
        (Guid Id, string Name) member = raid.First(r => group.Find(r.Id) is not null);
        (Guid Id, string Name) free = raid.First(r => group.Find(r.Id) is null);
        await using WorldTestClient real = await world.World.EnterWorldAsync("RUNREAL", "Runreal", AccountSecurity.Player);

        const AreaTriggerWaivers All = AreaTriggerWaivers.Item | AreaTriggerWaivers.Quest | AreaTriggerWaivers.Condition;
        await world.OnWorldAsync(() =>
        {
            Player bot = world.Player(member.Id);
            PlayerbotGroupCoordinator coordinator = world.Coordinator;

            // A member of the raid at the raid's entrance: the key item, the quest and the condition, never the level.
            Assert.Equal(All, coordinator.Waives(bot, OnyxiaTeleport));
            Assert.True(AreaTriggerRequirements.Check(bot, OnyxiaTeleport with { RequiredLevel = 61 }, null, [], coordinator.Waives(bot, OnyxiaTeleport)).Failed == AreaTriggerRequirement.Level);

            // Another teleport (another raid, a dungeon): checked as always.
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(bot, MoltenCoreTeleport));
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(bot, NaxxramasTeleport));
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(bot, DeadminesTeleport));

            // A bot that is not in the run, and a real player (a session that is not a managed bot's).
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(world.Player(free.Id), OnyxiaTeleport));
            Player human = world.World.Host.World.FindOnlinePlayer("Runreal")!;
            Assert.False(human.Session is WorldSession { IsManaged: true });
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(human, OnyxiaTeleport));

            // The options are live: each switch that turns the feature off ends the waiver at once.
            world.Options.Groups.DungeonRuns.Raids.SkipPrerequisites = false;
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(bot, OnyxiaTeleport));
            world.Options.Groups.DungeonRuns.Raids.SkipPrerequisites = true;
            Assert.Equal(All, coordinator.Waives(bot, OnyxiaTeleport));
            world.Options.Groups.DungeonRuns.Raids.Enabled = false;
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(bot, OnyxiaTeleport));
            world.Options.Groups.DungeonRuns.Raids.Enabled = true;
            world.Options.Groups.DungeonRuns.Enabled = false;
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(bot, OnyxiaTeleport));
            world.Options.Groups.DungeonRuns.Enabled = true;
            Assert.Equal(All, coordinator.Waives(bot, OnyxiaTeleport));
            return true;
        });
    }
}
