using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Items;

/// <summary>
/// Classic real loopback item-use refusal feedback. Move into the world test project
/// after the production feedback lane lands; these tests intentionally use ItemTestContent and
/// WorldTestHost rather than testing CanStartItemUse in isolation.
/// </summary>
public sealed class ItemUseFailureWorldTests
{
    [Fact]
    public async Task Missing_item_and_invalid_spell_index_return_item_not_found_without_cast_or_charge_change()
    {
        FailureItemContent content = new();
        await using WorldTestHost host = FailureItemContent.Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync("ITEMFAIL", "Itemfail");
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgUseItem, [InventorySlots.Bag0, 31, 0, 0, 0]);
        AssertItemNotFound(await client.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure), 0);

        ulong itemGuid = await host.PlayerStateAsync("Itemfail", p => p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.Guid.Value);
        int charges = await host.PlayerStateAsync("Itemfail", p => p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.GetInt32(UpdateFields.ItemFieldSpellCharges));
        await client.SendAsync(WorldOpcode.CmsgUseItem, [InventorySlots.Bag0, InventorySlots.ItemStart, 1, 0, 0]);
        byte[] failure = await client.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure);
        AssertItemNotFound(failure, itemGuid);
        Assert.Equal(charges, await host.PlayerStateAsync("Itemfail", p => p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.GetInt32(UpdateFields.ItemFieldSpellCharges)));
        Assert.DoesNotContain((await client.CollectAsync()).Select(p => p.Opcode), op => op is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo);
    }

    [Fact]
    public async Task Non_on_use_and_unequipped_item_refusals_preserve_inventory_state()
    {
        FailureItemContent content = new();
        await using WorldTestHost host = FailureItemContent.Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync("ITEMNONUSE", "Itemnonuse");
        await client.CollectAsync();

        var nonUse = await FailureItemContent.PositionAsync(host, "Itemnonuse", 99022);
        await client.SendAsync(WorldOpcode.CmsgUseItem, [nonUse.Bag, nonUse.Slot, 0, 0, 0]);
        AssertItemNotFound(await client.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure), nonUse.Guid);
        await host.PlayerStateAsync("Itemnonuse", p =>
        {
            Item weapon = p.Inventory.AllItems.Single(item => item.Entry == 99023);
            p.Inventory.SwapItem(weapon.BagSlot, weapon.Slot, InventorySlots.Bag0, 38);
            Assert.Equal((byte)38, weapon.Slot);
            return true;
        });
        await client.CollectAsync();
        var unequipped = await FailureItemContent.PositionAsync(host, "Itemnonuse", 99023);
        await client.SendAsync(WorldOpcode.CmsgUseItem, [unequipped.Bag, unequipped.Slot, 0, 0, 0]);
        AssertItemNotFound(await client.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure), unequipped.Guid);
        Assert.Equal(1, await host.PlayerStateAsync("Itemnonuse", p => p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.GetInt32(UpdateFields.ItemFieldSpellCharges)));
    }

    [Fact]
    public async Task Level_one_non_equip_item_returns_required_level_trailer_without_cast_or_payment()
    {
        FailureItemContent content = new();
        await using WorldTestHost host = FailureItemContent.Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync("ITEMLEVEL", "Itemlevel");
        await client.CollectAsync();
        var item = await FailureItemContent.PositionAsync(host, "Itemlevel", 99024);

        await client.SendAsync(WorldOpcode.CmsgUseItem, [item.Bag, item.Slot, 0, 0, 0]);
        byte[] failure = await client.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure);
        Assert.Equal((byte)InventoryResult.CantEquipLevelI, failure[0]);
        Assert.Equal(22, failure.Length);
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(failure.AsSpan(1)));
        Assert.Equal(item.Guid, BinaryPrimitives.ReadUInt64LittleEndian(failure.AsSpan(5)));
        Assert.DoesNotContain((await client.CollectAsync()).Select(p => p.Opcode), op => op is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo);
        Assert.Equal(1, await host.PlayerStateAsync("Itemlevel", p => p.Inventory.GetItemByGuid(new ObjectGuid(item.Guid))!.GetInt32(UpdateFields.ItemFieldSpellCharges)));
    }

    private static void AssertItemNotFound(byte[] payload, ulong expectedItemGuid)
    {
        Assert.Equal((byte)InventoryResult.ItemNotFound, payload[0]);
        Assert.Equal(18, payload.Length);
        Assert.Equal(expectedItemGuid, BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(1)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(9)));
    }
}

internal sealed class FailureItemContent
{
    public InMemoryItemTemplateSource Templates { get; } = new();
    public InMemoryItemStore Items { get; } = new();
    public SpellContent? SpellContent { get; }

    public FailureItemContent(bool includePeacefulItem = false)
    {
        Templates.Templates.AddRange([
            new ItemTemplate { Entry = 99021, Class = 0, Stackable = 1, Spells = [new ItemSpell(Heal, 0, 1, 0, 0, 0, 0)] },
            new ItemTemplate { Entry = 99022, Class = 0, Stackable = 1, Spells = [new ItemSpell(Heal, 1, 1, 0, 0, 0, 0)] },
            new ItemTemplate { Entry = 99023, Class = 2, SubClass = 14, InventoryType = 21, Spells = [new ItemSpell(Heal, 0, 1, 0, 0, 0, 0)] },
            new ItemTemplate { Entry = 99024, Class = 0, Stackable = 1, RequiredLevel = 10, Spells = [new ItemSpell(Heal, 0, 1, 0, 0, 0, 0)] },
        ]);
        Templates.StartingItems.AddRange([
            new StartingItem(1, 1, 99021, 1), new StartingItem(1, 1, 99022, 1), new StartingItem(1, 1, 99023, 1), new StartingItem(1, 1, 99024, 1)]);
        if (includePeacefulItem)
        {
            const uint peacefulSpell = 99030;
            Templates.Templates.Add(new ItemTemplate { Entry = 99025, Class = 0, Stackable = 1, Spells = [new ItemSpell(peacefulSpell, 0, 1, 0, 0, 0, 0)] });
            Templates.StartingItems.Add(new StartingItem(1, 1, 99025, 1));
            SpellContent baseContent = Content();
            SpellContent = baseContent with
            {
                Spells = [.. baseContent.Spells, new SpellTemplateRow
                {
                    Id = peacefulSpell, SpellName = "Peaceful Item", Attributes = (uint)SpellAttributesCombat.NotInCombatOnlyPeaceful,
                }],
            };
        }
    }

    public static WorldTestHost Start(FailureItemContent content)
    {
        using (content.Use())
            return WorldTestHost.Start(configureServices: services =>
            {
                if (content.SpellContent is { } spellContent)
                    services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(spellContent));
            });
    }

    public static Task<(ulong Guid, byte Bag, byte Slot)> PositionAsync(WorldTestHost host, string name, uint entry)
        => host.PlayerStateAsync(name, player =>
        {
            Item item = player.Inventory.AllItems.Single(value => value.Entry == entry);
            return (item.Guid.Value, item.BagSlot, item.Slot);
        });

    private IDisposable Use()
    {
        ZFailureItemWorldServices.Current.Value = this;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => ZFailureItemWorldServices.Current.Value = null;
    }
}

internal sealed class ZFailureItemWorldServices : IWorldTestServices
{
    public static readonly AsyncLocal<FailureItemContent?> Current = new();

    public void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        if (Current.Value is not { } content) return;
        services.AddSingleton<IItemTemplateSource>(content.Templates);
        services.AddSingleton<IItemStore>(content.Items);
        services.AddSingleton<IItemStateStore>(content.Items);
        if (content.SpellContent is { } spellContent)
            services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(spellContent));
    }
}
