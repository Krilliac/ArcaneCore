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
    /// vmangos SpellCaster::InterruptSpellsWithInterruptFlags (SpellCaster.cpp:2051-2058): stop the unit's cast while it is preparing (or a
    /// running channel) when the spell has a cast time, is not a next-swing spell, not triggered, and its Spell.dbc InterruptFlags carry any
    /// of <paramref name="flags"/>. A cast that is already finished is untouched, so a second interrupt of the same cast sends nothing.
    /// </summary>
    public void InterruptSpellsWithInterruptFlags(Unit unit, SpellInterruptFlags flags)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid)?.CurrentCast is { } cast
            && cast.State is SpellCastState.Preparing or SpellCastState.Casting
            && cast.CastTime != 0 && !cast.IsTriggered && !cast.Spell.IsNextMeleeSwing
            && (cast.Spell.InterruptFlags & flags) != 0)
        {
            Cancel(cast);
        }
    }

    /// <summary>
    /// mangos-classic Unit::InterruptSpell(spellType) (Entities/Unit.cpp:4302-4329) as the DB script command INTERRUPT_SPELL (47) calls it
    /// (DBScripts/ScriptMgr.cpp:2983-2991). <paramref name="currentSpellType"/> is the CurrentSpellTypes slot (Unit.h:797-804): 0 the queued
    /// next-swing spell, 1 the cast being prepared, 2 the auto-repeat spell, 3 the channel. Only the unit's own matching cast is cancelled:
    /// a prepared cast is not a channel and the reverse. A type above 3 is refused by the loader in mangos-classic (ScriptMgr.cpp:815-819), so it
    /// does nothing here. Not ported: the CanBeInterrupted gate and the OnSpellInterrupt AI callback.
    /// </summary>
    /// <returns>True when a cast was cancelled.</returns>
    public bool InterruptCurrentSpell(Unit unit, uint currentSpellType)
    {
        ArgumentNullException.ThrowIfNull(unit);
        UnitSpellState? state = GetState(unit.Guid);
        if (state is null || !ReferenceEquals(state.Unit, unit))
        {
            return false;
        }

        switch (currentSpellType)
        {
            case 0 when state.MeleeCast is { State: not SpellCastState.Finished } melee:
                Cancel(melee);
                return true;
            case 1 when state.CurrentCast is { State: SpellCastState.Preparing } generic:
                Cancel(generic);
                return true;
            case 2:
                return CancelAutoRepeat(unit);
            case 3 when state.CurrentCast is { State: SpellCastState.Casting } channel:
                Cancel(channel);
                return true;
            default:
                return false;
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
