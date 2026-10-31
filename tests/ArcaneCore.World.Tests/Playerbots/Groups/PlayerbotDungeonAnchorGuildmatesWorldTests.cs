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
/// Guildmates of an in-band waiter and a seed that leaves: derived reasons never anchor a group. Split from the former <c>PlayerbotDungeonAnchorWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotDungeonAnchorWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotDungeonAnchorGuildmatesWorldTests : PlayerbotDungeonAnchorWorldTestBase
{
    // --- negative companion chain ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GuildmatesOfAnInBandWaiter_AtLevel60WithAGreyQuest_GetNoCompanionReason_AndNeverFormAGroup_AtTheDefaultLevelRange()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], NoOwnGuilds, dungeon: true);
        Assert.Equal(5, world.Options.Groups.LevelRange);
        var seed = await AddSeedAsync(world, "Seeda");
        (Guid Id, string Name)[] sixties = await AddLevel60TrioAsync(world, "Chain");
        await MakeGuildAsync(world, "Chainguild", seed, sixties);

        Assert.True(await world.RunUntilAsync(60_000, () => sixties.All(b => world.HasQuest(b.Id, DungeonQuest))), await world.TraceAsync());
        bool sixtyWaitedOrGrouped = false;
        Assert.False(await world.RunUntilAsync(120_000, () =>
        {
            sixtyWaitedOrGrouped |= world.Coordinator.WaitingBots.Any(w => w.Name.StartsWith("Chain", StringComparison.Ordinal))
                || world.Coordinator.Groups.Any(g => sixties.Any(s => g.Find(s.Id) is not null));
            return sixtyWaitedOrGrouped;
        }), await world.TraceAsync());

        string[] events = await EventsAsync(world);
        Assert.DoesNotContain(events, e => e.Contains("Chain", StringComparison.Ordinal) && e.Contains("needs a group", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Contains("companion", StringComparison.Ordinal) || e.Contains("forminggroup", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Contains("passes over", StringComparison.Ordinal) && e.Contains("no reason", StringComparison.Ordinal));
        Assert.Empty(world.Coordinator.Groups);
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        Assert.Single(world.Coordinator.WaitingBots, w => w.BotId == seed.Id); // the check is not vacuous: the seed still waits
    }

    [Fact]
    public async Task GuildmatesOfAnInBandWaiter_AtLevel60_WaitOnDerivedReasonsOnly_WhenTheLevelRangeReachesThem_AndNeverFormAGroupOfTheirOwn()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], o => { NoOwnGuilds(o); o.Groups.LevelRange = 50; }, dungeon: true);
        var seed = await AddSeedAsync(world, "Seedb");
        // The seed leaves the meeting area (more than MaxMeetingYards away): it still waits, but it is no candidate of the match, so only
        // the level-60 guildmates could be matched. Before the anchor rule they formed a group of three level-60 bots.
        await world.OnWorldAsync(() =>
        {
            world.Player(seed.Id).Relocate(StartX, StartY, StartZ, 0, world.World.Host.World.NowMs);
            return true;
        });
        (Guid Id, string Name)[] sixties = await AddLevel60TrioAsync(world, "Range");
        await MakeGuildAsync(world, "Rangeguild", seed, sixties);

        Assert.True(await world.RunUntilAsync(60_000, () => sixties.All(b => world.HasQuest(b.Id, DungeonQuest))), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Count(w => w.Name.StartsWith("Range", StringComparison.Ordinal)) == 3), await world.TraceAsync());
        Assert.False(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count > 0), await world.TraceAsync());

        string[] events = await EventsAsync(world);
        foreach ((Guid _, string name) in sixties)
            Assert.Contains(events, e => e.Contains(name + " needs a group", StringComparison.Ordinal) && e.Contains("fit=out-of-band 60 vs ", StringComparison.Ordinal)
                && e.EndsWith(": companion+forminggroup", StringComparison.Ordinal));
        Assert.Empty(world.Coordinator.Groups);
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        Assert.Equal(4, world.Coordinator.WaitingBots.Count());
    }

    [Fact]
    public async Task WhenTheSeedLeaves_TheLevel60Waiters_OnDerivedReasonsOnly_StopWaiting_WithinTheSeedRecheck()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], o => { NoOwnGuilds(o); o.Groups.LevelRange = 50; }, dungeon: true);
        var seed = await AddSeedAsync(world, "Seedc");
        await world.OnWorldAsync(() =>
        {
            world.Player(seed.Id).Relocate(StartX, StartY, StartZ, 0, world.World.Host.World.NowMs);
            return true;
        });
        (Guid Id, string Name)[] sixties = await AddLevel60TrioAsync(world, "Leave");
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Count(w => w.Name.StartsWith("Leave", StringComparison.Ordinal)) == 3), await world.TraceAsync());

        var stopped = await world.Bots.StopAsync(seed.Name);
        Assert.True(stopped.Success, stopped.Code);
        Assert.True(await world.RunUntilAsync(PlayerbotGroupCoordinator.SeedRecheckMs * 6, () => !world.Coordinator.WaitingBots.Any(w => w.Name.StartsWith("Leave", StringComparison.Ordinal))),
            await world.TraceAsync());
        Assert.Contains(await EventsAsync(world), e => e.Contains("has no companion or forming group left", StringComparison.Ordinal));
        Assert.Empty(world.Coordinator.Groups);
    }

    // --- positive companion --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AGuildmateJustAboveTheBand_WithinLevelRange_WaitsOnACompanionReason_AndFormsWithTheInBandAnchor()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], NoOwnGuilds, dungeon: true);
        var seed = await AddSeedAsync(world, "Seedd", level: 27); // in band 15-28; the guildmates are 31: three above the band, four from the seed
        var healer = await world.AddBotAsync("Compprie", Human, Priest, 31, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Compmage", Human, Mage, 31, [Fireball], EntranceArea);
        await MakeGuildAsync(world, "Compguild", seed, healer, mage);

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count > 0), await world.TraceAsync());

        string[] events = await EventsAsync(world);
        foreach (string name in new[] { healer.Name, mage.Name })
            Assert.Contains(events, e => e.Contains(name + " needs a group", StringComparison.Ordinal) && e.Contains("fit=out-of-band 31 vs ", StringComparison.Ordinal)
                && e.Contains("companion", StringComparison.Ordinal));
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.NotNull(group.Find(seed.Id)); // the in-band bot anchors the group
        Assert.NotNull(group.Find(healer.Id));
        Assert.NotNull(group.Find(mage.Id));
    }
}
