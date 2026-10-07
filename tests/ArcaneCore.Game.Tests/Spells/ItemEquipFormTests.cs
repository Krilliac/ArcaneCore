using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class ItemEquipFormTests
{
    [Fact]
    public void FormReconcile_RemovesIncompatibleAndRestoresCompatibleItemAura()
    {
        const uint spellId = 49301;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)) with
        {
            Stances = 1u << ((int)ShapeshiftForm.Cat - 1),
        });
        (Player player, _) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate { Entry = 49302, Class = 4, InventoryType = 12,
            Stackable = 1, Spells = [new ItemSpell(spellId, 1, 0, 0, 0, 0, 0)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        var equip = new ItemEquipSpells(kit.System);
        equip.Attach(player);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(49302, 1, out Item? item));
        player.Inventory.EquipItem(InventorySlots.Trinket1, item!);
        Assert.False(kit.System.HasAura(player, spellId));
        player.SetByte(UpdateFields.UnitFieldBytes1, 2, (byte)ShapeshiftForm.Cat);
        equip.ReconcileAtFormChange(player);
        Assert.True(kit.System.HasAura(player, spellId));
        player.SetByte(UpdateFields.UnitFieldBytes1, 2, 0);
        equip.ReconcileAtFormChange(player);
        Assert.False(kit.System.HasAura(player, spellId));
    }
}
