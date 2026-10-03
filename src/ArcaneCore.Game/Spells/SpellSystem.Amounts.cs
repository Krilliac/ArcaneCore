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
            _ => null,
        };
        return AmountModifier is null || stage is null
            ? context.Value
            : (int)AmountModifier.Modify(stage.Value, context.Caster, context.Target, context.Spell, context.EffectIndex, context.Value, 1);
    }

    /// <summary>One periodic tick's amount through the target side of <see cref="AmountModifier"/>, dithered like vmangos rand_ditheru.</summary>
    private uint ModifyTick(SpellAmountStage stage, SpellAuraHolder holder, SpellAura aura, Unit caster, uint amount)
    {
        if (AmountModifier is null)
        {
            return amount;
        }

        float modified = AmountModifier.Modify(stage, caster, holder.Target, holder.Spell, aura.EffectIndex, amount, holder.StackAmount);
        return (uint)Math.Floor(Math.Max(modified, 0f) + Random.NextSingle());
    }
}
