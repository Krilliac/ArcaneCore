using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class SpellAreaRadiusOverrideTests
{
    private const uint HeiganManaBurn = 29310;
    private const uint OrdinaryArea = 900931;

    [Fact]
    public void HeiganManaBurn_UsesIts28YardTargetMapRadius_WithoutChangingOtherSpells()
    {
        static SpellInfo Area(uint id) => Spell(id,
            Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.EnumUnitsEnemyWithinCasterRange)
                with { Radius = 25f }) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

        using var kit = new SpellTestKit(Area(HeiganManaBurn), Area(OrdinaryArea));
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        (Player caster, _) = kit.AddPlayer(1);
        (Player insideOverride, _) = kit.AddPlayer(2, 27, 0);
        (Player outsideOverride, _) = kit.AddPlayer(3, 31, 0);
        relations.Hostile.Add(insideOverride.Guid);
        relations.Hostile.Add(outsideOverride.Guid);
        kit.World.RunTick(0);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, OrdinaryArea, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(60u, insideOverride.Health);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, HeiganManaBurn, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(50u, insideOverride.Health);
        Assert.Equal(60u, outsideOverride.Health);
    }
}
