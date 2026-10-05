using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class ItemConsumableTargetTests
{
    [Fact]
    public void ExplicitFriendTarget_UsesTargetHealth_WhenCasterIsFull()
    {
        const uint spellId = 991005;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitFriend)));
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2, 0);
        Item item = AddConsumable(caster, spellId, 991006);
        caster.Health = caster.GetUInt32(UpdateFields.UnitFieldMaxhealth);
        target.Health = 1;
        uint before = target.Health;

        Assert.Equal(SpellCastResult.CastOk,
            kit.System.HandleItemUse(caster, item.BagSlot, item.Slot, 0, SpellCastTargets.ForUnit(target.Guid)));
        Assert.True(target.Health > before);
        Assert.Equal(0, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
    }

    [Fact]
    public void ExplicitFriendTarget_RejectsFullTarget_EvenWhenCasterIsInjured()
    {
        const uint spellId = 991007;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitFriend)));
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2, 0);
        Item item = AddConsumable(caster, spellId, 991008);
        caster.Health = 1;
        target.Health = target.GetUInt32(UpdateFields.UnitFieldMaxhealth);
        int beforeCharges = item.GetInt32(UpdateFields.ItemFieldSpellCharges);

        Assert.Equal(SpellCastResult.AlreadyAtFullHealth,
            kit.System.HandleItemUse(caster, item.BagSlot, item.Slot, 0, SpellCastTargets.ForUnit(target.Guid)));
        Assert.Equal(beforeCharges, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
    }

    [Fact]
    public void CurrentSpell_UsesCasterHealthForSelfTarget()
    {
        const uint spellId = 991001;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.Heal, 10)));
        (Player player, _) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate
        {
            Entry = 991002, Class = (uint)ItemClass.Consumable, Stackable = 1,
            Spells = [new ItemSpell(spellId, 0, 1, 0, 0, 0, 0)],
        }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(991002, 1, out Item? item));
        player.Health = player.GetUInt32(UpdateFields.UnitFieldMaxhealth);
        Assert.Equal(SpellCastResult.AlreadyAtFullHealth,
            kit.System.HandleItemUse(player, item!.BagSlot, item.Slot, 0, SpellCastTargets.ForSelf()));
    }

    [Fact]
    public void InvalidEnergizePowerFailsWithoutOutOfRangeAccess()
    {
        const uint spellId = 991003;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.Energize, 10, misc: -1)));
        (Player player, _) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate
        {
            Entry = 991004, Class = (uint)ItemClass.Consumable, Stackable = 1,
            Spells = [new ItemSpell(spellId, 0, 1, 0, 0, 0, 0)],
        }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(991004, 1, out Item? item));
        Assert.Equal(SpellCastResult.AlreadyAtFullPower,
            kit.System.HandleItemUse(player, item!.BagSlot, item.Slot, 0, SpellCastTargets.ForSelf()));
    }

    private static Item AddConsumable(Player player, uint spellId, uint entry)
    {
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate
        {
            Entry = entry, Class = (uint)ItemClass.Consumable, Stackable = 1,
            Spells = [new ItemSpell(spellId, 0, 1, 0, 0, 0, 0)],
        }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out Item? item));
        return item!;
    }
}
