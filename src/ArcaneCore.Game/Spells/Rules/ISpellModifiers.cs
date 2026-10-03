using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Rules;

/// <summary>The spell-modifier operations the combat rules apply (vmangos SpellModOp, SpellDefines.h:602-631; the talents area owns the storage).</summary>
public enum SpellModOp
{
    CriticalChance = 7,
    CritDamageBonus = 15,
    ResistMissChance = 16,
    ResistDispelChance = 28,
}

/// <summary>
/// The talent spell-modifier seam (vmangos Player::ApplySpellMod): <see cref="Apply"/> returns the
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
