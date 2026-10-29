using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// Checks only explicit positive unit selectors. Existing target presence, liveness, range,
    /// line of sight, group, and other target rules remain owned by the surrounding cast check.
    /// </summary>
    private SpellCastResult CheckExplicitHelpfulTargetRules(Unit caster, SpellInfo spell, Unit? target)
    {
        if (!spell.IsPositive || target is null || !spell.Effects.Any(effect => !effect.IsEmpty
            && effect.TargetA is SpellImplicitTarget.UnitFriend or SpellImplicitTarget.UnitFriendChainHeal))
        {
            return SpellCastResult.CastOk;
        }

        if (ReferenceEquals(caster, target) && spell.HasAttribute(SpellAttributesEx.CantTargetSelf))
        {
            return SpellCastResult.BadTargets;
        }

        return Relations.CanAssist(caster, target) ? SpellCastResult.CastOk : SpellCastResult.BadTargets;
    }

    /// <summary>
    /// Explicit negative unit selectors (vmangos IsExplicitNegativeTarget, SpellEntry.h:258-270: TARGET_UNIT_ENEMY and
    /// TARGET_LOCATION_CASTER_TARGET_POSITION; the loop reads EffectImplicitTargetA only, Spell.cpp:5592-5612) must name a unit the
    /// caster may attack (<see cref="ISpellTargetRelations.IsHostile"/>, the relation IsValidAttackTarget maps to); a crafted cast
    /// request cannot aim a hostile spell at a friendly or unflagged target. A self-aimed cast is left to the existing self-target rules.
    /// vmangos passes checkAlive=false while IsHostile also requires a live target, so a dead explicit target is refused here
    /// (documented limit, docs/areas/spells.md).
    /// </summary>
    private SpellCastResult CheckExplicitHostileTargetRules(Unit caster, SpellInfo spell, Unit target)
    {
        if (ReferenceEquals(caster, target) || !spell.Effects.Any(effect => !effect.IsEmpty
            && effect.TargetA is SpellImplicitTarget.UnitEnemy or SpellImplicitTarget.LocationCasterTargetPosition))
        {
            return SpellCastResult.CastOk;
        }

        return Relations.IsHostile(caster, target) ? SpellCastResult.CastOk : SpellCastResult.BadTargets;
    }
}
