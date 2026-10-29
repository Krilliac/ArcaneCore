using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// vmangos game/Spells/SpellEffects.cpp Spell::EffectScriptEffect, case 22539:
/// living targets without Onyxia Scale Cloak (22683) receive triggered Shadow Flame (22682).
/// Direct cone damage remains the spell's ordinary SCHOOL_DAMAGE effect. The Shadow Flame chain 22972, 22975-22985 shares the case (all script effects,
/// not only the last, so the final triggered blast cannot be dodged).
/// </summary>
[SpellScript(22539, 22972, 22975, 22976, 22977, 22978, 22979, 22980, 22981, 22982, 22983, 22984, 22985)]
public sealed class ShadowFlameScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.Target.IsAlive && !context.System.HasAura(context.Target, 22683))
        {
            context.System.CastSpell(context.Caster, 22682, SpellCastTargets.ForUnit(context.Target.Guid, context.Cast), triggered: true);
        }
    }
}
