using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Loot;
using Xunit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// Prospecting and milling are not vanilla 1.12 (they arrive with the Burning Crusade client) and must stay absent: vmangos has no
/// prospecting or milling loot store (LootMgr.cpp:46-54 lists the stores), mangos-classic leaves effect 127 "future Prospecting spell, not have spells"
/// unused (SpellEffects.cpp:185), and no classic-db item carries the prospectable/millable flags (0x40000 / 0x20000000). These are regression guards,
/// not RED proofs: they pass today and fail the day somebody adds a later-era feature.
/// </summary>
public sealed class NoProspectingOrMillingTests
{
    [Fact]
    public void LootTableKind_HasNoProspectingOrMilling()
        => Assert.DoesNotContain(Enum.GetNames<LootTableKind>(), name => name.Contains("Prospect", StringComparison.OrdinalIgnoreCase) || name.Contains("Mill", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void LootType_HasNoProspectingOrMilling_AndNoValue7Or8()
    {
        Assert.DoesNotContain(Enum.GetNames<LootType>(), name => name.Contains("Prospect", StringComparison.OrdinalIgnoreCase) || name.Contains("Mill", StringComparison.OrdinalIgnoreCase));
        Assert.False(Enum.IsDefined((LootType)7));  // LOOT_PROSPECTING in later clients
        Assert.False(Enum.IsDefined((LootType)8));  // LOOT_MILLING
    }

    [Fact]
    public void NoSpellEffectIsNamedProspectingOrMilling_AndEffect127HasNoHandler()
    {
        Assert.DoesNotContain(Enum.GetNames<SpellEffectName>(), name => name.Contains("Prospect", StringComparison.OrdinalIgnoreCase) || name.Contains("Mill", StringComparison.OrdinalIgnoreCase));
        var system = new SpellSystem(SpellStore.Empty, () => 0);
        Assert.False(system.HasEffectHandler((SpellEffectName)127)); // SPELL_EFFECT_PROSPECTING in later clients
        Assert.False(system.HasEffectHandler((SpellEffectName)158)); // SPELL_EFFECT_MILLING
    }

    [Fact]
    public void AnItemFlaggedProspectableOrMillable_IsNotALootableContainer()
    {
        // 0x40000 (prospectable) and 0x20000000 (millable) mean nothing in 1.12: only ITEM_FLAG_LOOTABLE (0x4) opens an item.
        var loot = new LootService(LootContent.Empty) { Items = GameObjectTestKit.ItemStore };
        loot.ItemLoot = new ItemLootSource(loot);
        (ArcaneCore.Game.Entities.Player player, _) = GameObjectTestKit.Player(1);
        player.Inventory.GuidAllocator = new ArcaneCore.Game.Items.ItemGuidAllocator();
        player.Inventory.Templates = new ArcaneCore.Game.Items.ItemTemplateStore(
            [new ArcaneCore.Kernel.Items.ItemTemplate { Entry = 90501, Class = 7, Name = "Ore", DisplayId = 1, Flags = 0x40000 | 0x20000000 }], []);
        player.Inventory.Load([]);
        Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(90501, 1, out ArcaneCore.Game.Items.Item? item));

        Assert.Equal(LootResult.NotLootable, loot.OpenItem(player, item!));
    }
}