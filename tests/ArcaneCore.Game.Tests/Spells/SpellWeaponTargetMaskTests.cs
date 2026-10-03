using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Weapon combinations use the effects selected by the actual explicit/area target maps.</summary>
public sealed class SpellWeaponTargetMaskTests
{
    private const uint Strike = 900620;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitAndAreaWeaponEffects_SecondaryUsesFirstSelectedEffectAndExcludesOtherBonuses(bool areaFirst)
    {
        SpellEffectInfo explicitFlat = Effect(SpellEffectName.WeaponDamage, 5, SpellImplicitTarget.UnitEnemy);
        SpellEffectInfo areaFlat = Effect(SpellEffectName.WeaponDamageNoschool, 3, SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 10 };
        SpellEffectInfo explicitPercent = Effect(SpellEffectName.WeaponPercentDamage, 150, SpellImplicitTarget.UnitEnemy);
        using var kit = new SpellTestKit(WeaponSpell(areaFirst ? areaFlat : explicitFlat,
            areaFirst ? explicitFlat : areaFlat, explicitPercent));
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player primary, _) = kit.AddPlayer(2, 2);
        (Player secondary, _) = kit.AddPlayer(3, 4);
        (Player friend, _) = kit.AddPlayer(4, 6);
        relations.Hostile.UnionWith([primary.Guid, secondary.Guid]);
        caster.SetFloat(UpdateFields.UnitFieldMindamage, 10);
        caster.SetFloat(UpdateFields.UnitFieldMaxdamage, 10);
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Strike, SpellCastTargets.ForUnit(primary.Guid), triggered: true));

        Assert.Equal(33u, primary.Health); // All three effects: (weapon 10 + flat 5 + flat 3) x 150%.
        Assert.Equal(47u, secondary.Health); // Area effect only: weapon 10 + flat 3, once.
        Assert.Equal(60u, friend.Health);
        Assert.Equal(60u, caster.Health);
        Assert.Equal(2, Packets(session, WorldOpcode.SmsgSpellnonmeleedamagelog).Count);
    }

    [Fact]
    public void SameTargetWeaponFamily_CombinesAllSelectedFlatAndPercentEffectsOnce()
    {
        using var kit = new SpellTestKit(WeaponSpell(
            Effect(SpellEffectName.WeaponDamage, 5, SpellImplicitTarget.UnitEnemy),
            Effect(SpellEffectName.WeaponDamageNoschool, 3, SpellImplicitTarget.UnitEnemy),
            Effect(SpellEffectName.WeaponPercentDamage, 150, SpellImplicitTarget.UnitEnemy)));
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.SetFloat(UpdateFields.UnitFieldMindamage, 10);
        caster.SetFloat(UpdateFields.UnitFieldMaxdamage, 10);
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Strike, SpellCastTargets.ForUnit(target.Guid), triggered: true));

        Assert.Equal(33u, target.Health);
        Assert.Single(Packets(session, WorldOpcode.SmsgSpellnonmeleedamagelog));
    }

    private static SpellInfo WeaponSpell(params SpellEffectInfo[] effects) => Spell(Strike, effects) with
    {
        DamageClass = SpellDamageClass.Melee, RangeIndex = 4, Range = new SpellRange(0, 30),
        StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };
}
