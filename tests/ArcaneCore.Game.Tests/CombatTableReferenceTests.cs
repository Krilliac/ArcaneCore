using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Spells.Rules;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>
/// Independent boundary vectors from vmangos SpellCaster.cpp:336-412, 446-725, 795-879,
/// 882-925, 1121-1145; Unit.cpp:1571-1601; Player.cpp:2243-2267. Reference data
/// stays in D:/refs; these cases pin the arithmetic and level transitions.
/// </summary>
public sealed class CombatTableReferenceTests
{
    [Theory]
    [InlineData(5, 0, false, 0f, 2.5f)]
    [InlineData(10, 0, false, 0f, 5f)]
    [InlineData(60, 0, false, 0f, 5f)]
    [InlineData(61, -5, false, 0f, 5.5f)]
    [InlineData(62, -10, false, 0f, 6f)]
    [InlineData(63, -15, false, 0f, 8f)]
    [InlineData(63, -15, true, 0f, 27f)]
    [InlineData(63, -15, true, 3f, 25f)]
    public void WhiteSwingMissCrossesTheTenSkillThreshold(
        byte victimLevel, int weaponMinusDefense, bool dualWield, float hitBonus, float expected)
    {
        var input = new MeleeRollInput
        {
            VictimLevel = victimLevel,
            VictimStanding = true,
            DualWield = dualWield,
            AttackType = WeaponAttackType.BaseAttack,
            HitBonus = hitBonus,
        };
        Assert.Equal(expected, MeleeHitTable.MissChance(input, weaponMinusDefense), 3);
    }

    [Theory]
    [InlineData(50, 35)]
    [InlineData(150, 135)]
    [InlineData(315, 300)]
    public void ThreeLevelCreatureCrushingRangeIsFifteenPercent(int creatureSkill, int playerDefense)
    {
        var input = new MeleeRollInput
        {
            AttackType = WeaponAttackType.BaseAttack,
            VictimIsPlayer = true,
            AttackerIsCreature = true,
            AttackerCanCrush = true,
            VictimStanding = true,
            AttackerMaxSkill = creatureSkill,
            AttackerWeaponSkill = creatureSkill,
            VictimMaxSkill = playerDefense,
            VictimDefenseSkill = playerDefense,
        };
        // Miss is 4.4% (440 slots), the 15-point skill lead adds nominal
        // 0.6% crit (59 slots after float-to-int truncation), then crushing
        // occupies 1500 slots (vmangos
        // SpellCaster.cpp:682-724; Unit.cpp:2552-2595).
        Assert.Equal(MeleeHitOutcome.Miss, MeleeHitTable.Roll(input, 439).Outcome);
        Assert.Equal(MeleeHitOutcome.Crit, MeleeHitTable.Roll(input, 440).Outcome);
        Assert.Equal(MeleeHitOutcome.Crushing, MeleeHitTable.Roll(input, 499).Outcome);
        Assert.Equal(MeleeHitOutcome.Crushing, MeleeHitTable.Roll(input, 1998).Outcome);
        Assert.Equal(MeleeHitOutcome.Normal, MeleeHitTable.Roll(input, 1999).Outcome);
    }

    [Theory]
    [InlineData(150, 150, Class.Warrior, 0.91f, 0.99f)]
    [InlineData(165, 150, Class.Warrior, 0.55f, 0.75f)]
    [InlineData(315, 300, Class.Mage, 0.01f, 0.45f)]
    public void GlancingDamageBoundsUseTheReferenceCasterPenalty(
        int defense, int weaponSkill, Class attackerClass, float expectedLow, float expectedHigh)
    {
        (float low, float high) = MeleeHitTable.GlancingRange(defense, weaponSkill, attackerClass);
        Assert.Equal(expectedLow, low, 3);
        Assert.Equal(expectedHigh, high, 3);
    }

    [Theory]
    [InlineData(-2, false, 98f)]
    [InlineData(0, false, 96f)]
    [InlineData(1, false, 95f)]
    [InlineData(2, false, 94f)]
    [InlineData(3, false, 83f)]
    [InlineData(4, false, 72f)]
    [InlineData(10, false, 22f)]
    [InlineData(3, true, 87f)]
    [InlineData(4, true, 80f)]
    [InlineData(20, true, 22f)]
    public void SpellHitLevelCurveUsesRetailPlayerAndCreatureBranches(int levelDiff, bool player, float expected)
        => Assert.Equal(expected, MagicHitChance.Base(levelDiff, player, 22f));

    [Theory]
    [InlineData(10, 0, 0f)]
    [InlineData(30, 100, 0.5f)]
    [InlineData(60, 100, 0.25f)]
    [InlineData(60, 300, 0.75f)]
    [InlineData(60, 1000, 0.75f)]
    public void SchoolResistanceScalesWithAttackerLevelAndCapsAtSeventyFive(
        int casterLevel, int resistance, float expected)
        => Assert.Equal(expected, SpellResistance.Chance(resistance, 0, casterLevel * 5, casterLevel, 0, false), 3);

    [Theory]
    [InlineData(60, 0, 0f)]
    [InlineData(60, 1, 0.0175f)]
    [InlineData(60, 3, 0.055f)]
    public void HigherCreatureInnateResistanceTruncatesBeforeConversion(
        int casterLevel, int levelDiff, float expected)
        => Assert.Equal(expected, SpellResistance.Chance(0, 0, casterLevel * 5, casterLevel, levelDiff, true), 4);

    [Theory]
    [InlineData(10, 100u, 29u)]
    [InlineData(60, 100u, 64u)]
    [InlineData(60, 1000u, 647u)]
    [InlineData(60, 1000u, 250u, 100000f)]
    public void ArmorMitigationUsesAttackerLevelAndSeventyFivePercentCap(
        int level, uint damage, uint expected, float armor = 3000f)
        => Assert.Equal(expected, MeleeHitTable.ApplyArmor(damage, armor, level));
}
