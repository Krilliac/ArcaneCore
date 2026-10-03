using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Hooks the spell combat rules (<c>Spells/Rules</c>) share with the rest of the spell system. A separate
/// partial file so the rule slices add their seams without editing the shared system files.
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// Close the open loot window of a player (vmangos WorldSession::DoLootRelease); a stun or fear
    /// calls it. Null until the loot area installs one, which means no window can be open.
    /// </summary>
    public Action<Player>? ReleaseLoot { get; set; }

    /// <summary>
    /// Whether a live root or stun aura roots <paramref name="unit"/> (vmangos Unit::SetRooted via HandleAuraModRoot /
    /// HandleAuraModStun). The creature movement code reads this; players are rooted through <see cref="Player.SetRooted"/>.
    /// </summary>
    public bool IsRooted(Unit unit) => HasLiveAura(unit, AuraType.ModRoot, AuraType.ModStun);

    /// <summary>True while a live (not removed) aura of any of <paramref name="types"/> is on <paramref name="unit"/> (vmangos Unit::HasAuraType).</summary>
    internal bool HasLiveAura(Unit unit, params AuraType[] types)
    {
        foreach (SpellAuraHolder holder in GetAuras(unit))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is not null && Array.IndexOf(types, aura.Type) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// vmangos Unit::InterruptNonMeleeSpells(false) restricted by <paramref name="filter"/>: cancel the cast or
    /// channel in progress when it matches (all when null). True when one was interrupted.
    /// </summary>
    internal bool InterruptCurrentCast(Unit unit, Func<SpellInfo, bool>? filter = null)
    {
        if (GetState(unit.Guid)?.CurrentCast is { } cast && cast.State != SpellCastState.Finished && (filter is null || filter(cast.Spell)))
        {
            Cancel(cast);
            return true;
        }

        return false;
    }
}
