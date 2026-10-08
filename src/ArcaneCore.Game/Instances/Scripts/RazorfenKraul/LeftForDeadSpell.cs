using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.RazorfenKraul;

/// <summary>ScriptDev2 LeftForDead (mangos-classic razorfen_kraul/razorfen_kraul.cpp:
/// OnEffectExecute of spell 8555 casts 8359 on its target).</summary>
[SpellScript(8555)]
public sealed class LeftForDeadSpell : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex == 0)
            context.System.CastSpell(context.Target, 8359, SpellCastTargets.ForSelf(), triggered: true);
    }
}
