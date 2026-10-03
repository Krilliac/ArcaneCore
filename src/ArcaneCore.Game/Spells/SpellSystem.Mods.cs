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

    internal float ModFloat(Unit caster, SpellInfo spell, SpellModOp op, float value) => SpellModifiers.Apply(caster, spell, op, value);
}
