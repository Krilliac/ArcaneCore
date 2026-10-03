using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>Where in a spell's life an amount is being modified (the call sites of <see cref="ISpellAmountModifier"/>).</summary>
public enum SpellAmountStage
{
    /// <summary>A direct damage effect (school damage, health leech): caster and target modifiers, before armor, crit and resist.</summary>
    DirectDamage,

    /// <summary>A direct heal effect: caster and target modifiers, before the crit roll.</summary>
    DirectHeal,

    /// <summary>A damage-over-time aura being created: the caster side only, stored in the aura (patch 1.10 snapshot).</summary>
    DamageOverTimeSnapshot,

    /// <summary>A heal-over-time aura being created: the caster side only, stored in the aura (patch 1.11 snapshot).</summary>
    HealOverTimeSnapshot,

    /// <summary>One tick of a damage aura: the target side only, with the aura's stack count.</summary>
    DamageOverTimeTick,

    /// <summary>One tick of a heal aura: the target side only, with the aura's stack count.</summary>
    HealOverTimeTick,
}

/// <summary>
/// Adjusts the amount of a spell damage or healing effect (spell power, Amplify/Dampen Magic, healing taken
/// modifiers). <see cref="SpellSystem.AmountModifier"/> is null by default, which leaves every amount untouched.
/// </summary>
public interface ISpellAmountModifier
{
    /// <summary>The adjusted amount (may be fractional or negative; the caller floors, truncates or dithers it).</summary>
    /// <param name="stage">Which call site is asking.</param>
    /// <param name="caster">The spell's caster (the aura's caster for ticks).</param>
    /// <param name="target">The unit the amount is applied to.</param>
    /// <param name="spell">The spell the effect belongs to.</param>
    /// <param name="effectIndex">The effect index in the spell (selects an explicit coefficient).</param>
    /// <param name="amount">The unmodified amount.</param>
    /// <param name="stack">Aura stack count (1 for direct effects and snapshots).</param>
    float Modify(SpellAmountStage stage, Unit caster, Unit target, SpellInfo spell, int effectIndex, float amount, uint stack);
}
