using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>Disenchant end to end over loopback: a cast on a green item shows the type 4 window of its disenchant loot, closing it stores the shard and destroys the item.</summary>
public sealed class DisenchantWorldTests
{
    private const uint DisenchantCast = 9500;
    private const uint GreenSword = 4601;
    private const uint Trinket = 4602;
    private const uint Shard = 14344;

    [Fact]
    public async Task ADisenchantCast_ShowsTheType4Window_AndClosingItStoresTheLootAndDestroysTheItem()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("ENCHANTER", "Enchanter");
        Player player = await host.PlayerAsync("Enchanter");
        Item sword = (await host.OnWorldAsync(() =>
        {
            Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(GreenSword, 1, out Item? item));
            return item;
        }))!;

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastOn(sword));

        var loot = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        Assert.Equal(sword.Guid.Value, loot.ReadUInt64());
        Assert.Equal((byte)4, loot.ReadByte());       // LOOT_DISENCHANTING
        Assert.Equal(0u, loot.ReadUInt32());
        Assert.Equal((byte)1, loot.ReadByte());
        Assert.Equal((byte)0, loot.ReadByte());
        Assert.Equal(Shard, loot.ReadUInt32());
        Assert.True(await host.OnWorldAsync(() => sword.IsSoulBound));

        await client.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(sword.Guid.Value));
        await client.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);
        await host.WaitForWorldAsync(() => player.Inventory.GetItemByGuid(sword.Guid) is null, "the item is destroyed");
        Assert.Equal(1u, await host.OnWorldAsync(() => player.Inventory.GetItemCount(Shard)));
    }

    [Fact]
    public async Task AnItemWithoutDisenchantLoot_AnswersCantBeDisenchanted()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("NOENCH", "Noench");
        Player player = await host.PlayerAsync("Noench");
        Item trinket = (await host.OnWorldAsync(() =>
        {
            player.Inventory.AddItem(Trinket, 1, out Item? item);
            return item;
        }))!;

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastOn(trinket));

        byte[] result = await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);
        Assert.Equal((DisenchantCast, (byte)SpellCastResultStatus.Failure, (byte)SpellCastResult.CantBeDisenchanted), (BitConverter.ToUInt32(result, 0), result[4], result[5]));
    }

    private static byte[] CastOn(Item item)
    {
        var w = new PacketWriter(24);
        w.WriteUInt32(DisenchantCast);
        new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = item.Guid }.Write(w);
        return w.ToArray();
    }

    private static WorldTestHost Start()
    {
        var goContent = new GameObjectContent([], [], [], [], []);
        var lootContent = new LootContent([(LootTableKind.Disenchant, new LootStoreRow(48, Shard, 100f, 0, 1, 1))], []);
        var context = new GameObjectTestContext(goContent, lootContent);

        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = GreenSword, Class = 2, SubClass = 7, Name = "Green Sword", DisplayId = 10, Quality = 2, InventoryType = 21, DisenchantId = 48 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = Trinket, Class = 4, SubClass = 0, Name = "Plain Trinket", DisplayId = 12, Quality = 2, InventoryType = 12 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = Shard, Class = 7, Name = "Large Brilliant Shard", DisplayId = 13, Quality = 3, Stackable = 20 });
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
        => services.AddSingleton<ISpellContentStore>(new Skills.InMemorySkillSpellContentStore(new SpellContent(
            [new SpellTemplateRow { Id = DisenchantCast, SpellName = "Test Disenchant", RangeIndex = 1, Targets = 0x10, Effect1 = 99, EffectImplicitTargetA1 = 1 }],
            [], [], [new SpellRangeRow { Id = 1 }], [],
            [new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = DisenchantCast }],
            [])));
}