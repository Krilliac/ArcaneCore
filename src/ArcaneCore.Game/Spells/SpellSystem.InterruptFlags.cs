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
    /// vmangos Unit::InterruptNonMeleeSpells(false): stop the unit's current cast, whether it is still being prepared or is a
    /// channel (a queued melee ability is not a non-melee spell and stays).
    /// </summary>
    public void InterruptNonMeleeSpells(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid)?.CurrentCast is { } cast && cast.State is SpellCastState.Preparing or SpellCastState.Casting)
        {
            Cancel(cast);
        }

        // ranged (autorepeat lane): InterruptNonMeleeSpells covers the auto-repeat slot too (SpellCaster.cpp:2068-2081).
        CancelAutoRepeat(unit);
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
