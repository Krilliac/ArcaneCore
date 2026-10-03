using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Tests.GameObjects;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

/// <summary>
/// Container items over the loopback daemon: Pick Lock (OPEN_LOCK_ITEM) on a lockbox marks it unlocked and shows its loot window (it used to be refused
/// even after a successful pick), CMSG_OPEN_ITEM opens a lock-less clam, a locked box refuses with ITEM_LOCKED, the loot is taken into the bags and the
/// emptied item is destroyed.
/// </summary>
public sealed class ItemLootWorldTests
{
    private const uint PickLockCast = 9600;
    private const uint Lockbox = 4632;
    private const uint Clam = 5523;
    private const uint Jerky = 117;
    private const uint BoxLock = 5;
    private const uint LockTypePicking = 1;

    [Fact]
    public async Task PickLock_OnALockbox_UnlocksIt_AndShowsTheLootWindow()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("PICKER", "Lockpicker");
        Player player = await host.PlayerAsync("Lockpicker");
        await host.WaitForWorldAsync(() => player.Skills is not null, "skills attached");
        await host.OnWorldAsync(() => player.Skills!.Set(SkillIds.Lockpicking, 1, 300, 1));
        Item box = await GiveAsync(host, player, Lockbox);

        // Before the pick: ITEM_LOCKED.
        await client.SendAsync(WorldOpcode.CmsgOpenItem, [box.BagSlot, box.Slot]);
        byte[] error = await client.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure);
        Assert.Equal((byte)InventoryResult.ItemLocked, error[0]);

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastOn(box));

        var loot = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        Assert.Equal(box.Guid.Value, loot.ReadUInt64());
        Assert.Equal((byte)1, loot.ReadByte());       // LOOT_CORPSE
        Assert.Equal(25u, loot.ReadUInt32());         // the lockbox's money (item min/max 25)
        Assert.Equal((byte)1, loot.ReadByte());
        Assert.Equal((byte)0, loot.ReadByte());
        Assert.Equal(Jerky, loot.ReadUInt32());
        Assert.True(await host.OnWorldAsync(() => (box.DynamicFlags & ItemDynFlags.Unlocked) != 0));

        await client.SendAsync(WorldOpcode.CmsgLootMoney, []);
        await client.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [0]);
        await client.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
        await client.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(box.Guid.Value));
        await client.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);

        await host.WaitForWorldAsync(() => player.Inventory.GetItemByGuid(box.Guid) is null, "the emptied lockbox is destroyed");
        Assert.Equal(1u, await host.OnWorldAsync(() => player.Inventory.GetItemCount(Jerky)));
        Assert.Equal(25u, await host.OnWorldAsync(() => player.Money));
    }

    [Fact]
    public async Task OpenItem_OnALocklessClam_ShowsItsLoot_AndTheUntakenLootStaysOnTheItem()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CLAMMER", "Clammer");
        Player player = await host.PlayerAsync("Clammer");
        Item clam = await GiveAsync(host, player, Clam);

        await client.SendAsync(WorldOpcode.CmsgOpenItem, [clam.BagSlot, clam.Slot]);

        var loot = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        Assert.Equal(clam.Guid.Value, loot.ReadUInt64());
        await client.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(clam.Guid.Value));
        await client.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);

        // Nothing taken: the clam stays, with its generated loot (saved with the inventory, never rerolled).
        Assert.NotNull(await host.OnWorldAsync(() => player.Inventory.GetItemByGuid(clam.Guid)));
        ItemLootData? kept = await host.OnWorldAsync(() => clam.ToData().Loot);
        Assert.Equal(Jerky, Assert.Single(kept!.Items).ItemId);
    }

    private static byte[] CastOn(Item item)
    {
        var w = new PacketWriter(24);
        w.WriteUInt32(PickLockCast);
        new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = item.Guid }.Write(w);
        return w.ToArray();
    }

    private static async Task<Item> GiveAsync(WorldTestHost host, Player player, uint entry)
        => (await host.OnWorldAsync(() =>
        {
            Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out Item? item));
            return item;
        }))!;

    private static WorldTestHost Start()
    {
        uint[] types = new uint[LockEntry.Cases];
        uint[] indexes = new uint[LockEntry.Cases];
        uint[] skills = new uint[LockEntry.Cases];
        (types[0], indexes[0], skills[0]) = (2, LockTypePicking, 1);
        var goContent = new GameObjectContent([], [], [new LockEntry(BoxLock, types, indexes, skills)], [], []);
        var lootContent = new LootContent(
            [
                (LootTableKind.Item, new LootStoreRow(Lockbox, Jerky, 100f, 0, 1, 1)),
                (LootTableKind.Item, new LootStoreRow(Clam, Jerky, 100f, 0, 1, 1)),
            ], []);
        var context = new GameObjectTestContext(goContent, lootContent);

        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = Lockbox, Class = 15, Name = "Large Iron Lockbox", DisplayId = 301, Flags = 0x4, LockId = BoxLock, MinMoneyLoot = 25, MaxMoneyLoot = 25 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = Clam, Class = 15, Name = "Small Barnacled Clam", DisplayId = 303, Flags = 0x4, Stackable = 20 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = Jerky, Class = 0, Name = "Tough Jerky", DisplayId = 2473, Quality = 1, Stackable = 20 });
        GameObjectTestStore.Current.Value = context;
        try
        {
            using (items.Use())
            {
                return WorldTestHost.Start(configureServices: Configure);
            }
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }
    }

    private static void Configure(IServiceCollection services)
    {
        services.AddSingleton(new SkillCatalog(
            [new SkillLineRecord(SkillIds.Lockpicking, SkillCategories.Secondary, "Lockpicking", 0)],
            [new SkillRaceClassInfoRecord(SkillIds.Lockpicking, 0, 0, 0, 0, 21)],
            [new SkillTierRecord(21, Enumerable.Repeat(0u, 16).ToArray(), Enumerable.Repeat(75u, 16).ToArray())],
            []));
        services.AddSingleton<ISpellContentStore>(new Skills.InMemorySkillSpellContentStore(new SpellContent(
            [
                new SpellTemplateRow
                {
                    Id = PickLockCast, SpellName = "Test Pick Lock", RangeIndex = 1, Targets = 0x10, Effect1 = 59, EffectImplicitTargetA1 = 26,
                    EffectMiscValue1 = (int)LockTypePicking, EffectBasePoints1 = -1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
                },
            ],
            [], [], [new SpellRangeRow { Id = 1 }], [],
            [new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = PickLockCast }],
            [])));
        services.AddSingleton<ICharacterSkillStore, Skills.InMemoryCharacterSkillStore>();
    }
}