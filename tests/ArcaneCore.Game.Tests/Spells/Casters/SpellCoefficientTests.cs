using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters.Bonus;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Casters;

/// <summary>
/// Spell power coefficient maths. Oracles are hand-derived from the vmangos formulas
/// (SpellEntry.cpp:517-634, 756-777; SpellCaster.cpp:1805-1812) and cross-checked against the
/// explicit coefficients the vmangos world database stores for the same spells
/// (sql/old_migrations/20200515174239_world.sql: Renew 139 = 0.11, Shadow Bolt 686 = 0.14,
/// Flash Heal 2061 = 0.429, Greater Heal 2060 = 0.857); no vmangos data is copied.
/// </summary>
public sealed class SpellCoefficientTests
{
    private const float Tolerance = 0.0005f;

    private static SpellInfo Direct(int castMs, uint level = 0, SpellEffectName effect = SpellEffectName.SchoolDamage,
        SpellImplicitTarget target = SpellImplicitTarget.UnitEnemy, params SpellEffectInfo[] more) => SpellTestKit.Spell(
            1000, [SpellTestKit.Effect(effect, 10, target), .. more]) with
        {
            CastTime = new SpellCastTime(castMs, 0, 0),
            SpellLevel = level,
        };

    [Theory]
    [InlineData(3500, 3500u)]
    [InlineData(3000, 3000u)]
    [InlineData(1000, 1500u)]
    [InlineData(0, 1500u)]
    [InlineData(9000, 7000u)]
    public void DirectCastTime_IsClampedBetween1500And7000(int castMs, uint expected)
        => Assert.Equal(expected, SpellCoefficients.CastTimeForBonus(Direct(castMs), SpellBonusKind.SpellDirect));

    [Fact]
    public void InstantDirectSpell_GetsTheMinimumCoefficient()
        => Assert.Equal(1500 / 3500f, SpellCoefficients.DefaultCoefficient(Direct(0), SpellBonusKind.SpellDirect), Tolerance);

    [Fact]
    public void FrostboltLike_SlowEffectCostsFivePercent()
    {
        // 3000 ms + one MOD_DECREASE_SPEED effect: uint32(3000 * 0.95f) = 2850 -> 2850/3500.
        SpellInfo spell = Direct(3000, more: SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModDecreaseSpeed));
        Assert.Equal(2850u, SpellCoefficients.CastTimeForBonus(spell, SpellBonusKind.SpellDirect));
        Assert.Equal(0.8143f, SpellCoefficients.DefaultCoefficient(spell, SpellBonusKind.SpellDirect), Tolerance);
    }

    [Fact]
    public void StunLikeEffect_CostsTwoSteps_EachTruncatedToWholeMilliseconds()
    {
        SpellInfo spell = Direct(3000, more: SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModStun));
        Assert.Equal(2707u, SpellCoefficients.CastTimeForBonus(spell, SpellBonusKind.SpellDirect));
    }

    [Fact]
    public void AreaSpell_ReceivesHalfTheBonus()
    {
        SpellInfo spell = Direct(3000, target: SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc);
        Assert.Equal(1500u, SpellCoefficients.CastTimeForBonus(spell, SpellBonusKind.SpellDirect));
    }

    [Fact]
    public void HealthLeech_HalvesButPowerDrainDoesNot()
    {
        Assert.Equal(1500u, SpellCoefficients.CastTimeForBonus(Direct(3000, effect: SpellEffectName.HealthLeech), SpellBonusKind.SpellDirect));
        Assert.Equal(3000u, SpellCoefficients.CastTimeForBonus(Direct(3000, effect: SpellEffectName.PowerDrain), SpellBonusKind.SpellDirect));
    }

    [Fact]
    public void FireballLike_CombinedSpell_SplitsBetweenDirectAndOverTime()
    {
        // 1500 ms cast + 8 s DoT (amplitude 2 s): PtOT = (8000/15000) / (8000/15000 + 1500/3500) = 0.5544555.
        SpellInfo spell = SpellTestKit.Spell(
            1001,
            SpellTestKit.Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy),
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 2, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 2000)) with
        {
            CastTime = new SpellCastTime(1500, 0, 0),
            Duration = new SpellDuration(8000, 0, 8000),
        };

        Assert.Equal(668u, SpellCoefficients.CastTimeForBonus(spell, SpellBonusKind.SpellDirect));
        Assert.Equal(1940u, SpellCoefficients.CastTimeForBonus(spell, SpellBonusKind.OverTime));
    }

    [Fact]
    public void RenewLike_PerTickCoefficientIs0Point2_AndWithLevelPenalty0Point11()
    {
        SpellInfo renew = SpellTestKit.Spell(
            139, SpellTestKit.Effect(SpellEffectName.ApplyAura, 40, aura: AuraType.PeriodicHeal, amplitude: 3000)) with
        {
            Duration = new SpellDuration(15000, 0, 15000),
            SpellLevel = 8,
        };

        Assert.Equal(0.2f, SpellCoefficients.DefaultCoefficient(renew, SpellBonusKind.OverTime), Tolerance);
        Assert.Equal(0.55f, SpellCoefficients.LevelPenalty(renew), Tolerance);
        Assert.Equal(0.11f, SpellCoefficients.Resolve(renew, SpellBonusKind.OverTime).Factor, Tolerance);
    }

    [Fact]
    public void ShadowBoltR1_MatchesTheStoredCoefficient()
    {
        SpellInfo spell = Direct(1700, level: 1);
        Assert.Equal(0.14f, SpellCoefficients.Resolve(spell, SpellBonusKind.SpellDirect).Factor, 0.001f);
    }

    [Fact]
    public void HealRanks_MatchTheStoredCoefficients()
    {
        Assert.Equal(0.857f, SpellCoefficients.Resolve(Direct(3000, level: 40, effect: SpellEffectName.Heal, target: SpellImplicitTarget.UnitFriend), SpellBonusKind.SpellDirect).Factor, Tolerance);
        Assert.Equal(0.429f, SpellCoefficients.Resolve(Direct(1500, level: 20, effect: SpellEffectName.Heal, target: SpellImplicitTarget.UnitFriend), SpellBonusKind.SpellDirect).Factor, Tolerance);
    }

    [Fact]
    public void ChannelledDamageOverTime_UsesTheDurationAndNoFifteenSecondFactor()
    {
        SpellInfo mindFlay = SpellTestKit.Spell(
            1002, SpellTestKit.Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 1000)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration(3000, 0, 3000),
        };

        Assert.Equal(3000u, SpellCoefficients.CastTimeForBonus(mindFlay, SpellBonusKind.OverTime));
        Assert.Equal(3000 / 3500f / 3, SpellCoefficients.DefaultCoefficient(mindFlay, SpellBonusKind.OverTime), Tolerance);
    }

    [Theory]
    [InlineData(1u, 0.2875f)]
    [InlineData(6u, 0.475f)]
    [InlineData(8u, 0.55f)]
    [InlineData(20u, 1.0f)]
    [InlineData(21u, 1.0f)]
    [InlineData(0u, 1.0f)]
    public void LevelPenalty_FollowsTheNostalriusFormula(uint level, float expected)
        => Assert.Equal(expected, SpellCoefficients.LevelPenalty(Direct(1500, level)), 0.00001f);

    [Fact]
    public void ExplicitCoefficient_SkipsTheLevelPenalty_AndZeroDisablesTheBonus()
    {
        SpellInfo spell = Direct(1500, level: 1);
        EffectiveCoefficient fireball = SpellCoefficients.Resolve(spell, SpellBonusKind.SpellDirect, 0.123f);
        Assert.Equal(0.123f, fireball.Factor, 0.00001f);
        Assert.Equal(1.0f, fireball.LevelPenalty);
        Assert.Equal(0f, SpellCoefficients.Resolve(spell, SpellBonusKind.SpellDirect, 0f).Factor);

        // A negative value means "use the formula" (vmangos stores -1).
        Assert.Equal(1500 / 3500f * 0.2875f, SpellCoefficients.Resolve(spell, SpellBonusKind.SpellDirect, -1f).Factor, 0.00001f);
    }

    [Fact]
    public void AuraMaxTicks_FollowsDurationOverAmplitude()
    {
        SpellEffectInfo tick = SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.PeriodicDamage, amplitude: 3000);
        SpellInfo Make(int duration, SpellEffectInfo effect) => SpellTestKit.Spell(1003, effect) with { Duration = new SpellDuration(duration, 0, duration) };

        Assert.Equal(6, SpellCoefficients.AuraMaxTicks(Make(18000, tick)));
        Assert.Equal(1, SpellCoefficients.AuraMaxTicks(Make(0, tick)));
        Assert.Equal(10, SpellCoefficients.AuraMaxTicks(Make(60000, tick)));
        Assert.Equal(6, SpellCoefficients.AuraMaxTicks(Make(18000, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 1))));
        Assert.Equal(6, SpellCoefficients.AuraMaxTicks(Make(18000, SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.PeriodicDamage, amplitude: 0))));
    }
}
