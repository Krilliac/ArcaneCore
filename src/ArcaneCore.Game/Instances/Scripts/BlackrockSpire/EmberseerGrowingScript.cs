using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.BlackrockSpire;

/// <summary>
/// Emberseer Growing 16049, the spell the periodic aura of 16048 triggers on Pyroguard Emberseer once the Blackrock Altar ritual started the
/// event (mangos-classic SpellEffects.cpp EffectDummy, case 16049): the caster's AI receives AI_EVENT_CUSTOM_A, which
/// boss_pyroguard_emberseer counts towards its twenty growing stacks (<see cref="PyroguardEmberseerAI"/>).
/// </summary>
[SpellScript(SpellId)]
public sealed class EmberseerGrowingScript : ISpellScript
{
    public const uint SpellId = 16049;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Effect.Effect == SpellEffectName.Dummy && context.Caster is Creature caster)
        {
            caster.ReceiveAiEvent(PyroguardEmberseerAI.AiEventCustomA, caster, caster);
        }
    }
}
