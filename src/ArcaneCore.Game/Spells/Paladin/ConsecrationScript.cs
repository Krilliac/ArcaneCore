using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Paladin;

/// <summary>
/// Consecration (26573, 20116, 20922, 20923, 20924; paladin family, CF_PALADIN_CONSECRATION bit 5): a persistent area aura whose PERIODIC_DAMAGE
/// tick is recalculated on each tick (vmangos <c>Aura::PeriodicTick</c>, SpellAuras.cpp:5872-5874: "Consecration: recalculate the damage on each
/// tick"): the caster side of the bonus (<c>SpellDamageBonusDone(..., m_currentBasePoints, DOT, stacks)</c>) is taken again from the aura's base
/// points with the caster's current spell power, instead of the amount stored when the aura was applied. The target side follows as for every tick.
/// </summary>
public sealed class ConsecrationScript : IPeriodicDamageScript
{
    /// <summary>The Consecration ranks of the build 5875 spell data.</summary>
    public static readonly uint[] Ranks = [26573, 20116, 20922, 20923, 20924];

    public float? CalculateTick(SpellSystem system, SpellAuraHolder holder, SpellAura aura, Unit caster, uint storedAmount)
    {
        // Aura::m_currentBasePoints: CalculateSimpleValue for an aura built without custom base points (SpellAuras.cpp:274), which a persistent
        // area aura always is.
        float basePoints = holder.Spell.SimpleValue(aura.EffectIndex);
        return system.AmountModifier is { } modifier
            ? modifier.Modify(SpellAmountStage.DamageOverTimeSnapshot, caster, holder.Target, holder.Spell, aura.EffectIndex, basePoints, holder.StackAmount)
            : basePoints;
    }
}
