using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Gear-upgrade reasons anchoring their own group, and a removed seed leaving derived-only waiters unheld. Split from the former <c>PlayerbotDungeonAnchorWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotDungeonAnchorWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotDungeonAnchorGearUpgradeWorldTests : PlayerbotDungeonAnchorWorldTestBase
{
    [Theory]
    [InlineData(58u)]
    [InlineData(55u)] // the level floor (60 - GearLevelFloor) is inclusive
    public async Task Level60Bots_WearingAWeakerNeck_WaitOnAGearUpgradeReason_AnchorTheirOwnGroup_AndForm(uint requiredLevel)
    {
        await using GroupTestWorld world = await GearWorldAsync(requiredLevel);
        (Guid Id, string Name)[] bots = await AddLevel60TrioAsync(world, "Gearu");
        await EquipWornNeckAsync(world, bots);

        Assert.True(await world.RunUntilAsync(60_000, () => bots.All(b => world.HasQuest(b.Id, DungeonQuest))), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count > 0), await world.TraceAsync());

        string[] events = await EventsAsync(world);
        Assert.Contains(events, e => e.Contains("needs a group", StringComparison.Ordinal) && e.Contains("fit=out-of-band 60", StringComparison.Ordinal)
            && e.Contains("gearupgrade", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Contains("no reason", StringComparison.Ordinal));
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.All(bots, b => Assert.NotNull(group.Find(b.Id)));
    }

    [Fact]
    public async Task Level60Bots_WearingAWeakerNeck_AreRefused_WhenTheBetterNeckNeedsAtLevelBelowTheFloor()
    {
        await using GroupTestWorld world = await GearWorldAsync(54);
        (Guid Id, string Name)[] bots = await AddLevel60TrioAsync(world, "Gearl");
        await EquipWornNeckAsync(world, bots);

        Assert.True(await world.RunUntilAsync(60_000, () => bots.All(b => world.HasQuest(b.Id, DungeonQuest))), await world.TraceAsync());
        bool waited = false;
        Assert.False(await world.RunUntilAsync(120_000, () =>
        {
            waited |= world.Coordinator.WaitingBots.Any() || world.Coordinator.Groups.Any();
            return waited;
        }), await world.TraceAsync());

        string[] events = await EventsAsync(world);
        Assert.Contains(events, e => e.Contains("no reason", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Contains("needs a group", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheSeedIsRemoved_Level30Waiters_OnDerivedReasonsOnly_AreUnheld_AndNoGroupOfLevel30sForms()
    {
        // Band 15-28: level 30 is out of band; the quest (level 10) is grey for a 30 (gray level 22), so the seed is their only reason.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], o => { NoOwnGuilds(o); o.Social.Lists.Enabled = false; o.Groups.LevelRange = 50; }, dungeon: true); // no guild or friend list: the line must end in exactly ": forminggroup"
        var seed = await AddSeedAsync(world, "Seede");
        await world.OnWorldAsync(() =>
        {
            world.Player(seed.Id).Relocate(StartX, StartY, StartZ, 0, world.World.Host.World.NowMs); // out of the match (beyond MaxMeetingYards), still waiting
            return true;
        });
        (Guid Id, string Name)[] thirties = await AddTrioAsync(world, "Thirty", 30);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Count(w => w.Name.StartsWith("Thirty", StringComparison.Ordinal)) == 3), await world.TraceAsync());
        Assert.Contains(await EventsAsync(world), e => e.Contains("Thirty", StringComparison.Ordinal) && e.Contains("fit=out-of-band 30 vs ", StringComparison.Ordinal)
            && e.EndsWith(": forminggroup", StringComparison.Ordinal));
        Assert.Empty(world.Coordinator.Groups); // while the seed waits, the level 30s alone are no group

        var stopped = await world.Bots.StopAsync(seed.Name);
        Assert.True(stopped.Success, stopped.Code);
        Assert.True(await world.RunUntilAsync(PlayerbotGroupCoordinator.SeedRecheckMs * 6, () => !world.Coordinator.WaitingBots.Any(w => w.Name.StartsWith("Thirty", StringComparison.Ordinal))),
            await world.TraceAsync());
        Assert.Contains(await EventsAsync(world), e => e.Contains("has no companion or forming group left", StringComparison.Ordinal));

        Assert.False(await world.RunUntilAsync(60_000, () => world.Coordinator.Groups.Count > 0 || world.Coordinator.WaitingBots.Any()), await world.TraceAsync());
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        Assert.DoesNotContain(thirties, t => world.Coordinator.Groups.Any(g => g.Find(t.Id) is not null));
    }
}
