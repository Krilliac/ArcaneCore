using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class ItemNextSwingTests
{
    private const uint Spell = 49101;
    private const uint ItemEntry = 49102;

    [Fact]
    public void ItemNextSwing_QueuesCastItemAndSettlesChargeOnlyOnSwing()
    {
        SpellInfo next = SpellTestKit.Spell(Spell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with
        {
            Attributes = (SpellAttributes)SpellAttributesCombat.OnNextSwing,
            PowerType = (int)PowerType.Rage,
            ManaCost = 100,
        };
        using var kit = new SpellTestKit(next);
        (Player player, FakeSession session) = kit.AddPlayer(1);
        Wire(player);
        Item item = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!;

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, InventorySlots.Bag0, InventorySlots.ItemStart, 0, SpellCastTargets.ForSelf()));
        Assert.Equal(3, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
        Assert.Equal(item.Guid.Value, new PacketReader(SpellTestKit.Packets(session, WorldOpcode.SmsgSpellStart).Single()).ReadPackedGuid());

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastQueuedMeleeSpell(player, player));
        Assert.Equal(2, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
        InitialSpellCooldown cooldown = Assert.Single(kit.System.GetActiveCooldowns(player));
        Assert.Equal((ItemEntry, 77u), (cooldown.ItemId, cooldown.Category));
    }

    [Fact]
    public void ItemNextSwing_CancelledQueueDoesNotConsumeCharge()
    {
        SpellInfo next = SpellTestKit.Spell(Spell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with { Attributes = (SpellAttributes)SpellAttributesCombat.OnNextSwing };
        using var kit = new SpellTestKit(next);
        (Player player, _) = kit.AddPlayer(1);
        Wire(player);
        Item item = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!;
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, InventorySlots.Bag0, InventorySlots.ItemStart, 0, SpellCastTargets.ForSelf()));
        Assert.True(kit.System.CancelQueuedMeleeSpell(player));
        Assert.Equal(3, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
        Assert.Empty(kit.System.GetActiveCooldowns(player));
    }

    [Fact]
    public void ItemNextSwing_MovedOrDeletedBeforeSwing_IsRejectedWithoutCost()
    {
        SpellInfo next = SpellTestKit.Spell(Spell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with { Attributes = (SpellAttributes)SpellAttributesCombat.OnNextSwing };
        using var kit = new SpellTestKit(next);
        (Player player, _) = kit.AddPlayer(1);
        Wire(player);
        Item item = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!;
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, InventorySlots.Bag0, InventorySlots.ItemStart, 0, SpellCastTargets.ForSelf()));
        player.Inventory.SwapItem(InventorySlots.Bag0, InventorySlots.ItemStart, InventorySlots.Bag0, (byte)(InventorySlots.ItemStart + 1));
        Assert.NotEqual(SpellCastResult.CastOk, kit.System.CastQueuedMeleeSpell(player, player));
        Assert.Equal(3, item.GetInt32(UpdateFields.ItemFieldSpellCharges));

        kit.System.CancelQueuedMeleeSpell(player);
        kit.Advance(1500);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ItemEntry, 1, out Item? second));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, second!.BagSlot, second.Slot, 0, SpellCastTargets.ForSelf()));
        player.Inventory.DestroyItemCount(second, 1);
        Assert.NotEqual(SpellCastResult.CastOk, kit.System.CastQueuedMeleeSpell(player, player));
        Assert.Empty(kit.System.GetActiveCooldowns(player));
    }

    [Fact]
    public void ItemNextSwing_TradeGuardRejectsQueuedItem()
    {
        SpellInfo next = SpellTestKit.Spell(Spell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with { Attributes = (SpellAttributes)SpellAttributesCombat.OnNextSwing };
        using var kit = new SpellTestKit(next);
        (Player player, _) = kit.AddPlayer(1);
        Wire(player);
        Item item = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!;
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, InventorySlots.Bag0, InventorySlots.ItemStart, 0, SpellCastTargets.ForSelf()));
        kit.System.ItemUseTradeGuard = (_, candidate) => ReferenceEquals(candidate, item);
        Assert.Equal(SpellCastResult.ItemNotReady, kit.System.CastQueuedMeleeSpell(player, player));
        Assert.Equal(3, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
        Assert.Empty(kit.System.GetActiveCooldowns(player));
    }

    private static void Wire(Player player)
    {
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate { Entry = ItemEntry, Class = 0, Stackable = 1,
            Spells = [new ItemSpell(Spell, 0, 3, 0, 5000, 77, 10000)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ItemEntry, 1, out _));
    }
}
