using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class ItemSpecialProcTests
{
    [Fact]
    public void CombatRangeEquippedSpell_ProducesTriggeredProcWithWeaponGuidAndNoChargeCost()
    {
        const uint special = 49520;
        const uint proc = 49521;
        SpellInfo specialSpell = SpellTestKit.Spell(special, SpellTestKit.Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with
        {
            EquippedItemClass = 2,
            RangeIndex = SpellConstants.RangeIndexCombat,
            Range = new SpellRange(0, 30),
        };
        using var kit = new SpellTestKit(specialSpell, SpellTestKit.Spell(proc, SpellTestKit.Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with
        { ProcChance = 100, StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2, 0);
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate { Entry = 49522, Class = 2, SubClass = 7,
            InventoryType = 21, Delay = 1000, Stackable = 1, Spells = [new ItemSpell(proc, 2, 3, 0, 1000, 77, 1000)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(49522, 1, out Item? item));
        player.Inventory.SwapItem(item!.BagSlot, item.Slot, InventorySlots.Bag0, InventorySlots.MainHand);
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, special, SpellCastTargets.ForUnit(victim.Guid), triggered: false));

        Assert.Contains(session.Sent, packet => packet.Opcode == WorldOpcode.SmsgSpellGo
            && new PacketReader(packet.Payload).ReadPackedGuid() == item.Guid.Value);
        Assert.Equal(3, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
    }

    [Fact]
    public void NonCombatRangeSpell_DoesNotProduceWeaponProc()
    {
        const uint spell = 49523;
        const uint proc = 49524;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spell, SpellTestKit.Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with
        { EquippedItemClass = 2, RangeIndex = 4, Range = new SpellRange(0, 30) },
            SpellTestKit.Spell(proc, SpellTestKit.Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with
            { ProcChance = 100, StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2, 0);
        Item item = Equip(player, proc);
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spell, SpellCastTargets.ForUnit(victim.Guid), triggered: false));
        Assert.DoesNotContain(session.Sent, packet => packet.Opcode == WorldOpcode.SmsgSpellGo
            && new PacketReader(packet.Payload).ReadPackedGuid() == item.Guid.Value);
    }

    [Fact]
    public void OffhandWeaponSpell_UsesOffhandItemProvenance()
    {
        const uint spell = 49526;
        const uint proc = 49527;
        SpellInfo special = SpellTestKit.Spell(spell, SpellTestKit.Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with
        {
            EquippedItemClass = 2, RangeIndex = SpellConstants.RangeIndexCombat, Range = new SpellRange(0, 30),
            AttributesEx3 = (uint)SpellAttributesEx3Combat.RequiresOffhandWeapon,
        };
        using var kit = new SpellTestKit(special, SpellTestKit.Spell(proc, SpellTestKit.Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with
        { ProcChance = 100, StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2, 0);
        player.StatState.SetCanDualWield(true);
        player.Inventory.Requirements = new DualWielder();
        Item item = Equip(player, proc, InventorySlots.OffHand);
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spell, SpellCastTargets.ForUnit(victim.Guid), triggered: false));
        Assert.Contains(session.Sent, packet => packet.Opcode == WorldOpcode.SmsgSpellGo
            && new PacketReader(packet.Payload).ReadPackedGuid() == item.Guid.Value);
    }

    [Fact]
    public void MissedCombatRangeSpell_StillAttemptsWeaponProc_ButLethalTargetDoesNot()
    {
        const uint spell = 49528;
        const uint proc = 49529;
        const uint lethalId = 49530;
        SpellInfo special = SpellTestKit.Spell(spell, SpellTestKit.Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with
        { EquippedItemClass = 2, RangeIndex = SpellConstants.RangeIndexCombat, Range = new SpellRange(0, 30) };
        SpellInfo lethal = SpellTestKit.Spell(lethalId, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with
        { EquippedItemClass = 2, RangeIndex = SpellConstants.RangeIndexCombat, Range = new SpellRange(0, 30) };
        using var kit = new SpellTestKit(special, lethal, SpellTestKit.Spell(proc, SpellTestKit.Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with
        { ProcChance = 100, StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2, 0);
        Item item = Equip(player, proc);
        kit.System.CombatRules = new AlwaysMissRules();
        session.Clear();
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spell, SpellCastTargets.ForUnit(victim.Guid), triggered: false));
        Assert.Contains(session.Sent, packet => packet.Opcode == WorldOpcode.SmsgSpellGo
            && new PacketReader(packet.Payload).ReadPackedGuid() == item.Guid.Value);

        kit.Advance(2000);
        victim.Health = victim.MaxHealth;
        kit.System.CombatRules = new AlwaysReflectRules();
        session.Clear();
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spell, SpellCastTargets.ForUnit(victim.Guid), triggered: false));
        Assert.DoesNotContain(session.Sent, packet => packet.Opcode == WorldOpcode.SmsgSpellGo
            && new PacketReader(packet.Payload).ReadPackedGuid() == item.Guid.Value);

        victim.Health = 1;
        kit.Advance(2000);
        kit.System.CombatRules = SpellCombatRules.Neutral;
        session.Clear();
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, lethalId, SpellCastTargets.ForUnit(victim.Guid), triggered: false));
        Assert.DoesNotContain(session.Sent, packet => packet.Opcode == WorldOpcode.SmsgSpellGo
            && new PacketReader(packet.Payload).ReadPackedGuid() == item.Guid.Value);
    }

    private static Item Equip(Player player, uint proc, byte slot = InventorySlots.MainHand)
    {
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate { Entry = 49525, Class = 2, SubClass = 7,
            InventoryType = slot == InventorySlots.OffHand ? 13u : 21u, Delay = 1000, Stackable = 1,
            Spells = [new ItemSpell(proc, 2, 3, 0, 1000, 77, 1000)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(49525, 1, out Item? item));
        player.Inventory.SwapItem(item!.BagSlot, item.Slot, InventorySlots.Bag0, slot);
        Assert.Same(item, player.Inventory.GetItem(InventorySlots.Bag0, slot));
        return item;
    }

    private sealed class DualWielder : IItemRequirements
    {
        public bool CanDualWield(PlayerInventory inventory) => true;
        public uint SkillValue(PlayerInventory inventory, uint skill) => 300;
        public bool HasSpell(PlayerInventory inventory, uint spellId) => true;
        public byte HonorRank(PlayerInventory inventory) => 0;
        public uint ReputationRank(PlayerInventory inventory, uint faction) => 3;
    }

    private class AlwaysMissRules : ISpellCombatRules
    {
        public virtual SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => SpellMissInfo.Miss;
        public bool RollCrit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => false;
        public float CritMultiplier(SpellInfo spell) => 1;
        public uint RollPartialResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage) => 0;
        public uint ApplyArmor(Unit caster, Unit target, SpellInfo spell, uint damage) => damage;
    }

    private sealed class AlwaysReflectRules : AlwaysMissRules
    {
        public override SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => SpellMissInfo.Reflect;
    }
}
