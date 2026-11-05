using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The call sites of the spell-modifier seam (vmangos <c>Player::ApplySpellMod</c>), kept in one partial file so the pipeline
/// edits in the other SpellSystem files are one-line calls (docs/areas/spell-mods.md lists every site and its vmangos line).
/// They go through <see cref="SpellModifiers"/>, so any <see cref="Rules.ISpellModifiers"/> installed there is consulted.
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// <see cref="SpellModifiers"/> applied to an integer: truncated toward zero like vmangos <c>T(float(base) + diff)</c>, and
    /// returned exactly when the modifiers changed nothing (an int above 2^24 does not survive a round trip through float).
    /// </summary>
    internal int ModInt(Unit caster, SpellInfo spell, SpellModOp op, int value)
    {
        // A unit standing in for a game object has no spell mods of its own (vmangos: the object has no mod owner).
        if (IsGameObjectStandIn(caster))
        {
            return value;
        }

        float result = SpellModifiers.Apply(caster, spell, op, value);
        return result == value ? value : (int)result;
    }

    /// <summary>The spell-modifier engine installed on the seam, or null when another implementation (or none) is.</summary>
    private Mods.ISpellModEngine? ModEngine => SpellModifiers as Mods.ISpellModEngine;

    /// <summary>Open a consume window for a cast's mod scope (null scope: nothing to spend, the default window does nothing).</summary>
    private Mods.SpellModWindow BeginModWindow(Mods.SpellModScope? scope) => scope is null || ModEngine is null ? default : ModEngine.Begin(scope);

    /// <summary>
    /// The window of the first CheckCast (Spell::prepare, Spell.cpp:3405): only the operations CheckCast reads with the spell (range,
    /// radius, jump targets) spend charges in it; the cost and cast time reads there are not counted (vmangos CheckPower uses the cost
    /// computed before, without dropping charges). Dispose closes the window and lifts the restriction.
    /// </summary>
    private Mods.SpellModWindow BeginModCheckWindow(Mods.SpellModScope? scope)
    {
        if (scope is null || ModEngine is null)
        {
            return default;
        }

        scope.ChecksOnly = true;
        return ModEngine.Begin(scope);
    }

    /// <summary>
    /// The pets (pet, guardians, mini pet) and totems that belong to <paramref name="owner"/> and have a spell state (vmangos
    /// CallForAllControlledUnits with CONTROLLED_PET and CONTROLLED_TOTEMS, Unit.cpp). A snapshot, so a caller may edit the states.
    /// </summary>
    internal List<Unit> ControlledPetsAndTotems(Player owner)
    {
        var found = new List<Unit>();
        foreach (UnitSpellState state in _states.Values)
        {
            if (state.Unit is Creature creature && (creature.IsPet || creature.IsTotem) && creature.OwnerGuid == owner.Guid
                && ReferenceEquals(creature.Map, owner.Map))
            {
                found.Add(creature);
            }
        }

        return found;
    }

    /// <summary>A cast that failed before it was built gives back the charges its first CheckCast spent (vmangos Spell::finish(false) -> RestoreSpellMods).</summary>
    private void RestoreModScope(Mods.SpellModScope? scope)
    {
        if (scope is not null)
        {
            ModEngine?.Restore(scope);
        }
    }

    /// <summary>A channel starts (or any cast ends): remove the auras of the mods this cast spent the last charge of.</summary>
    private void SealModScope(SpellCast cast)
    {
        if (cast.ModScope is { } scope)
        {
            ModEngine?.Seal(scope);
        }
    }

    internal float ModFloat(Unit caster, SpellInfo spell, SpellModOp op, float value)
        => IsGameObjectStandIn(caster) ? value : SpellModifiers.Apply(caster, spell, op, value);
}
