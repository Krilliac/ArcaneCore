using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// A unit died (raised after <c>MapCombat.Kill</c> moved it out of the alive state): remove every
    /// non-passive aura it carries, DoTs, stuns, roots and buffs alike, so none of them is ticked, shown
    /// or captured by <see cref="CaptureState"/> at logout (vmangos Unit::RemoveAllAurasOnDeath; the
    /// reference clones were not available when this was written, so the exact vmangos rule is
    /// unverified — only passives are kept). Each aura goes through the normal removal path, so its
    /// remove handler, the visible aura slot and area-aura children are cleaned up. Cooldowns, the cast
    /// in progress and auras the dead unit cast on others are left alone. World thread.
    /// </summary>
    public void OnUnitDied(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.IsAlive || GetState(unit.Guid) is not { } state || !ReferenceEquals(state.Unit, unit))
        {
            return;
        }

        foreach (SpellAuraHolder holder in state.Auras.Where(h => !h.Spell.IsPassive).ToArray())
        {
            RemoveHolder(state, holder);
        }
    }
}
