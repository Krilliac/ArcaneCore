using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters;
using ArcaneCore.Game.Spells.Casters.Bonus;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Casters;

/// <summary>The caster/target combiners of vmangos' spell power pipeline (SpellCaster.cpp:1457-1700, Unit.cpp:5175-5385).</summary>
public sealed class SpellBonusFormulaTests
{
    private static readonly EffectiveCoefficient Frostbolt = new(2850 / 3500f, 1.0f);

    [Fact]
    public void DamageDone_AddsBenefitTimesCoefficient()
        => Assert.Equal(100 + (100 * 2850 / 3500f), SpellBonusFormulas.AmountDone(100, 0, 100, Frostbolt, 1, 1.0f), 0.001f);

    [Fact]
    public void HealingDone_MultipliesThePercentAfterTheFlatBonus()
        => Assert.Equal((100 + (100 * (3000 / 3500f))) * 1.1f, SpellBonusFormulas.AmountDone(100, 0, 100, new EffectiveCoefficient(3000 / 3500f, 1.0f), 1, 1.1f), 0.001f);

    [Fact]
    public void DotSnapshot_UsesCoefficientTimesLevelPenalty()
    {
        // Renew-like: 40 + 100 * 0.2 * 0.55 = 51 per tick.
        Assert.Equal(51f, SpellBonusFormulas.AmountDone(40, 0, 100, new EffectiveCoefficient(0.2f, 0.55f), 1, 1.0f), 0.001f);
    }

    [Fact]
    public void StackCount_MultipliesTheFlatBonusNotTheBaseAmount()
        => Assert.Equal(10 + (3 * 20f), SpellBonusFormulas.AmountDone(10, 0, 20, new EffectiveCoefficient(1, 1), 3, 1.0f), 0.001f);

    [Fact]
    public void NoBenefit_LeavesTheAmountAlone_AndNegativeResultsFloorAtZero()
    {
        Assert.Equal(25f, SpellBonusFormulas.AmountDone(25, 0, 0, Frostbolt, 1, 1.0f));
        Assert.Equal(0f, SpellBonusFormulas.AmountDone(25, 0, -1000, new EffectiveCoefficient(1, 1), 1, 1.0f));
    }

    [Fact]
    public void ZeroCoefficient_DisablesTheBenefit()
        => Assert.Equal(50f, SpellBonusFormulas.AmountDone(50, 0, 500, new EffectiveCoefficient(0, 1), 1, 1.0f));

    [Fact]
    public void TakenFlatBonus_ScalesByTheCoefficient()
        => Assert.Equal(100 + (60 * 0.5f), SpellBonusFormulas.AmountTaken(100, 60, new EffectiveCoefficient(0.5f, 1.0f), 1, 1.0f), 0.001f);

    [Fact]
    public void TakenNegativeFlat_CannotRemoveMoreThanHalf()
    {
        // Dampen Magic-like -60 on an 8 damage tick: clamped to -4 -> 4.
        Assert.Equal(4f, SpellBonusFormulas.AmountTaken(8, -60, new EffectiveCoefficient(1, 1), 1, 1.0f), 0.001f);
    }

    [Fact]
    public void TakenFlat_UsesTheStackCount()
        => Assert.Equal(10 + (3 * 5f), SpellBonusFormulas.AmountTaken(10, 5, new EffectiveCoefficient(1, 1), 3, 1.0f), 0.001f);

    [Fact]
    public void HealingTakenPercent_AppliesTheMostNegativeAndMostPositiveSeparately()
    {
        Assert.Equal(0.5f * 1.2f, SpellBonusFormulas.HealingTakenPercentMultiplier(-50, 20), 0.0001f);
        Assert.Equal(1.0f, SpellBonusFormulas.HealingTakenPercentMultiplier(0, 0));
        Assert.Equal(1.2f, SpellBonusFormulas.HealingTakenPercentMultiplier(0, 20), 0.0001f);
    }

    [Fact]
    public void MultiplicativePercent_MultipliesEveryAura()
        => Assert.Equal(1.1f * 0.9f, SpellBonusFormulas.MultiplicativePercent([10, -10]), 0.0001f);

    [Fact]
    public void IgnoreAttributes_AreReadFromTheShiftedFlags()
    {
        SpellInfo plain = SpellTestKit.Spell(1, SpellTestKit.Effect(SpellEffectName.Heal, 1));
        Assert.False(SpellBonusFormulas.IgnoresCasterModifiers(plain));
        Assert.False(SpellBonusFormulas.IgnoresDamageTakenModifiers(plain));
        Assert.True(SpellBonusFormulas.IgnoresCasterModifiers(plain with { AttributesEx3 = CasterAttributes.Ex3IgnoreCasterModifiers }));
        Assert.True(SpellBonusFormulas.IgnoresDamageTakenModifiers(plain with { AttributesEx4 = CasterAttributes.Ex4IgnoreDamageTakenModifiers }));
    }
}
