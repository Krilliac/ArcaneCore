using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// The spell power / damage taken modifier applied to damage and healing amounts (casters lane, spell-bonus-wiring).
    /// Null leaves every amount exactly as the spell data states it.
    /// </summary>
    public ISpellAmountModifier? AmountModifier { get; set; }

    /// <summary>A direct effect amount through <see cref="AmountModifier"/>, truncated like the int32 damage of vmangos Spell.</summary>
    private uint ModifyDirect(SpellAmountStage stage, SpellEffectContext context, uint amount)
        => AmountModifier is null
            ? amount
            : (uint)Math.Max(0, (int)AmountModifier.Modify(stage, context.Caster, context.Target, context.Spell, context.EffectIndex, amount, 1));

    /// <summary>
    /// A periodic effect's amount when its aura is created: periodic damage, leech and heal auras get the caster side
    /// (vmangos Aura::HandlePeriodicDamage / HandlePeriodicLeech / HandlePeriodicHeal, stored as int32); anything else is unchanged.
    /// </summary>
    private int SnapshotAuraAmount(SpellEffectContext context)
    {
        SpellAmountStage? stage = context.Effect.AuraType switch
        {
            AuraType.PeriodicDamage or AuraType.PeriodicLeech => SpellAmountStage.DamageOverTimeSnapshot,
            AuraType.PeriodicHeal => SpellAmountStage.HealOverTimeSnapshot,
            AuraType.SchoolAbsorb => SpellAmountStage.AbsorbShield, // vmangos HandleSchoolAbsorb: int32 m_amount += bonus (truncation)
            _ => null,
        };
        return AmountModifier is null || stage is null
            ? context.Value
            : (int)AmountModifier.Modify(stage.Value, context.Caster, context.Target, context.Spell, context.EffectIndex, context.Value, 1);
    }

    /// <summary>One periodic tick's amount through the target side of <see cref="AmountModifier"/>, dithered like vmangos rand_ditheru.</summary>
    internal uint ModifyTick(SpellAmountStage stage, SpellAuraHolder holder, SpellAura aura, Unit caster, uint amount)
    {
        if (AmountModifier is null)
        {
            return amount;
        }

        float modified = AmountModifier.Modify(stage, caster, holder.Target, holder.Spell, aura.EffectIndex, amount, holder.StackAmount);
        return (uint)Math.Floor(Math.Max(modified, 0f) + Random.NextSingle());
    }

    /// <summary>The unit that cast an aura while it is still in the world and the aura's target's map (null otherwise); the caster of a tick.</summary>
    internal Unit? AuraCaster(SpellAuraHolder holder) => ResolveAuraCaster(holder);

    /// <summary>
    /// vmangos Unit::RemoveAurasWithInterruptFlags(AURA_INTERRUPT_DAMAGE_CANCELS) without any damage: auras that break when
    /// the unit takes damage (polymorph-like crowd control) go.
    /// </summary>
    internal void BreakDamageCancelledAuras(Unit target)
    {
        if (GetState(target.Guid) is { } state && ReferenceEquals(state.Unit, target))
        {
            foreach (SpellAuraHolder holder in state.Auras.Where(h => (h.Spell.AuraInterruptFlags & SpellAuraInterruptFlags.Damage) != 0).ToArray())
            {
                RemoveHolder(state, holder);
            }
        }
    }
}
