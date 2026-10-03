using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// The talent spell-modifier seam (vmangos Player::ApplySpellMod; operations are <see cref="ArcaneCore.Game.Spells.SpellModOp"/>, the spell-breadth enum): <see cref="Apply"/> returns the
/// modified <paramref name="value"/>. The default, <see cref="None"/>, is the identity: with no
/// talents area installed, nothing modifies any spell.
/// </summary>
public interface ISpellModifiers
{
    /// <summary>The identity modifier set.</summary>
    static ISpellModifiers None { get; } = new NoModifiers();

    float Apply(Unit caster, SpellInfo spell, SpellModOp op, float value);

    private sealed class NoModifiers : ISpellModifiers
    {
        public float Apply(Unit caster, SpellInfo spell, SpellModOp op, float value) => value;
    }
}
