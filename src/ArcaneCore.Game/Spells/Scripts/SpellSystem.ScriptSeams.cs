using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

// Small entry points the class scripts call (class-scripts lane): each forwards to the spell system's own implementation.
public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos Player::CastItemCombatSpell(pVictim, attType) called by a script (Seal of Righteousness procs the weapon's chance-on-hit spells and
    /// enchantments once more, UnitAuraProcHandler.cpp:1050-1051): the same item and enchantment loop a qualifying melee hit runs.
    /// </summary>
    internal void CastItemCombatSpell(Player player, Unit target, WeaponAttackType attackType)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(target);
        HandleItemCombatProcForTarget(player, target, attackType);
    }
}
