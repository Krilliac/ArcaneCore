using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// Returns part of the power of an <see cref="SpellAttributesExCombat.DiscountPowerOnMiss"/> ability the target
/// avoided (vmangos Spell::DoAllEffectOnTarget, Spell.cpp:1267-1285); the amounts are <see cref="PowerRules.Refund"/>.
/// Register it with <see cref="SpellSystem.RegisterObserver"/>.
/// </summary>
public sealed class PowerRefundObserver : ISpellCastObserver
{
    public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(cast);
        if (PowerRules.Refund(cast.Spell, outcome.Miss, cast.PowerCost) is not var (power, amount))
        {
            return;
        }

        // vmangos Unit::ModifyPower: clamped to [0, max].
        MapCombat.ModifyPower(cast.Caster, power, amount);
    }
}
