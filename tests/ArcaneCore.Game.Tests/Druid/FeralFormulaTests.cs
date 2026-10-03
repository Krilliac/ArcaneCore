using ArcaneCore.Game.Spells.Druid;
using Xunit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>vmangos StatSystem.cpp:194-296 and 354-435, Player.cpp:18271-18312, SpellAuras.cpp:1417-1433 and 4341-4363.</summary>
public class FeralFormulaTests
{
    [Fact]
    public void AttackPower_Level60_Str100_Agi200_PredatoryStrikesRank3()
    {
        // Predatory Strikes rank 3 (classic-db 16975, Dummy bp 149 -> amount 150).
        Assert.Equal(470f, FeralFormulas.AttackPower(60, 100, 200, DruidForms.Cat, 150)); // 90 + 200 + 200 - 20
        Assert.Equal(270f, FeralFormulas.AttackPower(60, 100, 200, DruidForms.Bear, 150)); // 90 + 200 - 20
        Assert.Equal(270f, FeralFormulas.AttackPower(60, 100, 200, DruidForms.DireBear, 150));
        Assert.Equal(180f, FeralFormulas.AttackPower(60, 100, 200, DruidForms.None, 150)); // 2 * str - 20
    }

    [Fact]
    public void AttackPower_PredatoryStrikesIsIgnoredOutsideCatAndBear()
    {
        Assert.Equal(180f, FeralFormulas.AttackPower(60, 100, 200, DruidForms.Moonkin, 150));
        Assert.Equal(180f, FeralFormulas.AttackPower(60, 100, 200, DruidForms.Travel, 150));
    }

    [Fact]
    public void AttackPower_WithoutPredatoryStrikes_HasNoLevelTerm()
    {
        Assert.Equal(380f, FeralFormulas.AttackPower(60, 100, 200, DruidForms.Cat, 0));
        Assert.Equal(180f, FeralFormulas.AttackPower(60, 100, 200, DruidForms.Bear, 0));
    }

    [Fact]
    public void RangedAttackPower_IsZeroInFeralFormsElseAgilityMinusTen()
    {
        Assert.Equal(0f, FeralFormulas.RangedAttackPower(200, DruidForms.Cat));
        Assert.Equal(0f, FeralFormulas.RangedAttackPower(200, DruidForms.Bear));
        Assert.Equal(0f, FeralFormulas.RangedAttackPower(200, DruidForms.DireBear));
        Assert.Equal(190f, FeralFormulas.RangedAttackPower(200, DruidForms.None));
        Assert.Equal(190f, FeralFormulas.RangedAttackPower(200, DruidForms.Moonkin));
    }

    [Fact]
    public void AttackTime_Cat1000_Bear2500_OtherwiseRegular()
    {
        Assert.Equal(1000, FeralFormulas.AttackTimeMs(DruidForms.Cat));
        Assert.Equal(2500, FeralFormulas.AttackTimeMs(DruidForms.Bear));
        Assert.Equal(2500, FeralFormulas.AttackTimeMs(DruidForms.DireBear));
        Assert.Null(FeralFormulas.AttackTimeMs(DruidForms.None));
        Assert.Null(FeralFormulas.AttackTimeMs(DruidForms.Moonkin));
    }

    [Fact]
    public void DamageRange_Cat_Level60_Ap470()
    {
        (float min, float max) = FeralFormulas.DamageRange(60, 470, FeralFormulas.AttackSpeed(1000), 0);

        Assert.Equal((470f / 14f) + (60 * 0.85f), min, 3);
        Assert.Equal((470f / 14f) + (60 * 1.25f), max, 3);
        Assert.Equal(84.571, min, 3);
        Assert.Equal(108.571, max, 3);
    }

    [Fact]
    public void DamageRange_Bear_Level60_Ap270_Speed2_5()
    {
        (float min, float max) = FeralFormulas.DamageRange(60, 270, FeralFormulas.AttackSpeed(2500), 0);

        Assert.Equal(175.714, min, 3);
        Assert.Equal(235.714, max, 3);
    }

    [Fact]
    public void DamageRange_LevelAbove60IsClampedTo60()
    {
        Assert.Equal(FeralFormulas.DamageRange(60, 470, 1f, 0), FeralFormulas.DamageRange(70, 470, 1f, 0));
    }

    [Fact]
    public void DamageRange_SecondDamageIndexContributesNothing()
    {
        Assert.Equal((0f, 0f), FeralFormulas.DamageRange(60, 470, 1f, 1));
    }

    [Fact]
    public void RipTick_AttackPowerTermCapsAtFourComboPoints()
    {
        Assert.Equal(40f, RipDamageRules.AttackPowerTerm(1000, 4));
        Assert.Equal(40f, RipDamageRules.AttackPowerTerm(1000, 5));
        Assert.Equal(20f, RipDamageRules.AttackPowerTerm(1000, 2));
        Assert.Equal(0f, RipDamageRules.AttackPowerTerm(1000, 0));
    }

    [Fact]
    public void RipTick_TermIsAddedAfterTheComboScaledBase()
    {
        // Rip rank 1 (classic-db 1079): bp 2 -> 3, EffectPointsPerComboPoint 4. Five points: 3 + 4*5 = 23.
        float comboScaled = 3 + (4 * 5);

        Assert.Equal(63f, RipDamageRules.TickAmount(comboScaled, 1000, 5));
    }

    [Fact]
    public void FrenziedRegeneration_Rank1WithPlentyOfRage_ConsumesHundredAndHealsHundred()
    {
        // Rank 1 aura amount 10 (classic-db 22842 bp 9).
        Assert.Equal((100u, 100f), FrenziedRegenerationRules.Tick(250, 10));
    }

    [Fact]
    public void FrenziedRegeneration_Rank3WithLittleRage_ConsumesAllAndHealsDouble()
    {
        // Rank 3 aura amount 20 (classic-db 22896 bp 19): 30 stored rage * 20 / 10.
        Assert.Equal((30u, 60f), FrenziedRegenerationRules.Tick(30, 20));
    }

    [Fact]
    public void FrenziedRegeneration_WithNoRage_HealsNothing()
    {
        Assert.Equal((0u, 0f), FrenziedRegenerationRules.Tick(0, 20));
    }
}
