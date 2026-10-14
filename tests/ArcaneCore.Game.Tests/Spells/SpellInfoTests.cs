using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Spell formulas (vmangos SpellEntry / SpellCaster).</summary>
public sealed class SpellInfoTests
{
    [Fact]
    public void EffectValue_FixedValues_AreBasePointsPlusBaseDice()
    {
        SpellInfo spell = SpellTestKit.Spell(1, SpellTestKit.Effect(SpellEffectName.Heal, 20));
        Assert.Equal(20, SpellMath.CalculateEffectValue(spell, spell.Effects[0], 1, new Random(1)));
    }

    [Fact]
    public void EffectValue_RollsBetweenBaseDiceAndDieSides_AndScalesWithLevel()
    {
        var effect = new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, BasePoints = 9, BaseDice = 1, DieSides = 5, RealPointsPerLevel = 2 };
        SpellInfo spell = new() { Id = 1, BaseLevel = 1, SpellLevel = 1, MaxLevel = 10, Effects = [effect] };
        var random = new Random(3);
        for (int i = 0; i < 50; i++)
        {
            int value = SpellMath.CalculateEffectValue(spell, effect, 5, random); // level 5 → +8
            Assert.InRange(value, 9 + 8 + 1, 9 + 8 + 5);
        }

        // Levels above MaxLevel are clamped.
        Assert.InRange(SpellMath.CalculateEffectValue(spell, effect, 60, random), 9 + 18 + 1, 9 + 18 + 5);
    }

    [Fact]
    public void CastTime_HasAFloor_AndScalesWithCastSpeed()
    {
        SpellInfo spell = new() { Id = 1, CastTime = new SpellCastTime(3000, -100, 1500), SpellLevel = 1, Effects = [] };
        Assert.Equal(3000, spell.GetCastTime(1, 1.0f));
        Assert.Equal(1500, spell.GetCastTime(1, 0.5f));
    }

    [Fact]
    public void Duration_IsBase_AndMinusOneIsPermanent()
    {
        Assert.Equal(12000, new SpellInfo { Id = 1, Duration = new SpellDuration(12000, 0, 12000), Effects = [] }.GetDuration());
        Assert.Equal(-1, new SpellInfo { Id = 1, Duration = new SpellDuration(-1, 0, -1), Effects = [] }.GetDuration());
    }

    [Fact]
    public void Positivity_HarmfulTargetsAndDamageAreNegative()
    {
        Assert.True(SpellTestKit.Spell(1, SpellTestKit.Effect(SpellEffectName.Heal, 5)).IsPositive);
        Assert.False(SpellTestKit.Spell(2, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy)).IsPositive);
        Assert.False((SpellTestKit.Spell(3, SpellTestKit.Effect(SpellEffectName.Heal, 5)) with { Attributes = SpellAttributes.AuraIsDebuff }).IsPositive);
    }

    [Fact]
    public void PowerCost_AddsThePercentOfBaseMana_WhenSet()
    {
        SpellInfo spell = new() { Id = 1, ManaCost = 30, Effects = [] };
        Assert.Equal(30, spell.CalculatePowerCost(1, 0, 0, 60, 100, 60));
        SpellInfo percent = spell with { ManaCostPercentage = 10 };
        Assert.Equal(40, percent.CalculatePowerCost(1, 0, 0, 60, 100, 60));
    }

    [Fact]
    public void EffectValue_ScalesWithCreatureLevel_MatchesVmangosSpellCaster()
    {
        // Spell 13322 Frostbolt (Darkeye Bonecaster 1522, Defias Rogue Wizard 474): SpellLevel 20, Attributes 0x00090000 (ScalesWithCreatureLevel),
        // Effect1 SchoolDamage (BasePoints 54, BaseDice 1, DieSides 10) -> unscaled roll [55..64], scaled at level 7 by 10.3671 / 34.2057 (~0.3031) -> [16..19].
        var damage = new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, BasePoints = 54, BaseDice = 1, DieSides = 10 };
        var slow = new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModDecreaseSpeed, BasePoints = -41, BaseDice = 1, DieSides = 1 };
        SpellInfo frostbolt = new()
        {
            Id = 13322,
            SpellLevel = 20,
            Attributes = (SpellAttributes)ArcaneCore.Game.Spells.Casters.CasterAttributes.ScalesWithCreatureLevel,
            Effects = [damage, slow],
        };

        var random = new Random(7);
        for (int i = 0; i < 20; i++)
        {
            Assert.InRange(SpellMath.CalculateEffectValue(frostbolt, damage, 7, random), 16, 19);
            Assert.InRange(SpellMath.CalculateEffectValue(frostbolt, damage, 20, random), 55, 64);
        }

        // SPELL_AURA_MOD_DECREASE_SPEED is exempt from creature-level scaling (vmangos SpellCaster.cpp:1218).
        Assert.Equal(-40, SpellMath.CalculateEffectValue(frostbolt, slow, 7, random));
    }
}
