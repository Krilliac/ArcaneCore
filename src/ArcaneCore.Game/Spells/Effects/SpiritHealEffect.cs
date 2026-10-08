using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_SPIRIT_HEAL (117), vmangos Spell::EffectSpiritHeal (SpellEffects.cpp:5821-5846): the battleground spirit guide's
/// resurrection wave (Spirit Heal, 22012, cast by the guide on the dead around it). Only a dead player in the world that carries
/// Waiting to Resurrect (2584, the aura a battleground release casts) is taken: a player whose match is not in progress is sent to its
/// graveyard first, then the aura goes and the player comes back at full health without its corpse (<see cref="MapCombat.ResurrectBySpiritGuide"/>,
/// which removes the aura only on the way to a resurrection that happens), and its pet comes back (<see cref="SpellSystem.PlayerSpiritHealed"/>,
/// Player::AutoReSummonPet).
/// </summary>
public sealed class SpiritHealEffect : ISpellHandlerModule
{
    /// <summary>Waiting to Resurrect (vmangos <c>HasAura(2584)</c>).</summary>
    public const uint WaitingToResurrect = 2584;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.SpiritHeal, Effect);
    }

    private static void Effect(SpellEffectContext context)
    {
        if (context.Target is not Player player || player.Combat.DeathState == DeathState.Alive || !player.IsInWorld
            || player.Map is not { } map || !context.System.HasAura(player, WaitingToResurrect))
        {
            return;
        }

        SpellSystem system = context.System;
        if (map.Combat.ResurrectBySpiritGuide(player, healed => system.RemoveAuras(healed, WaitingToResurrect)))
        {
            context.System.NotifySpiritHealed(player);
        }
    }
}
