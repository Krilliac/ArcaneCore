using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.ClassScripts;

/// <summary>
/// Vanish ranks 1856/1857/27617: vmangos spell_rogue.cpp RogueVanishScript runs before effect index 1,
/// the second TRIGGER_SPELL (18461) in the imported 1.12.1 spell_template rows.
/// </summary>
[SpellScript(1856, 1857, 27617, ExecuteEffects = [SpellEffectName.TriggerSpell])]
public sealed class VanishScript : ISpellScript
{
    private const uint FirstStealthRank = 1784;

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 1 || context.Effect.Effect != SpellEffectName.TriggerSpell)
            return;

        SpellSystem system = context.System;
        Unit target = context.Target;
        system.RemoveAurasByType(target, AuraType.ModRoot);
        system.RemoveAurasByType(target, AuraType.ModDecreaseSpeed);
        system.RemoveAurasByType(target, AuraType.ModStalked);

        if (target is not Player player)
            return;

        uint stealth = system.HighestKnownRank(player, FirstStealthRank);
        if (stealth == 0 || system.Store.Get(stealth) is not { } spell)
            return;

        if (!system.IsSpellReady(player, spell))
            system.ClearCooldown(player, stealth);
        system.CastSpell(player, stealth, SpellCastTargets.ForSelf(), triggered: true);
    }
}
