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

    public bool IsCasting(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return spells().GetState(unit.Guid) is { Unit: var owner, CurrentCast: { State: SpellCastState.Preparing or SpellCastState.Casting } }
            && ReferenceEquals(owner, unit);
    }
}
