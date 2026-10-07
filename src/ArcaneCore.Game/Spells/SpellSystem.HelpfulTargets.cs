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
}
