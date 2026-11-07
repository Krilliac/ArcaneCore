using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;

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
    /// vmangos passes checkAlive=false and so does the combat hook behind <c>IsHostile</c> (it has no alive test); a dead explicit target is
    /// refused by CheckCast before this call (the CanTargetDead check, or the same alive test in the else-if branch of CheckCast that runs only for a spell with the destination selector), not here. Only the game object stand-in relation also needs a live target.
    /// </summary>
    private SpellCastResult CheckExplicitHostileTargetRules(Unit caster, SpellInfo spell, Unit target)
    {
        if (ReferenceEquals(caster, target) || !spell.Effects.Any(effect => !effect.IsEmpty
            && effect.TargetA is SpellImplicitTarget.UnitEnemy or SpellImplicitTarget.LocationCasterTargetPosition))
        {
            return SpellCastResult.CastOk;
        }

        // vmangos Spell::CheckCast (Spell.cpp:6534) asks IsTargetableBy(caster, forAoE: false, ..., positive): a caster that is not a player or under a
        // player's control cannot aim a single-target harmful spell at a unit successfully feigning death (Unit.cpp:6158). Area effects are not checked here.
        if (!caster.IsCharmerOrOwnerPlayerOrPlayerItself && IsFeigningDeath(target))
        {
            return SpellCastResult.BadTargets;
        }

        return Relations.IsHostile(caster, target) ? SpellCastResult.CastOk : SpellCastResult.BadTargets;
    }
}
