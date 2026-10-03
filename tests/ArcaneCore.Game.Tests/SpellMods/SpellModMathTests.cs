using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Mods;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellMods;

/// <summary>
/// The modifier formula against vmangos Player::ApplySpellMod (Player.cpp:22417-22466) and the mask rule against
/// SpellModifier::IsAffectedOnSpell (SpellModifier.cpp:34-41). Every number is worked out by hand from the C++ and pinned.
/// </summary>
public sealed class SpellModMathTests
{
    private const uint Family = 3;
    private const uint Ex3IgnoreCasterModifiers = 0x20000000;

    private static SpellInfo Spell(ulong flags = 1, uint family = Family, uint ex3 = 0) =>
        new() { Id = 1, SpellFamilyName = family, SpellFamilyFlags = flags, AttributesEx3 = ex3 };

    private static SpellMod Mod(SpellModOp op, SpellModType type, int value, ulong mask = 1, uint family = Family, uint spellId = 900) =>
        new(op, type, value, mask, family, spellId, 0);

    private static float Eval(SpellModOp op, float value, SpellInfo? spell = null, params SpellMod[] mods) =>
        SpellModMath.Evaluate(mods, spell ?? Spell(), op, value);

    [Fact]
    public void FlatThenPercent_OnTheFlatAdjustedValue()
    {
        // diff = (100 + 10) * -25 / 100 + 10 = -17.5; 100 + diff = 82.5.
        float result = Eval(SpellModOp.Damage, 100, null,
            Mod(SpellModOp.Damage, SpellModType.Flat, 10), Mod(SpellModOp.Damage, SpellModType.Pct, -25));

        Assert.Equal(82.5f, result);
    }

    [Fact]
    public void ModsOfSeveralAuras_AddUpPerType()
    {
        // flat 3 + 4 = 7, pct 10 + 20 = 30: diff = (50 + 7) * 30 / 100 + 7 = 24.1.
        float result = Eval(SpellModOp.Damage, 50, null,
            Mod(SpellModOp.Damage, SpellModType.Flat, 3), Mod(SpellModOp.Damage, SpellModType.Flat, 4),
            Mod(SpellModOp.Damage, SpellModType.Pct, 10), Mod(SpellModOp.Damage, SpellModType.Pct, 20));

        Assert.Equal(50f + (57f * 30f / 100.0f + 7f), result);
    }

    [Theory]
    [InlineData(100, 10, -25, 82)]    // 82.5 truncates toward zero
    [InlineData(5, 0, 10, 5)]          // 5.5 -> 5
    [InlineData(5, 0, -10, 4)]         // 4.5 -> 4
    [InlineData(-5, 0, 10, -5)]        // base -5: -5 + (-5 * 10 / 100) = -5.5 -> -5 (toward zero, not floor)
    public void IntegerBases_TruncateTowardZero(int baseValue, int flat, int pct, int expected)
    {
        var mods = new List<SpellMod>();
        if (flat != 0)
        {
            mods.Add(Mod(SpellModOp.Damage, SpellModType.Flat, flat));
        }

        mods.Add(Mod(SpellModOp.Damage, SpellModType.Pct, pct));

        float result = SpellModMath.Evaluate(mods, Spell(), SpellModOp.Damage, baseValue);
        Assert.Equal(expected, (int)result);
    }

    [Fact]
    public void APercentModOnAZeroBase_ContributesNothing_ButFlatStillAdds()
    {
        Assert.Equal(0f, Eval(SpellModOp.Cost, 0, null, Mod(SpellModOp.Cost, SpellModType.Pct, -50)));
        Assert.Equal(3f, Eval(SpellModOp.Cost, 0, null, Mod(SpellModOp.Cost, SpellModType.Flat, 3), Mod(SpellModOp.Cost, SpellModType.Pct, 50)));
    }

    [Fact]
    public void PercentBelowMinusHundred_IsNotClamped_LikeVmangos()
    {
        // Three -50% mods: totalpct = -150, diff = 100 * -150 / 100 = -150, result -50.
        float result = Eval(SpellModOp.Cost, 100, null,
            Mod(SpellModOp.Cost, SpellModType.Pct, -50), Mod(SpellModOp.Cost, SpellModType.Pct, -50), Mod(SpellModOp.Cost, SpellModType.Pct, -50));

        Assert.Equal(-50f, result);
    }

    [Fact]
    public void CastingTime_PercentMinusHundred_ForcesInstant_AndDiscardsFlat()
    {
        // Barkskin (+1 s flat) with Nature's Swiftness (-100%): 0, not 1000 (Player.cpp:22455-22460).
        Assert.Equal(0f, Eval(SpellModOp.CastingTime, 2500, null,
            Mod(SpellModOp.CastingTime, SpellModType.Flat, 1000), Mod(SpellModOp.CastingTime, SpellModType.Pct, -100)));
    }

    [Fact]
    public void CastingTime_MinusHundredPercent_StopsReadingLaterMods()
    {
        // The -100% mod breaks the loop: a flat +500 listed after it is never summed.
        Assert.Equal(0f, Eval(SpellModOp.CastingTime, 2500, null,
            Mod(SpellModOp.CastingTime, SpellModType.Pct, -100), Mod(SpellModOp.CastingTime, SpellModType.Flat, 500)));
    }

    [Fact]
    public void CastingTime_OfTenSecondsOrMore_SkipsTheInstantPercentMod()
    {
        Assert.Equal(12000f, Eval(SpellModOp.CastingTime, 12000, null, Mod(SpellModOp.CastingTime, SpellModType.Pct, -100)));
        Assert.Equal(0f, Eval(SpellModOp.CastingTime, 9999, null, Mod(SpellModOp.CastingTime, SpellModType.Pct, -100)));
    }

    [Fact]
    public void AMinusHundredPercent_OnAnotherOp_IsAnOrdinaryPercent()
    {
        Assert.Equal(0f, Eval(SpellModOp.Cost, 12000, null, Mod(SpellModOp.Cost, SpellModType.Pct, -100)));
    }

    [Fact]
    public void ASpellThatIgnoresCasterModifiers_IsReturnedUnchanged()
    {
        Assert.Equal(100f, Eval(SpellModOp.Damage, 100, Spell(ex3: Ex3IgnoreCasterModifiers), Mod(SpellModOp.Damage, SpellModType.Flat, 50)));
    }

    [Fact]
    public void Mask_NeedsTheSameFamily_AndASharedBit()
    {
        SpellMod mod = Mod(SpellModOp.Damage, SpellModType.Flat, 5, mask: 0b0110);

        Assert.True(mod.IsAffectedOnSpell(Spell(0b0010)));
        Assert.True(mod.IsAffectedOnSpell(Spell(0b1110)));
        Assert.False(mod.IsAffectedOnSpell(Spell(0b1001)));            // no shared bit
        Assert.False(mod.IsAffectedOnSpell(Spell(0b0010, family: 4)));  // wrong family
    }

    [Fact]
    public void AZeroMask_AffectsNothing()
    {
        Assert.False(Mod(SpellModOp.Damage, SpellModType.Flat, 5, mask: 0).IsAffectedOnSpell(Spell(ulong.MaxValue)));
    }

    [Fact]
    public void MaskBitsAboveThirtyOne_Match()
    {
        const ulong Bit40 = 1UL << 40;

        Assert.True(Mod(SpellModOp.Damage, SpellModType.Flat, 5, mask: Bit40).IsAffectedOnSpell(Spell(Bit40 | 1)));
        Assert.False(Mod(SpellModOp.Damage, SpellModType.Flat, 5, mask: Bit40).IsAffectedOnSpell(Spell(0xFFFFFFFF)));
        Assert.True(Mod(SpellModOp.Damage, SpellModType.Flat, 5, mask: 1UL << 63).IsAffectedOnSpell(Spell(1UL << 63)));
    }

    [Fact]
    public void ModsThatDoNotAffectTheSpell_AreIgnored()
    {
        float result = Eval(SpellModOp.Damage, 100, Spell(0b0001),
            Mod(SpellModOp.Damage, SpellModType.Flat, 50, mask: 0b0010), Mod(SpellModOp.Damage, SpellModType.Pct, 50, family: 9));

        Assert.Equal(100f, result);
    }
}
