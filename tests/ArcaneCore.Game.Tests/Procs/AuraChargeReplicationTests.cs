using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Procs;

/// <summary>
/// A visible aura's proc charges reach the client through UNIT_FIELD_AURAAPPLICATIONS (vmangos SpellAuraHolder::UpdateAuraApplication,
/// SpellAuras.cpp:7547-7560): the slot byte holds <c>charges * stacks</c> (just the stacks without charges), clamped to 255, minus one, and it is
/// rewritten on every charge change (SetAuraCharges / DropAuraCharge, SpellAuras.h:202-218), so the client's count follows each spent charge.
/// </summary>
public sealed class AuraChargeReplicationTests
{
    private const uint ThreeCharges = 993_001;
    private const uint StackingCharges = 993_002;
    private const uint ManyCharges = 993_003;
    private const uint ChargedShield = 993_004;
    private const uint FireBolt = 993_005;

    private static SpellInfo Visible(uint id, SpellEffectInfo effect) => Spell(id, effect) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellTestKit NewKit() => new(
        Visible(ThreeCharges, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModResistance, misc: 1)) with
        {
            ProcFlags = ProcFlags.TakeMeleeSwing,
            ProcChance = 100,
            ProcCharges = 3,
        },
        Visible(StackingCharges, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModResistance, misc: 1)) with
        {
            StackAmount = 3,
            ProcCharges = 2,
        },
        Visible(ManyCharges, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModResistance, misc: 1)) with
        {
            StackAmount = 2,
            ProcCharges = 200,
        },
        Visible(ChargedShield, Effect(SpellEffectName.ApplyAura, 1000, aura: AuraType.SchoolAbsorb, misc: (int)SpellSchoolMasks.All)) with
        {
            ProcCharges = 2,
        },
        Spell(FireBolt, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            School = SpellSchool.Fire,
            DamageClass = SpellDamageClass.Magic,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });

    private static SpellAuraHolder Holder(SpellTestKit kit, Unit unit, uint spell) => kit.System.GetAuras(unit).Single(h => h.Spell.Id == spell);

    private static int ApplicationsField(byte slot) => UpdateFields.UnitFieldAuraapplications + (slot / 4);

    private static byte Applications(Unit unit, byte slot) => unit.GetByte(ApplicationsField(slot), slot % 4);

    [Fact]
    public void AChargedVisibleAura_ShowsItsChargesMinusOne_AndEverySpentChargeRewritesTheField()
    {
        using SpellTestKit kit = NewKit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        combat.Random = new ScriptedRandom { DefaultInt = 9999 }; // every swing an ordinary hit
        attacker.SetFloat(UpdateFields.UnitFieldMindamage, 10);
        attacker.SetFloat(UpdateFields.UnitFieldMaxdamage, 10);
        attacker.Health = attacker.MaxHealth = 1000;
        victim.Health = victim.MaxHealth = 1000;
        RuleTestSupport.Apply(kit, victim, ThreeCharges);
        kit.Now++; // an aura does not proc from the event that applied it

        byte slot = Holder(kit, victim, ThreeCharges).Slot;
        Assert.NotEqual(SpellAuraHolder.NoSlot, slot);
        Assert.Equal(2, Applications(victim, slot)); // 3 charges * 1 stack - 1

        victim.ClearChangedFields();
        combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack);
        Assert.Equal(2, Holder(kit, victim, ThreeCharges).Charges);
        Assert.Equal(1, Applications(victim, slot));
        Assert.True(victim.ChangedFields.GetBit(ApplicationsField(slot))); // queued for the client's values update

        combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack);
        Assert.Equal(0, Applications(victim, slot));

        combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack);
        Assert.False(kit.System.HasAura(victim, ThreeCharges)); // the last charge removes it
        Assert.Equal(0u, victim.GetUInt32(UpdateFields.UnitFieldAura + slot));
    }

    [Fact]
    public void ChargesMultiplyTheStacks()
    {
        using SpellTestKit kit = NewKit();
        (Player player, _) = kit.AddPlayer(1);
        RuleTestSupport.Apply(kit, player, StackingCharges);
        byte slot = Holder(kit, player, StackingCharges).Slot;
        Assert.Equal(1, Applications(player, slot)); // 2 charges * 1 stack - 1

        RuleTestSupport.Apply(kit, player, StackingCharges);

        Assert.Equal(2, Holder(kit, player, StackingCharges).StackAmount);
        Assert.Equal(3, Applications(player, slot)); // 2 charges * 2 stacks - 1
    }

    [Fact]
    public void TheShownCount_IsClampedTo255()
    {
        using SpellTestKit kit = NewKit();
        (Player player, _) = kit.AddPlayer(1);
        RuleTestSupport.Apply(kit, player, ManyCharges);
        byte slot = Holder(kit, player, ManyCharges).Slot;
        Assert.Equal(199, Applications(player, slot)); // 200 * 1 - 1

        RuleTestSupport.Apply(kit, player, ManyCharges);

        Assert.Equal(254, Applications(player, slot)); // 400 clamps to 255, minus one
    }

    [Fact]
    public void AnAbsorbShieldsSpentCharge_RewritesTheField()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        victim.Health = victim.MaxHealth = 1000;
        RuleTestSupport.Apply(kit, victim, ChargedShield);
        byte slot = Holder(kit, victim, ChargedShield).Slot;
        Assert.Equal(1, Applications(victim, slot));

        SpellDamageResult result = kit.System.DealDirectDamage(caster, victim, kit.Store.Get(FireBolt)!, 10, allowCrit: false);

        Assert.Equal(10u, result.Absorbed);
        Assert.Equal(1, Holder(kit, victim, ChargedShield).Charges);
        Assert.Equal(0, Applications(victim, slot));
    }
}
