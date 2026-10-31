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
public abstract class PlayerbotDungeonAnchorWorldTestBase
{
    private protected const byte Human = 1;
    private protected const byte Warrior = 1, Priest = 5, Mage = 8;
    private protected const uint LesserHeal = 2050, Fireball = 133, Smite = 585;
    private protected const uint WornNeck = 990771, BetterNeck = 990772;

    private protected static void NoOwnGuilds(PlayerbotOptions options) => options.Social.Guilds.Enabled = false;

    private protected static bool Noted(GroupTestWorld world, string text) => world.Coordinator.Events.Any(e => e.Contains(text, StringComparison.Ordinal));

    private protected static Task<string[]> EventsAsync(GroupTestWorld world) => world.OnWorldAsync(() => world.Coordinator.Events.ToArray());

    private protected static Task MakeGuildAsync(GroupTestWorld world, string name, (Guid Id, string Name) leader, params (Guid Id, string Name)[] members)
        => world.OnWorldAsync(() =>
        {
            GuildManager guilds = world.World.Services.GetRequiredService<SocialFeature>().Context.Guilds;
            Assert.Equal(GuildAdminResult.Ok, guilds.Create(world.Player(leader.Id).Guid.Low, name, out Guild? _));
            foreach ((Guid id, string _) in members) Assert.Equal(GuildAdminResult.Ok, guilds.AdminInvite(world.Player(id).Guid.Low, name));
            return true;
        });

    /// <summary>An in-band (15-28) level-20 warrior that holds the quest and waits for the Deadmines goal.</summary>
    private protected static async Task<(Guid Id, string Name)> AddSeedAsync(GroupTestWorld world, string name, byte level = 20)
    {
        var seed = await world.AddBotAsync(name, Human, Warrior, level, [Taunt], EntranceArea);
        return await AwaitSeedWaitingAsync(world, seed);
    }

    private protected static async Task<(Guid Id, string Name)> AwaitSeedWaitingAsync(GroupTestWorld world, (Guid Id, string Name) seed)
    {
        string name = seed.Name;
        Assert.True(await world.RunUntilAsync(60_000, () => world.HasQuest(seed.Id, DungeonQuest) && Noted(world, name + " needs a group")), await world.TraceAsync());
        string[] events = await EventsAsync(world);
        Assert.Contains(events, e => e.Contains(name + " needs a group", StringComparison.Ordinal) && !e.Contains("fit=", StringComparison.Ordinal)); // in band
        return seed;
    }

    private protected static async Task<(Guid Id, string Name)[]> AddLevel60TrioAsync(GroupTestWorld world, string prefix)
    {
        var tank = await world.AddBotAsync(prefix + "tank", Human, Warrior, 60, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync(prefix + "prie", Human, Priest, 60, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync(prefix + "mage", Human, Mage, 60, [Fireball], EntranceArea);
        return [tank, healer, mage];
    }

    // --- positive gear -------------------------------------------------------------------------------------------------

    private protected static ItemTemplate Neck(uint entry, uint requiredLevel, int stamina, int strength) => new()
    {
        Entry = entry, Name = "Anchor Amulet " + entry, Class = 4, SubClass = 0, InventoryType = 2, Quality = 2,
        RequiredLevel = requiredLevel, ItemLevel = requiredLevel, Stackable = 1, Stats = [new ItemStat(7, stamina), new ItemStat(4, strength)],
    };

    private protected static Task EquipWornNeckAsync(GroupTestWorld world, IEnumerable<(Guid Id, string Name)> bots)
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

    private protected static Task<GroupTestWorld> GearWorldAsync(uint requiredLevel) => GroupTestWorld.StartAsync([DungeonQuest], dungeon: true,
        extraItems: [Neck(WornNeck, 1, 1, 0), Neck(BetterNeck, requiredLevel, 30, 20)],
        extraLoot: [(LootTableKind.Creature, new LootStoreRow(BossEntry, BetterNeck, 100f, 0, 1, 1))]);

    // --- seed leaves, level-30 waiters -----------------------------------------------------------------------------------

    private protected static async Task<(Guid Id, string Name)[]> AddTrioAsync(GroupTestWorld world, string prefix, byte level)
    {
        var tank = await world.AddBotAsync(prefix + "tank", Human, Warrior, level, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync(prefix + "prie", Human, Priest, level, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync(prefix + "mage", Human, Mage, level, [Fireball], EntranceArea);
        return [tank, healer, mage];
    }
}
