using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// The aura crit suppression against creatures three or more levels above a player (<c>Combat:AuraCritSuppressionPercent</c>, default 1.8).
/// Blizzard confirmed that crit from auras (Berserker Stance, Cruelty, crit on gear) is cut against +3 creatures, separately from the 3%
/// weapon-skill term, "as they did in 1.12" (Aggrend, 2019-07-10, https://us.forums.blizzard.com/en/wow/t/bug-berserker-stance/196414
/// posts 9 and 11); the size is the Classic combat-log estimate, about 72,000 auto attacks giving 4.84% total suppression
/// (https://github.com/magey/classic-warrior/wiki/Crit-aura-suppression), and aura crit of 1% was suppressed entirely.
/// </summary>
public sealed class AuraCritSuppressionTests
{
    private const uint Crit4 = 989_420;       // MOD_CRIT_PERCENT +4 (a crit-on-gear or talent aura)
    private const uint Crit1 = 989_421;       // MOD_CRIT_PERCENT +1
    private const uint MeleeStrike = 989_422; // a melee class spell (Sinister Strike-like)

    /// <summary>A level-60 player (weapon skill 300) hitting a creature of <paramref name="mobLevel"/> from the front.</summary>
    private static MeleeRollInput PlayerVsMob(byte mobLevel) => new()
    {
        AttackType = WeaponAttackType.BaseAttack,
        AttackerIsPlayerControlled = true,
        VictimStanding = true,
        VictimLevel = mobLevel,
        AttackerMaxSkill = 300,
        VictimMaxSkill = mobLevel * 5,
        AttackerWeaponSkill = 300,
        VictimDefenseSkill = mobLevel * 5,
    };

    private static SpellTestKit Kit() => new(
        RuleTestKit.Grant(Crit4, AuraType.ModCritPercent, 4),
        RuleTestKit.Grant(Crit1, AuraType.ModCritPercent, 1),
        SpellTestKit.Spell(MeleeStrike, SpellTestKit.Effect(SpellEffectName.WeaponDamage, 10, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Melee });

    private static (SpellTestKit Kit, Player Attacker, CombatTestUnit Victim, MapCombat Combat) Scene(byte victimLevel, bool worldBoss = false)
    {
        SpellTestKit kit = Kit();
        (Player attacker, _) = kit.AddPlayer(1);
        attacker.Level = 60;
        var victim = new CombatTestUnit(victimLevel) { IsWorldBoss = worldBoss };
        victim.Spawn(attacker.Map!, 2, 0, orientation: MathF.PI);
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        return (kit, attacker, victim, combat);
    }

    [Fact]
    public void CritChance_AgainstAPlusThreeMob_LosesTheSuppressionFromAuraCrit_OnTopOfTheSkillTerm()
    {
        // 10% sheet crit of which 4% from auras; 300 skill against 315 defense: 10 - 1.8 - 15 x 0.2 = 5.2 (4.8% total, the Classic figure).
        MeleeRollInput input = PlayerVsMob(63) with { BaseCritChance = 10f, AttackerAuraCritChance = 4f, AuraCritSuppression = 1.8f };

        Assert.Equal(5.2f, MeleeHitTable.CritChance(input), 3);
        Assert.Equal(7f, MeleeHitTable.CritChance(input with { AuraCritSuppression = 0f }), 3); // the skill term alone (vmangos)
    }

    [Fact]
    public void CritChance_NeverSuppressesMoreThanTheAuraCrit()
    {
        MeleeRollInput input = PlayerVsMob(63) with { BaseCritChance = 10f, AuraCritSuppression = 1.8f };

        Assert.Equal(7f, MeleeHitTable.CritChance(input), 3);                                         // no aura crit: untouched
        Assert.Equal(6f, MeleeHitTable.CritChance(input with { AttackerAuraCritChance = 1f }), 3);    // 1% aura crit: all of it
    }

    [Theory]
    [InlineData(true, false, 60, 63, 1.8f, 1.8f)]
    [InlineData(true, false, 60, 64, 1.8f, 1.8f)]
    [InlineData(true, false, 60, 62, 1.8f, 0f)]  // +2: no aura cut
    [InlineData(true, true, 57, 60, 1.8f, 0f)]   // player victim: not covered by the evidence
    [InlineData(false, false, 60, 63, 1.8f, 0f)] // creature or pet attacker
    [InlineData(true, false, 60, 63, 0f, 0f)]    // option off
    [InlineData(true, false, 60, 63, 3f, 3f)]    // tuned
    public void AuraCritSuppressionFor_AppliesToAPlayerAgainstACreatureThreeLevelsAbove(
        bool attackerIsPlayer, bool victimIsPlayer, int attackerLevel, int victimLevel, float option, float expected)
        => Assert.Equal(expected, MeleeHitTable.AuraCritSuppressionFor(attackerIsPlayer, victimIsPlayer, attackerLevel, victimLevel, option));

    [Fact]
    public void BuildRollInput_AgainstAWorldBoss_CutsTheAttackersAuraCrit()
    {
        (SpellTestKit kit, Player attacker, CombatTestUnit boss, MapCombat combat) = Scene(60, worldBoss: true);
        using SpellTestKit _ = kit;
        attacker.SetFloat(UpdateFields.PlayerCritPercentage, 10f);
        RuleTestKit.Apply(kit, attacker, Crit4);

        MeleeRollInput input = combat.BuildRollInput(attacker, boss, WeaponAttackType.BaseAttack);

        Assert.Equal((4f, 1.8f), (input.AttackerAuraCritChance, input.AuraCritSuppression));
        Assert.Equal(5.2f, MeleeHitTable.CritChance(input), 3); // 10 - 1.8 - 3
    }

    [Fact]
    public void BuildRollInput_AgainstAPlusTwoMob_LeavesTheAuraCrit()
    {
        (SpellTestKit kit, Player attacker, CombatTestUnit mob, MapCombat combat) = Scene(62);
        using SpellTestKit _ = kit;
        attacker.SetFloat(UpdateFields.PlayerCritPercentage, 10f);
        RuleTestKit.Apply(kit, attacker, Crit4);

        MeleeRollInput input = combat.BuildRollInput(attacker, mob, WeaponAttackType.BaseAttack);

        Assert.Equal(0f, input.AuraCritSuppression);
        Assert.Equal(8f, MeleeHitTable.CritChance(input), 3); // 10 - 10 x 0.2
    }

    [Fact]
    public void BuildRollInput_AnAuraCritBelowTheCut_IsSuppressedEntirely()
    {
        (SpellTestKit kit, Player attacker, CombatTestUnit mob, MapCombat combat) = Scene(63);
        using SpellTestKit _ = kit;
        attacker.SetFloat(UpdateFields.PlayerCritPercentage, 10f);
        RuleTestKit.Apply(kit, attacker, Crit1);

        Assert.Equal(6f, MeleeHitTable.CritChance(combat.BuildRollInput(attacker, mob, WeaponAttackType.BaseAttack)), 3); // 10 - 1 - 3
    }

    [Fact]
    public void TheMeleeSpellCritChance_AgainstAPlusThreeMob_IsSuppressedToo()
    {
        (SpellTestKit kit, Player attacker, CombatTestUnit mob, _) = Scene(63);
        using SpellTestKit owned = kit;
        attacker.SetFloat(UpdateFields.PlayerCritPercentage, 10f);
        RuleTestKit.Apply(kit, attacker, Crit4);
        var rules = new VanillaSpellCombatRules();

        Assert.Equal(5.2f, rules.CritChance(kit.System, attacker, mob, kit.Store.Get(MeleeStrike)!), 3);
    }
}
