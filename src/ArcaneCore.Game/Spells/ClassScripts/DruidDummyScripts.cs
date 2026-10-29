using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.ClassScripts;

/// <summary>
/// Brittle Armor 29284 (Zandalarian Hero Badge) and Mercurial Shield 29286 (Petrified Scarab): the DUMMY effect 0 gives the target one more stack of the
/// trinket buff, 24575 and 26464, as a triggered cast of the badge's wearer (vmangos src/scripts/spells/spell_item.cpp BrittleArmorDummyScript and
/// MercurialShieldDummyScript, which run on effect index 0 with a unit target). Spell.dbc files both rows under the druid family (7), which is why
/// they sit with the class scripts.
/// </summary>
[SpellScript(29284, 29286)]
public sealed class TrinketBuffStackScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex != 0)
        {
            return;
        }

        GadgetCasts.Cast(context, context.Caster, context.Target, context.Spell.Id == 29284 ? 24575u : 26464u);
    }
}
