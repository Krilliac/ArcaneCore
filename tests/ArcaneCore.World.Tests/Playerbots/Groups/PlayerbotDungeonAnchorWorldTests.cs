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
/// The anchor rule inside the real group coordinator (Deadmines, map 36; fallback band 15-28): a bot whose only reasons are derived from
/// other waiting bots (Companion, FormingGroup) never anchors a group; Match skips a group with no anchor and rejects a plan with none.
/// Direct reasons (a gear upgrade here) anchor on their own.
/// </summary>
public sealed class PlayerbotDungeonAnchorWorldTests
{
    private const byte Human = 1;
    private const byte Warrior = 1, Priest = 5, Mage = 8;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;
    private const uint WornNeck = 990771, BetterNeck = 990772;

    private static void NoOwnGuilds(PlayerbotOptions options) => options.Social.Guilds.Enabled = false;

    private static bool Noted(GroupTestWorld world, string text) => world.Coordinator.Events.Any(e => e.Contains(text, StringComparison.Ordinal));

    private static Task<string[]> EventsAsync(GroupTestWorld world) => world.OnWorldAsync(() => world.Coordinator.Events.ToArray());

    private static Task MakeGuildAsync(GroupTestWorld world, string name, (Guid Id, string Name) leader, params (Guid Id, string Name)[] members)
        => world.OnWorldAsync(() =>
        {
            GuildManager guilds = world.World.Services.GetRequiredService<SocialFeature>().Context.Guilds;
            Assert.Equal(GuildAdminResult.Ok, guilds.Create(world.Player(leader.Id).Guid.Low, name, out Guild? _));
            foreach ((Guid id, string _) in members) Assert.Equal(GuildAdminResult.Ok, guilds.AdminInvite(world.Player(id).Guid.Low, name));
            return true;
        });

    /// <summary>An in-band (15-28) level-20 warrior that holds the quest and waits for the Deadmines goal.</summary>
    private static async Task<(Guid Id, string Name)> AddSeedAsync(GroupTestWorld world, string name, byte level = 20)
    {
        var seed = await world.AddBotAsync(name, Human, Warrior, level, [Taunt], EntranceArea);
        return await AwaitSeedWaitingAsync(world, seed);
    }

    private static async Task<(Guid Id, string Name)> AwaitSeedWaitingAsync(GroupTestWorld world, (Guid Id, string Name) seed)
    {
        string name = seed.Name;
        Assert.True(await world.RunUntilAsync(60_000, () => world.HasQuest(seed.Id, DungeonQuest) && Noted(world, name + " needs a group")), await world.TraceAsync());
        string[] events = await EventsAsync(world);
        Assert.Contains(events, e => e.Contains(name + " needs a group", StringComparison.Ordinal) && !e.Contains("fit=", StringComparison.Ordinal)); // in band
        return seed;
    }

    private static async Task<(Guid Id, string Name)[]> AddLevel60TrioAsync(GroupTestWorld world, string prefix)
    {
        var tank = await world.AddBotAsync(prefix + "tank", Human, Warrior, 60, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync(prefix + "prie", Human, Priest, 60, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync(prefix + "mage", Human, Mage, 60, [Fireball], EntranceArea);
        return [tank, healer, mage];
    }

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

    // --- positive gear -------------------------------------------------------------------------------------------------

    private static ItemTemplate Neck(uint entry, uint requiredLevel, int stamina, int strength) => new()
    {
        Entry = entry, Name = "Anchor Amulet " + entry, Class = 4, SubClass = 0, InventoryType = 2, Quality = 2,
        RequiredLevel = requiredLevel, ItemLevel = requiredLevel, Stackable = 1, Stats = [new ItemStat(7, stamina), new ItemStat(4, strength)],
    };

    private static Task EquipWornNeckAsync(GroupTestWorld world, IEnumerable<(Guid Id, string Name)> bots)
    {
        (Guid Id, string Name)[] list = [.. bots];
        return world.OnWorldAsync(() =>
        {
            foreach ((Guid id, string _) in list)
            {
                PlayerInventory inventory = world.Player(id).Inventory;
                Assert.Equal(InventoryResult.Ok, inventory.AddItem(WornNeck, 1, out Item? neck));
                inventory.AutoEquipItem(neck!.BagSlot, neck.Slot);
                Assert.Contains(inventory.Equipped, e => ReferenceEquals(e.Item, neck));
            }

            return true;
        });
    }

    private static Task<GroupTestWorld> GearWorldAsync(uint requiredLevel) => GroupTestWorld.StartAsync([DungeonQuest], dungeon: true,
        extraItems: [Neck(WornNeck, 1, 1, 0), Neck(BetterNeck, requiredLevel, 30, 20)],
        extraLoot: [(LootTableKind.Creature, new LootStoreRow(BossEntry, BetterNeck, 100f, 0, 1, 1))]);

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

    // --- seed leaves, level-30 waiters -----------------------------------------------------------------------------------

    private static async Task<(Guid Id, string Name)[]> AddTrioAsync(GroupTestWorld world, string prefix, byte level)
    {
        var tank = await world.AddBotAsync(prefix + "tank", Human, Warrior, level, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync(prefix + "prie", Human, Priest, level, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync(prefix + "mage", Human, Mage, level, [Fireball], EntranceArea);
        return [tank, healer, mage];
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

    // --- recruiting follows the qualifying anchor, not the oldest waiter ------------------------------------------------------

    [Fact]
    public async Task ACompanionOnlyWaiter_OlderThanTheInBandSeed_DoesNotPullFreeLevel35BotsIn_ButARecruitNearTheSeedStillComes()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], o =>
        {
            o.MoveSpeed = 0.1f; // bots crawl: the free recruits never walk to the quest giver
            NoOwnGuilds(o);
            o.Groups.Cooperation.RecruitWaitSeconds = 5;
            o.Groups.Cooperation.RecruitRadiusYards = 1500;
            o.Groups.Cooperation.RecruitBaseChance = 1f;
        }, dungeon: true);
        var far = EntranceArea + new System.Numerics.Vector3(0, -900f, 0);

        // The first seed (in band, 27) makes the level-31 priest wait on a forming-group reason; a second in-band warrior then waits too (younger than the priest);
        // the first seed leaves. The priest is now the oldest waiter, but it cannot anchor: the second seed (27) is the qualifying anchor.
        var first = await AddSeedAsync(world, "Firstseed", 27);
        // A mage: the waiters (two warriors and a mage) have no healer, so they cannot form a group among themselves.
        var companion = await world.AddBotAsync("Oldcomp", Human, Mage, 31, [Fireball], EntranceArea);
        Assert.True(await world.RunUntilAsync(60_000, () => world.HasQuest(companion.Id, DungeonQuest) && Noted(world, "Oldcomp needs a group")), await world.TraceAsync());
        Assert.Contains(await EventsAsync(world), e => e.Contains("Oldcomp needs a group", StringComparison.Ordinal) && e.Contains("fit=out-of-band 31 vs ", StringComparison.Ordinal));
        var second = await AddSeedAsync(world, "Secondseed", 27);
        var stopped = await world.Bots.StopAsync(first.Name);
        Assert.True(stopped.Success, stopped.Code);
        // Precondition: the two remaining waiters (no healer) are not a group, and the companion survives a seed recheck because the second seed is in band.
        await world.RunAsync(2 * PlayerbotGroupCoordinator.SeedRecheckMs);
        Assert.Empty(world.Coordinator.Groups);
        Assert.Contains(world.Coordinator.WaitingBots, w => w.Name == "Oldcomp");
        Assert.Contains(world.Coordinator.WaitingBots, w => w.Name == "Secondseed");

        var tank35 = await world.AddBotAsync("Freetank", Human, Warrior, 35, [Taunt], far);
        var healer35 = await world.AddBotAsync("Freeheal", Human, Priest, 35, [LesserHeal, Smite], far + new System.Numerics.Vector3(5f, 0, 0));
        Assert.False(await world.RunUntilAsync(60_000, () => Noted(world, "is recruited") || world.Coordinator.Groups.Count > 0), await world.TraceAsync());
        Assert.Contains(world.Coordinator.WaitingBots, w => w.Name == "Oldcomp"); // the check is not vacuous: the companion still waits
        Assert.Contains(world.Coordinator.WaitingBots, w => w.Name == "Secondseed");
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("Freetank", StringComparison.Ordinal) || e.Contains("Freeheal", StringComparison.Ordinal));

        // Control: a free level-29 priest is within LevelRange of the anchor (27) and is recruited, so recruiting itself is live.
        var near = await world.AddBotAsync("Freenear", Human, Priest, 29, [LesserHeal, Smite], far + new System.Numerics.Vector3(10f, 0, 0));
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count > 0), await world.TraceAsync());
        Assert.Contains(await EventsAsync(world), e => e.Contains("Freenear is recruited", StringComparison.Ordinal));
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.NotNull(group.Find(second.Id));
        Assert.Null(group.Find(tank35.Id));
        Assert.Null(group.Find(healer35.Id));
    }

    // --- a plan the Match steps accept, but that holds no waiting anchor -----------------------------------------------------

    [Fact]
    public async Task APlanOfDerivedOnlyWaiters_FilledByAnInBandRecruit_IsNotFormed_WhenTheInBandAnchorIsLeftOutOfIt()
    {
        // Waiters: the anchor (a level-20 mage, in band 15-28, the YOUNGEST waiter) and five level-60 bots on a forming-group reason only (a warrior
        // and four mages, no healer). Fill takes the tank, the healer and then the rest oldest first, so with a free in-band priest recruited as the
        // healer the plan is the five level-60 bots plus the priest minus one, and the anchor is left out. The recruit is in band, so the plan passes
        // Match's needQualifying; only PlanAnchored (no waiting bot of the plan anchors) rejects it.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], o =>
        {
            o.MoveSpeed = 0.1f; // bots crawl: the free recruit never walks to the quest giver
            NoOwnGuilds(o);
            o.Social.Lists.Enabled = false; // no companion reason: the derived waiters' lines end in exactly ": forminggroup"
            o.Groups.LevelRange = 50;
            o.Groups.Cooperation.RecruitWaitSeconds = 5;
            o.Groups.Cooperation.RecruitRadiusYards = 1500;
            o.Groups.Cooperation.RecruitBaseChance = 1f;
            o.Groups.Cooperation.RefusalMemoryMinutes = 1; // a refusal is rolled again every minute, so the recruit does come within the window
        }, dungeon: true);
        var far = EntranceArea + new System.Numerics.Vector3(0, -900f, 0);

        var first = await AddSeedAsync(world, "Firstseed", 20);
        var tank = await world.AddBotAsync("Dtank", Human, Warrior, 60, [Taunt], EntranceArea);
        var mages = new List<(Guid Id, string Name)>();
        for (int i = 0; i < 4; i++) mages.Add(await world.AddBotAsync("Dmage" + (char)('a' + i), Human, Mage, 60, [Fireball], EntranceArea));
        (Guid Id, string Name)[] derived = [tank, .. mages];
        Assert.True(await world.RunUntilAsync(120_000, () => derived.All(b => world.HasQuest(b.Id, DungeonQuest) && Noted(world, b.Name + " needs a group"))), await world.TraceAsync());

        var seed = await world.AddBotAsync("Younganchor", Human, Mage, 20, [Fireball], EntranceArea);
        await AwaitSeedWaitingAsync(world, seed);
        var stopped = await world.Bots.StopAsync(first.Name);
        Assert.True(stopped.Success, stopped.Code);
        await world.RunAsync(2 * PlayerbotGroupCoordinator.SeedRecheckMs);

        // Preconditions: the anchor and all five derived-only bots wait, the anchor younger than every one of them; nothing has formed (no healer yet).
        string[] events = await EventsAsync(world);
        foreach ((Guid Id, string Name) bot in derived)
        {
            Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == bot.Id);
            string need = Assert.Single(events, e => e.Contains(bot.Name + " needs a group", StringComparison.Ordinal));
            Assert.True(need.EndsWith(": forminggroup", StringComparison.Ordinal), need);
        }

        var anchorWaiting = Assert.Single(world.Coordinator.WaitingBots, w => w.BotId == seed.Id);
        Assert.All(world.Coordinator.WaitingBots.Where(w => derived.Any(d => d.Id == w.BotId)), w => Assert.True(unchecked((int)(anchorWaiting.SinceMs - w.SinceMs)) > 0, w.Name));
        Assert.Empty(world.Coordinator.Groups);

        // The filler: a free level-25 priest (in band), near the anchor (inside the recruit radius, far from the giver), holding no quest.
        var priest = await world.AddBotAsync("Freeheal", Human, Priest, 25, [LesserHeal, Smite], far);
        await world.OnWorldAsync(() =>
        {
            var low = world.Player(seed.Id);
            var filler = world.Player(priest.Id);
            Assert.InRange(filler.Level, 15, 28);
            Assert.True(System.Numerics.Vector3.Distance(new System.Numerics.Vector3(filler.X, filler.Y, filler.Z), new System.Numerics.Vector3(low.X, low.Y, low.Z)) <= 1500f);
            return true;
        });
        Assert.DoesNotContain(world.Coordinator.WaitingBots, w => w.BotId == priest.Id);

        // No group of the five level-60 bots and the recruit may form in the window, and the recruit is never taken.
        Assert.False(await world.RunUntilAsync(300_000, () => world.Coordinator.Groups.Count > 0 || Noted(world, "Freeheal is recruited")), await world.TraceAsync());
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == seed.Id);
        Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == tank.Id);
        Assert.DoesNotContain(world.Coordinator.WaitingBots, w => w.BotId == priest.Id);
    }
}
