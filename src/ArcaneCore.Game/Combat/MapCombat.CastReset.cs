using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>
    /// A cast that restarts the swing (ranged (autorepeat lane); vmangos Spell::cast, Spell.cpp:3805-3810): the main-hand timer and, with an
    /// off-hand weapon, the off-hand timer go back to the full (hasted) delay. The ranged timer is never reset by a cast (the vmangos
    /// ranged variant is commented out, Spell.cpp:4371). The spell system decides which casts qualify; <c>Combat:CastResetsMeleeSwing</c>
    /// switches the rule off.
    /// </summary>
    public void ResetMeleeTimersAfterCast(Unit caster)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (!CombatEnvironment.For(_world).Options.CastResetsMeleeSwing)
        {
            return;
        }

        caster.Combat.ResetAttackTimer(WeaponAttackType.BaseAttack);
        if (HasOffhandWeapon(caster))
        {
            caster.Combat.ResetAttackTimer(WeaponAttackType.OffAttack);
        }
    }
}
