using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos Unit::RemoveAurasWithInterruptFlags: remove every aura whose spell has any of <paramref name="flags"/> in
    /// its aura interrupt flags (the mount and dismount cancels, entering and leaving water, and so on).
    /// </summary>
    public void RemoveAurasWithInterruptFlags(Unit unit, SpellAuraInterruptFlags flags)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid) is not { } state)
        {
            return;
        }

        foreach (SpellAuraHolder holder in state.Auras.Where(h => (h.Spell.AuraInterruptFlags & flags) != 0).ToArray())
        {
            RemoveHolder(state, holder);
        }
    }

    /// <summary>vmangos Unit::RemoveSpellsCausingAura: remove every aura holder that has an aura of the type.</summary>
    public void RemoveAurasByType(Unit unit, AuraType type)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid) is not { } state)
        {
            return;
        }

        foreach (SpellAuraHolder holder in state.Auras.Where(h => h.Auras.Any(a => a is not null && a.Type == type)).ToArray())
        {
            RemoveHolder(state, holder);
        }
    }

    /// <summary>
    /// vmangos Unit::InterruptSpellsWithChannelFlags: stop the unit's channel when its channel interrupt flags carry any of
    /// <paramref name="flags"/> (a channeled spell that ends when the caster mounts, for instance).
    /// </summary>
    public void InterruptChannelsWithFlags(Unit unit, SpellAuraInterruptFlags flags)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid)?.CurrentCast is { State: SpellCastState.Casting } cast && (cast.Spell.ChannelInterruptFlags & flags) != 0)
        {
            Cancel(cast);
        }
    }
}
