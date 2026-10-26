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
        kit.System.Relations = CombatHookRelations.Instance;   // the production relations; the kit default opens the gate
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
        kit.System.Relations = new HostileSetRelations(foe.Guid);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Bolt, SpellCastTargets.ForUnit(foe.Guid)));
    }

    /// <summary>Only the listed units are attackable; the explicit-target gate takes its default (<see cref="ISpellTargetRelations.IsHostile"/>).</summary>
    private sealed class HostileSetRelations(ObjectGuid hostile) : ISpellTargetRelations
    {
        public bool IsHostile(Unit caster, Unit target) => target.Guid == hostile;

        public bool IsFriendly(Unit caster, Unit target) => !IsHostile(caster, target);
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
