using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ScriptedRandom = ArcaneCore.Game.Tests.CombatCore.ScriptedRandom;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// vmangos SpellCaster::CalcArmorReducedDamage (SpellCaster.cpp:1121-1145): the armor a hit is reduced by is the victim's armor plus the
/// attacker's SPELL_AURA_MOD_TARGET_RESISTANCE with the physical school bit (Serrated Blades, the Bonereaver's Edge proc, Insight of the
/// Qiraji), clamped at 0. Both white swings (Unit::CalculateMeleeDamage, Unit.cpp:1373) and physical spells (SpellCaster.cpp:1284) use it.
/// </summary>
public sealed class ArmorPenetrationTests
{
    private const uint IgnoreArmor = 989_400;     // -2100 physical target resistance: the Bonereaver's Edge proc (21153) at three stacks
    private const uint IgnoreFire = 989_401;      // -2100 fire target resistance: no effect on armor
    private const uint IgnoreAllArmor = 989_402;  // -5000 physical: more than the victim has, armor clamps at 0
    private const uint PhysicalStrike = 989_403;  // a physical (school NORMAL) direct damage spell
    private const int VictimArmor = 3750;

    private static SpellTestKit Kit() => new(
        RuleTestKit.Grant(IgnoreArmor, AuraType.ModTargetResistance, -2100, misc: 1),
        RuleTestKit.Grant(IgnoreFire, AuraType.ModTargetResistance, -2100, misc: 4),
        RuleTestKit.Grant(IgnoreAllArmor, AuraType.ModTargetResistance, -5000, misc: 1),
        RuleTestKit.Magic(PhysicalStrike) with { School = SpellSchool.Normal, DamageClass = SpellDamageClass.Melee });

    private static (Player Attacker, Player Victim) Duel(SpellTestKit kit, uint? auraSpell)
    {
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2); // faces away from the attacker: no dodge, parry or block
        attacker.Level = 60;
        victim.Level = 60;
        victim.MaxHealth = 100_000; // the sink clamps Dealt to current health; keep the victim alive past every expected amount
        victim.Health = 100_000;
        victim.SetInt32(UpdateFields.UnitFieldResistances, VictimArmor);
        if (auraSpell is { } id)
        {
            RuleTestKit.Apply(kit, attacker, id);
        }

        return (attacker, victim);
    }

    private static uint WhiteCrit(uint? auraSpell)
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player victim) = Duel(kit, auraSpell);
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        attacker.SetFloat(UpdateFields.UnitFieldMindamage, 100);
        attacker.SetFloat(UpdateFields.UnitFieldMaxdamage, 100);
        attacker.SetFloat(UpdateFields.PlayerCritPercentage, 100f);
        var random = new ScriptedRandom();
        random.Ints.Enqueue(5000); // past the 5% miss range, inside the 100% crit range
        combat.Random = random;

        MeleeDamageInfo hit = combat.CalculateMeleeDamage(attacker, victim, WeaponAttackType.BaseAttack);
        Assert.Equal(MeleeHitOutcome.Crit, hit.Outcome);
        return hit.TotalDamage;
    }

    private static uint PhysicalSpell(uint? auraSpell)
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player victim) = Duel(kit, auraSpell);
        kit.System.CombatRules = new VanillaSpellCombatRules();

        return kit.System.DealDirectDamage(attacker, victim, kit.Store.Get(PhysicalStrike)!, 100, allowCrit: false).Dealt;
    }

    [Fact]
    public void AWhiteSwing_WithoutPenetration_IsReducedByTheFullArmor()
    {
        // 3750 armor at level 60: 375 / 550 = 0.682 -> 0.405 reduction; 100 -> 59, crit x2 = 118.
        Assert.Equal(118u, WhiteCrit(null));
    }

    [Fact]
    public void AWhiteSwing_IgnoresTheArmorOfTheAttackersPhysicalTargetResistance()
    {
        // 3750 - 2100 = 1650 armor: 165 / 550 = 0.3 -> 0.231 reduction; 100 -> 76, crit x2 = 152.
        Assert.Equal(152u, WhiteCrit(IgnoreArmor));
    }

    [Fact]
    public void AWhiteSwing_IgnoresTargetResistanceOfAnotherSchool()
    {
        Assert.Equal(118u, WhiteCrit(IgnoreFire));
    }

    [Fact]
    public void AWhiteSwing_ClampsPenetrationPastTheVictimsArmorAtZeroArmor()
    {
        Assert.Equal(200u, WhiteCrit(IgnoreAllArmor));
    }

    [Fact]
    public void APhysicalSpell_IgnoresTheArmorOfTheAttackersPhysicalTargetResistance()
    {
        Assert.Equal(59u, PhysicalSpell(null));
        Assert.Equal(76u, PhysicalSpell(IgnoreArmor));
        Assert.Equal(59u, PhysicalSpell(IgnoreFire));
        Assert.Equal(100u, PhysicalSpell(IgnoreAllArmor));
    }
}
