using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// vmangos Unit::CalculateMeleeDamage, MELEE_HIT_CRIT (Unit.cpp:1425-1436): a white crit deals
/// <c>(200 + SPELL_AURA_MOD_CRIT_PERCENT_VERSUS for the victim's creature type) / 100</c> of the hit.
/// </summary>
public sealed class MeleeCritVersusTests
{
    private const uint HumanoidCritVersus = 989_300; // +6 percent crit damage against humanoids (creature type 7, mask 64)
    private const uint BeastCritVersus = 989_301;    // the same against beasts (mask 1)

    private static MeleeDamageInfo Crit(uint auraSpell)
    {
        using var kit = new SpellTestKit(
            RuleTestSupport.Grant(HumanoidCritVersus, AuraType.ModCritPercentVersus, 6, misc: 64),
            RuleTestSupport.Grant(BeastCritVersus, AuraType.ModCritPercentVersus, 6, misc: 1));
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2); // faces away from the attacker: no dodge, parry or block
        attacker.Level = 60;
        victim.Level = 60;
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        attacker.SetFloat(UpdateFields.UnitFieldMindamage, 100);
        attacker.SetFloat(UpdateFields.UnitFieldMaxdamage, 100);
        attacker.SetFloat(UpdateFields.PlayerCritPercentage, 100f);
        RuleTestSupport.Apply(kit, attacker, auraSpell);
        var random = new ScriptedRandom();
        random.Ints.Enqueue(5000); // past the 5% miss range, inside the 100% crit range
        combat.Random = random;

        return combat.CalculateMeleeDamage(attacker, victim, WeaponAttackType.BaseAttack);
    }

    [Fact]
    public void AWhiteCrit_AddsTheCritVersusBonusForTheVictimsCreatureType()
    {
        MeleeDamageInfo hit = Crit(HumanoidCritVersus);

        Assert.Equal(MeleeHitOutcome.Crit, hit.Outcome);
        Assert.Equal(206u, hit.TotalDamage); // 100 x (200 + 6) / 100
    }

    [Fact]
    public void AWhiteCrit_IgnoresACritVersusBonusForAnotherCreatureType()
    {
        MeleeDamageInfo hit = Crit(BeastCritVersus);

        Assert.Equal(MeleeHitOutcome.Crit, hit.Outcome);
        Assert.Equal(200u, hit.TotalDamage);
    }
}
