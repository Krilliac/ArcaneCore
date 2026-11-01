using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// Food 6410 (Scorpid Surprise, vmangos spell_item.cpp ScorpidSurpriseScript): effect index 1 casts the Scorpid Poison 6411 on the eater, and the
/// script lets it run in only 1 of 11 cases (<c>urand(0, 10)</c> must draw 0; vmangos notes "10% proc rate, no source"). Without it the poison
/// always bit, its damage stood the seated eater up and the food ended at once.
/// </summary>
[SpellScript(6410, ExecuteEffects = [SpellEffectName.TriggerSpell])]
public sealed class ScorpidSurpriseScript : ISpellScript
{
    /// <summary>The draw is 0..<see cref="DrawMax"/> inclusive; the poison runs on a 0.</summary>
    public const int DrawMax = 10;

    public bool OnEffectExecute(SpellEffectContext context, bool runByDefault)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex != 1)
        {
            return runByDefault;
        }

        return context.System.Random.Next(0, DrawMax + 1) == 0 && runByDefault;
    }
}
