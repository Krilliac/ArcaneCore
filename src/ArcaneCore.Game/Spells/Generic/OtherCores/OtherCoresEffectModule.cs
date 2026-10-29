using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Generic.OtherCores;

/// <summary>
/// SPELL_EFFECT_TELEPORT_GRAVEYARD (120), used by Graveyard Teleport 24237 and Graveyard Teleport Test 24253 (cmangos SpellEffects.cpp:5580-5587
/// EffectTeleportGraveyard): a player target on a battleground map is sent to its graveyard as a released spirit would be (Player::RepopAtGraveyard).
/// Other targets and maps do nothing. World-thread only; the module holds no state.
/// </summary>
public sealed class OtherCoresEffectModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.TeleportGraveyard, context =>
        {
            if (context.Target is Player { Map: { Template.IsBattleground: true } map } player)
            {
                map.FindUpdater<MapCombat>()?.Hooks.RepopAtGraveyard(player);
            }
        });
    }
}
