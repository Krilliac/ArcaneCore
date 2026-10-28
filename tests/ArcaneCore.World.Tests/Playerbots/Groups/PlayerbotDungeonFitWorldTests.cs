using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The fit rule inside the real group coordinator: level-60 bots holding a Deadmines quest (map 36) that is grey for them must not wait for
/// or form a group, while the same bots with a quest that is not grey do, and the group goal records why a level-60 is in a low dungeon.
/// </summary>
public sealed class PlayerbotDungeonFitWorldTests
{
    private const byte Human = 1;
    private const byte Warrior = 1, Priest = 5, Mage = 8;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    private static async Task<(Guid Id, string Name)[]> AddLevel60TrioAsync(GroupTestWorld world, string prefix)
    {
        var tank = await world.AddBotAsync(prefix + "tank", Human, Warrior, 60, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync(prefix + "prie", Human, Priest, 60, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync(prefix + "mage", Human, Mage, 60, [Fireball], EntranceArea);
        return [tank, healer, mage];
    }

    [Fact]
    public async Task Level60Bots_HoldingAGreyDeadminesQuest_NeverWaitForOrFormADungeonGroup()
    {
        // Quest level 10 is grey for a 60 (gray level 47) and Deadmines' band is far below 60: no reason, no group.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], dungeon: true);
        (Guid Id, string Name)[] bots = await AddLevel60TrioAsync(world, "Grey");

        Assert.True(await world.RunUntilAsync(60_000, () => bots.All(b => world.HasQuest(b.Id, DungeonQuest))), await world.TraceAsync());
        bool everWaited = false;
        Assert.False(await world.RunUntilAsync(120_000, () =>
        {
            everWaited |= world.Coordinator.WaitingBots.Any() || world.Coordinator.Groups.Any();
            return everWaited;
        }), await world.TraceAsync());

        Assert.Empty(world.Coordinator.Groups);
        Assert.Empty(world.Coordinator.WaitingBots);
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        string[] events = await world.OnWorldAsync(() => world.Coordinator.Events.ToArray());
        Assert.Contains(events, e => e.Contains("no reason", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Contains("needs a group", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Level60Bots_HoldingADeadminesQuestThatIsNotGreyForThem_DoWaitAndForm_AndTheGoalRecordsTheQuestReason()
    {
        // Quest level 58 is above the gray level of 60 (47): the quest justifies the run although the band does not hold the bots.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], dungeon: true, dungeonQuestLevel: 58);
        (Guid Id, string Name)[] bots = await AddLevel60TrioAsync(world, "Live");

        Assert.True(await world.RunUntilAsync(60_000, () => bots.All(b => world.HasQuest(b.Id, DungeonQuest))), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.WaitingBots.Any() || world.Coordinator.Groups.Any()), await world.TraceAsync());

        string[] events = await world.OnWorldAsync(() => world.Coordinator.Events.ToArray());
        Assert.Contains(events, e => e.Contains("fit=", StringComparison.Ordinal) && e.Contains("quest", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Contains("no reason", StringComparison.Ordinal));
    }
}
