using ArcaneCore.Game.Spells.Rules.Application;

namespace ArcaneCore.Game.Spells.Rules.Immunity;

/// <summary>
/// Drops the effects a target is immune to before they run (vmangos Spell::AddUnitTarget / CheckAtDelay,
/// Spell.cpp:879-997: <c>effectMask &amp;= ~effect</c> when IsImmuneToSpellEffect). Install it first in
/// <see cref="SpellSystem.ApplicationRules"/>.
/// </summary>
public sealed class ImmunityApplicationRule : ISpellApplicationRule
{
    public void Begin(SpellApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        SpellInfo spell = application.Cast.Spell;
        bool onSelf = ReferenceEquals(application.Cast.Caster, application.Target);
        for (int i = 0; i < SpellConstants.MaxEffects; i++)
        {
            if ((application.EffectMask & (1 << i)) != 0 && ImmunityRules.IsImmuneToSpellEffect(application.System, application.Target, spell, i, onSelf))
            {
                application.EffectMask &= ~(1 << i);
            }
        }
    }

    public bool AcceptHolder(SpellApplication application, SpellAuraHolder holder) => true;
}
