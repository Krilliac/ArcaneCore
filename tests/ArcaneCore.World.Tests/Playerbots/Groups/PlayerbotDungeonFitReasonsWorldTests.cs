using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The reasons that let a bot outside a dungeon's band wait for it, inside the real group coordinator (Deadmines, map 36): an attunement step,
/// a forming group (seeded only by in-band waiters), and the gear reason that must NOT be given by a low-level item or by an empty slot.
/// </summary>
public sealed class PlayerbotDungeonFitReasonsWorldTests
{
    private const byte Human = 1;
    private const byte Warrior = 1, Priest = 5, Mage = 8;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;
    private const uint GearItem = 990770;

    private static async Task<(Guid Id, string Name)[]> AddLevel60TrioAsync(GroupTestWorld world, string prefix)
    {
        var tank = await world.AddBotAsync(prefix + "tank", Human, Warrior, 60, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync(prefix + "prie", Human, Priest, 60, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync(prefix + "mage", Human, Mage, 60, [Fireball], EntranceArea);
        return [tank, healer, mage];
    }

    private static Task<string[]> EventsAsync(GroupTestWorld world) => world.OnWorldAsync(() => world.Coordinator.Events.ToArray());

    private static bool Noted(GroupTestWorld world, string text) => world.Coordinator.Events.Any(e => e.Contains(text, StringComparison.Ordinal));

    // Bots may befriend or guild each other mid-run, which would turn "forminggroup" into "companion+forminggroup"; a test of the exact reason line switches both off.
    private static void NoGuildsOrFriendsWithLevelRange50(ArcaneCore.World.Playerbots.PlayerbotOptions o)
    {
        o.Social.Guilds.Enabled = false;
        o.Social.Lists.Enabled = false;
        o.Groups.LevelRange = 50;
    }

    // An uncommon neck piece (anyone can wear it) with a stat, so a gain is real if the slot were worn.
    private static ItemTemplate Neck(uint entry, uint requiredLevel) => new()
    {
        Entry = entry, Name = "Test Amulet " + entry, Class = 4, SubClass = 0, InventoryType = 2, Quality = 2,
        RequiredLevel = requiredLevel, ItemLevel = requiredLevel, Stackable = 1, Stats = [new ItemStat(7, 10)],
    };

    // --- attunement -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADungeonQuestThatIsAnAttunementStep_RequiredQuestDoneOfTheEntrance_LetsLevel60BotsWait()
    {
        // Quest level 10 is grey for a 60, so the only reason is that DungeonQuest is the entrance's required_quest_done.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], dungeon: true,
            services: s => DeadminesTestContent.RegisterWithEntranceRequirements(s, requiredQuestDone: DungeonQuest));
        (Guid Id, string Name)[] bots = await AddLevel60TrioAsync(world, "Attq");

        Assert.True(await world.RunUntilAsync(60_000, () => bots.All(b => world.HasQuest(b.Id, DungeonQuest))), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.WaitingBots.Any() || world.Coordinator.Groups.Any()), await world.TraceAsync());

        string[] events = await EventsAsync(world);
        Assert.Contains(events, e => e.Contains("needs a group", StringComparison.Ordinal) && e.Contains("fit=out-of-band 60 vs ", StringComparison.Ordinal)
            && e.EndsWith(": attunement", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Contains("no reason", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADungeonQuestWhoseObjectiveItemIsARequiredItemOfTheEntrance_LetsLevel60BotsWait()
    {
        // DungeonItemQuest asks for BossTrophy (a drop of the boss, grey quest level 10); the entrance requires BossTrophy as required_item.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonItemQuest], dungeon: true,
            services: s => DeadminesTestContent.RegisterWithEntranceRequirements(s, requiredItem: BossTrophy));
        (Guid Id, string Name)[] bots = await AddLevel60TrioAsync(world, "Atti");

        Assert.True(await world.RunUntilAsync(60_000, () => bots.All(b => world.HasQuest(b.Id, DungeonItemQuest))), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.WaitingBots.Any() || world.Coordinator.Groups.Any()), await world.TraceAsync());

        string[] events = await EventsAsync(world);
        Assert.Contains(events, e => e.Contains("needs a group", StringComparison.Ordinal) && e.Contains("fit=out-of-band 60 vs ", StringComparison.Ordinal)
            && e.EndsWith(": attunement", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Contains("no reason", StringComparison.Ordinal));
    }

    // --- forming group: seeded only by in-band waiters ----------------------------------------------------------------

    [Fact]
    public async Task AnInBandWaiterOfTheSameTeam_SeedsAFormingGroupReason_ForAnOutOfBandBotWithAGreyQuest()
    {
        // Quest level 40: grey for a 60 (gray level 47), not grey for a 35 (27). LevelRange 50 so only the band decides who may seed.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], configure: NoGuildsOrFriendsWithLevelRange50, dungeon: true, dungeonQuestLevel: 40);
        var anchor = await world.AddBotAsync("Anchora", Human, Warrior, 20, [Taunt], EntranceArea);
        Assert.True(await world.RunUntilAsync(60_000, () => world.HasQuest(anchor.Id, DungeonQuest) && Noted(world, "Anchora needs a group")), await world.TraceAsync());

        var drifter = await world.AddBotAsync("Drifter", Human, Warrior, 60, [Taunt], EntranceArea);
        Assert.True(await world.RunUntilAsync(60_000, () => world.HasQuest(drifter.Id, DungeonQuest)), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(120_000, () => Noted(world, "Drifter needs a group") || Noted(world, "Drifter passes over")), await world.TraceAsync());

        string[] events = await EventsAsync(world);
        Assert.Contains(events, e => e.Contains("Anchora needs a group", StringComparison.Ordinal) && !e.Contains("fit=", StringComparison.Ordinal)); // in band: no fit text
        string drifterNeed = Assert.Single(events, e => e.Contains("Drifter needs a group", StringComparison.Ordinal));
        // Guilds and friend lists are off, so nothing derives a companion reason: the line ends in exactly the forming-group reason (the quest is grey for a 60).
        Assert.True(drifterNeed.Contains("fit=out-of-band 60 vs ", StringComparison.Ordinal), drifterNeed);
        Assert.True(drifterNeed.EndsWith(": forminggroup", StringComparison.Ordinal), drifterNeed);
        Assert.DoesNotContain(events, e => e.Contains("Drifter passes over", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOutOfBandWaiter_NeverSeedsAReasonForAnotherOutOfBandBot()
    {
        // Cantor (35) is outside the band but waits on the quest reason (quest level 40 > gray 27). Drifter (60, quest grey for it) shares the team,
        // the dungeon and the level window (LevelRange 50), yet Cantor's goal carries a fit, so it is no forming group: Drifter has no reason.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], configure: o => o.Groups.LevelRange = 50, dungeon: true, dungeonQuestLevel: 40);
        var cantor = await world.AddBotAsync("Cantor", Human, Warrior, 35, [Taunt], EntranceArea);
        Assert.True(await world.RunUntilAsync(60_000, () => world.HasQuest(cantor.Id, DungeonQuest) && Noted(world, "Cantor needs a group")), await world.TraceAsync());
        string[] before = await EventsAsync(world);
        Assert.Contains(before, e => e.Contains("Cantor needs a group", StringComparison.Ordinal) && e.Contains("fit=out-of-band 35 vs ", StringComparison.Ordinal)
            && e.EndsWith(": quest", StringComparison.Ordinal));

        var drifter = await world.AddBotAsync("Drifter", Human, Warrior, 60, [Taunt], EntranceArea);
        Assert.True(await world.RunUntilAsync(60_000, () => world.HasQuest(drifter.Id, DungeonQuest)), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(120_000, () => Noted(world, "Drifter needs a group") || Noted(world, "Drifter passes over")), await world.TraceAsync());

        string[] events = await EventsAsync(world);
        Assert.Contains(events, e => e.Contains("Drifter passes over", StringComparison.Ordinal) && e.Contains("no reason", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Contains("Drifter needs a group", StringComparison.Ordinal));
    }

    // --- gear: a low-level uncommon, or an empty slot, is no reason ----------------------------------------------------------------

    private static async Task AssertLevel60TrioRefusedAsync(GroupTestWorld world, string prefix)
    {
        (Guid Id, string Name)[] bots = await AddLevel60TrioAsync(world, prefix);
        Assert.True(await world.RunUntilAsync(60_000, () => bots.All(b => world.HasQuest(b.Id, DungeonQuest))), await world.TraceAsync());
        bool everWaited = false;
        Assert.False(await world.RunUntilAsync(120_000, () =>
        {
            everWaited |= world.Coordinator.WaitingBots.Any() || world.Coordinator.Groups.Any();
            return everWaited;
        }), await world.TraceAsync());

        string[] events = await EventsAsync(world);
        Assert.Contains(events, e => e.Contains("no reason", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Contains("needs a group", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(20u)] // far below the bot (the level floor, GearLevelFloor 5 under level 60)
    [InlineData(55u)] // at the floor: the slot is empty, and filling an empty slot is no upgrade
    public async Task ALevel60WithAnEmptySlot_IsRefusedAGreyDungeonQuest_WhenTheBossDropsAnUncommonItsOwnLootTableHolds(uint requiredLevel)
    {
        Assert.Equal(5, ArcaneCore.World.Playerbots.Groups.PlayerbotGroupCoordinator.GearLevelFloor);
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], dungeon: true,
            extraItems: [Neck(GearItem, requiredLevel)],
            extraLoot: [(LootTableKind.Creature, new LootStoreRow(BossEntry, GearItem, 100f, 0, 1, 1))]);
        await AssertLevel60TrioRefusedAsync(world, "Gear");
    }

    [Fact]
    public async Task ALevel60WithAnEmptySlot_IsRefusedAGreyDungeonQuest_WhenTheUncommonItemIsOnlyInAReferenceLootTable()
    {
        // Reference tables are world-drop pools shared by many creatures: they do not make a dungeon an upgrade (the existing boss row references
        // BossLootReference with a negative mincountOrRef; the item is added to that table).
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], dungeon: true,
            extraItems: [Neck(GearItem, 55)],
            extraLoot: [(LootTableKind.Reference, new LootStoreRow(BossLootReference, GearItem, 100f, 0, 1, 1))]);
        await AssertLevel60TrioRefusedAsync(world, "Gref");
    }
}
