using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// What EventAI asks of the spell system about any unit (not only its own creature): how many stacks of a spell a unit
/// has, and whether it is casting. A seam bound by <c>CreatureAiServicesBinder</c>; the default adapter is
/// <see cref="SpellSystemUnitSpellQueries"/>.
/// </summary>
public interface IUnitSpellQueries
{
    /// <summary>The stack amount of the aura of <paramref name="spellId"/> on <paramref name="unit"/>; 0 when it has none (cmangos GetSpellAuraHolder + GetStackAmount).</summary>
    int GetAuraStacks(Unit unit, uint spellId);

    /// <summary>Whether <paramref name="unit"/> has a non-melee spell or channel in progress (cmangos IsNonMeleeSpellCasted).</summary>
    bool IsCasting(Unit unit);

    /// <summary>Whether a live root aura holds <paramref name="unit"/> (an AI root lifted then keeps the aura's root).</summary>
    bool IsRooted(Unit unit) => false;

    /// <summary>cmangos Unit::RemoveAurasDueToSpell (EventAI REMOVEAURASFROMSPELL); false when nothing could be removed.</summary>
    bool RemoveAuras(Unit unit, uint spellId) => false;

    /// <summary>
    /// Whether the spell carries cmangos SPELL_ATTR_EX_EXCLUDE_CASTER (vmangos SPELL_ATTR_EX_CANT_TARGET_SELF, 0x00080000): its caster cannot
    /// be its target (EventAI FRIENDLY_HP then leaves the creature itself out, CreatureEventAIMgr.cpp:1082-1096). False for an unknown spell.
    /// </summary>
    bool ExcludesCaster(uint spellId) => false;
}

/// <summary><see cref="IUnitSpellQueries"/> over the world <see cref="SpellSystem"/>.</summary>
public sealed class SpellSystemUnitSpellQueries(Func<SpellSystem> spells) : IUnitSpellQueries
{
    public SpellSystemUnitSpellQueries(SpellSystem spells)
        : this(() => spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
    }

    public int GetAuraStacks(Unit unit, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(unit);
        SpellAuraHolder? holder = spells().GetAuras(unit).FirstOrDefault(h => h.Spell.Id == spellId);
        return holder?.StackAmount ?? 0;
    }

    public bool IsRooted(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return spells().IsRooted(unit);
    }

    public bool RemoveAuras(Unit unit, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(unit);
        SpellSystem system = spells();
        if (!system.GetAuras(unit).Any(h => h.Spell.Id == spellId))
        {
            return false;
        }

        system.RemoveAuras(unit, spellId);
        return true;
    }

    public bool ExcludesCaster(uint spellId) => spells().Store.Get(spellId) is { } spell && spell.HasAttribute(SpellAttributesEx.CantTargetSelf);

    public bool IsCasting(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return spells().GetState(unit.Guid) is { Unit: var owner, CurrentCast: { State: SpellCastState.Preparing or SpellCastState.Casting } }
            && ReferenceEquals(owner, unit);
    }
}
