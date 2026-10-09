using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.Uldaman;

/// <summary>ScriptDev2 boss_archaedas.cpp AwakenEarthenArchaedas / AwakenVaultWarder: awaken each unit selected by the spell.</summary>
[SpellScript(ArchaedasAi.AwakenGuardians, ArchaedasAi.AwakenWarders, ExecuteEffects = [SpellEffectName.ApplyAura])]
public sealed class ArchaedasAwakenSpell : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.Caster is Creature { AI: ArchaedasAi archaedas }
            && context.Target is Creature target)
            archaedas.Awaken(target);
    }
}
