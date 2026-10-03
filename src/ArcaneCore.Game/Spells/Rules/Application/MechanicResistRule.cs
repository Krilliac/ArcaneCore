namespace ArcaneCore.Game.Spells.Rules.Application;

/// <summary>
/// Per-effect mechanic resistance (vmangos Unit::IsEffectResist, Unit.cpp:2461-2470, called from
/// Spell::DoSpellHitOnUnit :1584-1592): an effect whose mechanic differs from the spell's own is dropped
/// when a 0-99 roll is below the target's MOD_MECHANIC_RESISTANCE of that mechanic. The spell's own
/// mechanic is resisted in the hit roll instead (<see cref="VanillaSpellCombatRules.MagicHitPercent"/>).
/// </summary>
public sealed class MechanicResistRule : ISpellApplicationRule
{
    public void Begin(SpellApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        SpellInfo spell = application.Cast.Spell;
        SpellSystem system = application.System;
        for (int i = 0; i < SpellConstants.MaxEffects; i++)
        {
            if ((application.EffectMask & (1 << i)) == 0)
            {
                continue;
            }

            uint mechanic = spell.Effects[i].Mechanic;
            if (mechanic == 0 || mechanic == spell.Mechanic)
            {
                continue;
            }

            int resistance = system.GetTotalAuraModifier(application.Target, AuraType.ModMechanicResistance, a => a.MiscValue == (int)mechanic);
            if (system.Random.Next(0, 100) < resistance)
            {
                application.EffectMask &= ~(1 << i);
            }
        }
    }

    public bool AcceptHolder(SpellApplication application, SpellAuraHolder holder) => true;
}
