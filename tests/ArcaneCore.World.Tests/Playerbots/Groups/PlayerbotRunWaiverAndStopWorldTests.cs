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
/// The waiver without SkipPrerequisites, null arguments, the entrance level, a stopped leader and an operator stop. Split from the former <c>PlayerbotRunWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRunWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRunWaiverAndStopWorldTests : PlayerbotRunWorldTestBase
{
    [Fact]
    public async Task WithoutSkipPrerequisites_ABotOfARaidRunIsWaivedNothing()
    {
        await using GroupTestWorld world = await StartAsync();
        var raid = await AddRaidAsync(world, "Ns", warriors: 1, priests: 3, mages: 6);
        await GiveAmuletAsync(world, raid);
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());

        await world.OnWorldAsync(() =>
        {
            Assert.All(raid, r => Assert.Equal(AreaTriggerWaivers.None, world.Coordinator.Waives(world.Player(r.Id), OnyxiaTeleport)));
            return true;
        });
    }

    [Fact]
    public async Task TheWaiver_RefusesNullArguments()
    {
        await using GroupTestWorld world = await StartAsync();
        var bot = await world.AddBotAsync("Nullbot", Human, Mage, 12, [Fireball], EntranceArea);

        await world.OnWorldAsync(() =>
        {
            Assert.Throws<ArgumentNullException>(() => world.Coordinator.Waives(null!, OnyxiaTeleport));
            Assert.Throws<ArgumentNullException>(() => world.Coordinator.Waives(world.Player(bot.Id), null!));
            return true;
        });
    }

    [Fact]
    public async Task TheEntranceLevel_IsNeverSkipped_ABotBelowItInABandOverrideStaysUnmatched()
    {
        // The band is widened under the entrance's level 50 on purpose: only the entrance check can refuse the level-45 bots now.
        await using GroupTestWorld world = await StartAsync(options =>
        {
            options.MaxBots = 12;
            options.Groups.DungeonRuns.Raids.SkipPrerequisites = true;
            options.Groups.DungeonRuns.LevelBandOverrides[OnyxiasLair.ToString()] = "40-63";
        });
        var bots = new List<(Guid Id, string Name)>
        {
            await world.AddBotAsync("Lvwara", Human, Warrior, 45, [Taunt], EntranceArea),
        };
        foreach (char letter in "abc") bots.Add(await world.AddBotAsync("Lvpri" + letter, Human, Priest, 45, [LesserHeal, Smite], EntranceArea));
        foreach (char letter in "abcdef") bots.Add(await world.AddBotAsync("Lvmag" + letter, Human, Mage, 45, [Fireball], EntranceArea));
        world.Coordinator.RunRoll = () => 0d;

        await world.RunAsync(30_000);

        Assert.Empty(world.Coordinator.Groups);
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        await AssertNoted(world, "run Onyxia's Lair: 10 bots in the level band lack the level requirement");
    }

    [Fact]
    public async Task WhenTheRunsLeaderStops_EveryMemberLosesTheRaidWaiver_AndTheRunIsCountedOnce()
    {
        await using GroupTestWorld world = await StartAsync(options =>
        {
            options.MaxBots = 12;
            options.Groups.DungeonRuns.Raids.SkipPrerequisites = true;
        });
        var raid = await AddRaidAsync(world, "Sd", warriors: 1, priests: 3, mages: 7);
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.True(await world.RunUntilAsync(30_000, () => group.Members.All(m => m.Joined)), await world.TraceAsync()); // the waiver is for joined members
        (Guid Id, string Name)[] members = [.. raid.Where(r => group.Find(r.Id) is not null)];
        Assert.Equal(10, members.Length);
        PlayerbotRunMachine run = group.Run!;

        await world.OnWorldAsync(() =>
        {
            Assert.All(members, m => Assert.NotEqual(AreaTriggerWaivers.None, world.Coordinator.Waives(world.Player(m.Id), OnyxiaTeleport)));

            // The feature shuts down: every bot is forgotten, the leader first (ManagedPlayerbotFeature.StopAsync calls Forget per bot).
            world.Coordinator.Forget(group.LeaderBotId);
            foreach ((Guid id, string _) in members) world.Coordinator.Forget(id);
            world.Coordinator.Forget(Guid.NewGuid()); // a bot the coordinator never knew: nothing happens

            Assert.Equal(PlayerbotRunStep.GiveUp, run.Step);
            Assert.Equal("leader-stopped", run.Reason);
            Assert.Empty(world.Coordinator.Groups);
            Assert.All(members, m =>
            {
                Assert.False(world.Coordinator.Drives(m.Id));
                Assert.Null(world.Coordinator.GroupOf(m.Id));
                Assert.Equal(AreaTriggerWaivers.None, world.Coordinator.Waives(world.Player(m.Id), OnyxiaTeleport));
            });
            Assert.Equal((0, 1), world.Coordinator.RunTotals);
            Assert.Equal(1, world.Coordinator.Totals.Failed);
            return true;
        });
    }

    [Fact]
    public async Task AnOperatorStop_CountsAsAGivenUpRun_ButReportsNothingToBotQa()
    {
        var bus = new PlayerbotQaSignalBus(64);
        await using GroupTestWorld world = await StartWithQaAsync(bus);
        await AddPartyAsync(world, "Qaz");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;
        Assert.Equal(0, world.Coordinator.RunOutcomeCount(PlayerbotRunMeters.GivenUp)); // negative control: nothing counted before the stop

        // A GM stop, a scale-down, a shutdown or a controller taking over ends a run through Forget: not a finding about content or code.
        await world.OnWorldAsync(() =>
        {
            world.Coordinator.Forget(group.LeaderBotId);
            return true;
        });

        Assert.Equal("leader-stopped", group.Run!.Reason);
        var signals = new List<QaSignal>();
        bus.Drain(signals, 16);
        Assert.Empty(signals);
        Assert.Equal(1, world.Coordinator.RunTotals.GivenUp);
        Assert.Equal(1, world.Coordinator.RunOutcomeCount(PlayerbotRunMeters.GivenUp));
    }
}
