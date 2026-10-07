using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos Spell::DoAllEffectOnTarget special-attack bridge (Spell.cpp:1529-1538).
    /// The source attempts weapon procs before returning ordinary misses; reflection redirects
    /// the target to the caster before this point (Spell.cpp:1207-1222), so reflected casts do
    /// not produce an item proc. This slice implements only the represented EquippedItemClass
    /// weapon + combat-range predicate.
    /// </summary>
    internal void HandleItemSpecialProc(SpellCast cast, Unit target, SpellMissInfo miss)
    {
        if (miss == SpellMissInfo.Reflect || cast.Caster is not Player player || cast.Spell.EquippedItemClass != 2
            || cast.Spell.RangeIndex != SpellConstants.RangeIndexCombat)
        {
            return;
        }

        WeaponAttackType attackType = (cast.Spell.AttributesEx3 & (uint)SpellAttributesEx3Combat.RequiresOffhandWeapon) != 0
            ? WeaponAttackType.OffAttack
            : WeaponAttackType.BaseAttack;
        HandleItemCombatProcForTarget(player, target, attackType);
    }
}
