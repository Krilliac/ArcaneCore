using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Honor;

namespace ArcaneCore.Game.Honor;

/// <summary>
/// SPELL_EFFECT_ADD_HONOR (45) and the Honorless Target aura (SPELL_AURA_NO_PVP_CREDIT, 159). 14 classic spells carry
/// the effect (honor bonus rewards of battleground quests); spell 2479 Honorless Target is the only one with the aura.
/// vmangos <c>Spell::EffectAddHonor</c> (SpellEffects.cpp:2979-2988): a player target is given the effect value as
/// honor (not scaled by level) of type quest, anything else is ignored. The aura itself does nothing: its presence is
/// what <see cref="HonorKillRewards"/> asks about. Discovered like every <see cref="ISpellHandlerModule"/>; a spell
/// system without an honor service (<see cref="HonorService.InstallForSpells"/>) ignores the effect.
/// </summary>
public sealed class HonorSpellEffects : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.AddHonor, EffectAddHonor);
        system.RegisterAura(AuraType.NoPvpCredit, new AuraHandler(null, null));
    }

    private static void EffectAddHonor(SpellEffectContext context)
    {
        if (context.Target is Player target)
        {
            HonorService.ForSpells(context.System)?.Add(target, context.Value, HonorKind.Quest, null);
        }
    }
}
