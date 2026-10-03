using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// UNIT_FIELD_RESISTANCES and UNIT_FIELD_ATTACK_POWER_MODS are signed in vmangos (SetArmor takes an int32,
/// Unit.h:377; the AP mods are two int16 halves, Unit::GetTotalAttackPowerValue, Unit.cpp:8037-8060), and aura
/// handlers now write negative armor and negative attack power (Sunder Armor, Demoralizing Shout). The readers
/// must treat them as signed.
/// </summary>
public sealed class SignedStatReaderTests
{
    [Fact]
    public void NegativeArmor_CountsAsZeroArmor_ForSpellPhysicalDamage()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        var rules = new VanillaSpellCombatRules();
        SpellInfo physical = Spell(930001, Effect(SpellEffectName.SchoolDamage, 10)) with { School = SpellSchool.Normal };

        target.SetInt32(UpdateFields.UnitFieldResistances, -50);

        // vmangos Unit::CalcArmorReducedDamage: armor below zero is zero, so 100 damage stays 100.
        Assert.Equal(100u, rules.ApplyArmor(caster, target, physical, 100));
    }

    [Fact]
    public void NegativeArmor_CountsAsZeroArmor_ForMeleeSwings()
    {
        (WorldRuntime world, Map map, ScriptedRandom random, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetFloat(UpdateFields.UnitFieldMindamage, 50);
        player.SetFloat(UpdateFields.UnitFieldMaxdamage, 50);
        var mob = new CombatTestUnit();
        mob.Spawn(map, 2, 0, orientation: MathF.PI);

        random.Ints.Enqueue(5000);
        MeleeDamageInfo unarmored = map.Combat.CalculateMeleeDamage(player, mob, WeaponAttackType.BaseAttack);
        Assert.Equal(MeleeHitOutcome.Normal, unarmored.Outcome);

        mob.SetInt32(UpdateFields.UnitFieldResistances, -3000);
        random.Ints.Enqueue(5000);
        MeleeDamageInfo negative = map.Combat.CalculateMeleeDamage(player, mob, WeaponAttackType.BaseAttack);

        Assert.Equal(unarmored.TotalDamage, negative.TotalDamage);
        Assert.Equal(0u, negative.CleanDamage);
    }

    [Fact]
    public void NegativeAttackPowerMods_ReduceTheNormalizedWeaponRoll()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.SetFloat(UpdateFields.UnitFieldMindamage, 10);
        caster.SetFloat(UpdateFields.UnitFieldMaxdamage, 10);
        caster.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2000);
        caster.SetInt32(UpdateFields.UnitFieldAttackPower, 140);

        // Normalized speed 2.4 vs 2.0: (2.4 - 2.0) * AP / 14 on top of the 10 roll (SpellSystemTests pins 14 for AP 140).
        Assert.Equal(14f, kit.System.WeaponDamageRoll(caster, WeaponAttackType.BaseAttack, normalized: true), 3);

        // Positive half +70, negative half -70 (vmangos adds both halves to the AP: 140 + 70 - 70).
        caster.SetUInt32(UpdateFields.UnitFieldAttackPowerMods, unchecked((ushort)(short)70) | ((uint)unchecked((ushort)(short)-70) << 16));
        Assert.Equal(14f, kit.System.WeaponDamageRoll(caster, WeaponAttackType.BaseAttack, normalized: true), 3);

        // Only a negative half of -70: AP 70 -> 10 + 0.4 * 70 / 14 = 12.
        caster.SetUInt32(UpdateFields.UnitFieldAttackPowerMods, (uint)unchecked((ushort)(short)-70) << 16);
        Assert.Equal(12f, kit.System.WeaponDamageRoll(caster, WeaponAttackType.BaseAttack, normalized: true), 3);
    }
}
