using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>vmangos game/Spells/SpellEffects.cpp Spell::EffectScriptEffect, case 23645.</summary>
[SpellScript(23645)]
public sealed class HourglassSandScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex == 0) context.System.RemoveAuras(context.Caster, 23170);
    }
}
