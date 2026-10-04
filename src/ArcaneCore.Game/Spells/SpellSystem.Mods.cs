using ArcaneCore.Game.Entities;

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
        float result = SpellModifiers.Apply(caster, spell, op, value);
        return result == value ? value : (int)result;
    }

    /// <summary>The spell-modifier engine installed on the seam, or null when another implementation (or none) is.</summary>
    private Mods.ISpellModEngine? ModEngine => SpellModifiers as Mods.ISpellModEngine;

    /// <summary>Open a consume window for a cast's mod scope (null scope: nothing to spend, the default window does nothing).</summary>
    private Mods.SpellModWindow BeginModWindow(Mods.SpellModScope? scope) => scope is null || ModEngine is null ? default : ModEngine.Begin(scope);

    /// <summary>A channel starts (or any cast ends): remove the auras of the mods this cast spent the last charge of.</summary>
    private void SealModScope(SpellCast cast)
    {
        if (cast.ModScope is { } scope)
        {
            ModEngine?.Seal(scope);
        }
    }

    internal float ModFloat(Unit caster, SpellInfo spell, SpellModOp op, float value) => SpellModifiers.Apply(caster, spell, op, value);
}
