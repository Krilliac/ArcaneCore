using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class ItemCombatProcTests
{
    [Fact]
    public void ItemEnchantPpm_UsesFirstRankOverrideForHigherRank()
    {
        SpellRankChains ranks = new([
            new SkillLineAbilityRecord(1, 8, 49621, 0, 0, 0, 49622, 0, 0, 0),
            new SkillLineAbilityRecord(2, 8, 49622, 0, 0, 0, 0, 0, 0, 0)]);
        ItemEnchantmentCatalog catalog = new([], [new ItemEnchantProc(49621, 6)], ranks);
        Assert.Equal(6, catalog.PpmRate(49622));
    }

    [Fact]
    public void TemporaryCombatEnchant_UsesCatalogTargetsVictimAndConsumesOneCharge()
    {
        const uint spellId = 49501;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)));
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2, 0);
        kit.System.Random = new AlwaysRandom();
        kit.System.ItemEnchantments = new ItemEnchantmentCatalog([
            new ItemEnchantmentDefinition(49502, [new ItemEnchantmentEffect(1, spellId, 100)])]);
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate { Entry = 49503, Class = 2, SubClass = 7,
            InventoryType = 21, Delay = 1000, Stackable = 1 }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([new InventoryItemData(0, InventorySlots.MainHand, new ItemInstanceData {
            Guid = 49504, Entry = 49503, Enchantments = [0, 0, 0, 49502, 0, 2, .. new uint[15]] })]);
        Item item = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!;

        kit.System.HandleItemCombatProc(new MeleeDamageInfo { Attacker = player, Target = victim,
            AttackType = WeaponAttackType.BaseAttack, HitInfo = HitInfo.AffectsVictim,
            TargetState = VictimState.Normal, Outcome = MeleeHitOutcome.Normal });

        var reader = new PacketReader(Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgSpellGo).Payload);
        Assert.Equal(item.Guid.Value, reader.ReadPackedGuid());
        Assert.Equal(1u, item.EnchantmentCharges(1));
    }

    [Fact]
    public void TemporaryCombatEnchant_AppliesChanceOfSuccessSpellModifier()
    {
        const uint spellId = 49511;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)));
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2, 0);
        kit.System.Random = new FixedRandom(.75);
        kit.System.SpellModifiers = new DoubleChanceModifier();
        kit.System.ItemEnchantments = new ItemEnchantmentCatalog([
            new ItemEnchantmentDefinition(49512, [new ItemEnchantmentEffect(1, spellId, 50)])]);
        Item item = EquipProcItem(player, 49513);
        item.Load(new ItemInstanceData { Guid = item.Guid.Low, Entry = item.Entry,
            Enchantments = [0, 0, 0, 49512, 0, 1, .. new uint[15]] });
        kit.System.HandleItemCombatProc(new MeleeDamageInfo { Attacker = player, Target = victim,
            AttackType = WeaponAttackType.BaseAttack, HitInfo = HitInfo.AffectsVictim,
            TargetState = VictimState.Normal, Outcome = MeleeHitOutcome.Normal });
        Assert.Contains(session.Sent, packet => packet.Opcode == WorldOpcode.SmsgSpellGo);
    }

    [Fact]
    public void TriggerTwoExtraAttackProc_IsSuppressedWhileExtraAttackIsPending()
    {
        const uint spellId = 49521;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.AddExtraAttacks, 1)));
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2, 0);
        kit.System.Random = new AlwaysRandom();
        Item item = EquipProcItem(player, spellId);
        player.Combat.QueueExtraAttacks(1);

        kit.System.HandleItemCombatProc(new MeleeDamageInfo { Attacker = player, Target = victim,
            AttackType = WeaponAttackType.BaseAttack, HitInfo = HitInfo.AffectsVictim,
            TargetState = VictimState.Normal, Outcome = MeleeHitOutcome.Normal });

        Assert.DoesNotContain(session.Sent, packet => packet.Opcode == WorldOpcode.SmsgSpellGo);
        Assert.Equal(3, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
    }

    [Fact]
    public void EnchantExtraAttackProc_IsNotSuppressedByPendingExtraAttack()
    {
        const uint spellId = 49531;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.AddExtraAttacks, 1)));
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2, 0);
        kit.System.Random = new AlwaysRandom();
        Item item = EquipProcItem(player, 49532);
        kit.System.ItemEnchantments = new ItemEnchantmentCatalog([
            new ItemEnchantmentDefinition(49533, [new ItemEnchantmentEffect(1, spellId, 100)])]);
        item.Load(new ItemInstanceData { Guid = item.Guid.Low, Entry = item.Entry,
            Enchantments = [0, 0, 0, 49533, 0, 1, .. new uint[15]] });
        player.Combat.QueueExtraAttacks(1);

        kit.System.HandleItemCombatProc(new MeleeDamageInfo { Attacker = player, Target = victim,
            AttackType = WeaponAttackType.BaseAttack, HitInfo = HitInfo.AffectsVictim,
            TargetState = VictimState.Normal, Outcome = MeleeHitOutcome.Normal });

        Assert.Contains(session.Sent, packet => packet.Opcode == WorldOpcode.SmsgSpellGo);
        Assert.Equal(0u, item.EnchantmentId(1));
    }
    [Fact]
    public void SuccessfulWeaponHit_TriggersChanceOnHitSpellWithItemOwnerAndNoChargeCost()
    {
        const uint spellId = 49401;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with
        { PowerType = (int)PowerType.Rage, ManaCost = 20, ProcChance = 100 });
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2, 0);
        kit.System.Random = new AlwaysRandom();
        SpellSystem.SetPower(player, PowerType.Rage, 40);
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate { Entry = 49402, Class = 2, SubClass = 7,
            InventoryType = 21, Delay = 1000, Stackable = 1, Spells = [new ItemSpell(spellId, 2, 3, 0, 1000, 77, 1000)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(49402, 1, out Item? item));
        player.Inventory.SwapItem(item!.BagSlot, item.Slot, InventorySlots.Bag0, InventorySlots.MainHand);
        session.Clear();

        kit.System.HandleItemCombatProc(new MeleeDamageInfo { Attacker = player, Target = victim, AttackType = WeaponAttackType.BaseAttack,
            HitInfo = HitInfo.AffectsVictim, TargetState = VictimState.Normal, Outcome = MeleeHitOutcome.Normal });

        var reader = new PacketReader(Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgSpellGo).Payload);
        Assert.Equal(item.Guid.Value, reader.ReadPackedGuid());
        Assert.Equal(3, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
        Assert.Equal(40u, SpellSystem.GetPower(player, PowerType.Rage));
    }

    [Theory]
    [InlineData(MeleeHitOutcome.Miss, VictimState.Unaffected, HitInfo.None, false)]
    [InlineData(MeleeHitOutcome.Dodge, VictimState.Dodge, HitInfo.AffectsVictim, false)]
    [InlineData(MeleeHitOutcome.Parry, VictimState.Parry, HitInfo.AffectsVictim, false)]
    public void NonQualifyingWeaponResult_DoesNotTriggerOrConsume(MeleeHitOutcome outcome, VictimState state, HitInfo hitInfo, bool expected)
    {
        const uint spellId = 49403;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with { ProcChance = 100 });
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2, 0);
        kit.System.Random = new AlwaysRandom();
        Item item = EquipProcItem(player, spellId);
        session.Clear();

        kit.System.HandleItemCombatProc(new MeleeDamageInfo { Attacker = player, Target = victim, AttackType = WeaponAttackType.BaseAttack,
            HitInfo = hitInfo, TargetState = state, Outcome = outcome });

        Assert.Equal(expected, session.Sent.Any(p => p.Opcode == WorldOpcode.SmsgSpellGo));
        Assert.Equal(3, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
    }

    [Fact]
    public void DeadTargetAndGlobalCooldown_SuppressProcWithoutChangingItemOrPower()
    {
        const uint spellId = 49404;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.Energize, 20, misc: (int)PowerType.Rage)) with { ProcChance = 100 },
            SpellTestKit.Spell(49406, SpellTestKit.Effect(SpellEffectName.Dummy, 0)));
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2, 0);
        kit.System.Random = new AlwaysRandom();
        Item item = EquipProcItem(player, spellId);
        SpellSystem.SetPower(player, PowerType.Rage, 40);

        victim.Health = 0;
        kit.System.HandleItemCombatProc(new MeleeDamageInfo { Attacker = player, Target = victim, AttackType = WeaponAttackType.BaseAttack,
            HitInfo = HitInfo.AffectsVictim, TargetState = VictimState.Normal, Outcome = MeleeHitOutcome.Normal });
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgSpellGo);
        Assert.Equal(3, item.GetInt32(UpdateFields.ItemFieldSpellCharges));

        victim.Health = victim.MaxHealth;
        kit.System.CastSpell(player, 49406, SpellCastTargets.ForSelf(), triggered: false);
        session.Clear();
        kit.System.HandleItemCombatProc(new MeleeDamageInfo { Attacker = player, Target = victim, AttackType = WeaponAttackType.BaseAttack,
            HitInfo = HitInfo.AffectsVictim, TargetState = VictimState.Normal, Outcome = MeleeHitOutcome.Normal });
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgSpellGo);
        Assert.Equal(3, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
        Assert.Equal(40u, SpellSystem.GetPower(player, PowerType.Rage));
    }

    [Fact]
    public void ExplicitPpmAboveOneHundred_IsNotReplacedByOnePpmFallback()
    {
        const uint spellId = 49407;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.Dummy, 0)));
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2, 0);
        kit.System.Random = new FixedRandom(.02);
        Item item = EquipProcItem(player, spellId, ppm: 200);

        kit.System.HandleItemCombatProc(new MeleeDamageInfo { Attacker = player, Target = victim, AttackType = WeaponAttackType.BaseAttack,
            HitInfo = HitInfo.AffectsVictim, TargetState = VictimState.Normal, Outcome = MeleeHitOutcome.Normal });

        Assert.Contains(session.Sent, packet => packet.Opcode == WorldOpcode.SmsgSpellGo);
        Assert.Equal(3, item.GetInt32(UpdateFields.ItemFieldSpellCharges));
    }

    private static Item EquipProcItem(Player player, uint spellId, float ppm = 0)
    {
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate { Entry = 49405, Class = 2, SubClass = 7,
            InventoryType = 21, Delay = 1000, Stackable = 1, Spells = [new ItemSpell(spellId, 2, 3, ppm, 1000, 77, 1000)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(49405, 1, out Item? item));
        player.Inventory.SwapItem(item!.BagSlot, item.Slot, InventorySlots.Bag0, InventorySlots.MainHand);
        return item;
    }

    private sealed class AlwaysRandom : Random
    {
        public override double NextDouble() => 0;
    }

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    private sealed class DoubleChanceModifier : ISpellModifiers
    {
        public float Apply(Unit caster, SpellInfo spell, SpellModOp op, float value)
            => op == SpellModOp.ChanceOfSuccess ? value * 2 : value;
    }
}
