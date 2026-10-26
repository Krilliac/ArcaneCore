using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>A client-requested harmful spell aimed at an explicit target must name an attackable unit (vmangos Spell.cpp:5604-5610).</summary>
public sealed class ExplicitHostileTargetTests
{
    private const uint Bolt = 900420;

    [Fact]
    public void HostileSpellAtUnflaggedSameTeamPlayer_IsBadTargets_AndDealsNoDamage()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, 3);
        kit.Spellbook.Teach(caster, Bolt);
        uint before = ally.Health;

        // RED on origin/main: this assertion fails (CastOk) because CheckCast never tests hostility for a UnitEnemy selector.
        Assert.Equal(SpellCastResult.BadTargets, kit.System.HandleCastRequest(caster, Bolt, SpellCastTargets.ForUnit(ally.Guid)));
        Assert.Equal(before, ally.Health);
    }

    [Fact]
    public void HostileSpellAtAttackableTarget_StillCasts()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player foe, _) = kit.AddPlayer(2, 3);
        kit.Spellbook.Teach(caster, Bolt);
        var relations = new FakeRelations();
        relations.Hostile.Add(foe.Guid);
        kit.System.Relations = relations;

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Bolt, SpellCastTargets.ForUnit(foe.Guid)));
    }

    private static SpellTestKit NewKit() => new(
        Spell(Bolt, Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });
}
