using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>
/// A player's cast bar while incapacitated (vmangos Spell::update, Spell.cpp:4082-4095): feign, confuse and fear
/// stop it, and a stun stops it only when the spell has <see cref="SpellInterruptFlags.Stun"/>.
/// </summary>
public sealed class CastBarStunInterruptTests
{
    private const uint Flagged = 961_001;
    private const uint Unflagged = 961_002;
    private const uint SelfStun = 961_010;

    private static SpellInfo Bolt(uint id, SpellInterruptFlags interrupt) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy))
        with
        {
            School = SpellSchool.Fire,
            DamageClass = SpellDamageClass.Magic,
            CastTime = new SpellCastTime(3000, 0, 0),
            InterruptFlags = interrupt,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellTestKit Kit() => new(
        Bolt(Flagged, SpellInterruptFlags.Stun),
        Bolt(Unflagged, SpellInterruptFlags.Movement),
        RuleTestKit.Grant(SelfStun, AuraType.ModStun, 0));

    private static (Player Caster, Player Victim) Pair(SpellTestKit kit)
    {
        (Player caster, _) = kit.AddPlayer(1);
        SpellTestKit.FlagForPvp(caster);
        (Player victim, _) = kit.AddHostilePlayer(2, 2);
        kit.Spellbook.Teach(caster, Flagged, Unflagged);
        return (caster, victim);
    }

    [Fact]
    public void AStunInterruptFlag_CancelsTheCastBar_WhenTheCasterIsStunned()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Flagged, SpellCastTargets.ForUnit(victim.Guid)));
        Assert.Equal(SpellCastState.Preparing, kit.System.GetState(caster.Guid)!.CurrentCast!.State);

        // A self stun does not interrupt at apply (SpellAuras.cpp:3573-3576). The cast bar must still stop
        // on the next update because the spell carries SPELL_INTERRUPT_FLAG_STUN.
        RuleTestKit.Apply(kit, caster, SelfStun);
        Assert.NotNull(kit.System.GetState(caster.Guid)!.CurrentCast);
        Assert.True((caster.UnitFlags & UnitFlags.Stunned) != 0);

        kit.Advance(100);

        Assert.Null(kit.System.GetState(caster.Guid)?.CurrentCast);
    }

    [Fact]
    public void ACastBarWithoutTheStunFlag_ContinuesWhileStunned()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim) = Pair(kit);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Unflagged, SpellCastTargets.ForUnit(victim.Guid)));
        SpellCast cast = kit.System.GetState(caster.Guid)!.CurrentCast!;

        RuleTestKit.Apply(kit, caster, SelfStun);
        kit.Advance(100);

        Assert.Same(cast, kit.System.GetState(caster.Guid)!.CurrentCast);
        Assert.Equal(SpellCastState.Preparing, cast.State);
        Assert.Equal(2900, cast.Timer);
    }
}
